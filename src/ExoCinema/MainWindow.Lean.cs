using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExoCinema;

/// <summary>
/// Lean while playing (Exo-wide, Exo Launcher's research/lean-while-playing.md): while a game runs or streaming software
/// is open, Exo Cinema behind another window and playing nothing gets as light as it can by itself, with no setting.
/// <list type="bullet">
/// <item>The signal: Launcher's <c>Local\ExoLauncher.Lean</c> and <c>Local\ExoLauncher.Streaming</c>, looked at every 5 s.
/// Without Launcher (the events don't exist), Cinema's own look every 10 s: a streaming app among the processes by name,
/// or a full-screen app in front that isn't Cinema, Explorer or a browser.</item>
/// <item>Lean, behind and silent: Cinema and its WebView2 browser process at BelowNormal and low memory priority, Cinema on
/// EcoQoS, both trimmed once after 15 s (again only if one grew back over 32 MB). The anime shaders' warm-up waits. All
/// of it undone at once when the window comes to front, a video plays, or the game and the streaming app are gone.</item>
/// <item>Streaming: the page's Streamer Mode (no Exo account email shown, the email box masked).</item>
/// </list>
/// Never another process than Cinema's own, never a game's, never a system setting.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly string[] StreamingApps = ["obs64", "obs32", "streamlabs*", "xsplit.core", "prismlivestudio", "meldstudio", "twitchstudio", "vmix", "vmix64"];
    private static readonly string[] NotAGame = ["explorer", "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "exobrowser", "exolauncher", "exocinema"];
    private const long TrimAfterMs = 15_000, RegrowBytes = 32L << 20;

    private Timer? _leanWatch;
    private readonly Lock _leanGate = new();
    private bool _lean, _streaming, _leanOn, _playingNow;
    private long _behindSince, _ownLookAt;
    private (bool Lean, bool Streaming) _ownLook;
    private long _trimmedHost = -1, _trimmedBrowser = -1;

    private void StartLeanWatch() => _leanWatch = new Timer(_ => LeanTick(), null, 0, 5000);

    // A video started or stopped, or the window came to front: looked at now, not at the next tick.
    private void LeanNow() => ThreadPool.QueueUserWorkItem(_ => LeanTick());

    private void LeanTick()
    {
        if (_closingWindow) return;
        lock (_leanGate)
        {
            var (lean, streaming) = LeanSignals();
            if (streaming != _streaming)
            {
                _streaming = streaming;
                _log.Write(streaming ? "lean: streaming software open, Streamer Mode on" : "lean: streaming software gone, Streamer Mode off");
                OnUi(() => Post(new { type = "streaming", on = streaming }));
            }
            _lean = lean;
            var behind = GetForegroundWindow() != _hwnd;
            var go = lean && behind && !_playingNow;
            if (go != _leanOn)
            {
                _leanOn = go;
                _behindSince = Environment.TickCount64;
                // Which renderers are Cinema's, in the shared WebView2 (read on the UI thread, used from here on).
                if (go && _sharedWeb) OnUi(() => _ = ReadOwnRenderersAsync());
                SetLean(go);
                _log.Write(go ? "lean: a game or streaming runs and Cinema is behind; stepped back" : "lean: back to normal");
            }
            if (go && Environment.TickCount64 - _behindSince >= TrimAfterMs) Trim();
        }
    }

    private (bool Lean, bool Streaming) LeanSignals()
    {
        if (Signal(@"Local\ExoLauncher.Lean") is { } lean) return (lean, Signal(@"Local\ExoLauncher.Streaming") == true);
        // Exo Launcher isn't running: Cinema's own look, every 10 s.
        if (Environment.TickCount64 - _ownLookAt < 10_000) return _ownLook;
        _ownLookAt = Environment.TickCount64;
        var streaming = false;
        foreach (var p in Process.GetProcesses())
        {
            var name = p.ProcessName.ToLowerInvariant();
            p.Dispose();
            if (StreamingApps.Any(s => s.EndsWith('*') ? name.StartsWith(s[..^1], StringComparison.Ordinal) : name == s)) { streaming = true; break; }
        }
        return _ownLook = (streaming || FullScreenGameInFront(), streaming);
    }

    // An event of Launcher's: set, not set, or null when it doesn't exist (Launcher isn't running).
    private static bool? Signal(string name)
    {
        var handle = OpenEventW(0x00100000 /* SYNCHRONIZE */, false, name);
        if (handle == IntPtr.Zero) return null;
        try { return WaitForSingleObject(handle, 0) == 0; }
        finally { CloseHandle(handle); }
    }

    private static bool FullScreenGameInFront()
    {
        if (SHQueryUserNotificationState(out var state) != 0 || state is not (2 /* QUNS_BUSY */ or 3 /* QUNS_RUNNING_D3D_FULL_SCREEN */)) return false;
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        try
        {
            using var front = Process.GetProcessById((int)pid);
            return !NotAGame.Contains(front.ProcessName.ToLowerInvariant());
        }
        catch (ArgumentException) { return false; }
    }

    private void SetLean(bool on)
    {
        _trimmedHost = _trimmedBrowser = -1;
        using var host = Process.GetCurrentProcess();
        Step(host, on);
        if (!SetEcoQoS(host.Handle, on) && !_ecoQoSNoted)
        {
            // ERROR_INVALID_FUNCTION: a tweak playbook turned power throttling off (PowerThrottlingOff=1). Priority, memory
            // priority and the trims still work; Exo OS offers the fix where it matters (hybrid CPUs, laptops).
            _ecoQoSNoted = true;
            _log.Write($"lean: EcoQoS unavailable on this PC (error {Marshal.GetLastWin32Error()}); the rest of Lean applies");
        }
        foreach (var web in LeanWebProcesses()) using (web) Step(web, on);
        // In the shared WebView2 the browser's children are every Exo app's: only Cinema's own are its to raise.
        if (!on && !_sharedWeb) RaiseStartedLow((uint)Environment.ProcessId);
    }

    // A process started while Cinema was stepped back took its BelowNormal (CreateProcess's default): WebView2's renderers
    // and GPU process, started by its browser while that was low. Stepping forward puts each of Cinema's own still at
    // BelowNormal back to Normal; a priority the engine set itself (Idle for a background renderer) is left as it is.
    private static void RaiseStartedLow(uint root)
    {
        var parents = new Dictionary<uint, uint>();
        var snapshot = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                parents[entry.ProcessId] = entry.ParentProcessId;
        }
        finally { CloseHandle(snapshot); }
        var mine = new HashSet<uint> { root };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (pid, parent) in parents)
                if (!mine.Contains(pid) && mine.Contains(parent) && pid != parent) { mine.Add(pid); grew = true; }
        }
        mine.Remove(root);
        foreach (var pid in mine)
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (p.PriorityClass == ProcessPriorityClass.BelowNormal) p.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    private static void Step(Process p, bool on)
    {
        try
        {
            p.PriorityClass = on ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            var priority = new MemoryPriority { Value = on ? 2u /* MEMORY_PRIORITY_LOW */ : 5u /* MEMORY_PRIORITY_NORMAL */ };
            SetProcessInformation(p.Handle, 0 /* ProcessMemoryPriority */, ref priority, (uint)Marshal.SizeOf<MemoryPriority>());
            // Disk and network reads at Low too (Exo OS's O1 gap): Lean is never on while a video plays, so the player's
            // reads are never slowed.
            var io = on ? 1 /* IoPriorityLow */ : 2 /* IoPriorityNormal */;
            NtSetInformationProcess(p.Handle, 33 /* ProcessIoPriority */, ref io, sizeof(int));
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static bool SetEcoQoS(IntPtr process, bool on)
    {
        var state = new PowerThrottling { Version = 1, ControlMask = 1 /* EXECUTION_SPEED */, StateMask = on ? 1u : 0u };
        return SetProcessInformation(process, 4 /* ProcessPowerThrottling */, ref state, (uint)Marshal.SizeOf<PowerThrottling>());
    }

    private bool _ecoQoSNoted;

    // Each trimmed once, again only if it grew back more than 32 MB.
    private void Trim()
    {
        using (var host = Process.GetCurrentProcess()) _trimmedHost = TrimOne(host, _trimmedHost);
        foreach (var web in LeanWebProcesses()) using (web) _trimmedBrowser = TrimOne(web, _trimmedBrowser);
    }

    private static long TrimOne(Process p, long last)
    {
        try
        {
            p.Refresh();
            if (last >= 0 && p.WorkingSet64 - last < RegrowBytes) return last;
            EmptyWorkingSet(p.Handle);
            p.Refresh();
            return p.WorkingSet64;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return last; }
    }

    private Process? BrowserProcess()
    {
        try { return _core is { } core ? Process.GetProcessById((int)core.BrowserProcessId) : null; }
        catch (Exception e) when (e is ArgumentException or COMException or InvalidOperationException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryPriority { public uint Value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottling { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref MemoryPriority info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, uint size);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref int info, int size);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
