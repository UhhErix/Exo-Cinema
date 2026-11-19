using ExoCinema.Mpv;
namespace ExoCinema.Tests;

/// <summary>
/// The anime upscaler's warm-up: compiled on its own thread and never twice, a film that isn't anime waits for the
/// compile, and until the shaders are compiled anime plays without ArtCNN (on the driver's upscaler or mpv's own).
/// </summary>
public sealed class WarmupPlanTests
{
    private static WarmupPlan.Input Idle() => new(
        Closing: false, HighQualityProfile: true, CompileRunning: false, PlayerWarm: false, ShadersFolderExists: true,
        GameRunning: false, Lean: false, AlreadyCompiled: false, Playing: false);

    [Fact]
    public void Nothing_runs_while_the_window_closes_or_the_profile_is_not_high_quality_or_a_game_runs()
    {
        Assert.Equal(WarmupPlan.Step.None, WarmupPlan.Next(Idle() with { Closing = true }));
        Assert.Equal(WarmupPlan.Step.None, WarmupPlan.Next(Idle() with { HighQualityProfile = false }));
        Assert.Equal(WarmupPlan.Step.None, WarmupPlan.Next(Idle() with { GameRunning = true }));
        Assert.Equal(WarmupPlan.Step.None, WarmupPlan.Next(Idle() with { Lean = true }));
        Assert.Equal(WarmupPlan.Step.None, WarmupPlan.Next(Idle() with { ShadersFolderExists = false }));
    }

    [Fact]
    public void A_compile_is_never_started_twice()
    {
        Assert.Equal(WarmupPlan.Step.None, WarmupPlan.Next(Idle() with { CompileRunning = true }));
        Assert.Equal(WarmupPlan.Step.None, WarmupPlan.Next(Idle() with { CompileRunning = true, AlreadyCompiled = true }));
    }

    [Fact]
    public void A_player_already_on_the_compiled_shaders_needs_nothing()
    {
        Assert.Equal(WarmupPlan.Step.None, WarmupPlan.Next(Idle() with { PlayerWarm = true, AlreadyCompiled = true }));
    }

    [Fact]
    public void Compiled_shaders_in_the_cache_restart_the_player_once_it_is_idle()
    {
        Assert.Equal(WarmupPlan.Step.RestartWhenIdle, WarmupPlan.Next(Idle() with { AlreadyCompiled = true }));
    }

    [Fact]
    public void Not_compiled_compiles_now_when_nothing_plays_or_anime_plays()
    {
        Assert.Equal(WarmupPlan.Step.CompileNow, WarmupPlan.Next(Idle()));
        Assert.Equal(WarmupPlan.Step.CompileNow, WarmupPlan.Next(Idle() with { Playing = false }));
    }
    // (Playing is false once nothing plays: the compile then starts at once, on its own thread.)

    [Fact]
    public void Not_compiled_waits_for_idle_under_a_film_that_is_not_anime()
    {
        Assert.Equal(WarmupPlan.Step.CompileWhenIdle, WarmupPlan.Next(Idle() with { Playing = true }));
    }

    [Fact]
    public void Anime_wanted_but_not_compiled_plays_without_ArtCNN_and_with_it_once_compiled()
    {
        // The same rule the picture uses (PictureRules.AnimeUpscale): 1080p anime on High quality, nothing else wrong.
        Assert.False(PictureRules.AnimeUpscale(anime: true, compiled: false, high: true, offForThisVideo: false,
            gameRunning: false, height: 1080, gamma: "bt.1886", shownHeight: 1080));
        Assert.True(PictureRules.AnimeUpscale(anime: true, compiled: true, high: true, offForThisVideo: false,
            gameRunning: false, height: 1080, gamma: "bt.1886", shownHeight: 1080));
    }

    [Fact]
    public void A_clip_lands_only_after_its_own_start_and_its_playback_restart()
    {
        // The end of the clip it replaced comes before its own start: it must not fail the wait.
        var s = WarmupPlan.Landing.Waiting;
        s = WarmupPlan.Next(s, WarmupPlan.EventEndFile);
        Assert.Equal(WarmupPlan.Landing.Waiting, s);
        s = WarmupPlan.Next(s, WarmupPlan.EventPlaybackRestart);
        Assert.Equal(WarmupPlan.Landing.Waiting, s);
        s = WarmupPlan.Next(s, WarmupPlan.EventStartFile);
        Assert.Equal(WarmupPlan.Landing.Started, s);
        s = WarmupPlan.Next(s, WarmupPlan.EventPlaybackRestart);
        Assert.Equal(WarmupPlan.Landing.Landed, s);
    }

    [Fact]
    public void A_clip_that_ends_or_mpv_that_shuts_down_after_its_start_fails_the_wait()
    {
        Assert.Equal(WarmupPlan.Landing.Failed, WarmupPlan.Next(WarmupPlan.Landing.Started, WarmupPlan.EventEndFile));
        Assert.Equal(WarmupPlan.Landing.Failed, WarmupPlan.Next(WarmupPlan.Landing.Started, WarmupPlan.EventShutdown));
    }

    [Fact]
    public void A_landed_or_failed_wait_stays_that_way()
    {
        Assert.Equal(WarmupPlan.Landing.Landed, WarmupPlan.Next(WarmupPlan.Landing.Landed, WarmupPlan.EventEndFile));
        Assert.Equal(WarmupPlan.Landing.Failed, WarmupPlan.Next(WarmupPlan.Landing.Failed, WarmupPlan.EventPlaybackRestart));
    }

    [Fact]
    public void No_compile_starts_while_a_film_plays_anime_or_not()
    {
        // Playing, the compile waits; it is never started under a film (no CompileNow).
        Assert.Equal(WarmupPlan.Step.CompileWhenIdle, WarmupPlan.Next(Idle() with { Playing = true }));
        Assert.Equal(WarmupPlan.Step.CompileWhenIdle, WarmupPlan.Next(Idle() with { Playing = true, PlayerWarm = false }));
    }

    [Fact]
    public void The_compile_resumes_once_nothing_plays()
    {
        Assert.Equal(WarmupPlan.Step.CompileWhenIdle, WarmupPlan.Next(Idle() with { Playing = true }));
        Assert.Equal(WarmupPlan.Step.CompileNow, WarmupPlan.Next(Idle() with { Playing = false }));
    }

    [Fact]
    public void Shaders_already_compiled_switch_the_upscaler_in_only_as_a_restart_that_waits_for_the_film_to_stop()
    {
        // While a film plays the decision is the restart, pending: the player (MainWindow) restarts when the film stops,
        // so ArtCNN never switches in mid-film.
        Assert.Equal(WarmupPlan.Step.RestartWhenIdle, WarmupPlan.Next(Idle() with { AlreadyCompiled = true, Playing = true }));
        Assert.Equal(WarmupPlan.Step.RestartWhenIdle, WarmupPlan.Next(Idle() with { AlreadyCompiled = true, Playing = false }));
    }
}
