namespace ExoCinema.Tests;

/// <summary>Clear dialogue (C16): only surround folded to two channels, every channel of the layout named, nothing else.</summary>
public sealed class SoundTests
{
    [Theory]
    [InlineData("5.1", "BL", "BR")]
    [InlineData("5.1(side)", "SL", "SR")]
    [InlineData("7.1", "BL", "SR")]
    public void Surround_on_two_channels_gets_the_centre_ahead(string layout, string rearLeft, string rearRight)
    {
        var f = SoundOutput.DialogueFilter(layout, 2, passthrough: false);
        Assert.StartsWith("@exo-dialogue:lavfi=[pan=stereo|", f);
        Assert.Contains("0.85*FC", f);
        Assert.Contains(rearLeft, f);
        Assert.Contains(rearRight, f);
        Assert.DoesNotContain("LFE", f);
        // Left and right each take the centre once.
        Assert.Equal(2, f.Split("0.85*FC").Length - 1);
    }

    [Theory]
    [InlineData("stereo", 2L, false)]
    [InlineData("mono", 2L, false)]
    [InlineData("5.1", 6L, false)]   // a surround output plays every channel itself
    [InlineData("7.1", 8L, false)]
    [InlineData("5.1", 2L, true)]    // passed through to a receiver
    [InlineData("hexagonal", 2L, false)]
    [InlineData(null, 2L, false)]
    [InlineData("5.1", 0L, false)]   // the output not open yet
    public void Everything_else_keeps_mpvs_own(string? layout, long outputs, bool passthrough) =>
        Assert.Equal("", SoundOutput.DialogueFilter(layout, outputs, passthrough));
}
