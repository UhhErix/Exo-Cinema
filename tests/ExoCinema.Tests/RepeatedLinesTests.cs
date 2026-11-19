using ExoCinema.Mpv;

namespace ExoCinema.Tests;

/// <summary>mpv's per-frame notes are written once, not once a frame.</summary>
public sealed class RepeatedLinesTests
{
    private const string Rpu = "[ffmpeg/video] hevc: Multiple Dolby Vision RPUs found in one AU. Skipping previous.";

    [Fact]
    public void A_new_line_is_written()
    {
        var r = new RepeatedLines();
        Assert.Equal([Rpu], r.Accept(Rpu, 0));
    }

    [Fact]
    public void Repeats_inside_the_window_are_left_out()
    {
        var r = new RepeatedLines();
        r.Accept(Rpu, 0);
        for (var t = 1; t < 1000; t++) Assert.Empty(r.Accept(Rpu, t * 4)); // 240 messages a second for four seconds
    }

    [Fact]
    public void After_the_window_the_line_comes_again_with_how_many_were_left_out()
    {
        var r = new RepeatedLines();
        r.Accept(Rpu, 0);
        r.Accept(Rpu, 100);
        r.Accept(Rpu, 200);
        var again = r.Accept(Rpu, RepeatedLines.WindowMs + 10).ToArray();
        Assert.Equal(2, again.Length);
        Assert.Equal(Rpu + " (repeated 2 more times)", again[0]);
        Assert.Equal(Rpu, again[1]);
    }

    [Fact]
    public void Different_lines_are_not_taken_for_repeats()
    {
        var r = new RepeatedLines();
        const string other = "[vo/gpu-next] Ignoring invalid --video-crop= for 1188x892 image";
        r.Accept(Rpu, 0);
        Assert.Equal([other], r.Accept(other, 1));
        Assert.Empty(r.Accept(Rpu, 2));
    }
}
