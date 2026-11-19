using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;

namespace ExoCinema.Mpv;

/// <summary>
/// How the player reads a video from its host: mpv opens exoplay://N and every read comes through here instead of
/// FFmpeg's own http. Reading on is one open request, as FFmpeg's; what Exo adds:
/// <list type="bullet">
/// <item>Seek intent (C25): while the pointer rests on or drags the seek bar, the cluster a seek there needs is fetched
/// ahead (from the file's own keyframe index, MkvIndex; newest point only, at most 4 MB), so the release lands on data
/// already here and the request for what follows goes out while it plays.</item>
/// <item>A link that redirects (a debrid service's) is asked at the address it sent us to from then on, one round
/// trip less for every seek, and at the link again when that address stops answering.</item>
/// <item>Connections are kept between requests (one handshake, not one per seek), at most two to the host.</item>
/// <item>A dropped connection is asked again from where it stopped, up to five times.</item>
/// <item>A read never outlasts the film: it waits on the socket with no end of its own, so mpv's cancel (Stop, the
/// next file, closing the window) and a host that sends nothing for <see cref="StallMs"/> both close the answer being
/// read, which ends the wait at once. Before 1.5 nothing ended it: a host that went quiet left mpv's stop, and with it
/// the whole window, waiting for good.</item>
/// </list>
/// The video's address never reaches mpv or its log.
/// </summary>
internal sealed class PlayStream : IDisposable
{
    public const string Protocol = "exoplay";
    private const int LoadingFailed = -13, Unsupported = -18, Generic = -20; // MPV_ERROR_*
    private const int HeadBytes = 256 << 10, IntentMost = 4 << 20, SkipMost = 512 << 10;

    private static readonly ConcurrentDictionary<int, (string Url, string? Auth, Action<string>? Log)> Sources = new();
    private static int _lastSource;
    private static PlayStream? _current;

    /// <summary>Lets an mpv open exoplay:// addresses.</summary>
    public static unsafe int AddTo(IntPtr ctx) => MpvNative.StreamCbAddRo(ctx, Protocol, IntPtr.Zero, &Open);

    /// <summary>The address mpv opens a video at.</summary>
    public static string Register(string url, string? auth, Action<string>? log)
    {
        var n = Interlocked.Increment(ref _lastSource);
        Sources[n] = (url, auth, log);
        return $"{Protocol}://{n}";
    }

    /// <summary>Whether a video is read through here: a file over http(s). Not HLS, whose pieces FFmpeg asks for itself.</summary>
    public static bool Suits(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && !url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    public static void Unregister(string path)
    {
        if (int.TryParse(path.AsSpan(Protocol.Length + 3), out var n)) Sources.TryRemove(n, out _);
    }

    /// <summary>A seek to <paramref name="seconds"/> is likely soon in the video playing: what it needs is fetched now.</summary>
    public static void Intent(double seconds) => Volatile.Read(ref _current)?.Prefetch(seconds);

    private readonly Uri _link;
    private readonly string? _auth;
    private readonly Action<string>? _log;
    private Uri? _landed;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cancel = new();
    private long _size = -1, _pos;
    private bool _ranges;

    // The open request read on (mpv's stream thread only).
    private HttpResponseMessage? _response;
    private Stream? _body;
    private long _bodyAt;
    private Task<(HttpResponseMessage, Stream)?>? _next;
    private long _nextAt = -1;

    // The first bytes, for the index; then the index once its Cues are read.
    private readonly byte[] _head = new byte[HeadBytes];
    private int _headLength;
    private volatile MkvIndex? _index;
    private int _indexAsked;
    // The Cues' bytes, kept: mpv reads them itself at its first seek, and gets them from here.
    private volatile byte[]? _cuesBytes;
    private long _cuesAt = -1;

    // What a seek intent is fetching or fetched: newest only. A read that lands in it takes the bytes as they arrive
    // rather than asking the host again, so a seek released before the fetch is done still gains what came.
    private sealed class Ahead(long at, int length, CancellationTokenSource cancel)
    {
        public readonly long At = at;
        public readonly byte[] Bytes = new byte[length];
        public readonly CancellationTokenSource Cancel = cancel;
        public int Got;
        public bool Over; // done, failed or replaced
        public long End => At + Bytes.Length;
    }
    private readonly Lock _gate = new();
    private volatile Ahead? _ahead;

    /// <summary>How long a read may wait without a byte before the host is asked again from the same place.</summary>
    internal static int StallMs = 30_000;

    // The answer a read is waiting on right now, and since when (set around each read by mpv's stream thread). Whoever
    // must end the wait takes it from here and closes it: the cancel, or the stall watch.
    private HttpResponseMessage? _reading;
    private long _readingSince;
    private readonly Timer _stallWatch;

    private void EndRead() => Interlocked.Exchange(ref _reading, null)?.Dispose();

    // One read of the open answer. The socket wait inside it ends when bytes come, the host closes, or EndRead.
    private int ReadBody(Span<byte> buffer)
    {
        Volatile.Write(ref _readingSince, Environment.TickCount64);
        Volatile.Write(ref _reading, _response);
        try
        {
            // Cancelled between the caller's look and here: the cancel found nothing to close, so it ends here.
            if (_cancel.IsCancellationRequested) throw new OperationCanceledException();
            return _body!.Read(buffer);
        }
        finally { Volatile.Write(ref _reading, null); }
    }

    private PlayStream(string url, string? auth, Action<string>? log)
    {
        _link = new Uri(url);
        _auth = auth;
        _log = log;
        _cancel.Token.Register(EndRead);
        var every = Math.Clamp(StallMs / 3, 50, 5000);
        _stallWatch = new Timer(_ =>
        {
            if (Volatile.Read(ref _reading) is null || Environment.TickCount64 - Volatile.Read(ref _readingSince) < StallMs) return;
            _log?.Invoke($"stream: nothing came for {StallMs / 1000.0:0.#} s, asking again from where it stopped");
            EndRead();
        }, null, every, every);
        _http = new HttpClient(Net.Egress.Checkpoint(Net.Egress.Purpose.Stream, new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 2,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
        }), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private HttpRequestMessage Ask(Uri target, long from, long to)
    {
        var ask = new HttpRequestMessage(HttpMethod.Get, target);
        ask.Headers.Range = new RangeHeaderValue(from, to < 0 ? null : to);
        // As FFmpeg's requests say, so hosts that sort clients by it treat the player as before.
        ask.Headers.TryAddWithoutValidation("User-Agent", "libmpv");
        if (_auth != null && target.Authority == _link.Authority) ask.Headers.TryAddWithoutValidation("Authorization", _auth);
        return ask;
    }

    /// <summary>Asks for the bytes from <paramref name="from"/> (to <paramref name="to"/>, or the end), at the address the
    /// link landed on, else the link; null when the host refuses or can't be reached.</summary>
    private async Task<(HttpResponseMessage, Stream)?> RequestAsync(long from, long to, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var target = attempt == 0 && _landed != null ? _landed : _link;
            using var ask = Ask(target, from, to);
            HttpResponseMessage? answer = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(15_000);
                answer = await _http.SendAsync(ask, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var partial = answer.StatusCode == HttpStatusCode.PartialContent && answer.Content.Headers.ContentRange is { From: { } at } && at == from;
                var whole = from == 0 && answer.StatusCode == HttpStatusCode.OK;
                if (!partial && !whole)
                {
                    answer.Dispose();
                    if (target == _landed) { _landed = null; continue; }
                    return null;
                }
                if (partial)
                {
                    _ranges = true;
                    if (answer.Content.Headers.ContentRange!.Length is { } total) _size = total;
                }
                else if (answer.Content.Headers.ContentLength is { } length) _size = length;
                if (answer.RequestMessage?.RequestUri is { } landed && landed != target) _landed = landed;
                return (answer, await answer.Content.ReadAsStreamAsync(ct));
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            {
                answer?.Dispose();
                if (ct.IsCancellationRequested) return null;
                if (target == _landed) { _landed = null; continue; }
                return null;
            }
        }
        return null;
    }

    private void CloseBody()
    {
        _body?.Dispose();
        _response?.Dispose();
        _body = null;
        _response = null;
    }

    private void DropNext()
    {
        if (_next is { } next) _ = next.ContinueWith(t => { if (t.Result is { } r) { r.Item2.Dispose(); r.Item1.Dispose(); } }, TaskScheduler.Default);
        _next = null;
        _nextAt = -1;
    }

    // The open request moved to `at`: the one started ahead if it's for there, else a new one.
    private bool OpenAt(long at)
    {
        CloseBody();
        // A fetch ahead for another place would only share the line with this one.
        if (_ahead is { Over: false } stale && (at < stale.At || at >= stale.End)) stale.Cancel.Cancel();
        Task<(HttpResponseMessage, Stream)?> task;
        if (_next != null && _nextAt == at) { task = _next; _next = null; _nextAt = -1; }
        else
        {
            DropNext();
            task = RequestAsync(at, -1, _cancel.Token);
        }
        if (task.GetAwaiter().GetResult() is not { } open) return false;
        (_response, _body) = open;
        _bodyAt = at;
        return true;
    }

    private int ReadInto(Span<byte> buffer)
    {
        if (_size >= 0 && _pos >= _size) return 0;
        if (_cuesBytes is { } cues && _pos >= _cuesAt && _pos < _cuesAt + cues.Length)
        {
            var n = (int)Math.Min(buffer.Length, _cuesAt + cues.Length - _pos);
            cues.AsSpan((int)(_pos - _cuesAt), n).CopyTo(buffer);
            Advance(n, buffer[..n]);
            return n;
        }
        if ((_body is null || _bodyAt != _pos) && _ahead is { } a && _pos >= a.At && _pos < a.End && FromAhead(a, buffer) is var fromAhead and > 0) return fromAhead;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (_body != null && _pos > _bodyAt && _pos - _bodyAt <= SkipMost)
                {
                    // A short jump ahead: read through it rather than ask again.
                    Span<byte> skip = stackalloc byte[16 << 10];
                    while (_bodyAt < _pos)
                    {
                        var r = ReadBody(skip[..(int)Math.Min(skip.Length, _pos - _bodyAt)]);
                        if (r <= 0) throw new IOException("ended while skipping");
                        _bodyAt += r;
                    }
                }
                if ((_body is null || _bodyAt != _pos) && !OpenAt(_pos)) throw new IOException("refused");
                var got = ReadBody(buffer);
                if (got <= 0)
                {
                    if (_size < 0 || _pos >= _size) return 0;
                    throw new IOException("closed early");
                }
                _bodyAt += got;
                Advance(got, buffer[..got]);
                return got;
            }
            // Any failure of the read: an answer closed under it (EndRead) can surface as more than an IOException.
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                CloseBody();
                if (_cancel.IsCancellationRequested || attempt >= 5) return -1;
                _log?.Invoke($"stream: read again from {_pos / 1048576.0:0.0} MB ({AppLog.Kind(e)})");
                Thread.Sleep(Math.Min(4000, 250 << attempt));
            }
        }
    }

    // Bytes at the place from what a seek intent fetches, waiting for them while that fetch goes on; 0 when it ended
    // without them (the place is then asked for as usual).
    private int FromAhead(Ahead a, Span<byte> buffer)
    {
        CloseBody();
        var from = (int)(_pos - a.At);
        int got;
        bool over;
        lock (a)
        {
            while (a.Got <= from && !a.Over && !_cancel.IsCancellationRequested) Monitor.Wait(a, 100);
            (got, over) = (a.Got, a.Over);
        }
        if (got <= from) return 0;
        // What follows is asked for once the fetch is in, so the two don't share the line.
        if (over && got == a.Bytes.Length && _nextAt != a.End)
        {
            DropNext();
            _nextAt = a.End;
            _next = RequestAsync(a.End, -1, _cancel.Token);
        }
        var n = Math.Min(buffer.Length, got - from);
        a.Bytes.AsSpan(from, n).CopyTo(buffer);
        Advance(n, buffer[..n]);
        return n;
    }

    private void Advance(int n, ReadOnlySpan<byte> read)
    {
        if (_pos < HeadBytes && _pos == _headLength)
        {
            var keep = (int)Math.Min(n, HeadBytes - _pos);
            read[..keep].CopyTo(_head.AsSpan(_headLength));
            _headLength += keep;
        }
        _pos += n;
        if (_headLength >= 64 << 10 && _index is null && _ranges && Interlocked.Exchange(ref _indexAsked, 1) == 0) _ = Task.Run(ReadIndexAsync);
    }

    // The Cues, asked for once after the head has passed: a few hundred KB from near the end of the file.
    private async Task ReadIndexAsync()
    {
        try
        {
            if (MkvIndex.ReadHead(_head.AsSpan(0, _headLength)) is not { } head || head.CuesAt >= _size) return;
            var length = (int)Math.Min(IntentMost, _size - head.CuesAt);
            if (await FetchAsync(head.CuesAt, length, _cancel.Token) is not { } bytes) return;
            _index = MkvIndex.ReadCues(bytes, head.SegmentStart, head.Scale);
            if (_index != null)
            {
                _cuesAt = head.CuesAt;
                _cuesBytes = bytes;
            }
            _log?.Invoke(_index is { } i ? $"stream: keyframe index read ({i.Count} points)" : "stream: no keyframe index");
        }
        catch (Exception e) { _log?.Invoke($"stream: keyframe index not read ({AppLog.Kind(e)})"); }
    }

    private async Task<byte[]?> FetchAsync(long at, int length, CancellationToken ct)
    {
        if (await RequestAsync(at, at + length - 1, ct) is not { } open) return null;
        using var response = open.Item1;
        await using var body = open.Item2;
        var bytes = new byte[length];
        var got = 0;
        while (got < length)
        {
            var r = await body.ReadAsync(bytes.AsMemory(got), ct);
            if (r <= 0) break;
            got += r;
        }
        return got == length ? bytes : got > 0 ? bytes[..got] : null;
    }

    private void Prefetch(double seconds)
    {
        if (_index is not { } index || _cancel.IsCancellationRequested) return;
        var (from, to) = index.For(seconds);
        var length = (int)Math.Min(IntentMost, (to > from ? to : _size) - from);
        if (length <= 0) return;
        Ahead ahead;
        lock (_gate)
        {
            if (_ahead is { } old)
            {
                if (old.At == from && !(old.Over && old.Got < old.Bytes.Length)) return;
                old.Cancel.Cancel();
            }
            _ahead = ahead = new Ahead(from, length, CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token));
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (await RequestAsync(from, from + length - 1, ahead.Cancel.Token) is not { } open) return;
                using var response = open.Item1;
                await using var body = open.Item2;
                while (ahead.Got < length)
                {
                    var r = await body.ReadAsync(ahead.Bytes.AsMemory(ahead.Got), ahead.Cancel.Token);
                    if (r <= 0) break;
                    lock (ahead)
                    {
                        ahead.Got += r;
                        Monitor.PulseAll(ahead);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is HttpRequestException or IOException) { _log?.Invoke($"stream: seek ahead failed ({AppLog.Kind(e)})"); }
            finally
            {
                lock (ahead)
                {
                    ahead.Over = true;
                    Monitor.PulseAll(ahead);
                }
            }
        });
    }

    public void Dispose()
    {
        _stallWatch.Dispose();
        _cancel.Cancel();
        if (_ahead is { } a) lock (a) Monitor.PulseAll(a);
        Interlocked.CompareExchange(ref _current, null, this);
        CloseBody();
        DropNext();
        _http.Dispose();
    }

    // For tests, which have no mpv: the same open, read and cancel mpv's callbacks make.
    internal static PlayStream? OpenForTest(string url, Action<string>? log = null)
    {
        var stream = new PlayStream(url, null, log);
        if (stream.OpenAt(0)) return stream;
        stream.Dispose();
        return null;
    }
    internal int ReadForTest(Span<byte> buffer) => ReadInto(buffer);
    internal void CancelForTest() => _cancel.Cancel();

    private static PlayStream Of(IntPtr cookie) => (PlayStream)GCHandle.FromIntPtr(cookie).Target!;

    [UnmanagedCallersOnly]
    private static unsafe int Open(IntPtr user, byte* uri, MpvNative.StreamCbInfo* info)
    {
        try
        {
            var path = Marshal.PtrToStringUTF8((IntPtr)uri) ?? "";
            if (!path.StartsWith(Protocol + "://", StringComparison.Ordinal) || !int.TryParse(path.AsSpan(Protocol.Length + 3), out var n) || !Sources.TryGetValue(n, out var source))
                return LoadingFailed;
            var stream = new PlayStream(source.Url, source.Auth, source.Log);
            if (!stream.OpenAt(0))
            {
                stream.Dispose();
                return LoadingFailed;
            }
            Volatile.Write(ref _current, stream);
            info->Cookie = GCHandle.ToIntPtr(GCHandle.Alloc(stream));
            info->Read = &Read;
            info->Seek = &SeekTo;
            info->Size = &Size;
            info->Close = &Close;
            info->Cancel = &Cancel;
            return 0;
        }
        catch
        {
            return LoadingFailed;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe long Read(IntPtr cookie, byte* buffer, ulong count)
    {
        try { return Of(cookie).ReadInto(new Span<byte>(buffer, (int)Math.Min(count, int.MaxValue))); }
        catch { return -1; }
    }

    // A jump only moves the place; the next read finds the bytes there. A host without byte ranges can't jump.
    [UnmanagedCallersOnly]
    private static long SeekTo(IntPtr cookie, long offset)
    {
        try
        {
            var s = Of(cookie);
            if (offset < 0 || s._size >= 0 && offset > s._size) return Generic;
            if (!s._ranges && offset != s._pos) return Unsupported;
            s._pos = offset;
            return offset;
        }
        catch
        {
            return Generic;
        }
    }

    [UnmanagedCallersOnly]
    private static long Size(IntPtr cookie)
    {
        try { return Of(cookie)._size is > 0 and var size ? size : Unsupported; }
        catch { return Unsupported; }
    }

    [UnmanagedCallersOnly]
    private static void Close(IntPtr cookie)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(cookie);
            var s = (PlayStream)handle.Target!;
            handle.Free();
            s.Dispose();
        }
        catch { }
    }

    // From another thread: the read under way, and any after it, stop.
    [UnmanagedCallersOnly]
    private static void Cancel(IntPtr cookie)
    {
        try { Of(cookie)._cancel.Cancel(); }
        catch { }
    }
}
