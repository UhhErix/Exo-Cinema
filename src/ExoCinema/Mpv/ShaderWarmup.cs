using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ExoCinema.Mpv;

/// <summary>
/// The anime upscaler's shaders compiled ahead of the first anime, in a second mpv that is never shown. ArtCNN's are big:
/// the first time a graphics card compiles them takes about half a minute for each way a video can be decoded (37 s on
/// an RTX 3070), and mpv, with the player, is frozen meanwhile. Compiled here instead, into the player's shader cache,
/// from 1080p clips that take each of those ways (8-bit on the card, 8-bit and 10-bit in software). The compiled programs
/// depend on the picture's size, so the clips are 1080p, the size anime is mostly played at. mpv reads its cache when it
/// starts, so the player is started again once it is idle (MainWindow) and only then does anime use the upscaler. Done
/// once per graphics card, driver and shader set: ready.txt in the cache says which. Made on the UI thread, which owns
/// the window; mpv runs on a thread of its own.
/// </summary>
internal sealed unsafe class ShaderWarmup
{
    private static string Marker => Path.Combine(PlayerTuning.ShaderCache, "ready.txt");

    /// <summary>What the compiled shaders are for: the card, its driver, the shader files, and the clips the warm-up plays
    /// (warm-2: the 1080p clips). A ready.txt from before them, which 1.4.6 could write when nothing had compiled, doesn't count.</summary>
    private static string Stamp(string[] shaders) =>
        string.Join('|', ["warm-2", PlayerTuning.Gpu, DriverVersion(), .. shaders.Select(f => $"{Path.GetFileName(f)}:{(File.Exists(f) ? new FileInfo(f).Length : 0)}")]);

    /// <summary>The cache holds these shaders compiled for this card and driver.</summary>
    public static bool Ready(string[] shaders)
    {
        try { return File.Exists(Marker) && File.ReadAllText(Marker) == Stamp(shaders); }
        catch (IOException) { return false; }
    }

    // The ways a video reaches the upscaler, each its own compiled program: decoded on the card (8-bit nv12) or in software
    // (8-bit, 10-bit: 10-bit H.264, common in anime, has no hardware decoder). The programs also depend on the picture's
    // size: a 256x144 clip left a 1080p film to compile its own programs (about 20 s with the picture frozen), so every
    // way is 1080p.
    private static readonly string Clips = Path.Combine(AppContext.BaseDirectory, "Shaders", "Warmup");
    private static readonly (string Source, string Hwdec)[] Ways =
    [
        (Path.Combine(Clips, "warm-1080p.mp4"), "auto-safe"),
        ("av://lavfi:color=c=gray:s=1920x1080:d=0.5", "no"),
        ("av://lavfi:color=c=gray:s=1920x1080:d=0.5,format=yuv420p10le", "no"),
    ];

    private readonly IntPtr _window;
    private readonly string[] _shaders;
    private readonly Action<string> _log;

    private readonly object _gate = new();
    private IntPtr _ctx;
    private volatile bool _cancelled;

    /// <summary>Ends true when the cache holds the shaders; false when it failed or was paused (<see cref="Cancelled"/>).</summary>
    public Task<bool> Done { get; private set; } = null!;

    /// <summary>True once <see cref="Cancel"/> has paused the compile (a film started); it is then not a failure.</summary>
    public bool Cancelled => _cancelled;

    /// <summary>Compiles <paramref name="shaders"/> (paths, in order) on a thread of its own; <see cref="Done"/> ends true when
    /// the cache holds them. <paramref name="onUi"/> runs the window's last step on the UI thread.</summary>
    public static ShaderWarmup Start(string[] shaders, Action<string> log, Action<Action> onUi)
    {
        var warmup = new ShaderWarmup(shaders, log);
        var done = new TaskCompletionSource<bool>();
        warmup.Done = done.Task;
        var thread = new Thread(() =>
        {
            var ok = false;
            try { ok = warmup.Compile(); }
            catch (Exception e) { log($"shaders: {AppLog.Kind(e)}"); }
            onUi(() => DestroyWindow(warmup._window));
            done.TrySetResult(ok);
        }) { IsBackground = true, Name = "mpv shader warm-up" };
        thread.Start();
        return warmup;
    }

    /// <summary>Pauses the compile when a film starts. mpv runs its commands in turn, so the frame in progress finishes
    /// first; the programs compiled so far stay in the cache, and the next Start resumes from there. Thread-safe.</summary>
    public void Cancel()
    {
        if (_cancelled) return;
        _cancelled = true;
        // Asked from a thread of its own, never the caller's: mpv answers a command only once the frame in progress is
        // done, and that frame is a shader compile (seconds, cold). Play calls this on the UI thread, which waited here
        // and froze the window for five seconds and more when a film was started during the compile (seen 2026-11-12).
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_gate)
                if (_ctx != IntPtr.Zero) Command(_ctx, "quit");
        });
    }

    private ShaderWarmup(string[] shaders, Action<string> log)
    {
        _shaders = shaders;
        _log = log;
        // Full HD and never shown: the upscaler runs (the picture is shown bigger than it is) and nothing appears.
        _window = CreateWindowExW(0x08000000 /* NOACTIVATE */ | 0x80 /* TOOLWINDOW */, "STATIC", null, 0x80000000 /* POPUP */, 0, 0, 1920, 1080, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    private bool Compile()
    {
        var started = Environment.TickCount64;
        _log("shaders: compiling the anime upscaler for this graphics card, once");
        var ctx = MpvNative.Create();
        if (ctx == IntPtr.Zero) return false;
        lock (_gate) { _ctx = ctx; }
        try
        {
            if (_cancelled) return false;
            foreach (var (name, value) in PlayerTuning.Options())
                if (name is "vo" or "gpu-api" or "gpu-context" or "d3d11-adapter" or "profile" or "dscale" or "gpu-shader-cache-dir" or "target-colorspace-hint")
                    MpvNative.SetOptionString(ctx, name, value);
            foreach (var (name, value) in new[] { ("wid", _window.ToInt64().ToString(CultureInfo.InvariantCulture)), ("pause", "yes"), ("aid", "no"), ("sid", "no"), ("idle", "yes"),
                ("keep-open", "always"), ("osd-level", "0"), ("osc", "no"), ("ytdl", "no"), ("load-stats-overlay", "no"), ("load-auto-profiles", "no"),
                ("input-default-bindings", "no"), ("glsl-shaders", string.Join(";", _shaders)) })
                MpvNative.SetOptionString(ctx, name, value);
            if (MpvNative.Initialize(ctx) < 0) return false;
            var drawn = 0;
            foreach (var (source, hwdec) in Ways)
            {
                if (_cancelled) break;
                if (!source.StartsWith("av://") && !File.Exists(source)) continue;
                MpvNative.SetPropertyString(ctx, "hwdec", hwdec);
                Command(ctx, "loadfile", source, "replace");
                if (!Landed(ctx)) { if (!_cancelled) _log($"shaders: a clip did not play ({Path.GetFileName(source)})"); continue; }
                // A frame drawn with the shaders: that is when they compile (and go to the cache).
                Command(ctx, "screenshot-raw", "window");
                drawn++;
            }
            if (!_cancelled) Command(ctx, "stop");
            if (_cancelled) { _log("shaders: paused, a film started; the compile resumes when playback stops"); return false; }
            if (drawn == 0) { _log("shaders: no clip drew a frame, so nothing compiled"); return false; }
        }
        finally { lock (_gate) { _ctx = IntPtr.Zero; } MpvNative.TerminateDestroy(ctx); }
        // Only once something compiled: the marker says the shaders are in the cache, and the player then starts on them.
        try { File.WriteAllText(Marker, Stamp(_shaders)); }
        catch (IOException) { return false; }
        _log($"shaders: the anime upscaler is ready, compiled in {(Environment.TickCount64 - started) / 1000} s");
        return true;
    }

    /// <summary>One clip's wait, by mpv's events (WarmupPlan.Next): it lands once its own playback restarts, so the frame
    /// drawn after it is the clip's. Two minutes at most: a cold compile of the first frame takes 20 to 40 s, longer while
    /// a film plays beside it.</summary>
    private bool Landed(IntPtr ctx)
    {
        var until = Environment.TickCount64 + 120_000;
        var state = WarmupPlan.Landing.Waiting;
        while (!_cancelled && Environment.TickCount64 < until && state is WarmupPlan.Landing.Waiting or WarmupPlan.Landing.Started)
        {
            var e = MpvNative.WaitEvent(ctx, 0.2);
            state = WarmupPlan.Next(state, (int)e->EventIdValue);
        }
        return state == WarmupPlan.Landing.Landed;
    }

    private static void Command(IntPtr ctx, params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            fixed (IntPtr* p = ptrs) MpvNative.Command(ctx, p);
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>The display driver's version (Windows' own record of it), so a new driver compiles again.</summary>
    private static string DriverVersion()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? [])
            {
                try
                {
                    using var key = cls!.OpenSubKey(sub);
                    if (key?.GetValue("DriverDesc") as string == PlayerTuning.Gpu) return key.GetValue("DriverVersion") as string ?? "";
                }
                catch (System.Security.SecurityException) { }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return "";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string? name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
