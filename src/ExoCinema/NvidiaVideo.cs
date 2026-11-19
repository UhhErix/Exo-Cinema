using System.Runtime.InteropServices;

namespace ExoCinema;

/// <summary>
/// Whether NVIDIA's RTX Video Super Resolution and RTX Video HDR are on, read the way Exo Launcher reads them (its
/// Helpers/NvidiaVideo.cs, from NVIDIA Control Panel's own calls): NvAPI_GetVideoStateEx 0x0B6EF8B9 with a 0x80-byte
/// struct, version 0x10080, the component at 4 (29 Super resolution, 30 HDR); supported is flags@0xC bit 0, on is
/// enable@0x10, the value is lastValueSet@0x58 (Super resolution's quality 1-4, 5 Auto; HDR any value but 0).
/// The settings are global and Exo Launcher's to change (PC › Display): Exo Cinema only reads them, never writes them.
/// </summary>
internal static unsafe class NvidiaVideo
{
    private const int SuperResolution = 29, VideoHdr = 30;
    private static readonly Lock Gate = new();
    private static bool? _available;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint QueryInterface(uint id);

    /// <summary>Super resolution's setting: 0 off, 1-4 quality, 5 Auto; null without an NVIDIA driver that has it.</summary>
    public static int? SuperResolutionSetting() => Setting(SuperResolution);

    /// <summary>
    /// RTX Video HDR's switch: true on, false off, null without an NVIDIA driver that has it (551.23 and later, RTX cards).
    /// The driver's processor turns SDR into HDR even with the switch off when a player asks (seen on an RTX 3070 with
    /// 621.24), so the switch is the user's say, not the driver's limit: off, Exo Cinema doesn't ask.
    /// </summary>
    public static bool? VideoHdrOn() => Setting(VideoHdr) is { } value ? value != 0 : null;

    private static int? Setting(int component)
    {
        lock (Gate)
        {
            try
            {
                if (!Available() || AnyDisplay() is not { } handle) return null;
                var data = stackalloc byte[0x80];
                new Span<byte>(data, 0x80).Clear();
                *(uint*)data = 0x10080;
                *(uint*)(data + 4) = (uint)component;
                var get = (delegate* unmanaged[Cdecl]<nint, byte*, int>)Resolve(0x0B6EF8B9);
                if (get(handle, data) != 0 || (*(uint*)(data + 0xC) & 1) == 0) return null;
                return *(int*)(data + 0x10) == 0 ? 0 : *(int*)(data + 0x58);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return null; }
        }
    }

    private static bool Available()
    {
        if (_available is { } known) return known;
        try
        {
            var init = (delegate* unmanaged[Cdecl]<int>)QueryInterface(0x0150E828);
            _available = init is not null && init() == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { _available = false; }
        return _available.Value;
    }

    // The setting is global, so any screen on the NVIDIA card will do.
    private static nint? AnyDisplay()
    {
        var byName = (delegate* unmanaged[Cdecl]<byte*, nint*, int>)Resolve(0x35C29134);
        for (var i = 1; i <= 16; i++)
        {
            var name = System.Text.Encoding.ASCII.GetBytes($@"\\.\DISPLAY{i}" + "\0");
            nint handle = 0;
            fixed (byte* text = name)
                if (byName(text, &handle) == 0 && handle != 0) return handle;
        }
        return null;
    }

    private static nint Resolve(uint id)
    {
        var fn = QueryInterface(id);
        return fn != 0 ? fn : throw new EntryPointNotFoundException($"NvAPI 0x{id:X8} is not in this driver.");
    }
}
