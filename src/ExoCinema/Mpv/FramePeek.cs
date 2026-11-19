using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ExoCinema.Mpv;

/// <summary>
/// The seek bar's hover pictures for a video its server has none for. A second mpv opens the same video paused and
/// silent, jumps to the keyframe at a time and draws it small on the graphics card, with the player's own renderer and
/// tone mapping, into a window that is never shown; the frame comes back as a JPEG.
///
/// Pictures are made ahead, along the whole bar (ScrubPlan), once the player has the video well in hand, so the page
/// shows the one nearest the pointer in the same frame it moves. Where the pointer rests far from any, that time is
/// made first. Each video's pictures are kept on disk (ScrubStore), so the next time it plays they're all there at once.
/// The video is read through PeekStream: one connection to its host, one request per jump. Made on the UI thread,
/// which owns the window; mpv runs on a thread of its own.
/// </summary>
internal sealed unsafe class FramePeek : IDisposable
{
    private readonly record struct Ask(string Url, string? Auth, double Seconds, int Width, int Height, double Duration, int Video);

    // A video's pictures made ahead: the times left to make, and every picture made of it so far.
    private sealed class Ahead
    {
        public required string Url { get; init; }
        public string? Auth { get; init; }
        public double Duration { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int Video { get; init; }
        public required double[] Times { get; init; }
        public string? Key { get; init; }
        public Queue<double> Left { get; } = new();
        public Dictionary<double, (double At, byte[] Jpeg)> Made { get; } = [];
        public FileStream? File { get; set; }
    }

    private readonly IntPtr _window;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _gate = new();
    private readonly Action<string> _log;
    private readonly Action<Action> _onUi;
    private readonly Func<bool> _spare;
    private readonly (string Trc, string Prim, string Peak) _target;
    private Ask? _wanted;
    private Ahead? _nextAhead;
    private bool _stop, _forget;
    private volatile bool _closing, _abort, _holding;
    private readonly ManualResetEventSlim _letGo = new(true);

    // On the peek's own thread only.
    private IntPtr _ctx;
    private bool _ownReader;  // PeekStream registered with this mpv
    private string? _open;    // the URL mpv has open
    private string? _source;  // the exopeek:// address it has it at
    private (int W, int H) _size;
    private Ahead? _ahead;
    private string? _proven;  // a URL that has given a picture
    private string? _failing; // the URL the failures below are of
    private int _failures;
    private long _notBefore;  // nothing read from the host before this (Environment.TickCount64)

    /// <summary>A picture for a time: the time it really shows (its keyframe) and the JPEG; or none, for now or (gone)
    /// for good, as this video gives no pictures. With the page's number for the video it was asked for.</summary>
    public event Action<double, double, byte[]?, int, bool>? Ready;

    /// <param name="target">The player's own output (transfer, primaries, peak), so a picture looks as the video does.</param>
    /// <param name="spare">Whether the player can spare its host a second reader now (read well ahead, not seeking).</param>
    public FramePeek((string Trc, string Prim, string Peak) target, Func<bool> spare, Action<string> log, Action<Action> onUi)
    {
        _target = target;
        _spare = spare;
        _log = log;
        _onUi = onUi;
        // A popup that is never shown, can't be activated and stays off the taskbar: it only gives mpv somewhere to draw.
        _window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, "STATIC", null, WS_POPUP, 0, 0, 320, 180, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _thread = new Thread(Run) { IsBackground = true, Name = "mpv peek", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>The picture at <paramref name="seconds"/> in the video at <paramref name="url"/>, <paramref name="width"/> by
    /// <paramref name="height"/> device pixels, before any made ahead. <paramref name="duration"/> is the player's, to
    /// tell its video from a host's refusal notice.</summary>
    public void Request(string url, string? auth, double seconds, int width, int height, double duration, int video)
    {
        lock (_gate) _wanted = new Ask(url, auth, seconds, Math.Clamp(width, 64, 1280), Math.Clamp(height, 36, 1280), duration, video);
        _wake.Set();
    }

    /// <summary>Pictures along the whole bar for the video playing, on the page's grid of <paramref name="step"/> seconds;
    /// <paramref name="fileSize"/> (0 when unknown) sets how many and names the ones kept on disk.</summary>
    public void MakeAhead(string url, string? auth, double duration, double step, int width, int height, int video, long fileSize)
    {
        var ahead = new Ahead
        {
            Url = url, Auth = auth, Duration = duration, Video = video,
            Width = Math.Clamp(width, 64, 1280), Height = Math.Clamp(height, 36, 1280),
            Times = ScrubPlan.Times(duration, step, fileSize),
            Key = ScrubStore.Key(fileSize, duration),
        };
        lock (_gate) _nextAhead = ahead;
        _wake.Set();
    }

    /// <summary>The player moved on: the open video is let go (its connection with it); <paramref name="forget"/>, for a
    /// new video, drops the pictures being made ahead too.</summary>
    public void Stop(bool forget = false)
    {
        lock (_gate)
        {
            _wanted = null;
            _stop = true;
            if (forget)
            {
                _nextAhead = null;
                _forget = true;
            }
        }
        _abort = true;
        _wake.Set();
    }

    /// <summary>Whether a video is open here, holding a connection to its host.</summary>
    public bool Holding => _holding;

    /// <summary>Nothing is read from the host for the next <paramref name="ms"/>: the player is opening a request of its own.</summary>
    public void Hold(int ms)
    {
        var until = Environment.TickCount64 + ms;
        if (until > Volatile.Read(ref _holdUntil)) Volatile.Write(ref _holdUntil, until);
    }

    private long _holdUntil;
    private long NotBefore => Math.Max(_notBefore, Volatile.Read(ref _holdUntil));

    /// <summary>
    /// Lets go of the open video and waits up to <paramref name="ms"/> for its connection to close. The player opens a
    /// new connection for a seek before it closes its old one, so a host that allows two per link refused the seek
    /// while this one held the second, and the player took the video as ended.
    /// </summary>
    public void Release(int ms)
    {
        if (!_holding) return;
        _letGo.Reset();
        Stop();
        _letGo.Wait(ms);
    }

    private void Run()
    {
        var lastPicture = Environment.TickCount64;
        while (true)
        {
            _wake.WaitOne(Wait(lastPicture));
            if (_closing) break;
            Ahead? next;
            bool stop, forget;
            // Cleared before the asks are read: a Stop after this line cuts the drawing below short.
            _abort = false;
            lock (_gate)
            {
                next = _nextAhead;
                stop = _stop;
                forget = _forget;
                _nextAhead = null;
                _stop = _forget = false;
            }
            try
            {
                if (forget)
                {
                    _ahead?.File?.Dispose();
                    _ahead = null;
                }
                if (stop)
                {
                    LetGo();
                    // The player's seek, or its new video, takes the host's connection first.
                    _notBefore = Math.Max(_notBefore, Environment.TickCount64 + 3_000);
                }
                if (next != null) Begin(next);
                var now = Environment.TickCount64;
                if (now >= NotBefore)
                {
                    Ask? ask;
                    lock (_gate)
                    {
                        ask = _wanted;
                        _wanted = null;
                    }
                    if (ask is { } a)
                    {
                        Draw(a);
                        lastPicture = Environment.TickCount64;
                        continue;
                    }
                    if (_ahead is { Left.Count: > 0 } ahead && _spare())
                    {
                        MakeNext(ahead);
                        lastPicture = Environment.TickCount64;
                        continue;
                    }
                }
                // Two seconds without a picture made: the video is let go, so no connection is held while none is.
                // A minute: mpv goes, with its memory (about 50 MB). The next picture starts either again.
                if (_holding && now - lastPicture >= 2_000) LetGo();
                else if (!_holding && _ctx != IntPtr.Zero && now - lastPicture >= 60_000) Shut();
            }
            catch (Exception e) { _log($"peek: {AppLog.Kind(e)}"); }
        }
        _ahead?.File?.Dispose();
        Shut();
        _letGo.Set();
        _onUi(() => DestroyWindow(_window));
    }

    // How long to sleep before looking again: not at all while there's a picture to make and the host can be read.
    private int Wait(long lastPicture)
    {
        var now = Environment.TickCount64;
        bool wanted;
        lock (_gate)
        {
            if (_stop || _forget || _nextAhead != null) return 0;
            wanted = _wanted != null;
        }
        if (wanted || _ahead is { Left.Count: > 0 })
        {
            if (now < NotBefore) return (int)(NotBefore - now);
            if (wanted || _spare()) return 0;
            return 500;
        }
        if (_holding) return (int)Math.Max(0, 2_000 - (now - lastPicture));
        if (_ctx != IntPtr.Zero) return (int)Math.Max(0, 60_000 - (now - lastPicture));
        return Timeout.Infinite;
    }

    // A new video's pictures ahead: the ones kept from an earlier time it played go to the page at once, and only the
    // rest are made.
    private void Begin(Ahead next)
    {
        _ahead?.File?.Dispose();
        _ahead = next;
        if (next.Key is { } key) next.File = ScrubStore.Open(key, next.Made);
        foreach (var (t, p) in next.Made) Ready?.Invoke(t, p.At, p.Jpeg, next.Video, false);
        foreach (var t in next.Times)
            if (!next.Made.ContainsKey(t)) next.Left.Enqueue(t);
        _log($"peek: {next.Made.Count} pictures kept, {next.Left.Count} to make ahead");
    }

    private void MakeNext(Ahead ahead)
    {
        var t = ahead.Left.Peek();
        if (ahead.Made.ContainsKey(t) || Draw(new Ask(ahead.Url, ahead.Auth, t, ahead.Width, ahead.Height, ahead.Duration, ahead.Video)))
            ahead.Left.TryDequeue(out _);
    }

    // Closes the open video and waits until mpv has (its connection goes with it).
    private void LetGo()
    {
        if (_ctx != IntPtr.Zero && _open != null)
        {
            Command("stop");
            var until = Environment.TickCount64 + 500;
            while (Environment.TickCount64 < until && MpvNative.WaitEvent(_ctx, 0.05)->EventIdValue is not (MpvNative.EventId.EndFile or MpvNative.EventId.Idle or MpvNative.EventId.Shutdown)) { }
        }
        if (_source != null) PeekStream.Unregister(_source);
        _source = null;
        _open = null;
        _holding = false;
        _letGo.Set();
    }

    private void Shut()
    {
        if (_ctx == IntPtr.Zero) return;
        MpvNative.TerminateDestroy(_ctx);
        _ctx = IntPtr.Zero;
        _open = null;
        _size = (0, 0);
        if (_source != null) PeekStream.Unregister(_source);
        _source = null;
        _holding = false;
    }

    // The picture for an ask, handed on; true when there was one.
    private bool Draw(Ask a)
    {
        var ahead = _ahead is { } current && current.Url == a.Url ? current : null;
        if (ahead != null && ahead.Made.TryGetValue(a.Seconds, out var made))
        {
            Ready?.Invoke(a.Seconds, made.At, made.Jpeg, a.Video, false);
            return true;
        }
        if (_failing == a.Url && _failures < 0 || _ctx == IntPtr.Zero && !Start()) return Gone(a);
        if ((a.Width, a.Height) != _size)
        {
            // mpv's window inside follows this one's size; the picture is drawn at exactly the size asked.
            SetWindowPos(_window, IntPtr.Zero, 0, 0, a.Width, a.Height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            _size = (a.Width, a.Height);
        }
        var t = a.Seconds.ToString("0.###", CultureInfo.InvariantCulture);
        if (a.Url != _open)
        {
            var started = Environment.TickCount64;
            string path;
            if (_ownReader && (a.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || a.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                path = _source = PeekStream.Register(a.Url, a.Auth);
            }
            else
            {
                path = a.Url;
                Command("change-list", "http-header-fields", "clr", "");
                if (a.Auth != null) Command("change-list", "http-header-fields", "append", "Authorization: " + a.Auth);
            }
            // Holding from before the request goes out, so the player's own requests wait for it.
            _holding = true;
            _open = a.Url;
            Command("loadfile", path, "replace", "-1", "start=" + t);
            var landed = Landed(20_000);
            // Let go of mid-load (Release): nothing is known about this video yet.
            if (_abort) return false;
            // A host that refuses the second connection plays a short notice instead: not this video.
            var duration = Number("duration");
            if (!landed || a.Duration > 0 && Math.Abs(duration - a.Duration) > Math.Max(5, a.Duration * 0.02)) return Failed(a, "open");
            _log($"peek: opened in {Environment.TickCount64 - started} ms");
        }
        else
        {
            Command("seek", t, "absolute+keyframes");
            if (!Landed(10_000)) return !_abort && Failed(a, "seek");
        }
        if (Shot() is not { } jpeg) return Failed(a, "picture");
        var at = Number("time-pos");
        _proven = a.Url;
        if (_failing == a.Url) _failures = 0;
        if (ahead != null && ahead.Made.TryAdd(a.Seconds, (at, jpeg)) && ahead.File is { } file) ScrubStore.Add(file, a.Seconds, at, jpeg);
        Ready?.Invoke(a.Seconds, at, jpeg, a.Video, false);
        return true;
    }

    // A video that has given pictures failing now is its host's moment (a refused connection, a slow answer): it's
    // let go and read again later, 5 s on, doubling to a minute. One that never has gets a second try, then gives none.
    private bool Failed(Ask a, string what)
    {
        LetGo();
        if (_failing != a.Url)
        {
            _failing = a.Url;
            _failures = 0;
        }
        _failures++;
        if (_proven != a.Url && _failures >= 2)
        {
            _failures = -1; // for good
            _log("peek: this video gives no pictures");
            return Gone(a);
        }
        var wait = Math.Min(60_000, 5_000 << Math.Min(4, _failures - 1));
        _notBefore = Environment.TickCount64 + wait;
        _log($"peek: {what} failed, again in {wait / 1000} s");
        Ready?.Invoke(a.Seconds, -1, null, a.Video, false);
        return false;
    }

    // No pictures of this video, now or later: none are made ahead either.
    private bool Gone(Ask a)
    {
        if (_ahead?.Url == a.Url) _ahead.Left.Clear();
        Ready?.Invoke(a.Seconds, -1, null, a.Video, true);
        return false;
    }

    private bool Start()
    {
        _ctx = MpvNative.Create();
        if (_ctx == IntPtr.Zero) return false;
        string[][] options =
        [
            ["wid", _window.ToInt64().ToString(CultureInfo.InvariantCulture)],
            ["vo", "gpu-next"], ["gpu-api", "d3d11"], ["gpu-context", "d3d11"], ["hwdec", "auto-safe"],
            ["target-trc", _target.Trc], ["target-prim", _target.Prim], ["target-peak", _target.Peak],
            // Its own shader cache, beside the player's: two mpvs never write the same files.
            ["gpu-shader-cache-dir", Path.Combine(PlayerTuning.ShaderCache, "peek")],
            // Still and silent: no sound, no subtitles, nothing drawn over the picture.
            ["pause", "yes"], ["aid", "no"], ["sid", "no"], ["osd-level", "0"], ["osc", "no"],
            ["idle", "yes"], ["keep-open", "always"], ["ytdl", "no"], ["load-stats-overlay", "no"], ["load-auto-profiles", "no"],
            ["input-default-bindings", "no"], ["input-vo-keyboard", "no"], ["input-cursor", "no"], ["cursor-autohide", "no"],
            // The keyframe at the time, not the exact frame: one frame decoded per picture.
            ["hr-seek", "no"], ["keepaspect", "no"],
            // Only what one picture needs is read, nothing ahead.
            ["cache", "no"], ["demuxer-readahead-secs", "0"], ["demuxer-max-bytes", "2MiB"], ["demuxer-max-back-bytes", "0"],
            ["stream-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_on_network_error=1,reconnect_delay_max=5"],
        ];
        foreach (var o in options)
            if (MpvNative.SetOptionString(_ctx, o[0], o[1]) < 0) _log($"peek: option {o[0]} refused");
        // On the player's card, so its pictures are decoded and drawn as the video is.
        if (PlayerTuning.Adapter is { } adapter) MpvNative.SetOptionString(_ctx, "d3d11-adapter", adapter);
        if (MpvNative.Initialize(_ctx) >= 0)
        {
            _ownReader = PeekStream.AddTo(_ctx) >= 0;
            if (!_ownReader) _log("peek: reading through mpv's own http");
            return true;
        }
        MpvNative.TerminateDestroy(_ctx);
        _ctx = IntPtr.Zero;
        return false;
    }

    /// <summary>Waits for the picture after a load or seek; false when the video ended or failed instead.</summary>
    private bool Landed(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until && !_closing && !_abort)
        {
            var e = MpvNative.WaitEvent(_ctx, 0.1);
            if (e->EventIdValue == MpvNative.EventId.PlaybackRestart) return true;
            if (e->EventIdValue is MpvNative.EventId.EndFile or MpvNative.EventId.Shutdown) return false;
        }
        return false;
    }

    /// <summary>The window as mpv draws it (the picture, at the window's size) as a JPEG.</summary>
    private byte[]? Shot()
    {
        var args = new[] { "screenshot-raw", "window", "rgba" };
        var ptrs = new IntPtr[args.Length + 1];
        MpvNative.Node result;
        int r;
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            fixed (IntPtr* p = ptrs) r = MpvNative.CommandRet(_ctx, p, &result);
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
        if (r < 0) return null;
        try
        {
            if (result.Format != MpvNative.Format.NodeMap) return null;
            var map = (MpvNative.NodeList*)result.U;
            long w = 0, h = 0, stride = 0;
            MpvNative.ByteArray* data = null;
            for (var i = 0; i < map->Num; i++)
            {
                var value = map->Values[i];
                switch (MpvNative.Utf8(map->Keys[i]))
                {
                    case "w": w = value.U; break;
                    case "h": h = value.U; break;
                    case "stride": stride = value.U; break;
                    case "data": data = (MpvNative.ByteArray*)value.U; break;
                }
            }
            if (w <= 0 || h <= 0 || data == null) return null;
            var pixels = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                new ReadOnlySpan<byte>((byte*)data->Data + y * stride, (int)w * 4).CopyTo(pixels.AsSpan((int)(y * w * 4)));
            return Jpeg(pixels, (uint)w, (uint)h);
        }
        finally { MpvNative.FreeNodeContents(&result); }
    }

    private static byte[] Jpeg(byte[] pixels, uint w, uint h)
    {
        using var stream = new InMemoryRandomAccessStream();
        var quality = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.88f, Windows.Foundation.PropertyType.Single) };
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, quality).AsTask().GetAwaiter().GetResult();
        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, w, h, 96, 96, pixels);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        var bytes = new byte[stream.Size];
        using var read = stream.GetInputStreamAt(0).AsStreamForRead();
        read.ReadExactly(bytes);
        return bytes;
    }

    private void Command(params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            fixed (IntPtr* p = ptrs) MpvNative.Command(_ctx, p);
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    private double Number(string name)
    {
        var p = MpvNative.GetPropertyString(_ctx, name);
        if (p == IntPtr.Zero) return -1;
        try { return double.TryParse(MpvNative.Utf8(p), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1; }
        finally { MpvNative.Free(p); }
    }

    /// <summary>Lets the thread finish what it is drawing and close mpv there; the window goes after it, on the UI thread.</summary>
    public void Dispose()
    {
        _closing = true;
        _wake.Set();
    }

    private const uint WS_POPUP = 0x80000000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string? name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
