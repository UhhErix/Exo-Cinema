using System.Runtime.InteropServices;

namespace ExoCinema;

internal sealed partial record SoundOutput
{
    // What a device takes changes only with the device (or what's plugged into it): asked once for each.
    private static (string Id, string Name, Bitstreams Takes)? _known;
    private static readonly Lock Gate = new();

    /// <summary>
    /// Windows' default output for media, the one mpv's WASAPI output picks (eRender, eMultimedia); null when Windows
    /// can't be asked or there is no output. Which formats it takes is asked as mpv will ask (exclusive mode, the same
    /// IEC 61937 formats); whether another app plays on it, from the device's audio sessions.
    /// </summary>
    public static SoundOutput? Read() => OnDefault(device =>
    {
        var id = Id(device);
        if (_known is not { } known || known.Id != id) _known = known = (id, FriendlyName(device), Accepted(device));
        return new SoundOutput(known.Name, known.Takes, AnotherAppPlaying(device));
    }, null);

    /// <summary>
    /// The channels Windows mixes the default output to (its shared-mode format, the one mpv's WASAPI output opens when
    /// it doesn't pass through): 2 for headphones and stereo speakers, 6 or 8 for surround. Asked of Windows rather than
    /// mpv because mpv reports what our own filters hand it. 0 when Windows can't be asked.
    /// </summary>
    public static unsafe int MixChannels() => OnDefault(device =>
    {
        var iid = new Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2"); // IAudioClient
        IntPtr client;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)(*(IntPtr**)device)[3])(device, &iid, 0x17, IntPtr.Zero, &client) < 0) return 0; // Activate
        try
        {
            WaveFormatExtensible* mix;
            if (((delegate* unmanaged[Stdcall]<IntPtr, WaveFormatExtensible**, int>)(*(IntPtr**)client)[8])(client, &mix) < 0) return 0; // GetMixFormat
            try { return mix->Channels; }
            finally { Marshal.FreeCoTaskMem((IntPtr)mix); }
        }
        finally { Marshal.Release(client); }
    }, 0);

    // Windows' default output for media (eRender, eMultimedia), handed to use; none when Windows can't be asked.
    private static unsafe T OnDefault<T>(Func<IntPtr, T> use, T none)
    {
        lock (Gate)
        {
            try
            {
                _ = CoInitializeEx(IntPtr.Zero, 0); // this thread's COM, if it has none yet (multithreaded)
                if (CoCreateInstance(in EnumeratorClsid, IntPtr.Zero, 0x17 /* CLSCTX_ALL */, in EnumeratorIid, out var enumerator) < 0) return none;
                try
                {
                    IntPtr device;
                    var getDefault = (delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)(*(IntPtr**)enumerator)[4]; // GetDefaultAudioEndpoint
                    if (getDefault(enumerator, 0 /* eRender */, 1 /* eMultimedia */, &device) < 0) return none;
                    try { return use(device); }
                    finally { Marshal.Release(device); }
                }
                finally { Marshal.Release(enumerator); }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SEHException) { return none; }
        }
    }

    private static unsafe string Id(IntPtr device)
    {
        IntPtr text;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(IntPtr**)device)[5])(device, &text) < 0) return ""; // GetId
        try { return Marshal.PtrToStringUni(text) ?? ""; }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    private static unsafe string FriendlyName(IntPtr device)
    {
        IntPtr store;
        if (((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)(*(IntPtr**)device)[4])(device, 0 /* STGM_READ */, &store) < 0) return ""; // OpenPropertyStore
        try
        {
            var key = stackalloc byte[20]; // PKEY_Device_FriendlyName: {a45c254e-...}, 14
            *(Guid*)key = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
            *(uint*)(key + 16) = 14;
            var value = stackalloc byte[24]; // PROPVARIANT: vt at 0, the string at 8
            new Span<byte>(value, 24).Clear();
            if (((delegate* unmanaged[Stdcall]<IntPtr, byte*, byte*, int>)(*(IntPtr**)store)[5])(store, key, value) < 0) return ""; // GetValue
            try { return *(ushort*)value == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(*(IntPtr*)(value + 8)) ?? "" : ""; }
            finally { PropVariantClear(value); }
        }
        finally { Marshal.Release(store); }
    }

    // The formats as mpv's WASAPI output offers them: two channels at 48 kHz for AC-3 and DTS, at 192 kHz for E-AC-3;
    // eight at 192 kHz (HDMI's high bitrate) for DTS-HD and TrueHD. 16-bit, IEC 61937 subtypes (ksmedia.h).
    private static readonly (Bitstreams Format, ushort Channels, uint Rate, Guid Subtype)[] Formats =
    [
        (Bitstreams.Ac3, 2, 48_000, new("00000092-0000-0010-8000-00aa00389b71")),
        (Bitstreams.Dts, 2, 48_000, new("00000008-0000-0010-8000-00aa00389b71")),
        (Bitstreams.Eac3, 2, 192_000, new("0000000a-0cea-0010-8000-00aa00389b71")),
        (Bitstreams.DtsHd, 8, 192_000, new("0000000b-0cea-0010-8000-00aa00389b71")),
        (Bitstreams.TrueHd, 8, 192_000, new("0000000c-0cea-0010-8000-00aa00389b71")),
    ];

    private static unsafe Bitstreams Accepted(IntPtr device)
    {
        var iid = new Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2"); // IAudioClient
        IntPtr client;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)(*(IntPtr**)device)[3])(device, &iid, 0x17, IntPtr.Zero, &client) < 0) return Bitstreams.None; // Activate
        try
        {
            var supported = (delegate* unmanaged[Stdcall]<IntPtr, int, WaveFormatExtensible*, IntPtr, int>)(*(IntPtr**)client)[7]; // IsFormatSupported
            var takes = Bitstreams.None;
            foreach (var (format, channels, rate, subtype) in Formats)
            {
                var wave = new WaveFormatExtensible
                {
                    FormatTag = 0xFFFE, // WAVE_FORMAT_EXTENSIBLE
                    Channels = channels,
                    SamplesPerSec = rate,
                    BlockAlign = (ushort)(channels * 2),
                    AvgBytesPerSec = rate * channels * 2u,
                    BitsPerSample = 16,
                    Size = 22,
                    ValidBitsPerSample = 16,
                    ChannelMask = channels == 8 ? 0x63Fu : 0x3u, // 7.1, or front left and right
                    SubFormat = subtype,
                };
                if (supported(client, 1 /* AUDCLNT_SHAREMODE_EXCLUSIVE */, &wave, IntPtr.Zero) == 0) takes |= format;
            }
            return takes;
        }
        finally { Marshal.Release(client); }
    }

    // Another app's stream running on this device (a call, a game, music), not Windows' own sounds and not this app's.
    private static unsafe bool AnotherAppPlaying(IntPtr device)
    {
        var iid = new Guid("77aa99a0-1bd6-484f-8bc7-2c654c9a9b6f"); // IAudioSessionManager2
        IntPtr manager;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)(*(IntPtr**)device)[3])(device, &iid, 0x17, IntPtr.Zero, &manager) < 0) return false;
        try
        {
            IntPtr sessions;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(IntPtr**)manager)[5])(manager, &sessions) < 0) return false; // GetSessionEnumerator
            try
            {
                int count;
                if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)(*(IntPtr**)sessions)[3])(sessions, &count) < 0) return false; // GetCount
                var control2Iid = new Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"); // IAudioSessionControl2
                for (var i = 0; i < count; i++)
                {
                    IntPtr control;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)(*(IntPtr**)sessions)[4])(sessions, i, &control) < 0) continue; // GetSession
                    try
                    {
                        int state;
                        if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)(*(IntPtr**)control)[3])(control, &state) < 0 || state != 1 /* Active */) continue; // GetState
                        if (Marshal.QueryInterface(control, in control2Iid, out var control2) < 0) return true;
                        try
                        {
                            var vtable = *(IntPtr**)control2;
                            if (((delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[15])(control2) == 0) continue; // IsSystemSoundsSession: S_OK
                            uint pid;
                            if (((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)vtable[14])(control2, &pid) >= 0 && pid == Environment.ProcessId) continue; // GetProcessId
                            return true;
                        }
                        finally { Marshal.Release(control2); }
                    }
                    finally { Marshal.Release(control); }
                }
                return false;
            }
            finally { Marshal.Release(sessions); }
        }
        finally { Marshal.Release(manager); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatExtensible
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size, ValidBitsPerSample;
        public uint ChannelMask;
        public Guid SubFormat;
    }

    private static readonly Guid EnumeratorClsid = new("bcde0395-e52f-467c-8e3d-c4579291692e"); // MMDeviceEnumerator
    private static readonly Guid EnumeratorIid = new("a95664d2-9614-4f35-a746-de8db63617e6"); // IMMDeviceEnumerator

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern unsafe int PropVariantClear(void* value);
}
