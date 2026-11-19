using ExoCinema.Mpv;

namespace ExoCinema.Tests;

/// <summary>What the play plan decides on sample PCs, from the facts the player reads (PlaybackPlan).</summary>
public sealed class PlaybackPlanTests
{
    private const long Gb = 1L << 30;

    private static PlaybackPlan.Input Pc(GraphicsCard card, double totalGb, double freeGb, bool battery = false, bool game = false,
        double? link = null, double? need = null, double? bytesPerSecond = null, double remaining = 0, double? hz = null, bool? hdr = null) =>
        new(card, battery, game, (long)(totalGb * Gb), (long)(freeGb * Gb), link, link is null ? null : 0.5, need, bytesPerSecond, remaining, hz, hdr);

    [Fact]
    public void Eight_GB_laptop_on_battery_plays_lighter_with_a_small_read_ahead()
    {
        var plan = PlaybackPlan.Decide(Pc(SimulatedPcs.IrisXe, 8, 3, battery: true, link: 40, need: 20, bytesPerSecond: 2.5e6, remaining: 3600, hz: 60, hdr: false));
        Assert.Equal(PlaybackPlan.Fit.Fits, plan.Source);
        Assert.False(plan.HighQuality);
        Assert.Equal((long)(1.5 * Gb), plan.ReadAhead.ForwardBytes);
        Assert.False(plan.ReadAhead.Low);
        Assert.Equal(StartBuffer.MinSeconds, plan.Start.WaitSeconds);
        Assert.Contains("on battery", plan.Line);
        Assert.Contains("standard picture", plan.Line);
    }

    [Fact]
    public void Eight_GB_laptop_with_memory_short_drops_to_the_floors()
    {
        var plan = PlaybackPlan.Decide(Pc(SimulatedPcs.Uhd620, 8, 0.5, battery: true));
        Assert.True(plan.ReadAhead.Low);
        Assert.Equal(ReadAhead.Floor, plan.ReadAhead.ForwardBytes);
        Assert.Equal(ReadAhead.MinBack, plan.ReadAhead.BackBytes);
        Assert.Contains("memory short", plan.Line);
    }

    [Fact]
    public void Sixteen_GB_desktop_with_a_dedicated_card_plays_high_quality_on_mains()
    {
        var plan = PlaybackPlan.Decide(Pc(SimulatedPcs.Gtx1060, 16, 9, link: 300, need: 80, bytesPerSecond: 10e6, remaining: 5400, hz: 144, hdr: false));
        Assert.True(plan.HighQuality);
        Assert.Equal(PlaybackPlan.Fit.Fits, plan.Source);
        Assert.Equal((long)(4.5 * Gb), plan.ReadAhead.ForwardBytes);
        Assert.Equal(StartBuffer.MinSeconds, plan.Start.WaitSeconds);
        Assert.Contains("on mains", plan.Line);
        Assert.Contains("high-quality picture", plan.Line);
    }

    [Fact]
    public void Thirty_two_GB_desktop_with_an_RTX_card_reads_far_ahead_on_a_HDR_screen()
    {
        var plan = PlaybackPlan.Decide(Pc(SimulatedPcs.Rtx4090, 32, 20, link: 900, need: 60, bytesPerSecond: 7.5e6, remaining: 7200, hz: 144, hdr: true));
        Assert.True(plan.HighQuality);
        Assert.Equal(PlaybackPlan.Fit.Fits, plan.Source);
        Assert.Equal(10 * Gb, plan.ReadAhead.ForwardBytes);
        Assert.Contains("HDR screen", plan.Line);
        Assert.Contains("144 Hz", plan.Line);
    }

    [Fact]
    public void Slow_link_marks_the_version_too_slow_and_caps_the_wait()
    {
        var plan = PlaybackPlan.Decide(Pc(SimulatedPcs.Gtx1060, 16, 9, link: 5, need: 40, bytesPerSecond: 5e6, remaining: 5400));
        Assert.Equal(PlaybackPlan.Fit.TooSlow, plan.Source);
        Assert.True(plan.Start.TooSlow);
        Assert.Equal(StartBuffer.MaxSeconds, plan.Start.WaitSeconds);
        Assert.Contains("too slow", plan.Line);
    }

    [Fact]
    public void Gigabit_link_fits_a_remux_with_the_shortest_wait()
    {
        var plan = PlaybackPlan.Decide(Pc(SimulatedPcs.Rtx3070, 32, 20, link: 940, need: 80, bytesPerSecond: 10e6, remaining: 6000));
        Assert.Equal(PlaybackPlan.Fit.Fits, plan.Source);
        Assert.False(plan.Start.TooSlow);
        Assert.Equal(StartBuffer.MinSeconds, plan.Start.WaitSeconds);
        Assert.Contains("940.0 Mb/s", plan.Line);
    }

    [Fact]
    public void Unmeasured_link_is_not_judged_and_waits_the_default()
    {
        var plan = PlaybackPlan.Decide(Pc(SimulatedPcs.Rtx3070, 16, 9, need: 80, bytesPerSecond: 10e6, remaining: 6000));
        Assert.Equal(PlaybackPlan.Fit.Unknown, plan.Source);
        Assert.Equal(StartBuffer.UnknownSeconds, plan.Start.WaitSeconds);
        Assert.Contains("link not measured", plan.Line);
        Assert.Contains("not judged yet", plan.Line);
    }

    [Fact]
    public void Before_the_file_is_open_the_wait_is_the_default_even_on_a_known_link()
    {
        var plan = PlaybackPlan.Decide(Pc(SimulatedPcs.Rtx3070, 16, 9, link: 300));
        Assert.Equal(StartBuffer.UnknownSeconds, plan.Start.WaitSeconds);
        Assert.Equal(PlaybackPlan.Fit.Unknown, plan.Source);
    }

    [Fact]
    public void An_unknown_card_keeps_the_standard_picture_and_a_game_is_only_named()
    {
        Assert.False(PlaybackPlan.Decide(Pc(GraphicsCard.Unknown, 16, 9)).HighQuality);
        // A game running steps the upscalers aside (PictureRules), which the plan reports rather than changes.
        Assert.Contains("a game running", PlaybackPlan.Decide(Pc(SimulatedPcs.Rtx4090, 32, 20, game: true)).Line);
    }

    [Fact]
    public void The_line_states_facts_only_with_no_address_or_token()
    {
        var line = PlaybackPlan.Decide(Pc(SimulatedPcs.Rtx4090, 32, 20, link: 900, need: 60, bytesPerSecond: 7.5e6, remaining: 7200, hz: 144, hdr: true)).Line;
        Assert.StartsWith("plan: ", line);
        Assert.DoesNotContain("http", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\n', line);
    }
}
