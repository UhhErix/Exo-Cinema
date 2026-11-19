namespace ExoCinema.Mpv;

/// <summary>
/// A Matroska file's keyframe index (its Cues), read from the bytes themselves: where in the file the cluster holding
/// the keyframe at or before a time starts, so a seek can be fetched before it's asked for (C25). Only what that
/// needs is read: the segment's start, its SeekHead (where the Cues are) and its Info (the timestamp scale).
/// </summary>
internal sealed class MkvIndex
{
    private const uint Ebml = 0x1A45DFA3, Segment = 0x18538067, SeekHead = 0x114D9B74, Seek = 0x4DBB, SeekId = 0x53AB, SeekPosition = 0x53AC;
    private const uint Info = 0x1549A966, TimecodeScale = 0x2AD7B1, Cues = 0x1C53BB6B, Cluster = 0x1F43B675;
    private const uint CuePoint = 0xBB, CueTime = 0xB3, CueTrackPositions = 0xB7, CueClusterPosition = 0xF1;

    private readonly double[] _times;   // seconds, ascending
    private readonly long[] _offsets;   // absolute file offsets of the clusters

    private MkvIndex(double[] times, long[] offsets) => (_times, _offsets) = (times, offsets);

    public int Count => _times.Length;

    /// <summary>Where the Cues are, read from the file's first bytes: the segment's data start, the Cues' absolute offset
    /// and the timestamp scale in ns. Null when the head isn't Matroska or names no Cues.</summary>
    public static (long SegmentStart, long CuesAt, long Scale)? ReadHead(ReadOnlySpan<byte> head)
    {
        var p = 0;
        if (!Element(head, ref p, out var id, out var size) || id != Ebml) return null;
        p += (int)size;
        if (!Element(head, ref p, out id, out _) || id != Segment) return null;
        var segment = (long)p;
        long cues = -1, scale = 1_000_000;
        for (var start = p; p < head.Length && Element(head, ref p, out id, out size); start = p)
        {
            if (id == Cluster) break;
            var end = size < 0 ? head.Length : (int)Math.Min(head.Length, p + size);
            if (id == SeekHead)
            {
                for (var q = p; q < end && Element(head, ref q, out var sid, out var ssize);)
                {
                    var sEnd = (int)Math.Min(end, q + ssize);
                    if (sid == Seek)
                    {
                        uint target = 0;
                        long position = -1;
                        for (var r = q; r < sEnd && Element(head, ref r, out var fid, out var fsize);)
                        {
                            var f = head.Slice(r, (int)Math.Min(fsize, sEnd - r));
                            if (fid == SeekId) target = (uint)Uint(f);
                            else if (fid == SeekPosition) position = (long)Uint(f);
                            r += (int)fsize;
                        }
                        if (target == Cues && position >= 0) cues = segment + position;
                    }
                    q = sEnd;
                }
            }
            else if (id == Info)
            {
                for (var q = p; q < end && Element(head, ref q, out var iid, out var isize);)
                {
                    if (iid == TimecodeScale) scale = (long)Uint(head.Slice(q, (int)Math.Min(isize, end - q)));
                    q += (int)isize;
                }
            }
            else if (id == Cues && cues < 0) cues = start;
            if (size < 0 || p + size > head.Length) break;
            p += (int)size;
        }
        return cues >= 0 && scale > 0 ? (segment, cues, scale) : null;
    }

    /// <summary>The index from the Cues element's bytes (starting at its ID); null when they don't parse or hold none.</summary>
    public static MkvIndex? ReadCues(ReadOnlySpan<byte> bytes, long segmentStart, long scale)
    {
        var p = 0;
        if (!Element(bytes, ref p, out var id, out var size) || id != Cues) return null;
        var end = size < 0 ? bytes.Length : (int)Math.Min(bytes.Length, p + size);
        var points = new SortedDictionary<double, long>();
        while (p < end && Element(bytes, ref p, out id, out size))
        {
            var pEnd = (int)Math.Min(end, p + Math.Max(0, size));
            if (id == CuePoint)
            {
                double time = -1;
                long cluster = -1;
                for (var q = p; q < pEnd && Element(bytes, ref q, out var cid, out var csize);)
                {
                    var cEnd = (int)Math.Min(pEnd, q + csize);
                    if (cid == CueTime) time = Uint(bytes[q..cEnd]) * (double)scale / 1e9;
                    else if (cid == CueTrackPositions && cluster < 0)
                        for (var r = q; r < cEnd && Element(bytes, ref r, out var tid, out var tsize);)
                        {
                            if (tid == CueClusterPosition) cluster = segmentStart + (long)Uint(bytes.Slice(r, (int)Math.Min(tsize, cEnd - r)));
                            r += (int)tsize;
                        }
                    q = cEnd;
                }
                if (time >= 0 && cluster >= 0) points.TryAdd(time, cluster);
            }
            p = pEnd;
        }
        return points.Count == 0 ? null : new MkvIndex([.. points.Keys], [.. points.Values]);
    }

    /// <summary>The cluster to read for a seek to <paramref name="seconds"/>: the keyframe's at or before it, and where the
    /// next cluster after the time starts (the end of what a seek there needs), or -1 past the last.</summary>
    public (long From, long To) For(double seconds)
    {
        var i = Array.BinarySearch(_times, seconds);
        if (i < 0) i = Math.Max(0, ~i - 1);
        var next = i + 1;
        while (next < _offsets.Length && _offsets[next] <= _offsets[i]) next++;
        return (_offsets[i], next < _offsets.Length ? _offsets[next] : -1);
    }

    // An EBML element header at p: its ID (with its marker bits) and its data size (-1 when unknown); p moves past it.
    internal static bool Element(ReadOnlySpan<byte> b, ref int p, out uint id, out long size)
    {
        id = 0;
        size = 0;
        if (p >= b.Length) return false;
        int idLength = b[p] >= 0x80 ? 1 : b[p] >= 0x40 ? 2 : b[p] >= 0x20 ? 3 : b[p] >= 0x10 ? 4 : 0;
        if (idLength == 0 || p + idLength >= b.Length) return false;
        for (var i = 0; i < idLength; i++) id = id << 8 | b[p + i];
        var s = p + idLength;
        var first = b[s];
        var length = first == 0 ? 0 : System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;
        if (length is < 1 or > 8 || s + length > b.Length) return false;
        long value = first & (0xFF >> length);
        var allOnes = value == 0xFF >> length;
        for (var i = 1; i < length; i++)
        {
            value = value << 8 | b[s + i];
            allOnes &= b[s + i] == 0xFF;
        }
        size = allOnes ? -1 : value;
        p = s + length;
        return true;
    }

    private static ulong Uint(ReadOnlySpan<byte> b)
    {
        ulong v = 0;
        foreach (var x in b[..Math.Min(8, b.Length)]) v = v << 8 | x;
        return v;
    }
}
