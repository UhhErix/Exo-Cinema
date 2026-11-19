namespace ExoCinema.Mpv;

/// <summary>
/// mpv's log lines that repeat (a decoder's note on every frame, the video output's on every picture) are written once,
/// and then again only after a while, with how many times they came meanwhile. The app log keeps the reason without a
/// line per frame, which cost a write per frame during playback.
/// </summary>
internal sealed class RepeatedLines
{
    public const long WindowMs = 5_000;
    private const int Most = 512;
    private readonly Dictionary<string, (long Since, int Repeats)> _seen = new();

    /// <summary>The lines to write for this message at <paramref name="nowMs"/>: none while it repeats, the line again
    /// after <see cref="WindowMs"/>, with how many repeats were left out first.</summary>
    public IEnumerable<string> Accept(string line, long nowMs)
    {
        if (_seen.Count > Most) _seen.Clear();
        if (!_seen.TryGetValue(line, out var s))
        {
            _seen[line] = (nowMs, 0);
            return [line];
        }
        if (nowMs - s.Since < WindowMs)
        {
            _seen[line] = (s.Since, s.Repeats + 1);
            return [];
        }
        _seen[line] = (nowMs, 0);
        return s.Repeats > 0 ? [$"{line} (repeated {s.Repeats} more times)", line] : [line];
    }
}
