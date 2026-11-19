namespace ExoCinema.Mpv;

/// <summary>
/// How much read-ahead playback waits for, before it starts and after it runs dry. A file of B bytes a second read at S
/// bytes a second falls behind by (B/S - 1) seconds for every second played, so the film as a whole needs
/// remaining * (B/S - 1) seconds of read-ahead in hand to play to the end without a stall. The estimate adds a margin
/// for the link's ups and downs. A link at or below the film's bitrate can't be covered by any wait within the cap:
/// that is reported as too slow, and the wait is the cap.
/// </summary>
internal static class StartBuffer
{
    /// <summary>Never less than this: a few seconds of picture ready, so the first frames don't stall straight away.</summary>
    public const double MinSeconds = 6;

    /// <summary>The longest wait for read-ahead, before the film starts or resumes.</summary>
    public const double MaxSeconds = 90;

    /// <summary>Margin on the deficit, for the link's variation.</summary>
    public const double Safety = 1.25;

    /// <summary>The wait when the link's speed isn't measured yet.</summary>
    public const double UnknownSeconds = 12;

    /// <summary>The wait to hold, and whether the link is too slow for the film even at the longest wait.</summary>
    public readonly record struct Plan(double WaitSeconds, bool TooSlow);

    /// <param name="bytesPerSecond">The file's average bytes per second of video (size / duration).</param>
    /// <param name="speedBytesPerSecond">The link's measured speed, bytes per second; 0 or less when not known yet.</param>
    /// <param name="remainingSeconds">How much of the film is left to play from where it is.</param>
    public static Plan Estimate(double bytesPerSecond, double speedBytesPerSecond, double remainingSeconds)
    {
        if (!(bytesPerSecond > 0) || !(remainingSeconds > 0)) return new Plan(MinSeconds, false);
        if (!(speedBytesPerSecond > 0)) return new Plan(UnknownSeconds, false);
        var deficit = remainingSeconds * Math.Max(0, bytesPerSecond / speedBytesPerSecond - 1) * Safety;
        if (deficit > MaxSeconds) return new Plan(MaxSeconds, true);
        return new Plan(Math.Max(MinSeconds, deficit), false);
    }
}
