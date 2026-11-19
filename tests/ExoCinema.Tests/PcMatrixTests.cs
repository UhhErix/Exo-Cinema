using System.Text.Json.Nodes;

namespace ExoCinema.Tests;

/// <summary>
/// Every automatic choice the player makes for the hardware, on every simulated PC (SimulatedPcs), for every kind of
/// video, window, power and game state: nothing throws, every value stays in range, nothing made for one maker's cards
/// runs on another's, and each feature runs wherever it can and nowhere it can't.
/// </summary>
public sealed class PcMatrixTests
{
    public static TheoryData<string> Pcs => new(SimulatedPcs.All.Select(p => p.Label));

    private static Pc Find(string label) => SimulatedPcs.All.Single(p => p.Label == label);

    /// <summary>A video as mpv describes it: coded height, display size (DVDs are anamorphic), codec, bit depth, transfer, anime.</summary>
    private sealed record Video(string Label, int H, int Dw, int Dh, string Codec, bool TenBit, string Gamma, bool Anime);

    private static readonly Video[] Videos =
    [
        new("DVD", 480, 853, 480, "mpeg2video", false, "bt.1886", false),
        new("720p anime", 720, 1280, 720, "h264", false, "bt.1886", true),
        new("1080p widescreen film", 800, 1920, 800, "h264", false, "bt.1886", false),
        new("1080p 10-bit HEVC anime", 1080, 1920, 1080, "hevc", true, "bt.1886", true),
        new("1080p Hi10P anime", 1080, 1920, 1080, "h264", true, "bt.1886", true),
        new("1080p VP9", 1080, 1920, 1080, "vp9", false, "bt.1886", false),
        new("1440p AV1 anime", 1440, 2560, 1440, "av1", true, "bt.1886", true),
        new("4K HDR10 HEVC", 2160, 3840, 2160, "hevc", true, "pq", false),
        new("4K HLG", 2160, 3840, 2160, "hevc", true, "hlg", false),
        new("4K SDR AV1", 2160, 3840, 2160, "av1", false, "bt.1886", false),
        new("4K HDR anime", 2160, 3840, 2160, "hevc", true, "pq", true),
    ];

    /// <summary>The window's sizes on a screen: full screen, maximized (less the taskbar), half, small, minimized.</summary>
    private static IEnumerable<(int W, int H)> Windows(Screen s) =>
        [(s.W, s.H), (s.W, s.H - 48), (s.W / 2, s.H / 2), (640, 360), (0, 0)];

    [Theory, MemberData(nameof(Pcs))]
    public void Every_choice_holds(string label)
    {
        var pc = Find(label);
        foreach (var battery in pc.Laptop ? new[] { false, true } : [false])
        foreach (var choice in new int?[] { null, 1, 2 })
        {
            var preference = PictureRules.AdapterPreference(battery, choice);
            Assert.InRange(preference, 1, 2);
            if (choice != null) Assert.Equal(choice.Value, preference); // the user's choice in Windows wins
            else Assert.Equal(battery ? 1 : 2, preference);
            var card = pc.Card(preference);
            Assert.False(string.IsNullOrEmpty(card.ToString()));
            foreach (var lighter in new[] { false, true })
            {
                var high = PictureRules.High(card, battery, lighter);
                if (high) Assert.True(card.Strong && !card.Integrated && card.Vendor != "other" && !battery && !lighter);
                foreach (var screen in pc.Screens)
                foreach (var window in Windows(screen))
                foreach (var video in Videos)
                    Picture(card, high, battery, screen, window, video);
            }
        }
    }

    private static void Picture(GraphicsCard card, bool high, bool battery, Screen screen, (int W, int H) window, Video v)
    {
        var hwdec = card.Decodes(v.Codec, v.TenBit) ? "d3d11va" : "no";
        // mpv's osd-dimensions less its bars: the picture fitted into the window, its aspect kept.
        var fit = window.W > 0 && window.H > 0 ? Math.Min((double)window.W / v.Dw, (double)window.H / v.Dh) : 0;
        double shownW = Math.Round(v.Dw * fit), shownH = Math.Round(v.Dh * fit);
        var hdr = v.Gamma is "pq" or "hlg";
        foreach (var game in new[] { false, true })
        foreach (var compiled in new[] { true, false })
        foreach (var off in new[] { false, true })
        {
            var anime = PictureRules.AnimeUpscale(v.Anime, compiled, high, off, game, v.H, v.Gamma, shownH);
            if (anime) Assert.True(v.Anime && compiled && high && card.Strong && !off && !game && !hdr && v.H <= 1600, $"ArtCNN where it can't run: {card.Name}, {v.Label}");
            if (v.Anime && compiled && high && !off && !game && !hdr && v.H <= 1600 && shownH / v.H > 0.55) Assert.True(anime, $"ArtCNN missing: {card.Name}, {v.Label} in {window}");

            foreach (var previous in new[] { 0, 1.5, 2.0001 })
            {
                var scale = PictureRules.SuperResolutionScale(card, high, battery, anime, hwdec, v.Gamma, game, v.Dw, v.Dh, shownW, shownH, previous);
                Assert.True(double.IsFinite(scale));
                if (scale == 0) continue;
                Assert.InRange(scale, PictureRules.MinUpscale, 4);
                // The driver's upscaler only where the card has one (NVIDIA RTX on High quality, Intel Xe/Arc on mains), on
                // frames the card decoded, SDR, not under ArtCNN or a game.
                Assert.True(card.Rtx || card.Vendor == "intel" && card.Decoders.HasFlag(VideoDecoders.Av1), $"driver upscaling on {card.Name}");
                Assert.True(hwdec == "d3d11va" && !hdr && (card.Rtx ? high : !battery) && !anime && !game, $"driver upscaling where it can't run: {card.Name}, {v.Label}");
                // Never bigger than the picture's place by more than the 2-pixel hysteresis.
                Assert.True(scale * v.Dw <= shownW + 2.5 && scale * v.Dh <= shownH + 2.5, $"RTX Video overshoots: x{scale} for {v.Label} in {window}");
            }
            // ...and wherever it can run, it does.
            var can = card.SuperResolution is { } mode && (mode == "nvidia" ? high : !battery) && hwdec == "d3d11va" && !hdr && !anime && !game && fit >= PictureRules.MinUpscale + 0.001;
            var fresh = PictureRules.SuperResolutionScale(card, high, battery, anime, hwdec, v.Gamma, game, v.Dw, v.Dh, shownW, shownH, 0);
            if (can) Assert.True(Math.Abs(fresh - Math.Min(4, Math.Min(shownW / v.Dw, shownH / v.Dh))) < 0.001, $"RTX Video missing: {card.Name}, {v.Label} in {window}");

            // RTX Video HDR: only an RTX card, on High quality, an SDR picture the card decoded, with no game, not under
            // ArtCNN, its driver switch on; and it runs only on a screen in HDR. Wherever all that holds, it runs.
            foreach (var driverOn in new bool?[] { true, false, null })
            {
                var wanted = PictureRules.VideoHdrWanted(card, high, anime, hwdec, v.Gamma, game, driverOn);
                if (wanted) Assert.True(card.Rtx && card.Vendor == "nvidia" && high && !hdr && hwdec == "d3d11va" && !game && !anime && driverOn == true, $"RTX Video HDR where it can't run: {card.Name}, {v.Label}");
                if (card.Rtx && high && !hdr && hwdec == "d3d11va" && !game && !anime && driverOn == true) Assert.True(wanted, $"RTX Video HDR missing: {card.Name}, {v.Label}");
                Assert.Equal(wanted && screen.Hdr, PictureRules.VideoHdrRuns(wanted, screen.Hdr));
                if (card.Vendor != "nvidia") Assert.False(wanted, $"RTX Video HDR on {card.Name}");
            }
        }

        // Smooth motion: only when chosen, never on battery, during a game or once a video turned it off, and only with a
        // video no faster than the screen.
        foreach (var fps in new[] { 0, 23.976, 24, 25, 29.97, 50, 59.94, 60, 120, 240 })
        foreach (var chosen in new[] { true, false })
        foreach (var off in new[] { false, true })
        foreach (var game in new[] { false, true })
        {
            var smooth = PictureRules.SmoothMotion(chosen, off, battery, game, fps, screen.Hz);
            if (smooth) Assert.True(chosen && !off && !battery && !game && fps > 0 && fps <= screen.Hz * 1.01, $"smooth motion where it can't run: {fps} fps on {screen.Hz} Hz");
            if (chosen && !off && !battery && !game && fps > 0 && fps <= screen.Hz) Assert.True(smooth, $"smooth motion missing: {fps} fps on {screen.Hz} Hz");
        }

        var cap = PictureRules.HeightCap(card, v.Codec, v.TenBit);
        Assert.True(cap >= 1080);
        if (cap > 1080) Assert.True(card.Strong && card.Decodes(v.Codec, v.TenBit));

        // The hover pictures: SDR for the page whatever the screen, with a peak mpv takes.
        var target = screen.Hdr ? PictureRules.PeekTarget("pq", "bt.2020", "1000") : PictureRules.PeekTarget("bt.1886", "bt.709", "203");
        Assert.DoesNotContain(target.Trc, new[] { "pq", "hlg", "auto" });
        Assert.True(target.Peak == "auto" || double.Parse(target.Peak, System.Globalization.CultureInfo.InvariantCulture) is >= 10 and <= 10_000);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("auto", "auto", "auto")]
    [InlineData("", "", "")]
    [InlineData("hlg", "bt.2020", "1000")]
    [InlineData("gamma2.2", "display-p3", "NaN")]
    [InlineData("srgb", "bt.709", "Infinity")]
    [InlineData("srgb", "bt.709", "1e9")]
    [InlineData("srgb", "bt.709", "3")]
    public void Hover_pictures_take_any_target(string? gamma, string? primaries, string? peak)
    {
        var (trc, prim, p) = PictureRules.PeekTarget(gamma, primaries, peak);
        Assert.DoesNotContain(trc, new[] { "pq", "hlg", "auto", "" });
        Assert.False(string.IsNullOrEmpty(prim));
        Assert.True(p == "auto" || double.Parse(p, System.Globalization.CultureInfo.InvariantCulture) is >= 10 and <= 10_000, p);
    }

    // The cards a name alone told wrong before the player asked Direct3D.
    [Fact]
    public void Built_in_graphics_named_like_cards_are_built_in()
    {
        Assert.False(SimulatedPcs.MeteorLakeArc.Strong); // "Arc" is Intel's built-in graphics too
        Assert.False(SimulatedPcs.LunarLakeArc.Strong);
        Assert.False(SimulatedPcs.Vega11.Strong);       // "RX Vega" is AMD's built-in graphics
        Assert.False(SimulatedPcs.AllyZ1.Strong);       // 4 GB set aside is still shared memory
        Assert.True(SimulatedPcs.ProW6800.Strong);      // no "RX" in a Radeon PRO's name
        Assert.True(SimulatedPcs.ArcA770.Strong);
        Assert.False(SimulatedPcs.Gt710.Strong);        // a dedicated card from 2014 with 2 GB
        Assert.True(SimulatedPcs.Gtx970.Strong);
        Assert.False(GraphicsCard.Unknown.Strong);
        Assert.False(SimulatedPcs.VmwareSvga.Strong);
    }

    [Fact]
    public void A_hybrid_laptop_draws_on_its_geforce_on_mains_only()
    {
        var laptop = SimulatedPcs.All.Single(p => p.Fast == SimulatedPcs.Rtx3060Laptop);
        var mains = laptop.Card(PictureRules.AdapterPreference(false, null));
        var battery = laptop.Card(PictureRules.AdapterPreference(true, null));
        Assert.True(mains.Rtx && PictureRules.High(mains, false, false));
        Assert.True(PictureRules.SuperResolutionScale(mains, true, false, false, "d3d11va", "bt.1886", false, 1280, 720, 1920, 1080, 0) is 1.5);
        Assert.False(PictureRules.High(battery, true, false));
        Assert.Equal(SimulatedPcs.IrisXe, laptop.Card(PictureRules.AdapterPreference(false, 1))); // power saving chosen in Windows
    }

    [Fact]
    public void Driver_upscaling_follows_the_card()
    {
        // No driver upscaler mpv can call: GTX (no RTX Video), AMD (its Video Upscale is browsers only), UHD 620/630 (pre-Xe).
        foreach (var card in new[] { SimulatedPcs.Gtx1060, SimulatedPcs.Gtx1650, SimulatedPcs.Rx7900Xtx, SimulatedPcs.Radeon780M, SimulatedPcs.Uhd630, SimulatedPcs.Uhd620 })
        {
            Assert.Null(card.SuperResolution);
            Assert.Equal(0, PictureRules.SuperResolutionScale(card, true, false, false, "d3d11va", "bt.1886", false, 1280, 720, 1920, 1080, 0));
        }
        // NVIDIA's on RTX cards, on High quality.
        foreach (var card in new[] { SimulatedPcs.Rtx2060, SimulatedPcs.Rtx3070, SimulatedPcs.Rtx5090, SimulatedPcs.RtxA2000 })
        {
            Assert.Equal("nvidia", card.SuperResolution);
            Assert.Equal(1.5, PictureRules.SuperResolutionScale(card, true, false, false, "d3d11va", "bt.1886", false, 1280, 720, 1920, 1080, 0));
            Assert.Equal(0, PictureRules.SuperResolutionScale(card, false, false, false, "d3d11va", "bt.1886", false, 1280, 720, 1920, 1080, 0));
        }
        // RTX Video HDR on RTX cards only: GTX, AMD and Intel have no SDR-to-HDR a player can call.
        foreach (var card in new[] { SimulatedPcs.Gtx1060, SimulatedPcs.Gtx1650, SimulatedPcs.Rx7900Xtx, SimulatedPcs.Radeon780M, SimulatedPcs.ArcA770, SimulatedPcs.IrisXe })
        {
            Assert.False(card.VideoHdr);
            Assert.False(PictureRules.VideoHdrWanted(card, true, false, "d3d11va", "bt.1886", false, true));
        }
        foreach (var card in new[] { SimulatedPcs.Rtx2060, SimulatedPcs.Rtx3070, SimulatedPcs.Rtx5090 })
        {
            Assert.True(PictureRules.VideoHdrWanted(card, true, false, "d3d11va", "bt.1886", false, true));
            Assert.False(PictureRules.VideoHdrWanted(card, true, false, "d3d11va", "pq", false, true));    // already HDR
            Assert.False(PictureRules.VideoHdrWanted(card, true, false, "d3d11va", null, false, true));    // picture not known yet
            Assert.False(PictureRules.VideoHdrWanted(card, false, false, "d3d11va", "bt.1886", false, true)); // Balanced
            Assert.False(PictureRules.VideoHdrWanted(card, true, false, "d3d11va", "bt.1886", false, false)); // driver switch off
            Assert.False(PictureRules.VideoHdrWanted(card, true, false, "no", "bt.1886", false, true));    // decoded in software
            Assert.False(PictureRules.VideoHdrWanted(card, true, true, "d3d11va", "bt.1886", false, true)); // under ArtCNN
            Assert.False(PictureRules.VideoHdrWanted(card, true, false, "d3d11va", "bt.1886", true, true)); // a game runs
            Assert.False(PictureRules.VideoHdrRuns(true, screenHdrOn: false));
        }
        // Intel's on Xe and Arc, built in too (it isn't High quality there), on mains power only.
        foreach (var card in new[] { SimulatedPcs.ArcA770, SimulatedPcs.ArcB580, SimulatedPcs.IrisXe, SimulatedPcs.MeteorLakeArc, SimulatedPcs.LunarLakeArc })
        {
            Assert.Equal("intel", card.SuperResolution);
            Assert.Equal(1.5, PictureRules.SuperResolutionScale(card, false, false, false, "d3d11va", "bt.1886", false, 1280, 720, 1920, 1080, 0));
            Assert.Equal(0, PictureRules.SuperResolutionScale(card, false, true, false, "d3d11va", "bt.1886", false, 1280, 720, 1920, 1080, 0));
        }
    }

    [Fact]
    public void Smooth_motion_is_chosen_and_steps_aside()
    {
        Assert.True(PictureRules.SmoothMotion(true, false, false, false, 23.976, 60));
        Assert.True(PictureRules.SmoothMotion(true, false, false, false, 23.976, 164.917));
        Assert.True(PictureRules.SmoothMotion(true, false, false, false, 60, 59.94));   // within mpv's 1% nudge
        Assert.False(PictureRules.SmoothMotion(false, false, false, false, 23.976, 60)); // not chosen: off by default
        Assert.False(PictureRules.SmoothMotion(true, true, false, false, 23.976, 60));   // off for this video (dropped frames)
        Assert.False(PictureRules.SmoothMotion(true, false, true, false, 23.976, 60));   // on battery
        Assert.False(PictureRules.SmoothMotion(true, false, false, true, 23.976, 60));   // a game runs
        Assert.False(PictureRules.SmoothMotion(true, false, false, false, 0, 60));       // the video's rate not known yet
        Assert.False(PictureRules.SmoothMotion(true, false, false, false, 23.976, 0));   // the screen's rate not known
        Assert.False(PictureRules.SmoothMotion(true, false, false, false, 120, 60));     // faster than the screen
        Assert.False(PictureRules.SmoothMotion(true, false, false, false, double.NaN, 60));
    }

}
