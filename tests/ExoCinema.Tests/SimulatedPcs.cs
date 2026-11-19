using static ExoCinema.VideoDecoders;

namespace ExoCinema.Tests;

/// <summary>A screen: its size, refresh rate, HDR on in Windows, variable refresh.</summary>
internal sealed record Screen(int W, int H, int Hz, bool Hdr = false, bool Vrr = false);

/// <summary>
/// A PC as Direct3D would describe it: the card Windows picks for performance and, with two chips, the one it picks to
/// save power; usable memory (what Windows reports, less what built-in graphics keep); a laptop or a desktop; its screens.
/// </summary>
internal sealed record Pc(string Label, GraphicsCard Fast, GraphicsCard? Saving, double MemoryGb, bool Laptop, Screen[] Screens)
{
    /// <summary>The card DXGI hands over for a preference (1 save power, 2 performance); one chip answers both.</summary>
    public GraphicsCard Card(int preference) => preference == 1 && Saving != null ? Saving : Fast;

    public override string ToString() => Label;
}

/// <summary>
/// The simulated PCs, with the names, vendor ids and decoders their drivers report. Decoders by generation (DXVA
/// profiles with NV12/P010): GeForce 600-700 H.264 only; GeForce 900 (GM204) H.264 only; GeForce 10 and 16/20 add
/// HEVC and VP9 with 10-bit; RTX 30 on add AV1. Radeon RX 400-500 H.264 and HEVC; Vega APUs add VP9; RX 6000 on
/// and the 680M/780M add AV1. Intel UHD 600 series H.264, HEVC and VP9 with 10-bit; Xe (Iris Xe, UHD 700, Arc) adds AV1.
/// </summary>
internal static class SimulatedPcs
{
    private const VideoDecoders Kepler = H264;
    private const VideoDecoders Pascal = H264 | Hevc | HevcTenBit | Vp9 | Vp9TenBit;
    private const VideoDecoders Ampere = Pascal | Av1 | Av1TenBit;
    private const VideoDecoders Polaris = H264 | Hevc | HevcTenBit;
    private const VideoDecoders Raven = Polaris | Vp9 | Vp9TenBit;
    private const VideoDecoders Rdna2 = Raven | Av1 | Av1TenBit;
    private const VideoDecoders Gen9 = H264 | Hevc | HevcTenBit | Vp9 | Vp9TenBit;
    private const VideoDecoders Xe = Gen9 | Av1 | Av1TenBit;

    private const uint Nvidia = 0x10DE, Amd = 0x1002, Intel = 0x8086, Vmware = 0x15AD;

    private static GraphicsCard Dedicated(string name, uint vendor, long gb, VideoDecoders d) => new(name, vendor, false, gb * 1024, d);
    private static GraphicsCard BuiltIn(string name, uint vendor, long mb, VideoDecoders d) => new(name, vendor, true, mb, d);

    public static readonly GraphicsCard Uhd620 = BuiltIn("Intel(R) UHD Graphics 620", Intel, 128, Gen9);
    public static readonly GraphicsCard Uhd630 = BuiltIn("Intel(R) UHD Graphics 630", Intel, 128, Gen9);
    public static readonly GraphicsCard Uhd770 = BuiltIn("Intel(R) UHD Graphics 770", Intel, 128, Xe);
    public static readonly GraphicsCard IrisXe = BuiltIn("Intel(R) Iris(R) Xe Graphics", Intel, 128, Xe);
    public static readonly GraphicsCard MeteorLakeArc = BuiltIn("Intel(R) Arc(TM) Graphics", Intel, 128, Xe);
    public static readonly GraphicsCard LunarLakeArc = BuiltIn("Intel(R) Arc(TM) 140V GPU (16GB)", Intel, 128, Xe);
    public static readonly GraphicsCard RadeonIgpu = BuiltIn("AMD Radeon(TM) Graphics", Amd, 512, Rdna2);
    public static readonly GraphicsCard Radeon780M = BuiltIn("AMD Radeon 780M Graphics", Amd, 2048, Rdna2);
    // A handheld with 4 GB set aside for its graphics in the firmware: more "own" memory than a GTX 1650, still built in.
    public static readonly GraphicsCard AllyZ1 = BuiltIn("AMD Radeon(TM) Graphics", Amd, 4096, Rdna2);
    public static readonly GraphicsCard SteamDeckLike = BuiltIn("AMD Custom GPU 0405", Amd, 1024, Rdna2);
    public static readonly GraphicsCard Vega11 = BuiltIn("AMD Radeon(TM) RX Vega 11 Graphics", Amd, 1024, Raven);

    public static readonly GraphicsCard Gt710 = Dedicated("NVIDIA GeForce GT 710", Nvidia, 2, Kepler);
    public static readonly GraphicsCard Gtx970 = Dedicated("NVIDIA GeForce GTX 970", Nvidia, 4, Kepler);
    public static readonly GraphicsCard Gtx1060 = Dedicated("NVIDIA GeForce GTX 1060 6GB", Nvidia, 6, Pascal);
    public static readonly GraphicsCard Gtx1080 = Dedicated("NVIDIA GeForce GTX 1080", Nvidia, 8, Pascal);
    public static readonly GraphicsCard Gtx1650 = Dedicated("NVIDIA GeForce GTX 1650", Nvidia, 4, Pascal);
    public static readonly GraphicsCard Rtx2060 = Dedicated("NVIDIA GeForce RTX 2060", Nvidia, 6, Pascal);
    public static readonly GraphicsCard Rtx3060Laptop = Dedicated("NVIDIA GeForce RTX 3060 Laptop GPU", Nvidia, 6, Ampere);
    public static readonly GraphicsCard Rtx3060 = Dedicated("NVIDIA GeForce RTX 3060", Nvidia, 12, Ampere);
    public static readonly GraphicsCard Rtx3070 = Dedicated("NVIDIA GeForce RTX 3070", Nvidia, 8, Ampere);
    public static readonly GraphicsCard Rtx4090 = Dedicated("NVIDIA GeForce RTX 4090", Nvidia, 24, Ampere);
    public static readonly GraphicsCard Rtx5080 = Dedicated("NVIDIA GeForce RTX 5080", Nvidia, 16, Ampere);
    public static readonly GraphicsCard Rtx5090 = Dedicated("NVIDIA GeForce RTX 5090", Nvidia, 32, Ampere);
    public static readonly GraphicsCard RtxA2000 = Dedicated("NVIDIA RTX A2000", Nvidia, 6, Ampere);
    public static readonly GraphicsCard Rx580 = Dedicated("Radeon RX 580 Series", Amd, 8, Polaris);
    public static readonly GraphicsCard Rx6600 = Dedicated("AMD Radeon RX 6600", Amd, 8, Rdna2);
    public static readonly GraphicsCard Rx7900Xtx = Dedicated("AMD Radeon RX 7900 XTX", Amd, 24, Rdna2);
    public static readonly GraphicsCard Rx9070Xt = Dedicated("AMD Radeon RX 9070 XT", Amd, 16, Rdna2);
    public static readonly GraphicsCard ProW6800 = Dedicated("AMD Radeon PRO W6800", Amd, 32, Rdna2);
    public static readonly GraphicsCard ArcA770 = Dedicated("Intel(R) Arc(TM) A770 Graphics", Intel, 16, Xe);
    public static readonly GraphicsCard ArcB580 = Dedicated("Intel(R) Arc(TM) B580 Graphics", Intel, 12, Xe);
    public static readonly GraphicsCard VmwareSvga = new("VMware SVGA 3D", Vmware, false, 128, None);

    private static readonly Screen Fhd60 = new(1920, 1080, 60);
    private static readonly Screen Fhd144 = new(1920, 1080, 144, Vrr: true);
    private static readonly Screen Fhd360 = new(1920, 1080, 360, Vrr: true);
    private static readonly Screen Qhd165 = new(2560, 1440, 165, Vrr: true);
    private static readonly Screen Qhd240Hdr = new(2560, 1440, 240, Hdr: true, Vrr: true);
    private static readonly Screen Uhd120Hdr = new(3840, 2160, 120, Hdr: true, Vrr: true);
    private static readonly Screen Uhd240Hdr = new(3840, 2160, 240, Hdr: true, Vrr: true);
    private static readonly Screen Ultrawide = new(3440, 1440, 175, Vrr: true);
    private static readonly Screen Laptop768 = new(1366, 768, 60);
    private static readonly Screen Handheld800 = new(1280, 800, 60);
    private static readonly Screen Portrait = new(1080, 1920, 60);

    public static readonly Pc[] All =
    [
        new("Core i7-12700K, RTX 3070 (UHD 770 on too), 1440p 165 Hz", Rtx3070, Uhd770, 31.8, false, [Qhd165]),
        new("Core i5-13600K, Arc A770, 4K 120 Hz HDR", ArcA770, Uhd770, 31.8, false, [Uhd120Hdr]),
        new("Core i5-14400F, Arc B580, 1080p 60 Hz without VRR", ArcB580, null, 15.8, false, [Fhd60]),
        new("Core i5-8250U laptop, UHD 620 only, 8 GB", Uhd620, null, 7.8, true, [Fhd60]),
        new("Core i7-1165G7 laptop, Iris Xe only, 16 GB", IrisXe, null, 15.7, true, [new(1920, 1200, 60)]),
        new("Core Ultra 7 155H laptop, Arc graphics built in", MeteorLakeArc, null, 31.5, true, [new(2880, 1800, 120, Hdr: true)]),
        new("Core Ultra 7 258V laptop, Arc 140V built in", LunarLakeArc, null, 31.5, true, [new(2560, 1600, 120)]),
        new("Core i7-12700H laptop, RTX 3060 Laptop GPU and Iris Xe", Rtx3060Laptop, IrisXe, 15.7, true, [Fhd144]),
        new("Ryzen 5 5600, RX 6600, 1080p 144 Hz", Rx6600, null, 15.9, false, [Fhd144]),
        new("Ryzen 7 7800X3D, RX 7900 XTX, 4K 120 Hz HDR", Rx7900Xtx, RadeonIgpu, 31.6, false, [Uhd120Hdr]),
        new("Ryzen 7 9800X3D, RX 9070 XT, 1440p 240 Hz HDR", Rx9070Xt, RadeonIgpu, 31.6, false, [Qhd240Hdr]),
        new("Ryzen 9 7950X3D, RTX 4090, 4K 240 Hz HDR", Rtx4090, RadeonIgpu, 63.6, false, [Uhd240Hdr]),
        new("Ryzen 9 7950X3D, RTX 5090, 4K HDR beside a portrait 1080p", Rtx5090, RadeonIgpu, 63.6, false, [Uhd240Hdr, Portrait]),
        new("Ryzen 7 8700G, Radeon 780M only, 1440p", Radeon780M, null, 29.7, false, [Qhd165]),
        new("Ryzen Z1 Extreme handheld, 780M with 4 GB set aside, 1080p 120 Hz", AllyZ1, null, 11.6, true, [new(1920, 1080, 120, Vrr: true)]),
        new("Handheld with a custom AMD APU, 1280x800", SteamDeckLike, null, 14.5, true, [Handheld800]),
        new("Ryzen 5 2400G, RX Vega 11 graphics built in", Vega11, null, 6.9, false, [Fhd60]),
        new("Core i5-8400, GTX 1060 6GB, 1080p 60 Hz", Gtx1060, Uhd630, 15.9, false, [Fhd60]),
        new("Core i5-10400F, GTX 1650, 1080p 75 Hz", Gtx1650, null, 15.9, false, [new(1920, 1080, 75)]),
        new("Ryzen 5 3600, RTX 2060, 1080p 144 Hz", Rtx2060, null, 15.9, false, [Fhd144]),
        new("Core Ultra 9 285K, RTX 5080, 4K 240 Hz HDR", Rtx5080, null, 63.5, false, [Uhd240Hdr]),
        new("Core i7-7700K, GTX 1080 with the screen on UHD 630", Gtx1080, Uhd630, 15.9, false, [Fhd144]),
        new("Two makers: RX 6600 picked for performance, RTX 3060 beside it", Rx6600, Rtx3060, 31.8, false, [Qhd165]),
        new("Two makers: RTX 3060 picked for performance, RX 6600 beside it", Rtx3060, Rx6600, 31.8, false, [Qhd165]),
        new("Workstation, Radeon PRO W6800, ultrawide", ProW6800, null, 127.6, false, [Ultrawide]),
        new("Workstation, NVIDIA RTX A2000, two 1440p screens", RtxA2000, null, 63.6, false, [Qhd165, Qhd165]),
        new("Old office PC, GT 710", Gt710, null, 7.9, false, [Fhd60]),
        new("Core i7-4790K, GTX 970, 1080p 60 Hz", Gtx970, null, 15.9, false, [Fhd60]),
        new("RX 580 and 8 GB of memory, HDD only", Rx580, null, 7.9, false, [Fhd60]),
        new("Esports, RTX 3070 on a 1080p 360 Hz screen", Rtx3070, null, 31.8, false, [Fhd360]),
        new("Old laptop, UHD 620, 4 GB, 1366x768", Uhd620, null, 3.8, true, [Laptop768]),
        new("Virtual machine (VMware SVGA)", VmwareSvga, null, 7.9, false, [Fhd60]),
        new("No graphics driver (Direct3D can't be asked)", GraphicsCard.Unknown, null, 15.9, false, [new(1024, 768, 60)]),
    ];
}
