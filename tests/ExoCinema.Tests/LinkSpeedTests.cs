namespace ExoCinema.Tests;

/// <summary>The link's speed keeps when it was measured, and an old measure stops counting for planning (LinkSpeed).</summary>
public sealed class LinkSpeedTests
{
    private const long Day = 86_400_000;
    private const long Now = 1_800_000_000_000;

    [Fact]
    public void A_bare_number_from_an_older_build_is_read_as_stale()
    {
        Assert.Equal((12.5, 0L), LinkSpeed.Parse("12.5"));
        Assert.False(LinkSpeed.Fresh(0, Now));
    }

    [Fact]
    public void The_stored_measure_keeps_its_time()
    {
        Assert.Equal((40.0, 123L), LinkSpeed.Parse("{\"Mbps\":40,\"At\":123}"));
        Assert.Null(LinkSpeed.Parse(""));
        Assert.Null(LinkSpeed.Parse("abc"));
        Assert.Null(LinkSpeed.Parse("{\"Mbps\":-3,\"At\":5}"));
    }

    [Fact]
    public void A_measure_counts_for_two_weeks_and_no_longer()
    {
        Assert.True(LinkSpeed.Fresh(Now - Day, Now));
        Assert.True(LinkSpeed.Fresh(Now - 14 * Day, Now));
        Assert.False(LinkSpeed.Fresh(Now - 15 * Day, Now));
    }

    [Fact]
    public void A_fresh_measure_is_averaged_with_the_new_one()
    {
        var next = LinkSpeed.Next((100, Now - Day), 50, Now);
        Assert.Equal(85.0, next.Mbps, 6);
        Assert.Equal(Now, next.At);
    }

    [Fact]
    public void A_stale_measure_is_replaced_not_averaged()
    {
        Assert.Equal((50.0, Now), LinkSpeed.Next((100, Now - 30 * Day), 50, Now));
        Assert.Equal((50.0, Now), LinkSpeed.Next(null, 50, Now));
    }
}
