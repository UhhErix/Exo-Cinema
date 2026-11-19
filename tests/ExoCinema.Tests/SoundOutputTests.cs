using static ExoCinema.Bitstreams;

namespace ExoCinema.Tests;

/// <summary>
/// Passthrough on simulated sound outputs, as Windows reports what each takes in exclusive mode: only formats the output
/// takes go to mpv, as tokens mpv knows; and nothing passes through while it would silence something else.
/// </summary>
public sealed class SoundOutputTests
{
    private static readonly SoundOutput[] Outputs =
    [
        new("AV receiver over HDMI (Atmos, DTS:X)", Ac3 | Eac3 | Dts | DtsHd | TrueHd, false),
        new("TV over HDMI", Ac3 | Eac3 | Dts, false),
        new("TV with an eARC soundbar", Ac3 | Eac3 | TrueHd, false),
        new("TV without DTS", Ac3 | Eac3, false),
        new("Older receiver, no HDMI high bitrate", Ac3 | Dts, false),
        new("Optical S/PDIF to a receiver", Ac3 | Dts, false),
        new("Receiver that takes DTS-HD but no plain DTS", Ac3 | DtsHd, false),
        new("Headphones (High Definition Audio Device)", None, false),
        new("USB DAC", None, false),
        new("Bluetooth headset", None, false),
        new("Monitor speakers over DisplayPort", None, false),
        new("Virtual EQ device", None, false),
    ];

    private static readonly HashSet<string> MpvCodecs = ["ac3", "eac3", "dts", "dts-hd", "truehd"];

    [Fact]
    public void Passes_through_only_what_the_output_takes()
    {
        foreach (var output in Outputs)
        foreach (var others in new[] { false, true })
        foreach (var call in new[] { false, true })
        foreach (var game in new[] { false, true })
        {
            var spdif = (output with { OthersPlaying = others }).Passthrough(call, game);
            if (output.Takes == None || others || call || game)
            {
                Assert.Equal("", spdif);
                continue;
            }
            var codecs = spdif.Split(',');
            Assert.All(codecs, c => Assert.Contains(c, MpvCodecs));
            Assert.Equal(codecs.Length, codecs.Distinct().Count());
            Assert.False(codecs.Contains("dts") && codecs.Contains("dts-hd"), spdif); // mpv takes one or the other
            Assert.Equal(output.Takes.HasFlag(Ac3), codecs.Contains("ac3"));
            Assert.Equal(output.Takes.HasFlag(Eac3), codecs.Contains("eac3"));
            Assert.Equal(output.Takes.HasFlag(TrueHd), codecs.Contains("truehd"));
            Assert.Equal(output.Takes.HasFlag(DtsHd), codecs.Contains("dts-hd"));
            Assert.Equal(output.Takes.HasFlag(Dts) || output.Takes.HasFlag(DtsHd), codecs.Contains("dts") || codecs.Contains("dts-hd"));
        }
    }

    [Fact]
    public void A_receiver_gets_atmos_and_dts_x_as_they_are()
    {
        Assert.Equal("ac3,eac3,dts-hd,truehd", Outputs[0].Passthrough(false, false));
        Assert.Equal("", Outputs[0].Passthrough(voiceCall: true, gameRunning: false));
        Assert.Equal("", (Outputs[0] with { OthersPlaying = true }).Passthrough(false, false));
    }

    [Theory]
    [InlineData("spdif-truehd", true)]
    [InlineData("spdif-eac3", true)]
    [InlineData("floatp", false)]
    [InlineData("s16", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Tells_a_track_passed_through(string? format, bool expected) => Assert.Equal(expected, SoundOutput.IsPassthrough(format));
}
