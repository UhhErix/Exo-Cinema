namespace ExoCinema.Tests;

/// <summary>When Auto HDR for films switches a screen on, what it leaves alone, and what a marker from a crash undoes (HdrFilms).</summary>
public sealed class HdrFilmsTests
{
    [Fact]
    public void A_HDR_title_on_an_HDR_capable_screen_that_is_off_switches_it_on()
    {
        Assert.True(HdrFilms.ShouldSwitchOn(titleIsHdr: true, screenSupportsHdr: true, screenHdrOn: false, gameRunning: false));
    }

    [Fact]
    public void An_SDR_title_never_switches_HDR_on()
    {
        Assert.False(HdrFilms.ShouldSwitchOn(titleIsHdr: false, screenSupportsHdr: true, screenHdrOn: false, gameRunning: false));
    }

    [Fact]
    public void A_screen_without_HDR_is_never_touched()
    {
        Assert.False(HdrFilms.ShouldSwitchOn(titleIsHdr: true, screenSupportsHdr: false, screenHdrOn: false, gameRunning: false));
    }

    [Fact]
    public void A_screen_already_in_HDR_is_left_as_it_is()
    {
        // It was on before Exo came, so Exo must not switch it, and (through RestoreFilmHdr) must not switch it back off either.
        Assert.False(HdrFilms.ShouldSwitchOn(titleIsHdr: true, screenSupportsHdr: true, screenHdrOn: true, gameRunning: false));
    }

    [Fact]
    public void Nothing_switches_while_a_game_runs()
    {
        Assert.False(HdrFilms.ShouldSwitchOn(titleIsHdr: true, screenSupportsHdr: true, screenHdrOn: false, gameRunning: true));
    }

    [Fact]
    public void After_a_title_ends_HDR_is_held_for_ten_seconds_then_restored()
    {
        Assert.Equal(10_000, HdrFilms.RestoreHoldMs);
        Assert.Equal(HdrFilms.HoldStep.Wait, HdrFilms.Hold(nextTitleIsHdr: false, heldSinceMs: 1_000, nowMs: 1_000));
        Assert.Equal(HdrFilms.HoldStep.Wait, HdrFilms.Hold(nextTitleIsHdr: false, heldSinceMs: 1_000, nowMs: 10_999));
        Assert.Equal(HdrFilms.HoldStep.Restore, HdrFilms.Hold(nextTitleIsHdr: false, heldSinceMs: 1_000, nowMs: 11_000));
        Assert.Equal(HdrFilms.HoldStep.Restore, HdrFilms.Hold(nextTitleIsHdr: false, heldSinceMs: 1_000, nowMs: 60_000));
    }

    [Fact]
    public void An_HDR_title_that_starts_during_the_hold_keeps_HDR_on_at_once()
    {
        Assert.Equal(HdrFilms.HoldStep.Keep, HdrFilms.Hold(nextTitleIsHdr: true, heldSinceMs: 1_000, nowMs: 1_001));
        Assert.Equal(HdrFilms.HoldStep.Keep, HdrFilms.Hold(nextTitleIsHdr: true, heldSinceMs: 1_000, nowMs: 60_000));
    }

    [Theory]
    [InlineData("no", "no", "pq")]
    [InlineData("no", "no", "hlg")]
    public void An_HDR_title_playing_now_is_read_from_mpv_on_each_tick(string idle, string eof, string gamma) =>
        Assert.True(HdrFilms.HdrTitlePlaying(idle, eof, gamma));

    [Theory]
    [InlineData("yes", "no", "pq")]   // idle: nothing loaded
    [InlineData(null, "no", "pq")]    // mpv not answering
    [InlineData("no", "yes", "pq")]   // the title has ended: its gamma is still there
    [InlineData("no", null, "pq")]
    [InlineData("no", "no", "bt.1886")]
    [InlineData("no", "no", "srgb")]
    [InlineData("no", "no", null)]
    public void A_title_that_is_idle_ended_or_SDR_is_not_playing_HDR(string? idle, string? eof, string? gamma) =>
        Assert.False(HdrFilms.HdrTitlePlaying(idle, eof, gamma));

    [Fact]
    public void An_SDR_film_turned_HDR_by_RTX_Video_HDR_keeps_the_screen_in_HDR_like_an_HDR_film()
    {
        // The next episode is SDR, but RTX Video HDR is turning it into HDR: the hold keeps HDR on for it.
        var next = HdrFilms.HdrTitlePlaying("no", "no", "bt.1886", sdrToHdr: true);
        Assert.True(next);
        Assert.Equal(HdrFilms.HoldStep.Keep, HdrFilms.Hold(next, heldSinceMs: 1_000, nowMs: 3_000));
        // Without it, an SDR title lets HDR go back off; and an ended or idle one never holds it.
        Assert.False(HdrFilms.HdrTitlePlaying("no", "no", "bt.1886", sdrToHdr: false));
        Assert.False(HdrFilms.HdrTitlePlaying("no", "yes", "bt.1886", sdrToHdr: true));
        Assert.False(HdrFilms.HdrTitlePlaying("yes", "no", "bt.1886", sdrToHdr: true));
        // The switch itself follows the same rules as for an HDR film: never during a game, never a screen already in HDR.
        Assert.True(HdrFilms.ShouldSwitchOn(titleIsHdr: true, screenSupportsHdr: true, screenHdrOn: false, gameRunning: false));
        Assert.False(HdrFilms.ShouldSwitchOn(titleIsHdr: true, screenSupportsHdr: true, screenHdrOn: false, gameRunning: true));
    }

    [Fact]
    public void Two_HDR_films_in_a_row_with_the_same_gamma_keep_HDR_while_the_next_plays()
    {
        // The first film ended (eof yes, gamma still pq): the hold waits, and nothing is restored at 10 s.
        var firstEnded = HdrFilms.HdrTitlePlaying("no", "yes", "pq");
        Assert.False(firstEnded);
        Assert.Equal(HdrFilms.HoldStep.Wait, HdrFilms.Hold(firstEnded, heldSinceMs: 1_000, nowMs: 5_000));
        // The second, HDR too, starts with no change of gamma (no event): the tick reads it and keeps HDR on.
        var secondPlaying = HdrFilms.HdrTitlePlaying("no", "no", "pq");
        Assert.Equal(HdrFilms.HoldStep.Keep, HdrFilms.Hold(secondPlaying, heldSinceMs: 1_000, nowMs: 11_500));
    }

    [Fact]
    public void A_refused_restore_is_tried_again_within_a_bounded_number_of_tries()
    {
        Assert.True(HdrFilms.HasRetryLeft(1));
        Assert.True(HdrFilms.HasRetryLeft(HdrFilms.RestoreAttempts));
        Assert.False(HdrFilms.HasRetryLeft(HdrFilms.RestoreAttempts + 1));
        Assert.False(HdrFilms.HasRetryLeft(0));
        Assert.InRange(HdrFilms.RestoreRetryMs * HdrFilms.RestoreAttempts, 1_000, 30_000);
    }

    [Fact]
    public void Without_a_marker_nothing_is_done_at_start()
    {
        Assert.Equal(HdrFilms.StartAction.Nothing, HdrFilms.AtStart(markerPresent: false, screenFound: true, screenHdrOn: true));
    }

    [Fact]
    public void A_crash_left_HDR_on_is_turned_off_at_start()
    {
        Assert.Equal(HdrFilms.StartAction.TurnOff, HdrFilms.AtStart(markerPresent: true, screenFound: true, screenHdrOn: true));
    }

    [Fact]
    public void A_marker_for_a_screen_already_off_or_gone_is_only_forgotten()
    {
        Assert.Equal(HdrFilms.StartAction.Forget, HdrFilms.AtStart(markerPresent: true, screenFound: true, screenHdrOn: false));
        Assert.Equal(HdrFilms.StartAction.Forget, HdrFilms.AtStart(markerPresent: true, screenFound: false, screenHdrOn: false));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, null, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, null, false)]
    public void The_switch_is_reached_only_when_the_screen_reports_it(bool wantOn, bool? reported, bool expected) =>
        Assert.Equal(expected, HdrFilms.Reached(wantOn, reported));

    [Fact]
    public void The_marker_round_trips_and_a_bad_one_is_ignored()
    {
        var json = HdrFilms.MarkerJson(@"\\?\DISPLAY#ACI27A1#5&1a2b&0&UID4352#{guid}", @"\\.\DISPLAY1");
        var back = HdrFilms.ParseMarker(json);
        Assert.Equal(@"\\?\DISPLAY#ACI27A1#5&1a2b&0&UID4352#{guid}", back!.DevicePath);
        Assert.Equal(@"\\.\DISPLAY1", back.GdiName);
        Assert.Null(HdrFilms.ParseMarker("not json"));
        Assert.Null(HdrFilms.ParseMarker("{}"));
        Assert.Null(HdrFilms.ParseMarker(null));
    }
}
