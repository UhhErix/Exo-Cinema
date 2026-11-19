namespace ExoCinema;

/// <summary>
/// Auto HDR for films, as Exo Launcher does it for HDR games, always on: while a title with an HDR picture plays on a screen
/// that can show HDR and has it off, HDR goes on for that screen; when the title ends (or the player closes, or Exo does) HDR
/// goes back off. When the next title is HDR too, HDR stays on between them, so it doesn't go off and on. Only HDR Exo itself
/// switched on is ever switched off, a screen already in HDR or without HDR is never touched, and nothing switches while a
/// game runs. Plain facts in, decisions out (HdrFilmsTests); DisplayHdr and MainWindow.HdrFilms.cs do the Windows and mpv parts.
/// </summary>
internal static class HdrFilms
{
    /// <summary>The marker file beside the settings: the screen Exo switched HDR on for, written before the switch, so a
    /// crash or a kill can't leave HDR on. Removed once HDR is back off.</summary>
    public const string MarkerFile = "hdr-films.json";

    /// <summary>How long the player holds the first frame for the screen to take HDR (DisplayHdr.Find) before it goes on anyway.</summary>
    public const int SwitchWaitMs = 6_000;

    /// <summary>How long HDR stays on after a title ends, for a next HDR title to keep it. Closing the player never waits.</summary>
    public const int RestoreHoldMs = 10_000;

    /// <summary>Tries to take HDR off for a screen: the first, then retries RestoreRetryMs apart, up to RestoreAttempts in all.</summary>
    public const int RestoreAttempts = 6;
    public const int RestoreRetryMs = 1_000;

    /// <summary>Whether try number <paramref name="attempt"/> (1 = the first) is still allowed.</summary>
    public static bool HasRetryLeft(int attempt) => attempt >= 1 && attempt <= RestoreAttempts;

    /// <summary>An HDR title is playing now: a file is loaded (mpv not idle), it is not at its end, and its picture is HDR (gamma pq or hlg).
    /// Read on each tick of a hold, so a next HDR title keeps HDR on even when mpv reports no change of gamma.</summary>
    public static bool HdrTitlePlaying(string? idleActive, string? eofReached, string? gamma) =>
        idleActive == "no" && eofReached == "no" && PictureRules.Hdr(gamma);

    /// <summary>Whether to switch HDR on now for the title's screen.</summary>
    /// <param name="titleIsHdr">mpv reports an HDR transfer (PQ or HLG; HDR10, HDR10+, HLG and Dolby Vision's base layer).</param>
    /// <param name="screenSupportsHdr">Windows says the screen can show HDR.</param>
    /// <param name="screenHdrOn">HDR is already on for the screen.</param>
    /// <param name="gameRunning">A game is running: no display change during it.</param>
    public static bool ShouldSwitchOn(bool titleIsHdr, bool screenSupportsHdr, bool screenHdrOn, bool gameRunning) =>
        titleIsHdr && screenSupportsHdr && !screenHdrOn && !gameRunning;

    /// <summary>What a hold of HDR (after a title ended) does next.</summary>
    public enum HoldStep
    {
        /// <summary>Keep waiting: nothing has come and the hold has time left.</summary>
        Wait,
        /// <summary>Another HDR title started: HDR stays on for it, and the hold ends without a restore.</summary>
        Keep,
        /// <summary>No HDR title followed in time: HDR goes back off.</summary>
        Restore,
    }

    /// <summary>One step of a hold. An HDR title that starts during the hold wins at once; otherwise the hold runs out after RestoreHoldMs.</summary>
    /// <param name="nextTitleIsHdr">An HDR title has started since the hold began.</param>
    /// <param name="heldSinceMs">When the title ended (Environment.TickCount64).</param>
    /// <param name="nowMs">Now, on the same clock.</param>
    public static HoldStep Hold(bool nextTitleIsHdr, long heldSinceMs, long nowMs) =>
        nextTitleIsHdr ? HoldStep.Keep
        : nowMs - heldSinceMs >= RestoreHoldMs ? HoldStep.Restore
        : HoldStep.Wait;

    /// <summary>What to do with a marker left by an earlier run, at start.</summary>
    public enum StartAction { Nothing, TurnOff, Forget }

    /// <param name="markerPresent">The marker file exists: a run switched HDR on and didn't switch it back.</param>
    /// <param name="screenFound">The marker's screen is still connected.</param>
    /// <param name="screenHdrOn">HDR is on for that screen now.</param>
    public static StartAction AtStart(bool markerPresent, bool screenFound, bool screenHdrOn)
    {
        if (!markerPresent) return StartAction.Nothing;
        // The screen is gone, or HDR is already off there: nothing left to undo, the marker goes.
        if (!screenFound || !screenHdrOn) return StartAction.Forget;
        return StartAction.TurnOff;
    }

    /// <summary>Whether a screen's HDR is now in the state asked for (the switch took effect).</summary>
    public static bool Reached(bool wantOn, bool? screenHdrOn) => screenHdrOn == wantOn;

    /// <summary>The marker's content: the screen's device path and its GDI name, as one line of JSON.</summary>
    public static string MarkerJson(string devicePath, string gdiName) =>
        System.Text.Json.JsonSerializer.Serialize(new Marker(devicePath, gdiName));

    public sealed record Marker(string DevicePath, string GdiName);

    public static Marker? ParseMarker(string? json)
    {
        try
        {
            var m = System.Text.Json.JsonSerializer.Deserialize<Marker>(json ?? "");
            return m is { DevicePath.Length: > 0 } ? m : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
