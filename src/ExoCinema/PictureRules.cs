using System.Globalization;

namespace ExoCinema;

/// <summary>
/// The player's automatic choices as plain rules: the facts go in (the graphics card, the power, the video, the window),
/// the choice comes out. MainWindow.Player.cs and PlayerTuning read the facts from mpv and Windows; the simulated PCs in
/// tests/ExoCinema.Tests feed made-up ones.
/// </summary>
internal static class PictureRules
{
    /// <summary>
    /// The card mpv draws on, as a DXGI_GPU_PREFERENCE: the one the user chose for Exo Cinema in Windows' Settings ›
    /// Graphics; else the faster one on mains power (a laptop's GeForce or Radeon, so High quality and the upscalers run
    /// there) and the one that saves power on battery, where the picture is Balanced anyway. One card only: the same.
    /// </summary>
    public static int AdapterPreference(bool onBattery, int? windowsChoice) => windowsChoice is 1 or 2 ? windowsChoice.Value : onBattery ? 1 : 2;

    /// <summary>High quality: a dedicated card on mains power, until a video drops frames.</summary>
    public static bool High(GraphicsCard card, bool onBattery, bool lighterForThisVideo) => card.Strong && !onBattery && !lighterForThisVideo;

    public static bool Hdr(string? gamma) => gamma is "pq" or "hlg";

    /// <summary>
    /// ArtCNN for anime: on High quality, once its shaders are compiled, with no game running and not turned off for this
    /// video; SDR only; up to 1440p (a 4K source is already sharper than the screen, and the passes at 4K and above are
    /// heavy), and shown at more than half its size (a 1440p episode on a 1080p screen is drawn at 4K too).
    /// </summary>
    public static bool AnimeUpscale(bool anime, bool compiled, bool high, bool offForThisVideo, bool gameRunning, double height, string? gamma, double shownHeight) =>
        anime && compiled && high && !offForThisVideo && !gameRunning && height is > 0 and <= 1600 && !Hdr(gamma)
        && (shownHeight <= 0 || shownHeight / height > 0.55);

    /// <summary>The least enlargement worth the driver's upscaler: below it, mpv's own scaling is as good.</summary>
    public const double MinUpscale = 1.2;

    /// <summary>
    /// The driver upscaler's scale (<see cref="GraphicsCard.SuperResolution"/>: NVIDIA's RTX Video or Intel's), or 0 when
    /// it doesn't run: on a card that has one, decoding the video itself (d3d11va: the filter works on the card's frames),
    /// SDR, no game running, and not under ArtCNN, which draws anime itself. NVIDIA's on High quality; Intel's, which runs
    /// on the media engine and so suits built-in graphics too, on mains power. Exactly to the size the picture is shown at (the window's video area, less its bars), so mpv doesn't
    /// scale it again: shrinking a sharpened picture by a sliver ripples across it, and the video looked wavy. Only to
    /// enlarge, by <see cref="MinUpscale"/> or more, at most 4 times; a change under 2 pixels keeps <paramref name="previous"/>,
    /// so the filter is rebuilt only when the size really changes.
    /// </summary>
    public static double SuperResolutionScale(GraphicsCard card, bool high, bool onBattery, bool animeUpscaled, string? hwdec, string? gamma, bool gameRunning,
        double videoW, double videoH, double shownW, double shownH, double previous)
    {
        var runs = card.SuperResolution switch { "nvidia" => high, "intel" => !onBattery, _ => false };
        if (!runs || animeUpscaled || hwdec != "d3d11va" || Hdr(gamma) || gameRunning) return 0;
        if (!(videoW > 0 && videoH > 0 && shownW > 0 && shownH > 0)) return 0;
        var scale = Math.Round(Math.Min(4, Math.Min(shownW / videoW, shownH / videoH)), 4);
        if (previous > 0 && Math.Abs(scale - previous) * Math.Max(videoW, videoH) < 2) scale = previous;
        return scale >= MinUpscale ? scale : 0;
    }

    /// <summary>
    /// RTX Video HDR wants this video (<see cref="GraphicsCard.VideoHdr"/>): the driver turns an SDR picture into HDR
    /// in the same video processor as the upscaler, so the same rules hold. An RTX card on High quality, decoding the
    /// video itself, an SDR picture (an HDR one is shown as it is), no game running, not under ArtCNN (whose passes
    /// read the picture's own planes, which the HDR output no longer has), and its switch on in the driver
    /// (<paramref name="driverOn"/>: Exo Launcher's PC › Display or NVIDIA's settings). Whether it runs also takes a
    /// screen in HDR (<see cref="VideoHdrRuns"/>): on an SDR screen mpv would only map it back.
    /// </summary>
    public static bool VideoHdrWanted(GraphicsCard card, bool high, bool animeUpscaled, string? hwdec, string? gamma, bool gameRunning, bool? driverOn) =>
        card.VideoHdr && high && !animeUpscaled && hwdec == "d3d11va" && gamma is { Length: > 0 } and not "auto" && !Hdr(gamma) && !gameRunning && driverOn == true;

    /// <summary>RTX Video HDR runs: wanted, on a screen in HDR now. A screen that can show HDR and has it off is switched
    /// first, by Auto HDR's rules (HdrFilms.ShouldSwitchOn), and the filter follows once it is in HDR.</summary>
    public static bool VideoHdrRuns(bool wanted, bool screenHdrOn) => wanted && screenHdrOn;

    /// <summary>
    /// Smooth motion (Settings › Playback, off unless chosen): mpv shows each frame for exactly its share of the
    /// screen's refreshes (video-sync=display-resample, the video's speed nudged by up to 1% to the screen's rate) and
    /// blends only the refresh where one frame gives way to the next (interpolation with tscale=oversample). That ends
    /// the judder of 24 fps on a 60 Hz screen (3:2 pulldown) without making frames that were never filmed, so films
    /// don't take the soap-opera look. On mains power, with no game running, not turned off for this video by dropped
    /// frames, and only with both rates known and a video no faster than the screen.
    /// </summary>
    public static bool SmoothMotion(bool chosen, bool offForThisVideo, bool onBattery, bool gameRunning, double videoFps, double screenHz) =>
        chosen && !offForThisVideo && !onBattery && !gameRunning
        && videoFps is > 1 and < 1000 && screenHz is > 20 and < 1000 && videoFps <= screenHz * 1.01;

    /// <summary>
    /// The tallest picture worth picking for a stream: any on a strong card that decodes it itself; up to 1080p otherwise,
    /// as built-in graphics or a video the card can't decode (4K in software is what stutters).
    /// </summary>
    public static int HeightCap(GraphicsCard card, string? codec, bool tenBit) => card.Strong && card.Decodes(codec, tenBit) ? int.MaxValue : 1080;

    /// <summary>
    /// The hover pictures are drawn for the screen as the player draws for it, so a picture looks as the video does. The
    /// page is SDR, so on an HDR screen (or before the player has a target) they are plain SDR.
    /// </summary>
    public static (string Trc, string Prim, string Peak) PeekTarget(string? gamma, string? primaries, string? peak) =>
        gamma is { Length: > 0 } and not ("pq" or "hlg" or "auto")
            ? (gamma, primaries is { Length: > 0 } and not "auto" ? primaries : "bt.709",
                double.TryParse(peak, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p is >= 10 and <= 10_000 ? p.ToString("0", CultureInfo.InvariantCulture) : "auto")
            : ("gamma2.2", "bt.709", "auto");
}
