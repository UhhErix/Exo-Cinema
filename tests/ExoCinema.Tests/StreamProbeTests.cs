using ExoCinema.Net;

namespace ExoCinema.Tests;

/// <summary>A host's refusal of a film's link is told from the film, and said in plain words.</summary>
public sealed class StreamProbeTests
{
    [Theory]
    [InlineData("This stream link only works on the internet connection that requested it. Open the title again on this device. Expected IP: 203.0.113.7 Current IP: 198.51.100.9")]
    [InlineData("The stream host tied this link to another connection. Open the title again to ask for a fresh one.")]
    [InlineData("expected ip 203.0.113.7")]
    public void A_notice_about_the_connection_is_said_without_its_addresses(string notice)
    {
        Assert.True(StreamProbe.TiedToAnotherConnection(notice));
        var said = StreamProbe.Plain(notice);
        Assert.DoesNotContain("203.0.113.7", said);
        Assert.DoesNotContain("198.51.100.9", said);
        Assert.DoesNotContain("IP", said);
        Assert.Contains("play again", said);
    }

    [Theory]
    [InlineData("https://slate.example/v?title=No&body=This+stream+link+only+works+on+the+internet+connection+that+requested+it.+Expected+IP%3A+203.0.113.7", true)]
    [InlineData("https://slate.example/v", true)]
    [InlineData("https://notice.example/v?body=Expected%20IP%3A%20203.0.113.7%20Current%20IP%3A%20198.51.100.9", true)]
    [InlineData("https://cdn.example/dld/abc?token=1", false)]
    [InlineData("https://files.example/playback/x?name=Film.mkv&body=hello", false)]
    public void A_redirect_to_a_notice_is_told_from_a_redirect_to_the_file(string location, bool refused)
    {
        Assert.Equal(refused, StreamProbe.NoticeOf(new Uri(location)) is not null);
    }

    [Theory]
    [InlineData("This title is still downloading on your debrid service. Try again in a few minutes.")]
    [InlineData("Your subscription has ended.")]
    public void Any_other_notice_is_the_host_s_own_words(string notice)
    {
        Assert.False(StreamProbe.TiedToAnotherConnection(notice));
        Assert.Equal(notice, StreamProbe.Plain(notice));
    }
}
