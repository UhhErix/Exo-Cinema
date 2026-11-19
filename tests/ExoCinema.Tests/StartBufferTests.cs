using ExoCinema.Mpv;

namespace ExoCinema.Tests;

/// <summary>The read-ahead playback waits for before it starts, and after it runs dry: the deficit of the link against the file.</summary>
public sealed class StartBufferTests
{
    private const double MB = 1 << 20;

    [Fact]
    public void A_link_faster_than_the_file_waits_the_minimum()
    {
        // 80 Mb/s (10 MB/s) film on a 25 MB/s link: no deficit.
        var plan = StartBuffer.Estimate(10 * MB, 25 * MB, 7200);
        Assert.Equal(StartBuffer.MinSeconds, plan.WaitSeconds);
        Assert.False(plan.TooSlow);
    }

    [Fact]
    public void A_small_deficit_waits_for_the_deficit_with_margin()
    {
        // 10 MB/s film on 8 MB/s: deficit 0.25 of each second over 200 s, times the safety margin.
        var plan = StartBuffer.Estimate(10 * MB, 8 * MB, 200);
        Assert.Equal(200 * 0.25 * StartBuffer.Safety, plan.WaitSeconds, 6);
        Assert.False(plan.TooSlow);
    }

    [Fact]
    public void The_wait_grows_with_the_length_left_and_the_deficit()
    {
        var short_ = StartBuffer.Estimate(10 * MB, 9 * MB, 600).WaitSeconds;
        var longer = StartBuffer.Estimate(10 * MB, 9 * MB, 1200).WaitSeconds;
        var slower = StartBuffer.Estimate(10 * MB, 8.5 * MB, 600).WaitSeconds;
        Assert.True(longer > short_);
        Assert.True(slower > short_);
    }

    [Fact]
    public void A_deficit_past_the_cap_is_too_slow_and_waits_the_cap()
    {
        // A 2-hour film at 10 MB/s on 9 MB/s: 7200 * (10/9 - 1) * 1.25 is about 1000 s, past the cap.
        var plan = StartBuffer.Estimate(10 * MB, 9 * MB, 7200);
        Assert.Equal(StartBuffer.MaxSeconds, plan.WaitSeconds);
        Assert.True(plan.TooSlow);
    }

    [Fact]
    public void A_link_below_the_bitrate_is_too_slow_at_any_length()
    {
        var plan = StartBuffer.Estimate(10 * MB, 4 * MB, 60);
        Assert.Equal(StartBuffer.MaxSeconds, plan.WaitSeconds);
        Assert.True(plan.TooSlow);
    }

    [Fact]
    public void An_unmeasured_speed_waits_the_default_not_the_cap()
    {
        Assert.Equal(StartBuffer.UnknownSeconds, StartBuffer.Estimate(10 * MB, 0, 7200).WaitSeconds);
        Assert.Equal(StartBuffer.UnknownSeconds, StartBuffer.Estimate(10 * MB, double.NaN, 7200).WaitSeconds);
        Assert.False(StartBuffer.Estimate(10 * MB, 0, 7200).TooSlow);
    }

    [Fact]
    public void Unknown_size_or_nothing_left_waits_the_minimum()
    {
        Assert.Equal(StartBuffer.MinSeconds, StartBuffer.Estimate(0, 8 * MB, 7200).WaitSeconds);
        Assert.Equal(StartBuffer.MinSeconds, StartBuffer.Estimate(10 * MB, 8 * MB, 0).WaitSeconds);
    }
}
