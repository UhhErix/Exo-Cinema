namespace ExoCinema.Mpv;

/// <summary>
/// The seek bar's pictures made ahead for a video its server has none for (FramePeek): at which times, in what order.
/// The page keeps pictures on a grid of steps along the bar (player.js, peekStep); the pictures made ahead sit on every
/// stride-th step, coarse to fine, so early on the whole bar has a picture near every point and the gaps close after.
/// Each picture is a keyframe read from the host, about half a second of the video's bytes and at least one 256 KB
/// piece (PeekStream), and all of them together stay near 5% of the file (48 to 384 MB): one every 10 s or so, fewer
/// for a large remux. They're made once per file: ScrubStore keeps them.
/// </summary>
internal static class ScrubPlan
{
    /// <summary>Seconds along the video of the pictures to make ahead, in the order they're made.</summary>
    /// <param name="step">The page's grid step in seconds.</param>
    /// <param name="fileSize">The file's size in bytes; 0 or less when the host didn't say.</param>
    public static double[] Times(double duration, double step, long fileSize)
    {
        if (!(duration > 0) || !(step > 0)) return [];
        var grid = (int)Math.Floor(Math.Max(0, duration - 1) / step) + 1;
        var count = Count(duration, grid, fileSize);
        var stride = (grid + count - 1) / count;
        var points = (grid + stride - 1) / stride;
        // Coarse to fine: every span-th point, then the ones halfway between, halving the span down to 1.
        var span = 1;
        while (span * 2 < points) span *= 2;
        var order = new List<double>(points);
        var taken = new bool[points];
        for (; span >= 1; span /= 2)
            for (var i = 0; i < points; i += span)
                if (!taken[i])
                {
                    taken[i] = true;
                    order.Add(i * stride * step);
                }
        return [.. order];
    }

    /// <summary>How many of the grid's points get a picture ahead.</summary>
    internal static int Count(double duration, int grid, long fileSize)
    {
        if (fileSize <= 0) return Math.Min(grid, 200);
        var perPicture = Math.Max(256 << 10, fileSize / duration * 0.5);
        var budget = Math.Clamp(fileSize * 0.05, 48d * (1 << 20), 384d * (1 << 20));
        return (int)Math.Clamp(budget / perPicture, Math.Min(grid, 100), grid);
    }
}
