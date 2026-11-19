using System.Globalization;
using ExoCinema.Mpv;

namespace ExoCinema;

/// <summary>
/// What the player picks for a play, from the facts of this PC and this connection, in one place: whether the chosen
/// version fits the link (LinkFit), how far ahead and behind to read (ReadAhead), how long to wait before the start
/// (StartBuffer), and the picture's quality preset (PictureRules.High). Each rule is the existing one; this only gathers
/// the facts and writes the decisions down as one plain line for app.log, so a report can say what was picked and why.
/// Plain values in, plain values out (PlaybackPlanTests); the player reads the facts (MainWindow.Player.cs).
/// </summary>
internal static class PlaybackPlan
{
    /// <summary>Whether the version chosen fits the link: Unknown when either speed isn't known yet.</summary>
    public enum Fit { Unknown, Fits, TooSlow }

    /// <param name="Card">The graphics card the picture is drawn on.</param>
    /// <param name="OnBattery">Running on battery (the picture goes lighter).</param>
    /// <param name="GameRunning">A game runs (the upscalers step aside; no display change).</param>
    /// <param name="TotalBytes">The PC's physical memory.</param>
    /// <param name="FreeBytes">The physical memory free now.</param>
    /// <param name="LinkMbps">The link's speed to plan with (LinkSpeed.Fresh), or null.</param>
    /// <param name="LinkAgeDays">How old that measure is, in days, for the log; null when not measured.</param>
    /// <param name="NeedMbps">The chosen version's average bitrate, or null when the play doesn't say.</param>
    /// <param name="BytesPerSecond">The file's bytes a second of video, or null before the file is open.</param>
    /// <param name="RemainingSeconds">How much of the film is left to play.</param>
    /// <param name="RefreshHz">The screen's refresh rate, or null.</param>
    /// <param name="DisplayHdr">Whether the screen can show HDR, or null when Windows doesn't say.</param>
    public readonly record struct Input(GraphicsCard Card, bool OnBattery, bool GameRunning, long TotalBytes, long FreeBytes,
        double? LinkMbps, double? LinkAgeDays, double? NeedMbps, double? BytesPerSecond, double RemainingSeconds,
        double? RefreshHz, bool? DisplayHdr);

    /// <param name="Source">The version against the link.</param>
    /// <param name="ReadAhead">mpv's demuxer-max-bytes and back bytes.</param>
    /// <param name="Start">The wait before the start and after a dry spell, in seconds, and whether the link is too slow.</param>
    /// <param name="HighQuality">The high-quality picture preset (mpv's profile and the scalers).</param>
    /// <param name="Line">One line of plain facts for app.log.</param>
    public readonly record struct Result(Fit Source, ReadAhead.Plan ReadAhead, StartBuffer.Plan Start, bool HighQuality, string Line);

    public static Result Decide(Input i)
    {
        var readAhead = ReadAhead.Size(i.TotalBytes, i.FreeBytes);
        var fit = i.NeedMbps is not { } need || i.LinkMbps is not { } link || link <= 0 ? Fit.Unknown
            : LinkFit.Fits(need, link) ? Fit.Fits : Fit.TooSlow;
        // The wait needs the file's rate and the link's speed; until both are known it is the default, as the player has always had.
        var start = i.BytesPerSecond is { } bps && bps > 0 && i.LinkMbps is { } speed && speed > 0
            ? StartBuffer.Estimate(bps, speed * 1e6 / 8, i.RemainingSeconds)
            : new StartBuffer.Plan(StartBuffer.UnknownSeconds, false);
        var high = PictureRules.High(i.Card, i.OnBattery, lighterForThisVideo: false);
        return new Result(fit, readAhead, start, high, Describe(i, readAhead, start, high, fit));
    }

    private static string Describe(Input i, ReadAhead.Plan readAhead, StartBuffer.Plan start, bool high, Fit fit)
    {
        var inv = CultureInfo.InvariantCulture;
        string gb(long bytes) => (bytes / (double)ReadAhead.Gib).ToString("0.0", inv);
        var card = i.Card == GraphicsCard.Unknown ? "graphics unknown" : $"{i.Card.Name} ({(i.Card.Integrated ? "built in" : $"{i.Card.VramMb / 1024.0:0.#} GB")})";
        var link = i.LinkMbps is { } l
            ? $"link {l.ToString("0.0", inv)} Mb/s{(i.LinkAgeDays is { } age ? $", measured {age.ToString("0", inv)} d ago" : "")}"
            : "link not measured";
        var need = i.NeedMbps is { } n ? $"version {n.ToString("0.0", inv)} Mb/s" : "version bitrate unknown";
        var fitWord = fit switch { Fit.Fits => "fits", Fit.TooSlow => "too slow", _ => "not judged yet" };
        var wait = $"start wait {start.WaitSeconds.ToString("0", inv)} s{(start.TooSlow ? " (too slow even at the longest wait)" : "")}";
        var screen = i.RefreshHz is { } hz && hz > 0 ? $"{hz.ToString("0", inv)} Hz" : "refresh unknown";
        var hdr = i.DisplayHdr is { } h ? (h ? "HDR screen" : "SDR screen") : "HDR unknown";
        return $"plan: {card}, {(i.OnBattery ? "on battery" : "on mains")}, {(i.GameRunning ? "a game running" : "no game running")}; "
            + $"memory {gb(i.FreeBytes)} of {gb(i.TotalBytes)} GB free, read-ahead {gb(readAhead.ForwardBytes)} GB and {(readAhead.BackBytes / (double)ReadAhead.Mib).ToString("0", inv)} MB behind{(readAhead.Low ? " (memory short)" : "")}; "
            + $"{link}, {need}: {fitWord}; {wait}; {(high ? "high-quality" : "standard")} picture; {screen}, {hdr}";
    }
}
