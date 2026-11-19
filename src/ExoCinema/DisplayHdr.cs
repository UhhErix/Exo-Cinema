using System.Runtime.InteropServices;

namespace ExoCinema;

/// <summary>
/// Windows HDR on a screen, through the documented display configuration API (the calls Windows Settings uses; no admin
/// rights). Ported from Exo Launcher's WindowsHdr.cs, without its EDID, colour profile and ramp-calibration parts. Windows
/// 11 24H2 split HDR from Auto Color Management, so the HDR-only calls are used when Windows has them; older builds use the
/// advanced-colour calls. Switching HDR can reset the screen's gamma ramp, so the ramp is read before and put back after.
/// </summary>
internal static class DisplayHdr
{
    /// <summary>One active screen. Supported: the screen can do HDR. Enabled: HDR is on now. RefreshHz: its refresh rate (0 when Windows doesn't say).</summary>
    internal sealed record Screen(Luid Adapter, uint Target, uint Source, string GdiName, string DevicePath,
        bool Supported, bool Enabled, bool NewApi, double RefreshHz);

    /// <summary>Every active screen, in Windows' path order; empty when Windows does not answer.</summary>
    internal static List<Screen> All()
    {
        var screens = new List<Screen>();
        try
        {
            if (!ActivePaths(out var paths)) return screens;
            foreach (var path in paths)
            {
                var gdi = SourceGdiName(path);
                // A cloned source shows once, as its first target.
                if (gdi.Length == 0 || screens.Any(screen => string.Equals(screen.GdiName, gdi, StringComparison.OrdinalIgnoreCase))) continue;
                var (supported, enabled, newApi) = ReadHdr(path.targetInfo.adapterId, path.targetInfo.id);
                var devicePath = TargetDevicePath(path.targetInfo.adapterId, path.targetInfo.id);
                var rate = path.targetInfo.refreshDenominator == 0 ? 0 : (double)path.targetInfo.refreshNumerator / path.targetInfo.refreshDenominator;
                screens.Add(new Screen(path.targetInfo.adapterId, path.targetInfo.id, path.sourceInfo.id, gdi, devicePath, supported, enabled, newApi, rate));
            }
        }
        // Windows didn't answer (an old build, a driver mid-change): no screens, so nothing is switched.
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or ArgumentException) { }
        return screens;
    }

    /// <summary>The screen the window is mostly on (its nearest monitor), or null when Windows doesn't say.</summary>
    internal static Screen? ForWindow(nint hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>(), szDevice = "" };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
        return All().FirstOrDefault(screen => string.Equals(screen.GdiName, info.szDevice, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The screen with this device path, as it is now (null when it is no longer active).</summary>
    internal static Screen? Find(string devicePath) =>
        All().FirstOrDefault(screen => string.Equals(screen.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Switches HDR on that screen; true when Windows took the request (it may still take a moment to show).</summary>
    internal static bool Set(Screen screen, bool on)
    {
        try
        {
            if (screen.NewApi)
            {
                var set = new SetHdrState { header = Header(16, Marshal.SizeOf<SetHdrState>(), screen.Adapter, screen.Target), value = on ? 1u : 0u };
                return DisplayConfigSetDeviceInfo(ref set) == 0;
            }
            var legacy = new SetAdvancedColor { header = Header(10, Marshal.SizeOf<SetAdvancedColor>(), screen.Adapter, screen.Target), enable = on ? 1u : 0u };
            return DisplayConfigSetDeviceInfo(ref legacy) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>The screen's gamma ramp (3 x 256), or null.</summary>
    internal static ushort[]? ReadRamp(string gdiName)
    {
        var dc = CreateDC("DISPLAY", gdiName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try { var ramp = new ushort[3 * 256]; return GetDeviceGammaRamp(dc, ramp) ? ramp : null; }
        finally { DeleteDC(dc); }
    }

    /// <summary>Sets the screen's gamma ramp; false when Windows refused it.</summary>
    internal static bool WriteRamp(string gdiName, ushort[] ramp)
    {
        var dc = CreateDC("DISPLAY", gdiName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        try { return SetDeviceGammaRamp(dc, ramp); }
        finally { DeleteDC(dc); }
    }

    private static (bool Supported, bool Enabled, bool NewApi) ReadHdr(Luid adapter, uint target)
    {
        if (Environment.OSVersion.Version.Build >= 26100)
        {
            var info2 = new AdvancedColorInfo2 { header = Header(15, Marshal.SizeOf<AdvancedColorInfo2>(), adapter, target) };
            if (DisplayConfigGetDeviceInfo(ref info2) == 0)
            {
                var supported = (info2.value & 0x10) != 0;
                return (supported, supported && ((info2.value & 0x20) != 0 || info2.activeColorMode == 2), true);
            }
        }
        var info = new AdvancedColorInfo { header = Header(9, Marshal.SizeOf<AdvancedColorInfo>(), adapter, target) };
        if (DisplayConfigGetDeviceInfo(ref info) != 0) return (false, false, false);
        // Bit 2, wide colour enforced: an SDR screen under Auto color management, which reads as advanced colour too.
        var hdr = (info.value & 1) != 0 && (info.value & 4) == 0;
        return (hdr, hdr && (info.value & 2) != 0, false);
    }

    /// <summary>The target's device path (\\?\DISPLAY#...), the key that tells one screen from another across restarts.</summary>
    private static string TargetDevicePath(Luid adapter, uint target)
    {
        var request = new TargetDeviceName { header = Header(2, Marshal.SizeOf<TargetDeviceName>(), adapter, target), friendlyName = "", devicePath = "" };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request.devicePath ?? "" : "";
    }

    private static bool ActivePaths(out PathInfo[] paths)
    {
        paths = [];
        const uint onlyActive = 2;
        if (GetDisplayConfigBufferSizes(onlyActive, out var pathCount, out var modeCount) != 0) return false;
        paths = new PathInfo[pathCount];
        var modes = new ModeInfo[modeCount];
        if (QueryDisplayConfig(onlyActive, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return false;
        Array.Resize(ref paths, (int)pathCount);
        return true;
    }

    private static string SourceGdiName(PathInfo path)
    {
        var request = new SourceDeviceName { header = Header(1, Marshal.SizeOf<SourceDeviceName>(), path.sourceInfo.adapterId, path.sourceInfo.id), viewGdiDeviceName = "" };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request.viewGdiDeviceName ?? "" : "";
    }

    private static DeviceInfoHeader Header(int type, int size, Luid adapter, uint id) =>
        new() { type = type, size = (uint)size, adapterId = adapter, id = id };

    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfoHeader { public int type; public uint size; public Luid adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential)] private struct AdvancedColorInfo { public DeviceInfoHeader header; public uint value; public int colorEncoding; public uint bitsPerColorChannel; }
    [StructLayout(LayoutKind.Sequential)] private struct AdvancedColorInfo2 { public DeviceInfoHeader header; public uint value; public int colorEncoding; public uint bitsPerColorChannel; public int activeColorMode; }
    [StructLayout(LayoutKind.Sequential)] private struct SetAdvancedColor { public DeviceInfoHeader header; public uint enable; }
    [StructLayout(LayoutKind.Sequential)] private struct SetHdrState { public DeviceInfoHeader header; public uint value; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName { public DeviceInfoHeader header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader header; public uint flags; public uint outputTechnology; public ushort manufacturerId; public ushort productCode; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathSourceInfo { public Luid adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling;
        public uint refreshNumerator; public uint refreshDenominator; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public PathSourceInfo sourceInfo; public PathTargetInfo targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct ModeInfo { public uint infoType; public uint id; public Luid adapterId; public ulong u0, u1, u2, u3, u4, u5; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize; public int left, top, right, bottom, workLeft, workTop, workRight, workBottom; public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo2 request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] private static extern int DisplayConfigSetDeviceInfo(ref SetAdvancedColor request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] private static extern int DisplayConfigSetDeviceInfo(ref SetHdrState request);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);
    [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
}
