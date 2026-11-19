using System.Text.Json;

namespace ExoCinema.Mpv;

/// <summary>What the player's cache (mpv's demuxer-cache-state, as JSON) says about its next request to the host.</summary>
internal static class CacheState
{
    /// <summary>
    /// Whether the cache holds a stretch after the one it's reading into (after a seek back), or it's reading outside
    /// what it holds: either way mpv opens a new request soon, and opens it before closing the one it has.
    /// </summary>
    public static bool NewRequestComing(string? json)
    {
        if (json is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("seekable-ranges", out var ranges) || !root.TryGetProperty("reader-pts", out var r) || r.ValueKind != JsonValueKind.Number) return false;
            var reader = r.GetDouble();
            var end = double.NaN;
            foreach (var range in ranges.EnumerateArray())
                if (range.GetProperty("start").GetDouble() <= reader + 0.5 && reader <= range.GetProperty("end").GetDouble() + 0.5)
                    end = range.GetProperty("end").GetDouble();
            if (double.IsNaN(end)) return ranges.GetArrayLength() > 0;
            foreach (var range in ranges.EnumerateArray())
                if (range.GetProperty("start").GetDouble() > end) return true;
            return false;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }
}
