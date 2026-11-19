using System.Globalization;
using System.Text.Json;

namespace ExoCinema;

/// <summary>
/// How fast streams come in on this PC's connection, read from mpv in the first seconds of a stream and while it is filling
/// its buffer or waiting on the network (MainWindow.Player.cs): Best passes over a stream that needs more than that, which
/// would stop to buffer. A running average with the time it was last measured, kept in link.txt beside the settings. A
/// measure older than <see cref="FreshDays"/> no longer counts for planning: the next stream measures again.
/// </summary>
internal static class LinkSpeed
{
    /// <summary>How long a measure counts for planning. Longer than this and the connection is measured again.</summary>
    public const double FreshDays = 14;

    private static readonly string FilePath = Path.Combine(AppInfo.Data, "link.txt");
    private static (double Mbps, long At)? _last = Load();

    /// <summary>The connection's usual speed in Mb/s, however old the measure, or null before anything has been streamed.</summary>
    public static double? Mbps => _last?.Mbps;

    /// <summary>When the speed was last measured (Unix ms), or null; 0 for a measure kept before times were.</summary>
    public static long? MeasuredAt => _last?.At;

    /// <summary>The speed to plan with at <paramref name="nowMs"/> (Unix ms): the measure when it is fresh, else null.</summary>
    public static double? Fresh(long nowMs) => _last is { } l && Fresh(l.At, nowMs) ? l.Mbps : null;

    /// <summary>Whether a measure taken at <paramref name="measuredAtMs"/> still counts at <paramref name="nowMs"/>.</summary>
    public static bool Fresh(long measuredAtMs, long nowMs) => measuredAtMs > 0 && nowMs - measuredAtMs <= FreshDays * 86_400_000;

    public static void Record(double mbps) => Record(mbps, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static void Record(double mbps, long nowMs)
    {
        if (mbps is <= 0.1 or > 10_000) return;
        _last = Next(_last, mbps, nowMs);
        var (value, at) = _last.Value;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(new Stored(value, at))); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The new stored measure: a running average with the old one while it is fresh, the new one alone once it is not.</summary>
    public static (double Mbps, long At) Next((double Mbps, long At)? old, double mbps, long nowMs) =>
        old is { } o && Fresh(o.At, nowMs) ? (o.Mbps * 0.7 + mbps * 0.3, nowMs) : (mbps, nowMs);

    /// <summary>Reads what link.txt holds: the JSON of a newer build, or a bare number from an older one (its time is 0: stale).</summary>
    public static (double Mbps, long At)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<Stored>(text);
            return s is { Mbps: > 0 } ? (s.Mbps, s.At) : null;
        }
        catch (JsonException)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? (v, 0) : null;
        }
    }

    public sealed record Stored(double Mbps, long At);

    private static (double Mbps, long At)? Load()
    {
        try { return Parse(File.ReadAllText(FilePath)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
