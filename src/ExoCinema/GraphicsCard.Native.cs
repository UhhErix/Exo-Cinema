using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ExoCinema;

internal sealed partial record GraphicsCard
{
    /// <summary>
    /// The card Windows picks for <paramref name="preference"/> (DXGI_GPU_PREFERENCE: 1 to save power, 2 for performance),
    /// skipping Microsoft's software renderer, as Exo Launcher picks it (Services/PcHealth.Native.cs). Whether it is
    /// built in comes from Direct3D 11 (unified memory) and what it decodes from its video decoder profiles, the ones mpv's
    /// d3d11va uses. <see cref="Unknown"/> when Direct3D can't be asked.
    /// </summary>
    public static unsafe GraphicsCard Read(int preference)
    {
        try
        {
            var factoryIid = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17"); // IDXGIFactory6
            if (CreateDXGIFactory2(0, in factoryIid, out var factory) < 0) return Unknown;
            try
            {
                var adapterIid = new Guid("29038f61-3839-4626-91fd-086879011a05"); // IDXGIAdapter1
                var byPreference = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, Guid*, IntPtr*, int>)(*(IntPtr**)factory)[29]; // EnumAdapterByGpuPreference
                for (uint i = 0; i < 8; i++)
                {
                    IntPtr adapter;
                    if (byPreference(factory, i, preference, &adapterIid, &adapter) < 0) return Unknown;
                    try
                    {
                        AdapterDesc1 desc;
                        var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(IntPtr**)adapter)[10];
                        if (getDesc1(adapter, &desc) < 0 || (desc.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                        var name = new string(desc.Description).TrimEnd('\0').Trim();
                        var (integrated, decoders) = Ask(adapter);
                        return new GraphicsCard(name, desc.VendorId, integrated, (long)(desc.DedicatedVideoMemory / (1024 * 1024)), decoders);
                    }
                    finally { Marshal.Release(adapter); }
                }
                return Unknown;
            }
            finally { Marshal.Release(factory); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SEHException) { return Unknown; }
    }

    // Direct3D 11's word on the card: unified memory (built in) and the decoder profiles that have the surface format
    // mpv decodes to (NV12 for 8-bit, P010 for 10-bit). A card Direct3D 11 can't open counts as built in, decoding nothing.
    private static unsafe (bool Integrated, VideoDecoders Decoders) Ask(IntPtr adapter)
    {
        if (D3D11CreateDevice(adapter, 0 /* D3D_DRIVER_TYPE_UNKNOWN */, IntPtr.Zero, 0x800 /* VIDEO_SUPPORT */, IntPtr.Zero, 0, 7, out var device, IntPtr.Zero, IntPtr.Zero) < 0)
            return (true, VideoDecoders.None);
        try
        {
            var check = (delegate* unmanaged[Stdcall]<IntPtr, int, void*, uint, int>)(*(IntPtr**)device)[33]; // CheckFeatureSupport
            var options2 = stackalloc int[8]; // D3D11_FEATURE_DATA_D3D11_OPTIONS2; UnifiedMemoryArchitecture is the last
            var integrated = check(device, 14 /* D3D11_FEATURE_D3D11_OPTIONS2 */, options2, 32) >= 0 && options2[7] != 0;
            var videoIid = new Guid("10ec4d5b-975a-4689-b9e4-d0aac30fe333"); // ID3D11VideoDevice
            if (Marshal.QueryInterface(device, in videoIid, out var video) < 0) return (integrated, VideoDecoders.None);
            try
            {
                var count = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)video)[11])(video); // GetVideoDecoderProfileCount
                var profile = (delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, int>)(*(IntPtr**)video)[12]; // GetVideoDecoderProfile
                var format = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, int*, int>)(*(IntPtr**)video)[13]; // CheckVideoDecoderFormat
                var decoders = VideoDecoders.None;
                for (uint i = 0; i < count; i++)
                {
                    Guid id;
                    if (profile(video, i, &id) < 0) continue;
                    foreach (var (guid, flag, tenBit) in Profiles)
                    {
                        if (id != guid) continue;
                        int ok;
                        if (format(video, &id, tenBit ? 104 /* P010 */ : 103 /* NV12 */, &ok) >= 0 && ok != 0) decoders |= flag;
                    }
                }
                return (integrated, decoders);
            }
            finally { Marshal.Release(video); }
        }
        finally { Marshal.Release(device); }
    }

    // D3D11_DECODER_PROFILE_* (d3d11.h). AV1's profile 0 covers 8-bit and 10-bit: the surface format tells them apart.
    private static readonly (Guid Id, VideoDecoders Flag, bool TenBit)[] Profiles =
    [
        (new("1b81be68-a0c7-11d3-b984-00c04f2e73c5"), VideoDecoders.H264, false),      // H264_VLD_NOFGT
        (new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0"), VideoDecoders.Hevc, false),      // HEVC_VLD_MAIN
        (new("107af0e0-ef1a-4d19-aba8-67a163073d13"), VideoDecoders.HevcTenBit, true), // HEVC_VLD_MAIN10
        (new("463707f8-a1d0-4585-876d-83aa6d60b89e"), VideoDecoders.Vp9, false),       // VP9_VLD_PROFILE0
        (new("a4c749ef-6ecf-48aa-8448-50a7a1165ff7"), VideoDecoders.Vp9TenBit, true),  // VP9_VLD_10BIT_PROFILE2
        (new("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a"), VideoDecoders.Av1, false),       // AV1_VLD_PROFILE0
        (new("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a"), VideoDecoders.Av1TenBit, true),
    ];

    /// <summary>The card the user chose for Exo Cinema in Windows' Settings › Graphics: 1 to save power, 2 for performance; null if none.</summary>
    public static int? WindowsChoice()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            return key?.GetValue(Environment.ProcessPath ?? "") is string value
                && System.Text.RegularExpressions.Regex.Match(value, @"GpuPreference=([12]);") is { Success: true } m ? m.Groups[1].Value[0] - '0' : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory2(uint flags, in Guid riid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint levels, uint sdkVersion, out IntPtr device, IntPtr level, IntPtr context);
}
