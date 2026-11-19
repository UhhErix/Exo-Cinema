using ExoCinema.Mpv;

namespace ExoCinema.Tests;

/// <summary>The keyframe index a seek is fetched ahead from (C25): read from a Matroska file's own bytes.</summary>
public sealed class MkvIndexTests
{
    private static byte[] El(uint id, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var idBytes = BitConverter.GetBytes(id).Reverse().SkipWhile(b => b == 0).ToArray();
        var size = new byte[8];
        size[0] = 0x01;
        for (var i = 0; i < 7; i++) size[7 - i] = (byte)((long)body.Length >> (8 * i));
        return [.. idBytes, .. size, .. body];
    }

    private static byte[] Uint(uint id, ulong v)
    {
        var bytes = BitConverter.GetBytes(v).Reverse().SkipWhile(b => b == 0).DefaultIfEmpty((byte)0).ToArray();
        return El(id, bytes);
    }

    private static byte[] Cue(ulong timeMs, ulong cluster) =>
        El(0xBB, Uint(0xB3, timeMs), El(0xB7, Uint(0xF7, 1), Uint(0xF1, cluster)));

    // EBML header, then a Segment whose SeekHead points at Cues 5000 bytes into it, at a 1 ms timestamp scale.
    private static (byte[] Head, long SegmentStart) File(long cuesAt)
    {
        var ebml = El(0x1A45DFA3, Uint(0x4286, 1));
        var seekHead = El(0x114D9B74, El(0x4DBB, El(0x53AB, [0x1C, 0x53, 0xBB, 0x6B]), Uint(0x53AC, (ulong)cuesAt)));
        var info = El(0x1549A966, Uint(0x2AD7B1, 1_000_000));
        var segmentHeader = new byte[] { 0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        var cluster = El(0x1F43B675, Uint(0xE7, 0));
        byte[] head = [.. ebml, .. segmentHeader, .. seekHead, .. info, .. cluster];
        return (head, ebml.Length + segmentHeader.Length);
    }

    [Fact]
    public void The_head_names_where_the_cues_are_and_the_scale()
    {
        var (head, segment) = File(5000);
        var found = MkvIndex.ReadHead(head);
        Assert.NotNull(found);
        Assert.Equal(segment, found.Value.SegmentStart);
        Assert.Equal(segment + 5000, found.Value.CuesAt);
        Assert.Equal(1_000_000, found.Value.Scale);
    }

    [Fact]
    public void A_seek_reads_from_the_keyframe_at_or_before_it_to_the_next_cluster()
    {
        var cues = El(0x1C53BB6B, Cue(0, 100), Cue(2000, 4100), Cue(4000, 9000), Cue(6000, 15000));
        var index = MkvIndex.ReadCues(cues, segmentStart: 50, scale: 1_000_000);
        Assert.NotNull(index);
        Assert.Equal(4, index.Count);
        Assert.Equal((50 + 4100L, 50 + 9000L), index.For(3.5));
        Assert.Equal((50 + 4100L, 50 + 9000L), index.For(2.0));
        Assert.Equal((50 + 100L, 50 + 4100L), index.For(0));
        Assert.Equal((50 + 15000L, -1L), index.For(99));
    }

    [Fact]
    public void Not_matroska_or_no_cues_gives_nothing()
    {
        Assert.Null(MkvIndex.ReadHead(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70 }));
        Assert.Null(MkvIndex.ReadCues(El(0x1C53BB6B), 0, 1_000_000));
    }
}
