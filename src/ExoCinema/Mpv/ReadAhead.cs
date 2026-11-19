namespace ExoCinema.Mpv;

/// <summary>
/// How far ahead and how far behind a network film is held in memory, from the memory that is free now (not the PC's
/// total): half of the available for read-ahead, a quarter of that for read-behind. Free memory shrinks it before
/// Windows starts paging, and grows it again when memory frees. mpv uses these bytes only as it reads, so a small film
/// costs little. Plain numbers in, plain numbers out (ReadAheadTests); SystemMemory reads the PC.
/// </summary>
internal static class ReadAhead
{
    public const long Mib = 1L << 20, Gib = 1L << 30;

    /// <summary>The least read-ahead, even with little memory free.</summary>
    public const long Floor = 512 * Mib;

    /// <summary>The least read-behind.</summary>
    public const long MinBack = 128 * Mib;

    /// <summary>The share of the free memory the read-ahead may take.</summary>
    public const double ForwardShare = 0.5;

    /// <summary>The read-behind's share of the read-ahead.</summary>
    public const double BackShare = 0.25;

    /// <summary>Free memory under this share of the PC's total is short: the limits drop to their floors at once.</summary>
    public const double LowShare = 0.15;

    /// <param name="ForwardBytes">mpv's demuxer-max-bytes.</param>
    /// <param name="BackBytes">mpv's demuxer-max-back-bytes.</param>
    /// <param name="Low">Memory is short: the limits are the floors.</param>
    public readonly record struct Plan(long ForwardBytes, long BackBytes, bool Low);

    /// <param name="totalBytes">The PC's physical memory.</param>
    /// <param name="availableBytes">The physical memory free now.</param>
    public static Plan Size(long totalBytes, long availableBytes)
    {
        if (totalBytes > 0 && availableBytes < totalBytes * LowShare) return new Plan(Floor, MinBack, true);
        var forward = Math.Max(Floor, (long)(Math.Max(0, availableBytes) * ForwardShare));
        var back = Math.Max(MinBack, (long)(forward * BackShare));
        return new Plan(forward, back, false);
    }
}
