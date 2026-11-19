namespace ExoCinema.Mpv;

/// <summary>
/// What the anime upscaler's warm-up does next, as a pure decision so it can be tested without mpv or a window. The
/// compile runs on its own thread (ShaderWarmup) and never on the UI thread; while it runs, and until the player has
/// been started again on the compiled shaders, anime plays on the driver's upscaler or mpv's own scaling. Switching in
/// is the restart: mpv reads its shader cache only as it starts.
/// </summary>
internal static class WarmupPlan
{
    public enum Step { None, RestartWhenIdle, CompileWhenIdle, CompileNow }

    public readonly record struct Input(
        bool Closing,
        bool HighQualityProfile,
        bool CompileRunning,
        bool PlayerWarm,
        bool ShadersFolderExists,
        bool GameRunning,
        bool Lean,
        bool AlreadyCompiled,
        bool Playing);

    /// <summary>Where one clip's wait stands: started (mpv has opened it), landed (its own playback restarted, so its first
    /// frame, drawn with the shaders, can be taken), or failed (it ended first, or mpv shut down).</summary>
    public enum Landing { Waiting, Started, Landed, Failed }

    // mpv's event ids (MpvNative.EventId), kept here so the rules can be tested without mpv.
    public const int EventShutdown = 1, EventStartFile = 6, EventEndFile = 7, EventPlaybackRestart = 21;

    /// <summary>
    /// The next state after an mpv event. Only events after the clip's own start count: the end of the clip it replaced
    /// comes first, and must not fail the wait (in a 1.4.6 warm-up's replay, every clip after the first failed at once that way).
    /// </summary>
    public static Landing Next(Landing state, int eventId) => (state, eventId) switch
    {
        (Landing.Landed or Landing.Failed, _) => state,
        (_, EventStartFile) => Landing.Started,
        (Landing.Started, EventPlaybackRestart) => Landing.Landed,
        (Landing.Started, EventEndFile or EventShutdown) => Landing.Failed,
        _ => state,
    };

    /// <summary>
    /// Nothing when the warm-up can't or needn't run; the restart once the shaders are in the cache; while any film
    /// plays, the compile waits for it to stop (it never competes with a film: a compile that is running is paused when a
    /// film starts, and resumes at its stop); otherwise the compile now.
    /// </summary>
    public static Step Next(Input i)
    {
        if (i.Closing || !i.HighQualityProfile || i.CompileRunning || i.PlayerWarm || !i.ShadersFolderExists || i.GameRunning || i.Lean)
            return Step.None;
        if (i.AlreadyCompiled) return Step.RestartWhenIdle;
        if (i.Playing) return Step.CompileWhenIdle;
        return Step.CompileNow;
    }
}
