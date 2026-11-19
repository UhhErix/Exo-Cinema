using System.Text.Json.Nodes;
using ExoCinema.Library;
using ExoCinema.Mpv;
using Microsoft.UI.Windowing;

namespace ExoCinema;

/// <summary>mpv: what the page sees of it and what the library hears of it (Library/CinemaLibrary.cs).</summary>
public sealed partial class MainWindow
{
    private static readonly (string, MpvNative.Format)[] ObservedProperties =
    [
        ("pause", MpvNative.Format.Flag),
        ("duration", MpvNative.Format.Double),
        ("time-pos", MpvNative.Format.Double),
        ("demuxer-cache-time", MpvNative.Format.Double),
        ("cache-speed", MpvNative.Format.Double), // what the link gives, bytes a second (MainWindow.Player.cs, FollowBuffer)
        ("paused-for-cache", MpvNative.Format.Flag),
        ("eof-reached", MpvNative.Format.Flag),
        ("volume", MpvNative.Format.Double),
        ("mute", MpvNative.Format.Flag),
        ("media-title", MpvNative.Format.String),
        ("video-codec", MpvNative.Format.String),
        ("hwdec-current", MpvNative.Format.String),
        ("video-params/gamma", MpvNative.Format.String),
        ("video-params/primaries", MpvNative.Format.String),
        ("video-params/w", MpvNative.Format.Int64),
        ("video-params/h", MpvNative.Format.Int64),
        ("container-fps", MpvNative.Format.Double),
        ("audio-codec-name", MpvNative.Format.String),
        ("audio-out-params/format", MpvNative.Format.String), // "spdif-…": the receiver plays the track as it is (MainWindow.Sound.cs)
        ("audio-params/channels", MpvNative.Format.String), // the film's channel layout, and the output's count: clear dialogue
        ("audio-out-params/channel-count", MpvNative.Format.Int64),
        ("track-list/count", MpvNative.Format.Int64),
        ("seeking", MpvNative.Format.Flag),
        ("seekable", MpvNative.Format.Flag), // no, from a host that serves no byte ranges: only what's loaded can be reached
        ("core-idle", MpvNative.Format.Flag),
        ("video-params/pixelformat", MpvNative.Format.String),
        ("video-params/hw-pixelformat", MpvNative.Format.String),
        ("video-format", MpvNative.Format.String),
        ("osd-dimensions", MpvNative.Format.None), // the video's place in the window, for the glass
    ];

    // Timings: load and seek are measured from the command to mpv's playback-restart.
    private double _loadAt = -1, _seekAt = -1;
    private bool _firstFrameSeen, _reconfigSeen;
    private double _lastTimePost;

    private bool _fullScreen;
    private (Windows.Graphics.RectInt32 Bounds, bool Maximized)? _beforeFullScreen;

    // What the player has open, for the seek bar's hover pictures (FramePeek), and the second mpv that draws them.
    private (string Url, string? Auth)? _playing;
    private FramePeek? _peek;

    // The exoplay:// address mpv has the video open at (PlayStream), and how it was loaded, to load it the old way when
    // Exo's reader can't open it.
    private string? _playingVia, _playOptions;

    public void Play(string url, string? authHeader, double startSeconds, string? title)
    {
        if (!TryStartMpv()) throw new LibraryException("The player couldn't start. If your graphics driver is being updated, try again when it's done.");
        var mpv = _mpv!;
        _playing = (url, authHeader);
        _animeWaitLogged = false;
        if (_warmup is { } running && !running.Cancelled)
        {
            running.Cancel();
            _warmupWhenIdle = true;
            _log.Write("shaders: paused, a film started; the compile resumes when playback stops");
        }
        _peek?.Stop(forget: true);
        _seekAfterPeek = null;
        _intentAfterPeek = null;
        _setAfterPeek.Clear();
        // Anime or not is the page's to say for each video (SetAnime); a new one starts as not.
        _anime = false;
        _animeOff = false;
        _loadAt = MpvPlayer.Now;
        _firstFrameSeen = false;
        _reconfigSeen = false;
        ResetBuffer(mpv);
        LogPlan();
        // The Authorization header has commas in it, so it goes in as one list item rather than a parsed list.
        mpv.Command("change-list", "http-header-fields", "clr", "");
        if (authHeader != null) mpv.Command("change-list", "http-header-fields", "append", "Authorization: " + authHeader);
        mpv.Set("force-media-title", title ?? "");
        // keep-open pauses at the end of a file, and that pause would carry into the next one.
        mpv.Set("pause", "no");
        ChoosePassthrough(mpv);
        var options = startSeconds > 0 ? $"start={startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}" : "";
        // A file on this PC is read as it plays, as Kodi does: the network's minutes of read-ahead held a 4K file whole
        // in memory (300 MB a minute at 40 Mb/s), and a disk never runs dry the way a link does.
        if (!url.Contains("://") || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            options = string.Join(',', new[] { options, "cache=no,demuxer-readahead-secs=10,demuxer-max-bytes=64MiB,demuxer-max-back-bytes=16MiB" }.Where(o => o.Length > 0));
        if (_playingVia != null) PlayStream.Unregister(_playingVia);
        _playingVia = mpv.OwnStreams && PlayStream.Suits(url) ? PlayStream.Register(url, authHeader, _log.Write) : null;
        _playOptions = options;
        mpv.Command("loadfile", _playingVia ?? url, "replace", "-1", options);
        _readAheadNetwork = !options.Contains("cache=no");
        _readAheadSet = (-1, -1);
        ReadAheadTick();
        Post(new { type = "playing", title });
    }

    public void Stop()
    {
        _mpv?.Command("stop");
        RestoreFilmHdr("the player stopped");
        // Out of the player: the hover pictures' mpv goes too, with its memory, until the next video is hovered.
        _playing = null;
        _readAheadNetwork = false;
        if (_playingVia != null) PlayStream.Unregister(_playingVia);
        _playingVia = null;
        _peek?.Dispose();
        _peek = null;
        if (_restartWhenIdle) RestartMpv();
        if (_warmupWhenIdle && _warmup is null) { _warmupWhenIdle = false; StartWarmup(); }
    }

    // The anime upscaler compiled ahead (Mpv/ShaderWarmup.cs): started 20 s after the app opens, or when anime plays
    // first, never while a game runs. mpv reads its shader cache only as it starts, so once the shaders are in it the
    // player is started again as soon as nothing plays; until then anime gets the driver's upscaler.
    private static string[] AnimeShaderFiles => AnimeChain().Select(f => Path.Combine(AnimeShaders, f)).ToArray();
    private bool _mpvWarm, _restartWhenIdle, _warmupWhenIdle, _warmupFailed;
    private ShaderWarmup? _warmup;
    private bool _animeWaitLogged;

    private void StartWarmup()
    {
        if (_warmupFailed) return; // a compile that drew no frame is not retried until the next start
        // The decision is pure (WarmupPlan). The compile itself runs on its own thread (ShaderWarmup), never the UI's.
        var step = WarmupPlan.Next(new WarmupPlan.Input(_closingWindow, PlayerTuning.Strong, _warmup != null, _mpvWarm,
            Directory.Exists(AnimeShaders), GameRunning(), _lean, ShaderWarmup.Ready(AnimeShaderFiles), _playing != null));
        if (step == WarmupPlan.Step.None) return;
        if (step == WarmupPlan.Step.RestartWhenIdle) { RestartWhenIdle(); return; }
        // A film is playing: the compile waits for it to stop, so it never competes with a film.
        if (step == WarmupPlan.Step.CompileWhenIdle) { _warmupWhenIdle = true; return; }
        // The compile holds a full core for about 40 s, once per card and driver: Cinema steps back for it, so whatever else
        // runs (a game, a stream) keeps its share. Until the restart, anime plays on the driver's upscaler.
        WarmupPriority(true);
        var warmup = ShaderWarmup.Start(AnimeShaderFiles, _log.Write, OnUi);
        _warmup = warmup;
        warmup.Done.ContinueWith(t => OnUi(() => WarmupEnded(warmup, t.Result)));
    }

    private void WarmupEnded(ShaderWarmup warmup, bool ok)
    {
        _warmup = null;
        WarmupPriority(false);
        if (ok)
        {
            if (_playing is not null) _log.Write("shaders: ready; the film playing goes on without ArtCNN, the next film gets it");
            RestartWhenIdle();
        }
        else if (!warmup.Cancelled) _warmupFailed = true;
        // Paused by a film that then stopped before this ended: resume now.
        if (_warmupWhenIdle && _playing is null) { _warmupWhenIdle = false; StartWarmup(); }
    }

    private void WarmupPriority(bool low)
    {
        if (_leanOn) return;
        try { using var me = System.Diagnostics.Process.GetCurrentProcess(); me.PriorityClass = low ? System.Diagnostics.ProcessPriorityClass.BelowNormal : System.Diagnostics.ProcessPriorityClass.Normal; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private void RestartWhenIdle()
    {
        if (_playing is null) RestartMpv();
        else _restartWhenIdle = true;
    }

    /// <summary>A new mpv in place of the idle one, reading the shader cache afresh; what it was set to comes along.</summary>
    private void RestartMpv()
    {
        _restartWhenIdle = false;
        if (_closingWindow || _playing != null || _mpv is not { } old) return;
        // This runs on the UI thread. ApplyPicture holds the gate on another thread while mpv answers it, and mpv can be
        // busy for a minute with a shader compile: the UI thread never waits for the gate. If it is taken, the restart
        // is tried again at the next idle moment (or the next start), and the player goes on as it is meanwhile.
        if (!_pictureGate.TryEnter())
        {
            // Nothing else will call again when nothing plays: try again shortly, and RestartWhenIdle waits for a film's stop.
            _restartWhenIdle = true;
            _ = Task.Delay(300).ContinueWith(_ => OnUi(RestartWhenIdle));
            return;
        }
        try
        {
            // Nothing the old player still says reaches the page or reads _mpv while it is null.
            old.PropertyChanged -= OnProperty;
            old.EventRaised -= OnMpvEvent;
            old.Log -= OnMpvLog;
            _mpv = null;
            old.Dispose();
            // What was applied to the old player is applied to the new one again.
            _shaderList = null;
            _animeChain = [];
            _vppFilter = "";
            _upscaleScale = 0;
            _vppHdr = false;
            _smoothOn = false;
            _pictureHigh = PlayerTuning.Strong;
            _glassOpts = "";
            _subPos = "";
        }
        finally { _pictureGate.Exit(); }
        if (!TryStartMpv()) return;
        if (_props.GetValueOrDefault("volume") is double volume) _mpv!.Set("volume", volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_props.GetValueOrDefault("mute") is bool mute) _mpv!.Set("mute", mute ? "yes" : "no");
        _log.Write("player: started again with the anime upscaler compiled");
    }

    // What makes the player open a new connection to its host (a seek; a track turned on, which mpv reads in with a
    // seek of its own) waits for the hover pictures' mpv to let go of its connection (FramePeek.Release), and keeps it
    // off the host for 3 s after: on a host that allows two connections per link, a third is refused and mpv takes the
    // refusal as the end of the video. Seeks that come meanwhile only move the target, so it lands on the last one asked.
    private (double To, string Mode)? _seekAfterPeek;
    private readonly List<(string Name, string Value)> _setAfterPeek = [];
    private bool _peekLetting;
    private double? _intentAfterPeek;

    private void Seek(double to, string mode)
    {
        _peek?.Hold(3_000);
        if (_peekLetting || _peek is { Holding: true })
        {
            _seekAfterPeek = (to, mode);
            LetPeekGo();
            return;
        }
        SeekNow(to, mode);
    }

    private static bool OpensTrack(string name) => name is "aid" or "vid" or "sid" or "secondary-sid";

    private void SetTrack(string name, string value)
    {
        _peek?.Hold(3_000);
        if (_peekLetting || _peek is { Holding: true })
        {
            _setAfterPeek.Add((name, value));
            LetPeekGo();
            return;
        }
        _mpv?.Set(name, value);
    }

    private void LetPeekGo()
    {
        if (_peekLetting || _peek is not { } peek) return;
        _peekLetting = true;
        var asked = MpvPlayer.Now;
        Task.Run(() => peek.Release(800)).ContinueWith(_ => OnUi(() =>
        {
            _peekLetting = false;
            foreach (var (name, value) in _setAfterPeek) _mpv?.Set(name, value);
            _setAfterPeek.Clear();
            if (_intentAfterPeek is { } intent && _playingVia != null) PlayStream.Intent(intent);
            _intentAfterPeek = null;
            if (_seekAfterPeek is not { } wanted) return;
            _seekAfterPeek = null;
            _log.Write($"seek: after the hover pictures let go ({MpvPlayer.Now - asked:0} ms)");
            SeekNow(wanted.To, wanted.Mode);
        }));
    }

    private void SeekNow(double to, string mode)
    {
        if (_mpv is not { } mpv) return;
        _seekAt = MpvPlayer.Now;
        mpv.Command("seek", to.ToString(System.Globalization.CultureInfo.InvariantCulture), mode);
    }

    /// <summary>
    /// The picture at a time in the open video for the seek bar's hover preview. Not for an HLS stream: every picture
    /// would start another conversion on its host.
    /// </summary>
    private void Peek(JsonNode msg)
    {
        var t = (double)msg["t"]!;
        if (_playing is not { } now || now.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            Post(new { type = "peek", t, v = (int?)msg["v"] ?? 0, none = true });
            return;
        }
        _peek ??= NewPeek();
        _peek.Request(now.Url, now.Auth, t, (int)msg["w"]!, (int)msg["h"]!, (double?)msg["d"] ?? 0, (int?)msg["v"] ?? 0);
    }

    /// <summary>
    /// The page asks, once the video plays, for its seek bar's pictures along the whole bar (FramePeek.MakeAhead): the
    /// server has no tiles for it. Its size in bytes sets how many and names the ones kept on disk.
    /// </summary>
    /// <summary>The pointer rests on or drags the seek bar: what a seek there needs is fetched ahead (PlayStream). It
    /// comes before the hover pictures as a seek does (a picture near the place already shows): their connection is let
    /// go first, or it would be a third to the host.</summary>
    private void SeekIntent(JsonNode msg)
    {
        if (_playingVia is null) return;
        var t = (double)msg["t"]!;
        _peek?.Hold(3_000);
        if (_peekLetting || _peek is { Holding: true })
        {
            _intentAfterPeek = t;
            LetPeekGo();
            return;
        }
        PlayStream.Intent(t);
    }

    private void PeekAhead(JsonNode msg)
    {
        if (_playing is not { } now || now.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || _mpv is not { } mpv) return;
        var bytes = long.TryParse(mpv.Get("file-size"), out var size) ? size : 0;
        _peek ??= NewPeek();
        _peek.MakeAhead(now.Url, now.Auth, (double)msg["d"]!, (double)msg["step"]!, (int)msg["w"]!, (int)msg["h"]!, (int?)msg["v"] ?? 0, bytes);
    }

    // Whether the player can spare its host a second reader for the pictures made ahead: it isn't waiting on the
    // network or seeking, has 20 s read ahead (or the rest of the video), and won't open a new request soon. It does
    // when reading on reaches a stretch it already holds (after a seek back), so for as long as its cache has one
    // ahead, and 3 s after. Kept on mpv's event thread.
    private volatile bool _spare;
    private double _cachedTo, _at, _length;
    private bool _starved, _jumping;
    private long _rangesReadAt, _joinSeenAt;

    private void FollowSpare(string name, object? value)
    {
        switch (name)
        {
            case "time-pos": _at = value as double? ?? 0; break;
            case "demuxer-cache-time": _cachedTo = value as double? ?? 0; break;
            case "duration": _length = value as double? ?? 0; break;
            case "paused-for-cache": _starved = value is true; break;
            case "seeking": _jumping = value is true; break;
            default: return;
        }
        var now = Environment.TickCount64;
        if (now - _rangesReadAt >= 500)
        {
            _rangesReadAt = now;
            if (CacheState.NewRequestComing(_mpv?.Get("demuxer-cache-state"))) _joinSeenAt = now;
        }
        // The pictures ahead wait for the same read-ahead playback does (FollowBuffer), at least 20 s.
        _spare = !_starved && !_jumping && now - _joinSeenAt >= 3_000 && _cachedTo - _at >= Math.Min(Math.Max(20, _bufferWait), Math.Max(0, _length - _at - 1));
    }

    // The read-ahead playback waits for before it starts and after it runs dry (StartBuffer): the link's speed, measured
    // from mpv's cache-speed, against the file's average bitrate. mpv's cache-pause-wait is set to it, so a slow link
    // waits longer up front and resumes after one longer wait rather than stuttering. The page hears of it as "buffer";
    // slow is set when the link can't cover the rest of the film even at the longest wait.
    private const double SpeedSettleMs = 10_000;
    private double _speedBps = -1, _speedSince = -1, _bufferWait = StartBuffer.UnknownSeconds;
    private long _fileBytes;
    private bool _bufferSlow, _speedMeasured;

    private void ResetBuffer(MpvPlayer mpv)
    {
        _speedBps = -1;
        _speedSince = -1;
        _speedMeasured = false;
        _fileBytes = 0;
        _bufferWait = StartBuffer.UnknownSeconds;
        _bufferSlow = false;
        mpv.Set("cache-pause-wait", _bufferWait.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void FollowBuffer(string name, object? value)
    {
        if (name == "cache-speed")
        {
            // Only the samples in which the link gave something; a pause reads 0 and says nothing about the link.
            if (value is double s && s > 0)
            {
                if (_speedSince < 0) _speedSince = MpvPlayer.Now;
                _speedBps = _speedBps < 0 ? s : 0.8 * _speedBps + 0.2 * s;
            }
        }
        else if (name != "duration") return;
        if (_fileBytes <= 0 && _length > 0 && long.TryParse(_mpv?.Get("file-size"), out var size)) _fileBytes = size;
        if (_fileBytes <= 0 || _length <= 0) return;
        // The first samples come while the file's head and index are read, not the film: the link is measured for a while
        // from its first sample before the wait is set from it (the default meanwhile), or a slow open reads as a slow link.
        if (_speedSince < 0 || MpvPlayer.Now - _speedSince < SpeedSettleMs) return;
        // The first settled measure of this stream is the link's speed now (LinkSpeed keeps when it was taken): a PC that
        // hasn't streamed lately, or whose measure is stale, plans from the film it is playing rather than from an old guess.
        if (!_speedMeasured) { _speedMeasured = true; LinkSpeed.Record(_speedBps * 8 / 1e6); }
        var plan = StartBuffer.Estimate(_fileBytes / _length, _speedBps, Math.Max(0, _length - _at));
        if (Math.Abs(plan.WaitSeconds - _bufferWait) < 1 && plan.TooSlow == _bufferSlow) return;
        _bufferWait = plan.WaitSeconds;
        _bufferSlow = plan.TooSlow;
        _mpv?.Set("cache-pause-wait", _bufferWait.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        var wait = _bufferWait;
        var slow = _bufferSlow;
        var link = _speedBps * 8 / 1e6;
        var rate = _fileBytes / _length * 8 / 1e6;
        _log.Write($"buffer: waits {wait:0} s of read-ahead; the link gives {link:0.0} Mb/s, the file averages {rate:0.0} Mb/s{(slow ? "; too slow for the rest of this film even at the longest wait" : "")}");
        OnUi(() => Post(new { type = "buffer", wait, slow, link }));
    }

    /// <summary>What kind of address the video plays from, never the address: the stall log says which path it came by.</summary>
    private string SourceKind()
    {
        if (_playing is not { } p) return "no video";
        if (_playingVia != null) return "exoplay reader";
        if (!p.Url.Contains("://") || p.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return "local file";
        return p.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? "hls" : "direct http";
    }

    /// <summary>The page says whether the video playing is anime (its genres, where it was made, its audio).</summary>
    private void SetAnime(bool on)
    {
        if (on == _anime) return;
        _anime = on;
        // Off the UI thread: mpv takes new shaders at its next frame, and the call waits for that.
        Task.Run(() => ApplyPicture());
    }

    private FramePeek NewPeek()
    {
        var mpv = _mpv;
        var target = PictureRules.PeekTarget(mpv?.Get("video-target-params/gamma"), mpv?.Get("video-target-params/primaries"), mpv?.Get("video-target-params/max-luma"));
        var peek = new FramePeek(target, () => _spare, _log.Write, OnUi);
        peek.Ready += (asked, at, jpeg, v, gone) => OnUi(() =>
        {
            if (_peek != peek) return;
            // none: no pictures of this video at all; without a picture and without none, just not now.
            Post(jpeg is null ? new { type = "peek", t = asked, v, none = gone } : (object)new { type = "peek", t = asked, v, at, jpg = Convert.ToBase64String(jpeg) });
        });
        return peek;
    }

    /// <summary>The open file's tracks, as the page's audio and subtitle menu needs them.</summary>
    private object Tracks()
    {
        var mpv = _mpv!;
        var count = int.TryParse(mpv.Get("track-list/count"), out var n) ? n : 0;
        var list = new List<object>();
        for (var i = 0; i < count; i++)
        {
            string? F(string field) => mpv.Get($"track-list/{i}/{field}");
            list.Add(new
            {
                type = F("type"), id = F("id"), lang = F("lang"), title = F("title"), codec = F("codec"),
                selected = F("selected") == "yes", external = F("external") == "yes", forced = F("forced") == "yes",
                channels = F("demux-channel-count"), profile = F("codec-profile"),
                isDefault = F("default") == "yes", hearing = F("hearing-impaired") == "yes",
            });
        }
        return list;
    }

    /// <summary>The open file's chapters (name and start), where a file marks its opening or credits.</summary>
    private object Chapters()
    {
        var mpv = _mpv!;
        var count = int.TryParse(mpv.Get("chapter-list/count"), out var n) ? n : 0;
        var list = new List<object>();
        for (var i = 0; i < count; i++)
            list.Add(new { title = mpv.Get($"chapter-list/{i}/title"), time = double.TryParse(mpv.Get($"chapter-list/{i}/time"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0 });
        return list;
    }

    // Local\ExoCinema.Playing, a manual-reset event set while a video plays (mpv not idle: not paused, not waiting, a file
    // in), for Exo's other apps on this PC: Exo OS holds back anything that would interrupt playback (restarting audio or
    // display services, switching the power plan) until it clears, as they do for Launcher's GameRunning and VoiceCall.
    // A test copy (its own data folder) never raises it.
    private static readonly EventWaitHandle? PlayingSignal = AppInfo.OwnData ? null : new(false, EventResetMode.ManualReset, @"Local\ExoCinema.Playing");

    private void SignalPlaying(bool on)
    {
        if (on != _playingNow)
        {
            // Playing something heard: never lean (MainWindow.Lean.cs).
            _playingNow = on;
            LeanNow();
        }
        try { if (on) PlayingSignal?.Set(); else PlayingSignal?.Reset(); }
        catch (ObjectDisposedException) { }
    }

    private void OnProperty(string name, object? value)
    {
        if (name == "core-idle") SignalPlaying(value is false);
        if (name == "osd-dimensions")
        {
            QueueGlass();
            // The window changed size (or went full screen): the upscaler is sized again once it has settled.
            (_upscaleRecheck ??= new Timer(_ => ApplyPicture())).Change(400, Timeout.Infinite);
            return;
        }
        FollowSpare(name, value);
        FollowBuffer(name, value);
        FollowFilmHdr(name, value);
        if (name == "paused-for-cache") LogBuffering(value is true);
        if (name == "audio-out-params/format") OnSoundFormat(value as string);
        if (name is "audio-params/channels" or "audio-out-params/channel-count" or "audio-out-params/format") OnUi(ApplyDialogue);
        if (name == "time-pos")
        {
            if (value is double pos) OnUi(() => _library.Jellyfin.OnTime(pos));
            // Every frame changes time-pos; the page interpolates between posts.
            var now = MpvPlayer.Now;
            if (now - _lastTimePost < 100) return;
            _lastTimePost = now;
        }
        OnUi(() =>
        {
            if (name == "pause" && value is bool paused) _library.Jellyfin.OnPause(paused);
            _props[name] = value;
            MediaFollow(name);
            Post(new { type = "prop", name, value });
        });
    }

    private void OnMpvEvent(string name, object? data)
    {
        CrashReport.Running = "mpv " + name;
        try { OnMpvEventCore(name, data); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A player event the app survives: logged, kept for a report (CrashReport), and the app goes on.
            _log.Write($"mpv: an event ({name}) failed ({e.GetType().Name})");
            CrashReport.Keep(null, $"mpv event {name} failed: {AppLog.Trace(e)}",_log.Tail(200).Split('\n'), AppInfo.Data, sendNow: true);
        }
    }

    private void OnMpvEventCore(string name, object? data)
    {
        if (name == "end-file")
        {
            // Why a file ended, for the log: the reason, and mpv's words for an error (a playback failure is also reported).
            var reason = data?.GetType().GetProperty("reason")?.GetValue(data);
            if (data?.GetType().GetProperty("error")?.GetValue(data) is string failure)
            {
                _log.Write($"mpv: the file ended with an error ({failure}; reason {reason})");
                CrashReport.Keep("PlaybackFailed", $"mpv ended the file with an error: {failure}", _log.Tail(200).Split('\n'), AppInfo.Data, sendNow: true);
            }
            else _log.Write($"mpv: the file ended (reason {reason})");
        }
        var now = MpvPlayer.Now;
        object? timing = null;
        double? landed = null;
        if (name == "playback-restart")
        {
            // Where it landed, so the page can tell its latest seek from an earlier one that landed on the way.
            landed = double.TryParse(_mpv?.Get("time-pos"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pos) ? pos : null;
            data = new { pos = landed };
            if (!_firstFrameSeen && _loadAt >= 0)
            {
                _firstFrameSeen = true;
                timing = new { what = "first-frame", ms = Math.Round(now - _loadAt, 1) };
                _log.Mark("first-frame", now - _loadAt);
            }
            else if (_seekAt >= 0)
            {
                timing = new { what = "seek", ms = Math.Round(now - _seekAt, 1) };
                _log.Mark("seek", now - _seekAt);
                _seekAt = -1;
            }
        }
        else if (name == "file-loaded" && _loadAt >= 0)
        {
            _log.Mark("file-loaded", now - _loadAt);
        }
        // The first video-reconfig: the decoder knows the picture's size and format, so the output is set up next. The gap from
        // here to the first frame is the output (shaders, the swapchain), from file-loaded to here the file and its decoder.
        else if (name == "video-reconfig" && _loadAt >= 0 && !_reconfigSeen)
        {
            _reconfigSeen = true;
            _log.Mark("video-reconfig", now - _loadAt);
        }
        // A new video starts on the best picture this PC draws; once its decoder and size are known, upscale or not.
        if (name == "start-file")
        {
            _lighterForThisVideo = false;
            _smoothOff = false;
            _videoHdrAsked = false;
            // The filter stays in mpv's chain from the last video until this one's picture is known (ApplyPicture).
            _vppHdr = false;
        }
        if (name == "end-file" && !_firstFrameSeen && _playingVia != null && _playing is { } direct
            && data?.GetType().GetProperty("error")?.GetValue(data) is string why)
        {
            OnUi(() =>
            {
                if (_playingVia is not { } via || _playing?.Url != direct.Url) return;
                PlayStream.Unregister(via);
                _playingVia = null;
                _log.Write($"stream: Exo's reader couldn't open the video ({why}), FFmpeg's instead");
                _mpv?.Command("loadfile", direct.Url, "replace", "-1", _playOptions ?? "");
            });
            return;
        }
        if (name == "video-reconfig") ApplyPicture();
        // The output was opened again (another default device, a receiver turned on): clear dialogue follows it, and
        // smooth motion's timing (sound passed through to a receiver can't be resampled).
        if (name == "audio-reconfig")
        {
            OnUi(ApplyDialogue);
            if (_smoothChosen) Task.Run(() => ApplyPicture());
        }
        OnUi(() =>
        {
            // A Play-chosen version that won't open before its first frame gives way to the next one (JellyfinLibrary).
            if (name == "end-file" && !_firstFrameSeen && data?.GetType().GetProperty("error")?.GetValue(data) is string failed
                && _library.Jellyfin.TryNextVersion(failed)) return;
            if (name == "playback-restart")
            {
                // The server and Windows' media controls hear where it landed, not the time-pos from before the seek.
                if (landed is { } at)
                {
                    _library.Jellyfin.OnTime(at);
                    _props["time-pos"] = at;
                }
                _library.Jellyfin.OnPlaybackRestart();
                MediaFollow("seek");
            }
            if (name == "end-file") _library.Jellyfin.OnEnded();
            if (name == "file-loaded")
                foreach (var (url, title, lang) in _library.Jellyfin.TakePendingSubtitles())
                    _mpv?.Command("sub-add", url, "auto", title, lang);
            Post(new { type = "event", name, data });
            if (timing != null) Post(new { type = "timing", timing });
        });
    }

    // AI upscaling: on an RTX card with NVIDIA's RTX Video Super Resolution switched on (Exo Launcher's PC › Display;
    // Exo Cinema only reads it), a picture smaller than the screen is enlarged by the driver's upscaler through mpv's
    // Direct3D 11 video processor, as Edge and Chrome ask for it. Only for SDR video decoded on the card, with Picture
    // on High quality. With the switch off the driver's processor would only scale plainly, worse than mpv's own
    // scalers, so then nothing changes.
    // RTX Video HDR goes in the same processor (one d3d11vpp, @exo-vpp): with its switch on in the driver, an SDR film on
    // High quality is turned into HDR by the card (mpv's nvidia-true-hdr, BT.2020 PQ out) on a screen in HDR, and a
    // screen that can show HDR and has it off is switched before the first frame by Auto HDR's rules (HdrFilms).
    private bool _upscaleOffNoted, _videoHdrOffNoted, _videoHdrScreenNoted, _videoHdrAsked;
    private string _vppFilter = "";
    private Timer? _upscaleRecheck;
    private double _upscaleScale;
    private volatile bool _vppHdr;

    // The picture, chosen for this PC instead of in Settings: High quality (mpv's high-quality scalers, and the AI
    // upscaling above) on a dedicated graphics card on mains power; Balanced on integrated graphics (mpv's own scalers),
    // on battery, and for the rest of a video once it drops frames (a stream too heavy for the card, something else
    // using it). A new video starts on High again. Called from mpv's events and the minute timer, so one at a time.
    private readonly Lock _pictureGate = new();
    private bool _pictureHigh, _lighterForThisVideo; // _pictureHigh: as mpv was started (StartMpv)
    private string _pictureSaid = "";
    private static readonly (string, string)[] Balanced = [("scale", "spline36"), ("cscale", "spline36"), ("dscale", "mitchell"), ("deband", "no")];

    private void ApplyPicture(string why = "")
    {
        if (_mpv is not { } mpv || _closingWindow) return;
        lock (_pictureGate)
        {
            var high = PictureRules.High(PlayerTuning.Card, PlayerTuning.OnBattery(), _lighterForThisVideo);
            if (high != _pictureHigh)
            {
                if (high) { mpv.Command("apply-profile", "high-quality"); mpv.Set("dscale", PlayerTuning.HighDownscale); }
                else foreach (var (name, value) in Balanced) mpv.Set(name, value);
                _pictureHigh = high;
                _log.Write($"picture: {(high ? "high quality" : "balanced")}{why}");
            }
            ApplyAnime(mpv);
            ApplyVideoProcessor(mpv);
            ApplyMotion(mpv);
            // The player's readings say how the picture is drawn and how it moves right now.
            var said = (_pictureHigh ? "High quality" : "Balanced")
                + (_animeChain.Length > 0 ? " · ArtCNN" : _upscaleScale > 0 ? (PlayerTuning.Card.SuperResolution == "intel" ? " · Intel Super Resolution" : " · RTX Super Resolution") : "")
                + (_vppHdr ? " · RTX Video HDR" : "");
            if (said != _pictureSaid)
            {
                _pictureSaid = said;
                OnUi(() => Post(new { type = "picture", text = said }));
            }
            if (_motionSaid != _motionPosted)
            {
                var motion = _motionPosted = _motionSaid;
                OnUi(() => Post(new { type = "motion", text = motion }));
            }
        }
    }

    // Smooth motion (Settings › Playback, the page's choice, off unless picked; PictureRules.SmoothMotion): each frame
    // timed to the screen's refreshes and the change from one to the next blended over one refresh, which ends 24 fps
    // judder without inventing frames. Off for the rest of a video that keeps dropping frames, after the automatic
    // picture has gone lighter first; off while a game runs and on battery.
    private volatile bool _smoothChosen;
    private bool _smoothOn, _smoothOff;
    private string _motionSaid = "", _motionPosted = "";

    /// <summary>The page says whether Smooth motion is chosen (app.js, at its start and when the choice changes).</summary>
    private void SetSmooth(bool on)
    {
        if (on == _smoothChosen) return;
        _smoothChosen = on;
        _log.Write($"motion: smooth motion {(on ? "chosen" : "not chosen")}");
        if (_mpv is not null) Task.Run(() => ApplyPicture());
    }

    private void ApplyMotion(MpvPlayer mpv)
    {
        try
        {
            static double Num(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            var playing = mpv.Get("idle-active") == "no";
            // The file's own rate, or mpv's measure of it when the file doesn't say (it has one a moment into the video).
            var fps = Num(mpv.Get("container-fps")) is > 0 and var c ? c : Num(mpv.Get("estimated-vf-fps"));
            var hz = Num(mpv.Get("display-fps"));
            var battery = PlayerTuning.OnBattery();
            var game = GameRunning();
            var on = playing && PictureRules.SmoothMotion(_smoothChosen, _smoothOff, battery, game, fps, hz);
            // A receiver takes the sound as it is: it can't be resampled to the video's nudged speed, so the video keeps
            // time by repeating or dropping a frame now and then instead (display-vdrop), still blended.
            var passthrough = mpv.Get("audio-out-params/format")?.StartsWith("spdif", StringComparison.Ordinal) == true;
            var sync = on ? passthrough ? "display-vdrop" : "display-resample" : "audio";
            if (on != _smoothOn || on && mpv.Get("video-sync") != sync)
            {
                if (on) mpv.Set("tscale", "oversample");
                mpv.Set("video-sync", sync);
                mpv.Set("interpolation", on ? "yes" : "no");
                _smoothOn = on;
                _log.Write(on ? $"motion: smooth, {fps:0.###} fps on a {hz:0.###} Hz screen ({sync})" : "motion: as filmed");
            }
            // Not measured yet: asked again in a moment, once mpv has measured the video.
            if (_smoothChosen && playing && !on && fps <= 0) (_upscaleRecheck ??= new Timer(_ => ApplyPicture())).Change(2000, Timeout.Infinite);
            _motionSaid = on ? $"Smooth · {fps:0.###} fps on {hz:0} Hz"
                : !_smoothChosen || !playing ? ""
                : _smoothOff ? "Off for this video"
                : game ? "Off while a game runs"
                : battery ? "Off on battery"
                : "";
        }
        catch (Exception e) { _log.Write($"motion: {AppLog.Kind(e)}"); }
    }

    // Anime: ArtCNN (Artoriuz, MIT; Shaders/ArtCNN), a network trained on drawn art that doubles the picture's detail,
    // instead of the driver's upscaler. Picked on Exo's picture bench (2026-10-28: compressed copies of clean art scored
    // against the art itself): it came out closest to the original on every source, anime and film, 720p and 1080p, and
    // kept the backgrounds' texture that Anime4K wiped to paint. The page says when a video is anime (playerplus.js).
    // Changed to run on a picture shown at its own size or a little smaller too, with Exo's own pass after it to land
    // on twice the shown size: a 720p, 1080p or 1440p episode on a 1080p screen is drawn at up to 4K and shrunk back
    // (2:1 from 1080p and 1440p). Only for SDR video up to 1440p, on High quality, with no game running, once its
    // shaders are compiled (ShaderWarmup); a minute of dropped frames turns it off for that video.
    private static readonly string AnimeShaders = Path.Combine(AppContext.BaseDirectory, "Shaders", "ArtCNN");
    private bool _anime;
    private bool _animeOff;
    private string[] _animeChain = [];
    private string? _shaderList;

    // C4F16, the smaller of ArtCNN's real-time networks: within 0.03 dB of C4F32 on the bench, the same to the eye, and
    // several times lighter to run and to compile (37 s against 4 min the first time).
    private static string[] AnimeChain() => ["Exo_Supersample_ArtCNN_C4F16_DS.glsl", "Exo_Supersample_Downscale.glsl"];

    private void ApplyAnime(MpvPlayer mpv)
    {
        try
        {
            static double Num(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            string[] chain = [];
            var height = Num(mpv.Get("video-params/h"));
            // Not compiled yet: the driver's upscaler meanwhile, and the compiling starts now if it hasn't.
            if (_anime && !_mpvWarm)
            {
                OnUi(StartWarmup);
                if (_playing is not null && !_animeWaitLogged)
                {
                    _animeWaitLogged = true;
                    _log.Write("anime: this film plays without ArtCNN (its shaders aren't ready); the next film gets it, or the next start");
                }
            }
            if (_anime && _mpvWarm && _pictureHigh && !_animeOff && height > 0)
            {
                var shownH = Num(mpv.Get("osd-dimensions/h")) - Num(mpv.Get("osd-dimensions/mt")) - Num(mpv.Get("osd-dimensions/mb"));
                if (PictureRules.AnimeUpscale(_anime, _mpvWarm, _pictureHigh, _animeOff, GameRunning(), height, mpv.Get("video-params/gamma"), shownH)) chain = AnimeChain();
            }
            var list = string.Join(";", chain.Select(f => Path.Combine(AnimeShaders, f)).Concat(File.Exists(GlassShader) ? [GlassShader] : []));
            _shaderList ??= File.Exists(GlassShader) ? GlassShader : "";
            if (list == _shaderList) return;
            if (mpv.Set("glsl-shaders", list) < 0) { _log.Write("anime: mpv refused the shaders"); return; }
            _shaderList = list;
            _animeChain = chain;
            _log.Write(chain.Length > 0 ? $"anime: ArtCNN, {height:0}p" : "anime: off");
        }
        catch (Exception e) { _log.Write($"anime: {AppLog.Kind(e)}"); }
    }

    private void ApplyVideoProcessor(MpvPlayer mpv)
    {
        try
        {
            static double Num(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<string>();
            var height = Num(mpv.Get("video-params/h"));
            var card = PlayerTuning.Card;
            var hwdec = mpv.Get("hwdec-current");
            var gamma = mpv.Get("video-params/gamma");
            var anime = _animeChain.Length > 0;
            var game = GameRunning();
            // Sized from the screen's height, a maximized window (shorter than the screen) or a widescreen film (1920x800 on
            // a 1080p screen went up to 2592x1080) had mpv shrink the upscaled picture back a little: fit to the window's
            // video area instead (osd-dimensions less its bars). Only to enlarge: drawn at twice the size and shrunk back
            // (supersampled), a picture that already fills the screen came out softer and further from the original than
            // mpv's own on the picture bench.
            double V(string name) => Num(mpv.Get(name));
            var scale = height > 0
                ? PictureRules.SuperResolutionScale(card, _pictureHigh, PlayerTuning.OnBattery(), anime, hwdec, gamma, game,
                    V("video-params/dw"), V("video-params/dh"),
                    V("osd-dimensions/w") - V("osd-dimensions/ml") - V("osd-dimensions/mr"), V("osd-dimensions/h") - V("osd-dimensions/mt") - V("osd-dimensions/mb"), _upscaleScale)
                : 0;
            var mode = card.SuperResolution;
            var setting = scale > 0 && mode is not null ? mode == "nvidia" ? NvidiaVideo.SuperResolutionSetting() : 1 : null;
            if (scale > 0 && mode is not null)
            {
                // NVIDIA's runs only with its driver switch on (read from the driver); Intel's driver has no switch to read.
                if (setting > 0) parts.Add($"scaling-mode={mode}:scale={scale.ToString("0.####", inv)}");
                else if (!_upscaleOffNoted)
                {
                    // Said once: the picture would gain from it, but the driver's switch is off (or can't be read).
                    _upscaleOffNoted = true;
                    _log.Write($"upscaling: RTX Video Super Resolution is {(setting == 0 ? "off" : "unreadable")} in the driver; mpv scales instead");
                }
            }
            var upscaled = parts.Count > 0;

            // RTX Video HDR: the same rules as the upscaler, its own driver switch, and a screen in HDR.
            var hdr = false;
            if (card.VideoHdr && height > 0 && !PictureRules.Hdr(gamma))
            {
                var driverOn = NvidiaVideo.VideoHdrOn();
                if (PictureRules.VideoHdrWanted(card, _pictureHigh, anime, hwdec, gamma, game, driverOn))
                {
                    var screen = DisplayHdr.ForWindow(_hwnd);
                    hdr = PictureRules.VideoHdrRuns(true, screen?.Enabled == true);
                    // A screen that can show HDR and has it off is switched once for the video, before its first frame
                    // (TitleWantsHdr holds it), and the filter goes in once the screen is in HDR.
                    if (!hdr && !_videoHdrAsked && !_firstFrameSeen && screen is { Supported: true })
                    {
                        _videoHdrAsked = true;
                        TitleWantsHdr(sdrToHdr: true);
                    }
                    else if (!hdr && !_videoHdrScreenNoted)
                    {
                        _videoHdrScreenNoted = true;
                        _log.Write(screen is { Supported: true }
                            ? "hdr: RTX Video HDR waits for the screen to be in HDR; the film plays in SDR"
                            : "hdr: this screen can't show HDR; RTX Video HDR is not used");
                    }
                }
                else if (driverOn != true && !_videoHdrOffNoted)
                {
                    _videoHdrOffNoted = true;
                    _log.Write($"hdr: RTX Video HDR is {(driverOn == false ? "off" : "unreadable")} in the driver; SDR films stay SDR");
                }
            }
            if (hdr) parts.Add("nvidia-true-hdr");

            var want = parts.Count > 0 ? "@exo-vpp:d3d11vpp=" + string.Join(":", parts) : "";
            if (want == _vppFilter)
            {
                _vppHdr = hdr && want.Length > 0;
                return;
            }
            if (_vppFilter.Length > 0) mpv.Command("vf", "remove", "@exo-vpp");
            if (want.Length > 0 && mpv.Command("vf", "add", want) < 0)
            {
                _log.Write($"video processor: mpv refused {want}");
                want = "";
            }
            _vppFilter = want;
            _upscaleScale = want.Length > 0 && upscaled ? scale : 0;
            _vppHdr = want.Length > 0 && hdr;
            var said = new List<string>();
            if (_upscaleScale > 0) said.Add($"{(mode == "nvidia" ? $"RTX Video Super Resolution (driver quality {setting})" : "Intel Video Super Resolution")}, {height:0}p x{scale:0.##} in a {V("osd-dimensions/w"):0}x{V("osd-dimensions/h"):0} window");
            if (_vppHdr) said.Add($"RTX Video HDR, {height:0}p SDR to HDR10");
            _log.Write(said.Count > 0 ? $"video processor: {string.Join("; ", said)}" : "video processor: off");
            // A title ended with HDR held for a next one: this one keeps it, as an HDR title would.
            if (_vppHdr) lock (_filmHdrLock) { if (_filmHold is not null) _filmHold.NextIsHdr = true; }
        }
        catch (Exception e) { _log.Write($"video processor: {AppLog.Kind(e)}"); }
    }

    // A game is running: Exo Launcher signals it with a named event while any library game runs (its
    // Services/GameRunningSignal.cs, agreed with it). The upscaler takes graphics time from the game, so it steps aside.
    // Without Exo Launcher the event doesn't exist, which reads as no game. Opened afresh each time: a handle kept open
    // would keep the event alive after Exo Launcher closed.
    private static bool GameRunning()
    {
        var handle = OpenEventW(0x00100000 /* SYNCHRONIZE */, false, @"Local\ExoLauncher.GameRunning");
        if (handle == IntPtr.Zero) return false;
        try { return WaitForSingleObject(handle, 0) == 0; }
        finally { CloseHandle(handle); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr OpenEventW(uint access, bool inherit, string name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    // While a video plays, a line a minute in app.log: the frames dropped and late in that minute, and how the video
    // plays, so "is it smooth?" has an answer after the fact.
    private Timer? _performance;
    private long _drops, _late;

    private void WatchPerformance() => _performance ??= new Timer(_ => { LogPerformance(); ApplyPicture(); }, null, 60_000, 60_000);

    // The read-ahead (ReadAhead.Size) follows the memory free now while a network film plays. Looked at every 5 s, which
    // costs nothing: memory that runs short is given back at once; more is taken only once 30 s have passed since the
    // last change, so the limits don't creep with every reading. A game or Lean keeps it from growing (the step-down
    // stays as it was). Local files keep their own small per-file limits (cache=no) and are left alone.
    private Timer? _readAheadWatch;
    private bool _readAheadNetwork;
    private (long Forward, long Back) _readAheadSet = (-1, -1);
    private long _readAheadAt;
    private void WatchReadAhead() => _readAheadWatch ??= new Timer(_ => ReadAheadTick(), null, 5_000, 5_000);

    private void ReadAheadTick()
    {
        if (_closingWindow || _mpv is not { } mpv || !_readAheadNetwork) return;
        var (total, available) = SystemMemory.Read();
        if (total <= 0) return;
        var plan = ReadAhead.Size(total, available);
        if (plan.ForwardBytes == _readAheadSet.Forward && plan.BackBytes == _readAheadSet.Back) return;
        var now = Environment.TickCount64;
        var growing = _readAheadSet.Forward >= 0 && plan.ForwardBytes > _readAheadSet.Forward;
        if (growing && (now - _readAheadAt < 30_000 || _lean || GameRunning())) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        mpv.Set("demuxer-max-bytes", plan.ForwardBytes.ToString(inv));
        mpv.Set("demuxer-max-back-bytes", plan.BackBytes.ToString(inv));
        var was = _readAheadSet.Forward < 0 ? "" : $" (was {_readAheadSet.Forward / (double)ReadAhead.Gib:0.00} GiB ahead, {_readAheadSet.Back / (double)ReadAhead.Gib:0.00} GiB behind)";
        _log.Write($"read-ahead: {plan.ForwardBytes / (double)ReadAhead.Gib:0.00} GiB ahead, {plan.BackBytes / (double)ReadAhead.Gib:0.00} GiB behind; "
            + $"{available / (double)ReadAhead.Gib:0.0} of {total / (double)ReadAhead.Gib:0.0} GiB free{(plan.Low ? "; memory short, at the floors" : "")}{was}");
        _readAheadSet = (plan.ForwardBytes, plan.BackBytes);
        _readAheadAt = now;
    }

    // A minute with a frame dropped every second or so: lighter scalers for the rest of this video.
    private const int DropsThatLighten = 60;

    private void LogPerformance()
    {
        if (_mpv is not { } mpv || _closingWindow) return;
        try
        {
            long N(string name) => long.TryParse(mpv.Get(name), out var v) ? v : 0;
            long drops = N("frame-drop-count"), late = N("vo-delayed-frame-count");
            if (drops < _drops || late < _late) (_drops, _late) = (0, 0); // a new file counts from zero
            var (newDrops, newLate) = (drops - _drops, late - _late);
            (_drops, _late) = (drops, late);
            if (mpv.Get("pause") != "no" || mpv.Get("idle-active") == "yes") return;
            // A minute of dropped frames lightens one step at a time, the automatic choices first: ArtCNN, then High
            // quality with the driver's upscaler and RTX Video HDR, and last the smooth motion you chose.
            if (newDrops >= DropsThatLighten)
            {
                if (_pictureHigh && !_lighterForThisVideo && _animeChain.Length > 0)
                {
                    _animeOff = true;
                    _log.Write($"anime: ArtCNN off for this video, after {newDrops} dropped frames in a minute");
                    ApplyPicture();
                }
                else if (_pictureHigh && !_lighterForThisVideo)
                {
                    _lighterForThisVideo = true;
                    ApplyPicture($" for this video, after {newDrops} dropped frames in a minute");
                }
                else if (_smoothOn && !_smoothOff)
                {
                    _smoothOff = true;
                    _log.Write($"motion: smooth motion off for this video, after {newDrops} dropped frames in a minute");
                    ApplyPicture();
                }
            }
            // Filling the buffer well ahead of the picture, mpv reads as fast as the link gives: that is its speed.
            // (Once the buffer is full it reads only what the video uses, which says nothing about the link.)
            static double Num(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            var link = Num(mpv.Get("cache-speed")) * 8 / 1e6;
            var bitrate = (Num(mpv.Get("video-bitrate")) + Num(mpv.Get("audio-bitrate"))) / 1e6;
            if (bitrate > 0 && link > bitrate * 1.5) LinkSpeed.Record(link);
            var fps = double.TryParse(mpv.Get("container-fps"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0;
            var motion = _smoothOn ? $" · smooth on {Num(mpv.Get("display-fps")):0.###} Hz, {mpv.Get("mistimed-frame-count") ?? "0"} mistimed" : "";
            var output = _vppHdr ? $" · out {mpv.Get("video-out-params/gamma")} to a {mpv.Get("video-target-params/gamma")} screen" : "";
            _log.Write($"playing: {newDrops} dropped, {newLate} late in the last minute · {mpv.Get("hwdec-current")} · {mpv.Get("video-params/w")}x{mpv.Get("video-params/h")} {mpv.Get("video-format")} {mpv.Get("video-params/gamma")} · {fps:0.###} fps{motion}{output}");
        }
        catch (Exception e) { _log.Write($"playing: {AppLog.Kind(e)}"); }
    }

    // Each wait for the network goes in app.log with how fast data came and how fast the video needs it, so a
    // stream that buffers says whether the link or the server is short.
    private double _bufferingSince = -1;

    private void LogBuffering(bool waiting)
    {
        var mpv = _mpv!;
        if (waiting) { _bufferingSince = MpvPlayer.Now; return; }
        if (_bufferingSince < 0) return;
        var seconds = (MpvPlayer.Now - _bufferingSince) / 1000;
        _bufferingSince = -1;
        static double Num(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        var speed = Num(mpv.Get("cache-speed")) * 8 / 1e6;
        var needs = (Num(mpv.Get("video-bitrate")) + Num(mpv.Get("audio-bitrate"))) / 1e6;
        // The reasons to tell apart in a report: a link or source slower than the video (the speed against the need, the
        // file's average, the wait), read-ahead that ran dry while the link kept up (read ahead, the speed), and decoding
        // that fell behind (frames dropped, hwdec), which doesn't pause for the cache at all.
        var ahead = Num(mpv.Get("demuxer-cache-duration"));
        var size = Num(mpv.Get("file-size"));
        var length = Num(mpv.Get("duration"));
        var rate = length > 0 ? size * 8 / length / 1e6 : 0;
        _log.Write($"buffering: {seconds:0.0} s; {ahead:0.0} s read ahead; the link gave {speed:0.0} Mb/s, the video needs {needs:0.0} Mb/s, the file averages {rate:0.0} Mb/s; wait {_bufferWait:0} s; {SourceKind()}; hwdec {mpv.Get("hwdec-current") ?? "none"}; {mpv.Get("frame-drop-count") ?? "0"} frames dropped so far");
        // Waiting on the network, the link gives all it has: what it gave is its speed.
        if (seconds >= 1) LinkSpeed.Record(speed);
    }

    // Full screen is Exo Cinema's own: the window drops its frame and covers its monitor, moved with SWP_NOACTIVATE.
    // Windows' FullScreen presenter activated the window as it switched (it took the foreground from the user's app at
    // 16:25 on 2026-10-28), so a switch made behind a game (an episode ending, its player leaving full screen) brought
    // Exo Cinema in front of it. Now going to or from full screen never moves the foreground, in front or behind.
    private nint _styleBeforeFullScreen;
    private bool _maximizeWhenActive;

    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        _fullScreen = on;
        const int GWL_STYLE = -16;
        const nint WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200;
        const uint Quiet = SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOOWNERZORDER;
        if (on)
        {
            var maximized = IsMaximized;
            _beforeFullScreen = (new Windows.Graphics.RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height), maximized);
            if (maximized) ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE: restored, not activated */);
            // WinUI's presenter keeps a 1px border on the window (it put WS_BORDER back): asked to drop it, the client
            // fills the monitor exactly.
            if (AppWindow.Presenter is OverlappedPresenter frame) frame.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(MonitorFromWindow(_hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref info);
            _styleBeforeFullScreen = GetWindowLongPtrW(_hwnd, GWL_STYLE);
            SetWindowLongPtrW(_hwnd, GWL_STYLE, _styleBeforeFullScreen & ~(WS_CAPTION | WS_THICKFRAME));
            var m = info.rcMonitor;
            SetWindowPos(_hwnd, IntPtr.Zero, m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, Quiet);
        }
        else
        {
            SetWindowLongPtrW(_hwnd, GWL_STYLE, _styleBeforeFullScreen);
            if (AppWindow.Presenter is OverlappedPresenter frame) frame.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            if (_beforeFullScreen is { } before)
            {
                var b = before.Bounds;
                SetWindowPos(_hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height, Quiet);
                // Maximized again only as the active window (Windows' maximize activates); until then it fills the same space.
                if (before.Maximized)
                {
                    if (GetForegroundWindow() == _hwnd && AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
                    else _maximizeWhenActive = true;
                }
            }
        }
        UpdateCaptionDragRegions();
        Post(new { type = "fullscreen", on });
    }

    private void MaximizeIfWaiting()
    {
        if (!_maximizeWhenActive || _fullScreen) return;
        _maximizeWhenActive = false;
        if (AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public NativeRect rcMonitor, rcWork;
        public uint dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(IntPtr hwnd, int index, nint value);
}
