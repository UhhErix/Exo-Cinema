namespace ExoCinema;

/// <summary>
/// The graphics card mpv draws on: what Direct3D says of it on this PC (GraphicsCard.Native.cs), or a simulated PC's in
/// the tests. Every choice the player makes for the hardware starts here (PlayerTuning, PictureRules, StreamChoice).
/// </summary>
internal sealed partial record GraphicsCard(string Name, uint VendorId, bool Integrated, long VramMb, VideoDecoders Decoders)
{
    /// <summary>No card Direct3D could tell about: mpv picks its own, and the player takes the lighter choices.</summary>
    public static readonly GraphicsCard Unknown = new("unknown", 0, true, 0, VideoDecoders.None);

    public string Vendor => VendorId switch { 0x10DE => "nvidia", 0x1002 or 0x1022 => "amd", 0x8086 => "intel", _ => "other" };

    /// <summary>NVIDIA's cards with RTX Video Super Resolution: GeForce RTX 20 and later, NVIDIA RTX and Quadro RTX.</summary>
    public bool Rtx => Vendor == "nvidia" && Name.Contains("RTX", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The driver's own video upscaler, as mpv's d3d11vpp names it, or null where the card has none mpv can call:
    /// "nvidia" (RTX Video Super Resolution) on RTX cards; "intel" (Intel's Video Super Resolution) on Xe and Arc graphics,
    /// built in or not, told by their AV1 decoder (UHD 620/630 have none). AMD's Video Upscale is applied by its driver to
    /// browsers only, with no call for a player: unavailable here, mpv's scalers do it.
    /// </summary>
    public string? SuperResolution => Rtx ? "nvidia" : Vendor == "intel" && Decoders.HasFlag(VideoDecoders.Av1) ? "intel" : null;

    /// <summary>
    /// A dedicated card of the last decade: mpv's high-quality scalers, the AI upscaling and pictures above 1080p. Told by
    /// what it is rather than by its name ("Arc" is also Intel's built-in graphics, "RX Vega" AMD's): not built in, from a
    /// maker with a video driver, and either decoding 10-bit HEVC (2016 on: GeForce 10, Radeon RX 400, Arc) or with
    /// 3 GB or more of its own memory (the GeForce 900s and Radeon R9s before that).
    /// </summary>
    public bool Strong => !Integrated && Vendor != "other" && (Decoders.HasFlag(VideoDecoders.HevcTenBit) || VramMb >= 3072);

    /// <summary>The card decodes this video itself (mpv's d3d11va); a codec the text doesn't name counts as decodable.</summary>
    public bool Decodes(string? codec, bool tenBit) => codec?.ToLowerInvariant() switch
    {
        "h264" or "avc" => !tenBit && Decoders.HasFlag(VideoDecoders.H264), // 10-bit H.264 decodes on no card
        "hevc" or "h265" => Decoders.HasFlag(tenBit ? VideoDecoders.HevcTenBit : VideoDecoders.Hevc),
        "vp9" => Decoders.HasFlag(tenBit ? VideoDecoders.Vp9TenBit : VideoDecoders.Vp9),
        "av1" => Decoders.HasFlag(tenBit ? VideoDecoders.Av1TenBit : VideoDecoders.Av1),
        _ => true,
    };

    /// <summary>For app.log: the card, built in or its memory, and what it decodes.</summary>
    public override string ToString() =>
        $"{Name} · {(Integrated ? "built in" : $"{VramMb / 1024.0:0.#} GB")} · decodes {(Decoders == VideoDecoders.None ? "nothing" : Decoders.ToString())}";
}

/// <summary>The video formats a card decodes itself, from its Direct3D 11 video decoder profiles.</summary>
[Flags]
internal enum VideoDecoders
{
    None = 0,
    H264 = 1,
    Hevc = 2,
    HevcTenBit = 4,
    Vp9 = 8,
    Vp9TenBit = 16,
    Av1 = 32,
    Av1TenBit = 64,
}
