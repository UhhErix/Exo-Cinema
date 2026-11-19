using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoCinema.Jellyfin;
using ExoCinema.Library;
using ExoCinema.Mpv;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace ExoCinema;

/// <summary>
/// Exo Cinema's one window, on Exo Launcher's stack: WinUI 3 with one WebView2 carrying the whole UI. libmpv draws
/// the picture into this window's own HWND and the page sits over it with a transparent background; all input goes
/// to the page, which drives mpv through the bridge (OnWebMessage).
/// </summary>
public sealed partial class MainWindow : Window, IPlayerSurface
{
    private readonly LaunchArgs _launch;
    private readonly IntPtr _hwnd;
    private readonly AppLog _log = AppLog.Shared;
    private readonly CancellationTokenSource _watch = new();
    private int _offlineInARow;
    private readonly CinemaLibrary _library;
    private readonly Dictionary<string, object?> _props = [];
    private MpvPlayer? _mpv;
    private CoreWebView2? _core;
    private CoreWebView2Controller? _controller;
    private ArtCache? _art;
    private Net.LocalFileHost? _files;
    private Timer? _gameWatch;
    private bool _gameOn;
    private Ui.CaptionOverlay? _captionOverlay;
    private bool _closingWindow;

    public MainWindow(LaunchArgs launch)
    {
        _launch = launch;
        // The graphics card Direct3D is asked about meanwhile, off the UI thread: the player starts with it (PlayerTuning).
        var card = Task.Run(() => PlayerTuning.Card);
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // A screen left in HDR by a run that crashed before it could switch HDR back off is restored first (HdrFilms).
        RecoverFilmHdr();
        _log.Mark("process", (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds);
        card.ContinueWith(t => _log.Write($"graphics: {t.Result}"), TaskContinuationOptions.OnlyOnRanToCompletion);

        Ui.TransparentBackdrop.Connected += e => _log.Write(e is null ? "backdrop: transparent" : $"backdrop: {AppLog.Kind(e)}");
        ApplyWindowChrome();
        AttachCaptionInput();
        // The taskbar's and Alt+Tab's icon: the Ticket (tools/make-logo.py).
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ExoCinema.ico")); } catch { }
        RestoreWindowPlacement();
        // A file opened with Cinema: mpv starts and opens it now, on the window's handle, while the rest of the window
        // and then WebView2 come up, rather than after both.
        if (launch.Media is { } media && TryStartMpv()) Play(media, null, 0, null);

        _library = new CinemaLibrary(this, new JellyfinLibrary(this, new AccountStore(AppInfo.Data), () => PlayerTuning.Card), () => Watch.WatchStore.Ledger,
            () => ArtCache.SizeOnDisk() + ScrubStore.SizeOnDisk(), () => ArtCache.Clear() + ScrubStore.Clear());
        _library.Jellyfin.Log += message => _log.Write(message);
        // A Jellyfin play none of whose versions opens: the page hears of it as the player's own end-file error.
        _library.Jellyfin.PlayFailed += text => OnUi(() => Post(new { type = "event", name = "end-file", data = new { error = text } }));

        // Jellyfin's server and user name, from before 1.5, when they synced: taken off every PC in the profile. Today's
        // account (Jellyfin/AccountStore.cs) is on this PC alone and has nothing in this item.
        lock (SyncedSettings.Items)
            if (SyncedSettings.Items.Get("server", "last") is { Body: not null }) SyncedSettings.Items.Put("server", "last", null);

        // Keys go to the page whenever the window is active (focus otherwise stays on WinUI's hidden input window).
        Activated += (_, e) =>
        {
            // In front again or behind now: lean or not, looked at now (MainWindow.Lean.cs).
            LeanNow();
            if (e.WindowActivationState == WindowActivationState.Deactivated || _controller is null) return;
            SendXamlToBack();
            // Activation costs the Maximize region its hit-test, as a minimize does (MainWindow.Caption.cs).
            DispatcherQueue.Post(UpdateCaptionDragRegions, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
            FirstClickOnMaximize();
            try { _controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch (COMException) { }
            // Left full screen in the background from a maximized window: maximized again now (MainWindow.Player.cs).
            DispatcherQueue.Post(MaximizeIfWaiting);
        };
        AppWindow.Changed += (_, e) =>
        {
            if (_controller is not null && (e.DidPresenterChange || e.DidSizeChange || e.DidVisibilityChange)) SendXamlToBack();
            // The hidden XAML grid's SizeChanged isn't to be relied on for this: the window's own size is.
            if (e.DidPresenterChange || e.DidSizeChange)
            {
                PlaceWeb();
                UpdateCaptionDragRegions();
            }
            if (e.DidPresenterChange || e.DidSizeChange) NotifyWindowState();
            if (e.DidPresenterChange || e.DidSizeChange || e.DidVisibilityChange) FollowWindowVisibility();
            if (e.DidPositionChange || e.DidSizeChange) RememberWindowPlacement();
        };
        AppWindow.Closing += OnClosing;
    }

    // Minimized, the page has nothing to show: WebView2 stops drawing (the page sees document.hidden, so its loops
    // stop) and gives back the memory it can rebuild, until the window is back. mpv plays on regardless, and the page
    // catches up from the host's next posts.
    private bool _webAway;

    /// <summary>In view: shown and not minimized (see FollowWindowVisibility).</summary>
    // A closed window has no AppWindow: a timer's last tick after the close finds it out of view instead of failing.
    private bool WindowInView => AppWindow is { IsVisible: true } window && window.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    // The page's heartbeat (app.js, once a second, cmd "heartbeat"): the Stopwatch time of the last one, 0 until the page's first.
    // Away from view the clock is held at now, so a page that was hidden is not called frozen when it comes back.
    private long _pageBeat;
    private bool _pageReported;

    /// <summary>A page silent for five seconds while the window is in view is reported once per stall, as Hang.Page (PageHeartbeat).</summary>
    private void StartPageWatch()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = PageHeartbeat.Every;
        timer.Tick += (_, _) => UiGuard.Run(() =>
        {
            if (_watch.IsCancellationRequested) { timer.Stop(); return; }
            if (_pageBeat == 0) return;
            var inView = WindowInView;
            if (!inView) { _pageBeat = Stopwatch.GetTimestamp(); _pageReported = false; return; }
            var since = Stopwatch.GetElapsedTime(_pageBeat);
            if (PageHeartbeat.Stuck(since, inView) && !_pageReported)
            {
                _pageReported = true;
                CrashReport.Keep(PageHeartbeat.ErrorType, $"Hang: the page didn't answer for {since.TotalSeconds:0} s",
                    _log.Tail(200).Split('\n'), AppInfo.Data, sendNow: true);
            }
        });
        timer.Start();
    }

    private void FollowWindowVisibility()
    {
        if (_controller is null || _core is null) return;
        var away = !WindowInView;
        if (away == _webAway) return;
        _webAway = away;
        try
        {
            _controller.IsVisible = !away;
            _core.MemoryUsageTargetLevel = away ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (COMException) { }
        // Back from minimized: the caption's regions again, once the restore has gone through (MainWindow.Caption.cs).
        if (!away) DispatcherQueue.Post(UpdateCaptionDragRegions, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
    }

    /// <summary>Shows the window; with EXO_CINEMA_NO_ACTIVATE=1 it does so without taking the focus.</summary>
    public void Show()
    {
        if (Environment.GetEnvironmentVariable("EXO_CINEMA_NO_ACTIVATE") == "1") AppWindow.Show(activateWindow: false);
        else Activate();
        _log.Mark("window", (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds);
        _ = StartAsync().ContinueWith(t => App.WriteCrash("start", t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        // The reports kept by the last run go out now (CrashReport: by default, within the day's limits).
        _ = CrashReport.SendPendingAsync(AppInfo.Data, DateTime.UtcNow).ContinueWith(_ => _log.Write("report: sending failed"), TaskContinuationOptions.OnlyOnFaulted);
        // A freeze of the UI thread for five seconds is reported with what was running (UiWatchdog).
        UiWatchdog.Start(run => DispatcherQueue.Post(run), () => CrashReport.Running,
            (since, running) => CrashReport.Keep("Hang.Ui", $"Hang: the UI thread didn't answer for {since.TotalSeconds:0} s; running: {running}",
                _log.Tail(200).Split('\n'), AppInfo.Data, sendNow: true), _watch.Token);
        StartPageWatch();
#if DEBUG
        // The test-only crash for the crash-report check: a test copy with EXO_CINEMA_TEST_CRASH=1 throws on the UI thread
        // once it is up. Release builds have none of this.
        if (AppInfo.OwnData && Environment.GetEnvironmentVariable("EXO_CINEMA_TEST_CRASH") == "1")
        {
            // A real, unhandled throw on the dispatcher, as a bug would make: App's UnhandledException writes crash.log and
            // the report for the next start. Nothing here calls the writer.
            DispatcherQueue.Post(() => { throw new SmokeTestException("Forced by EXO_CINEMA_TEST_CRASH for the crash-report check."); });
        }
#endif
    }

    public void BringForward()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }

    private void ApplyWindowChrome()
    {
        // Exo's chrome: no Windows caption. The page draws the Bar, window controls included; the host turns the
        // Bar's empty space into Windows' own caption (MainWindow.Caption.cs).
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop ??= new Ui.TransparentBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter op)
        {
            op.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            op.PreferredMinimumWidth = 640;
            op.PreferredMinimumHeight = 400;
        }
        var tb = AppWindow.TitleBar;
        tb.ExtendsContentIntoTitleBar = true;
        tb.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        tb.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        tb.BackgroundColor = transparent;
        tb.InactiveBackgroundColor = transparent;
        tb.ButtonBackgroundColor = transparent;
        tb.ButtonInactiveBackgroundColor = transparent;
        var dark = 1;
        DwmSetWindowAttribute(_hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    private async Task StartAsync()
    {
        // The page first, the longest wait of the start (WebView2's own processes); the player and the update check
        // start while it loads.
        var web = StartWebAsync();
        TryStartMpv();
        _openCheck = CheckOnOpenAsync();
        await web;
        // Whether a game runs, for the page's effects and videos (window.js › exoHeld): looked at every two seconds, told
        // when it changes.
        _gameWatch = new Timer(_ =>
        {
            var on = GameRunning();
            if (on == _gameOn) return;
            _gameOn = on;
            OnUi(() => Post(new { type = "game", on }));
        }, null, 0, 2000);
        StartLeanWatch();
        _ = Task.Run(() =>
        {
            // A test copy on its own data leaves the installed Exo Cinema alone: Stamp would point its Start menu and
            // Desktop links at this copy's exe, and Refresh would write its installed-apps entry.
            if (AppInfo.OwnData) _log.Write("shortcuts and installed apps: left alone (a test copy on its own data)");
            else
            {
                try { if (ShortcutIdentity.Stamp() is > 0 and var n) _log.Write($"shortcuts: app ID set on {n}"); }
                catch (Exception e) { _log.Write($"shortcuts: {AppLog.Kind(e)}"); }
                try { if (InstalledAppsEntry.Refresh() is > 0 and var n) _log.Write($"installed apps: {n} values brought up to date"); }
                catch (Exception e) { _log.Write($"installed apps: {AppLog.Kind(e)}"); }
            }
            LegacyData.Clear(AppInfo.Data);
            StartAccountSync();
        });
        if (_launch.FullScreen) SetFullScreen(true);
        if (_launch.Media is { } later && _playing is null && TryStartMpv()) Play(later, null, 0, null);
        // The anime upscaler's shaders, compiled ahead once the app has settled (MainWindow.Player.cs).
        _ = Task.Delay(20_000).ContinueWith(_ => OnUi(StartWarmup));
    }

    // mpv can't load while the graphics driver is being installed (its Vulkan loader is gone for a minute or two):
    // the app opens anyway, and the player starts the next time something needs it.
    private bool TryStartMpv()
    {
        if (_mpv != null) return true;
        try
        {
            StartMpv();
            QueueGlass();
            return true;
        }
        catch (Exception e)
        {
            _mpv?.Dispose();
            _mpv = null;
            _log.Write($"player: can't start yet: {AppLog.Kind(e)}");
            return false;
        }
    }

    private void StartMpv()
    {
        // Whether this player's shader cache already holds the anime upscaler (it is read once, as mpv starts).
        _mpvWarm = ShaderWarmup.Ready(AnimeShaderFiles);
        _pictureHigh = PlayerTuning.Strong; // the high-quality profile is in mpv's options then
        var started = Stopwatch.GetTimestamp();
        _mpv = new MpvPlayer(_hwnd, PlayerTuning.Options());
        _mpv.Log += OnMpvLog;
        foreach (var (name, format) in ObservedProperties) _mpv.Observe(name, format);
        _mpv.PropertyChanged += OnProperty;
        _mpv.EventRaised += OnMpvEvent;
        WatchPerformance();
        WatchReadAhead();
        _log.Mark("mpv", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private async Task StartWebAsync()
    {
        var data = Path.Combine(AppInfo.Data, "WebView2");
        _log.Mark("web-begin", (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds);
        // The shared Exo WebView2 when Launcher has made it, else Cinema's own folder (MainWindow.SharedWeb.cs).
        (var env, _controller) = await CreateWebAsync(data);
        _log.Mark("web-env", (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds);
        _controller.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        _log.Mark("web-controller", (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds);
        PlaceWeb();
        SendXamlToBack();
        _captionOverlay = new Ui.CaptionOverlay(_hwnd);
        UpdateCaptionDragRegions();
        var core = _core = _controller.CoreWebView2;
#if DEBUG
        // Dev builds change the page's files under a running cache; start from them as they are on disk.
        await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
#endif
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        // Nothing typed into the page is kept by WebView2's form memory: Exo Cinema keeps what it needs itself, sealed.
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.SetVirtualHostNameToFolderMapping("cinema.exo", Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Deny);
        // The profile's media (Sync/ProfileMedia.cs), for its pictures and loops: served with a per-run token, never a
        // folder mapping, which any frame in the page could read (Net/LocalFileHost.cs).
        Directory.CreateDirectory(Sync.ProfileMedia.Folder);
        _files = new Net.LocalFileHost(core, DispatcherQueue);
        _files.Serve(Sync.ProfileMedia.VirtualHost, () => Sync.ProfileMedia.Folder, Net.LocalFileHost.ProfileTypes);
        core.WebMessageReceived += (s, e) => UiGuard.Run(() => OnWebMessage(s, e), "page");
        _art = new ArtCache(core, env);
        core.ContainsFullScreenElementChanged += (_, _) => UiGuard.Run(() => SetFullScreen(core.ContainsFullScreenElement), "page");
        var ready = new TaskCompletionSource();
        core.NavigationCompleted += (_, _) => UiGuard.Run(() => { ready.TrySetResult(); ApplyPageScale(again: true); }, "page");
        await StartAppearanceAsync(core);
        _log.Mark("web-navigate", (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds);
        core.Navigate("https://cinema.exo/index.html");
        await ready.Task;
        _log.Mark("web", (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds);
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var msg = JsonNode.Parse(e.WebMessageAsJson)!;
        if ((string?)msg["cmd"] is "toggle-pause" or "seek" or "set" or "command" or "load" or "tracks" or "chapters" or "get" && !TryStartMpv()) return;
        var mpv = _mpv!;
        switch ((string?)msg["cmd"])
        {
            case "heartbeat":
                // The page's event loop is answering (app.js, once a second): kept for the page watch, not logged.
                _pageBeat = Stopwatch.GetTimestamp();
                _pageReported = false;
                break;
            case "mark":
                // The opening's steps as the page reaches them (boot.js, app.js), in app.log beside the host's own.
                if ((string?)msg["what"] is { Length: > 0 and <= 32 } what && what.All(c => char.IsAsciiLetterLower(c) || c == '-'))
                    _log.Mark(what, (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds);
                break;
            case "ready":
                // The page loaded after mpv started: give it everything it missed.
                Post(new { type = "hello", tuning = PlayerTuning.Describe(), picture = _pictureSaid, motion = _motionPosted, timings = _log.Marks, app = Info() });
                if (_playing != null) Post(new { type = "playing", title = _mpv?.Get("force-media-title"), started = _firstFrameSeen });
                NotifyWindowState(force: true);
                Post(new { type = "fullscreen", on = _fullScreen });
                Post(new { type = "game", on = _gameOn });
                foreach (var (name, value) in _props) Post(new { type = "prop", name, value });
                break;
            case "toggle-pause":
                mpv.Command("cycle", "pause");
                break;
            case "seek":
                Seek((double)msg["to"]!, (string?)msg["mode"] ?? "absolute+keyframes");
                break;
            case "set":
                if (OpensTrack((string)msg["name"]!)) SetTrack((string)msg["name"]!, (string)msg["value"]!);
                else mpv.Set((string)msg["name"]!, (string)msg["value"]!);
                break;
            case "command":
                mpv.Command(msg["args"]!.AsArray().Select(a => (string)a!).ToArray());
                break;
            case "load":
                Play((string)msg["url"]!, null, 0, null);
                break;
            case "tracks":
                Post(new { type = "tracks", list = Tracks() });
                break;
            case "chapters":
                Post(new { type = "chapters", list = Chapters() });
                break;
            case "get":
                Post(new { type = "value", name = (string)msg["name"]!, value = mpv.Get((string)msg["name"]!) });
                break;
            case "fullscreen":
                SetFullScreen((bool)msg["on"]!);
                break;
            case "glass":
                SetGlass(msg);
                break;
            case "peek":
                Peek(msg);
                break;
            case "peek-ahead":
                PeekAhead(msg);
                break;
            case "seek-intent":
                SeekIntent(msg);
                break;
            case "sync-put":
                SyncPut(msg);
                break;
            case "sync-seed":
                SyncSeed(msg);
                break;
            case "anime":
                SetAnime((bool?)msg["on"] == true);
                break;
            case "smooth":
                SetSmooth((bool?)msg["on"] == true);
                break;
            case "media":
                SetMedia(msg);
                break;
            case "chrome":
                SetChromeLayout(Rects(msg["regions"]), Rect(msg["maximize"]));
                break;
            case "window":
                WindowCommand((string)msg["do"]!);
                break;
            case "jf":
                _ = AnswerAsync(msg);
                break;
            case "app":
                _ = AnswerAppAsync(msg);
                break;
        }
    }

    private async Task AnswerAsync(JsonNode msg)
    {
        var id = (int)msg["id"]!;
        // The server's address and token never appear in app.log (AppLog.Redact).
        HideServer();
        var op = (string?)msg["op"] ?? "";
        CrashReport.Running = "jf " + op;

        if ((string?)msg["args"]?["server"] is { } server)
        {
            _log.Hide(server);
            CrashReport.Note(server);
        }
        try
        {
            var data = await _library.HandleAsync(op, msg["args"]);
            HideServer();
            NoteServer(unreachable: data is JsonObject answer && answer["offline"]?.ToJsonString() == "true", op, null);
            PostJson($"{{\"type\":\"reply\",\"id\":{id},\"ok\":true,\"data\":{data?.ToJsonString() ?? "null"}}}");
        }
        catch (Exception e)
        {
            // A Jellyfin failure is one line (its kind and timings, from JellyfinLibrary.Note), never a stack trace.
            if (e is JellyfinException je)
            {
                _library.Jellyfin.Note((string?)msg["op"] ?? "", je);
                // Only when both tries failed (a read is retried once first) does the server count as unreachable.
                NoteServer(unreachable: je.Transient && je.Retried, op, e);
            }
            else _log.Write($"jf {(string?)msg["op"]}: {AppLog.Kind(e)}");
            Post(new { type = "reply", id, ok = false, error = e is LibraryException ? e.Message : "Something went wrong: " + e.Message });
        }
        finally
        {
            CrashReport.Running = "idle";
        }
    }

    /// <summary>
    /// Three answers in a row from the Jellyfin server that didn't come (no answer at all, or offline): a report of it, once.
    /// Any answer from it starts the count again.
    /// </summary>
    private void NoteServer(bool unreachable, string op, Exception? e)
    {
        if (!unreachable)
        {
            _offlineInARow = 0;
            return;
        }
        if (++_offlineInARow < 3) return;
        _offlineInARow = 0;
        var why = e is null ? "offline" : AppLog.Kind(e);
        CrashReport.Keep("ServerUnreachable", $"Jellyfin: the server didn't answer three times in a row ({op}; {why})",
            _log.Tail(200).Split('\n'), AppInfo.Data, sendNow: true);
    }

    private void HideServer()
    {
        _log.Hide(_library.Jellyfin.Address);
        _log.HideText(_library.Jellyfin.Token);
        CrashReport.Note(_library.Jellyfin.Token);
        CrashReport.Note(_library.Jellyfin.Address);
    }

    private static RectInt32? Rect(JsonNode? n) =>
        n is null ? null : new RectInt32((int)Math.Round((double)n["x"]!), (int)Math.Round((double)n["y"]!), (int)Math.Round((double)n["w"]!), (int)Math.Round((double)n["h"]!));

    private static RectInt32[]? Rects(JsonNode? n) => n?.AsArray().Select(Rect).OfType<RectInt32>().ToArray();

    private void Post(object message) => PostJson(JsonSerializer.Serialize(message));

    private void PostJson(string json)
    {
        if (_closingWindow) return;
        try { _core?.PostWebMessageAsJson(json); } catch (COMException) { } catch (InvalidOperationException) { }
    }

    /// <summary>Runs on the UI thread, from any thread.</summary>
    private void OnUi(Action action)
    {
        if (!_closingWindow) DispatcherQueue.Post(action);
    }

    private double GetWindowScale() => GetDpiForWindow(_hwnd) / 96.0;

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PlaceWeb();
        UpdateCaptionDragRegions();
    }

    /// <summary>
    /// WinUI's content (an empty, transparent grid here) is a child window of its own. Wherever it is in the window
    /// order, it takes pointer input over the whole window through its composition input routing, so real clicks
    /// never reached the page (posted ones did). It holds nothing, so it is hidden; the page is the only input.
    /// WinUI shows it again after a resize or a restore, later than any event here says so (the window then took no
    /// click until it was next activated), so its own window procedure refuses every show from the first hide on.
    /// </summary>
    private void SendXamlToBack()
    {
        var bridge = FindWindowEx(_hwnd, IntPtr.Zero, "Microsoft.UI.Content.DesktopChildSiteBridge", null);
        if (bridge == IntPtr.Zero) return;
        if (bridge != _bridgeHeld)
        {
            _bridgeHeld = bridge;
            _bridgeProc = (h, msg, w, l) =>
            {
                if (msg == Ui.HiddenWindow.WM_WINDOWPOSCHANGING) Ui.HiddenWindow.RefuseShow(l);
                return CallWindowProc(_bridgeNext, h, msg, w, l);
            };
            _bridgeNext = SetWindowLongPtr(bridge, -4 /* GWLP_WNDPROC */, Marshal.GetFunctionPointerForDelegate(_bridgeProc));
        }
        SetWindowPos(bridge, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOSIZE|NOMOVE|NOACTIVATE
        ShowWindow(bridge, 0); // SW_HIDE
    }

    private IntPtr _bridgeHeld, _bridgeNext;
    private WndProcDelegate? _bridgeProc; // kept alive while the window is subclassed

    /// <summary>The page fills the client area (physical pixels). A minimized window has none: the page keeps its size.</summary>
    private void PlaceWeb()
    {
        if (_controller is null) return;
        GetClientRect(_hwnd, out var r);
        if (r.Right <= r.Left || r.Bottom <= r.Top) return;
        _controller.Bounds = new Windows.Foundation.Rect(0, 0, r.Right - r.Left, r.Bottom - r.Top);
        ApplyPageScale();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _log.Write("closing");
        RestoreFilmHdr("Exo closed");
        SignalPlaying(false);
        _gameWatch?.Dispose();
        _leanWatch?.Dispose();
        _appearanceWatch?.Dispose();
        // Where playback stopped, kept before mpv goes.
        Task.Run(_library.Jellyfin.CancelPlayAsync).Wait(1500);
        // Any change to the synced stores still waiting to be written.
        Watch.WatchStore.Save();
        SyncedSettings.Save();
        _closingWindow = true;
        _performance?.Dispose();
        _readAheadWatch?.Dispose();
        _upscaleRecheck?.Dispose();
        if (_media != null) _media.IsEnabled = false;
        SaveWindowPlacement();
        _peek?.Dispose();
        _mpv?.Dispose();
        _mpv = null;
        _captionOverlay?.Dispose();
        _controller?.Close();
        _log.Flush();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
