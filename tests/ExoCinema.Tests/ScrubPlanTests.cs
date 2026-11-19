using ExoCinema.Mpv;

namespace ExoCinema.Tests;

/// <summary>The seek bar's pictures made ahead: on the page's grid, coarse to fine, about 5% of the file's bytes.</summary>
public sealed class ScrubPlanTests
{
    private const long MB = 1 << 20;

    [Fact]
    public void Pictures_sit_on_the_page_grid_once_each_inside_the_video()
    {
        var times = ScrubPlan.Times(2700, 5, 2600 * MB); // a 45-minute 4K web episode
        Assert.NotEmpty(times);
        Assert.Equal(times.Length, times.Distinct().Count());
        Assert.All(times, t => Assert.True(t % 5 == 0 && t >= 0 && t <= 2699));
        Assert.Contains(0, times);
    }

    [Fact]
    public void Early_pictures_cover_the_whole_bar()
    {
        var times = ScrubPlan.Times(7200, 12, 8000 * MB);
        // The first 17 made leave no gap wider than an eighth of the film.
        var first = times.Take(17).Order().Append(7200).ToArray();
        var gaps = first.Zip(first.Skip(1), (a, b) => b - a);
        Assert.True(gaps.Max() <= 7200 / 8.0 + 12, $"widest early gap {gaps.Max()} s");
    }

    [Fact]
    public void A_typical_file_gets_a_picture_about_every_ten_seconds()
    {
        var times = ScrubPlan.Times(2700, 5, 2600 * MB);
        Assert.InRange(times.Length, 2700 / 10 * 0.9, 2700 / 10 * 1.1);
    }

    [Fact]
    public void A_large_remux_reads_no_more_than_its_budget_but_keeps_a_hundred()
    {
        // 60 GB, two hours: about 4 MB a picture, 384 MB at most.
        var times = ScrubPlan.Times(7200, 12, 60_000 * MB);
        Assert.InRange(times.Length, 95, 105);
    }

    [Fact]
    public void A_low_bitrate_file_counts_a_whole_piece_a_picture()
    {
        // 150 MB over ten minutes: half a second is 125 KB, but every picture reads one 256 KB piece, so the 48 MB
        // floor buys 192 of the 600 steps (every 4th, 150 pictures), not 384.
        var times = ScrubPlan.Times(600, 1, 150 * MB);
        Assert.Equal(150, times.Length);
    }

    [Fact]
    public void A_short_video_gets_every_step()
    {
        Assert.Equal(90, ScrubPlan.Times(90, 1, 40 * MB).Length);
    }

    [Fact]
    public void Without_a_size_two_hundred_at_most()
    {
        Assert.Equal(200, ScrubPlan.Times(7200, 12, 0).Length);
        Assert.Equal(60, ScrubPlan.Times(60, 1, -1).Length);
    }

    [Fact]
    public void Nothing_for_a_video_without_a_length()
    {
        Assert.Empty(ScrubPlan.Times(0, 1, 100 * MB));
        Assert.Empty(ScrubPlan.Times(double.NaN, 1, 100 * MB));
    }
}
