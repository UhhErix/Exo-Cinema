using ExoCinema.Mpv;

namespace ExoCinema.Tests;

/// <summary>When the player is about to open a new request to its host, so the seek bar's pictures stay off it.</summary>
public sealed class CacheStateTests
{
    private static string N(double x) => x.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string State(double reader, params (double Start, double End)[] ranges) =>
        $$"""{"cache-end":{{N(ranges.Max(r => r.End))}},"reader-pts":{{N(reader)}},"eof":false,"idle":false,"seekable-ranges":[{{string.Join(",", ranges.Select(r => $$"""{"start":{{N(r.Start)}},"end":{{N(r.End)}}}"""))}}]}""";

    [Fact]
    public void Reading_on_with_nothing_held_ahead_opens_no_request()
    {
        Assert.False(CacheState.NewRequestComing(State(96.5, (0, 140))));
        Assert.False(CacheState.NewRequestComing(State(96.5, (0, 20), (60, 140)))); // a stretch behind doesn't matter
    }

    [Fact]
    public void A_stretch_held_ahead_after_a_seek_back_means_a_new_request()
    {
        // Seen live: back to 0:30 after 2:00 and 6:40, reading on into 2:00's stretch opened a third connection.
        Assert.True(CacheState.NewRequestComing(State(31, (30, 60), (120, 160), (400, 600))));
    }

    [Fact]
    public void Reading_outside_what_is_held_means_a_new_request()
    {
        Assert.True(CacheState.NewRequestComing(State(300, (0, 140))));
    }

    [Fact]
    public void Nothing_known_says_nothing()
    {
        Assert.False(CacheState.NewRequestComing(null));
        Assert.False(CacheState.NewRequestComing("{}"));
        Assert.False(CacheState.NewRequestComing("not json"));
        Assert.False(CacheState.NewRequestComing("""{"reader-pts":5,"seekable-ranges":[]}"""));
    }
}
