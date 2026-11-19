using System.Text.Json.Nodes;
using ExoCinema.Jellyfin;

namespace ExoCinema.Tests;

/// <summary>
/// Best by measured speed: a version whose bitrate fits the link (LinkFit) comes before one that would stop to buffer,
/// and with nothing measured the order is the server's.
/// </summary>
public sealed class LinkFitTests
{
    private static JsonNode Version(string id, long? bitrate)
    {
        var s = new JsonObject
        {
            ["Id"] = id,
            ["MediaStreams"] = new JsonArray(new JsonObject { ["Type"] = "Video", ["Height"] = 1080, ["Codec"] = "hevc" }),
        };
        if (bitrate is { } bps) s["Bitrate"] = bps;
        return s;
    }

    // Mb/s as the server writes a bitrate: bits a second.
    private static long Bps(double mbps) => (long)(mbps * 1e6);

    private static string[] Order(IEnumerable<JsonNode> ranked) => [.. ranked.Select(s => (string)s["Id"]!)];

    [Fact]
    public void A_version_fits_when_it_needs_no_more_than_the_share_of_the_link()
    {
        Assert.Equal(0.85, LinkFit.Share);
        Assert.True(LinkFit.Fits(50, 60));    // 50 <= 51
        Assert.False(LinkFit.Fits(52, 60));   // 52 > 51
        Assert.True(LinkFit.Fits(60, 71));    // 60 <= 60.35
        Assert.False(LinkFit.Fits(60, 70));   // 60 > 59.5
        Assert.True(LinkFit.Fits(null, 1));   // bitrate not known
        Assert.True(LinkFit.Fits(100, null)); // link not measured
        Assert.True(LinkFit.Fits(100, 0));
    }

    [Fact]
    public void The_best_version_that_fits_the_link_comes_first_and_none_is_dropped()
    {
        var ranked = SourceRank.Rank([Version("big", Bps(60)), Version("small", Bps(12))], GraphicsCard.Unknown, 50).ToList();
        Assert.Equal(2, ranked.Count);
        Assert.Equal(new[] { "small", "big" }, Order(ranked));
    }

    [Fact]
    public void Nothing_measured_or_nothing_fitting_keeps_todays_pick()
    {
        var sources = () => new List<JsonNode> { Version("big", Bps(60)), Version("small", Bps(12)) };
        Assert.Equal(new[] { "big", "small" }, Order(SourceRank.Rank(sources(), GraphicsCard.Unknown, null)));
        Assert.Equal(new[] { "big", "small" }, Order(SourceRank.Rank(sources(), GraphicsCard.Unknown, 0)));
        Assert.Equal(new[] { "big", "small" }, Order(SourceRank.Rank(sources(), GraphicsCard.Unknown, 5)));
    }

    [Fact]
    public void Among_versions_that_fit_today_s_order_holds()
    {
        var sources = new List<JsonNode> { Version("a", Bps(12)), Version("b", Bps(60)), Version("c", Bps(8)), Version("d", Bps(30)) };
        Assert.Equal(new[] { "d", "a", "c", "b" }, Order(SourceRank.Rank(sources, GraphicsCard.Unknown, 50)));
    }

    [Fact]
    public void A_version_whose_bitrate_is_not_known_counts_as_fitting()
    {
        var sources = new List<JsonNode> { Version("big", Bps(60)), Version("unknown", null) };
        Assert.Equal(new[] { "unknown", "big" }, Order(SourceRank.Rank(sources, GraphicsCard.Unknown, 50)));
    }

}
