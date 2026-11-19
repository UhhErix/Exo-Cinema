namespace ExoCinema;

/// <summary>
/// The sound output mpv plays to, Windows' default device for media: what Windows says of it on this PC
/// (SoundOutput.Native.cs), or a simulated one in the tests. Which surround formats it takes as they are, and whether
/// another app is playing on it.
/// </summary>
internal sealed partial record SoundOutput(string Name, Bitstreams Takes, bool OthersPlaying)
{
    /// <summary>
    /// mpv's audio-spdif for this output: the formats it takes as they are, sent untouched (passthrough), so a receiver
    /// decodes TrueHD Atmos and DTS:X itself with their height channels; mpv can only flatten those to 7.1. "" decodes in
    /// mpv. Passing through takes the device for mpv alone (WASAPI exclusive), so it stays off while another app plays on
    /// the same output, during an Exo voice call and while a game runs: they would go silent.
    /// </summary>
    public string Passthrough(bool voiceCall, bool gameRunning)
    {
        if (Takes == Bitstreams.None || OthersPlaying || voiceCall || gameRunning) return "";
        var codecs = new List<string>(4);
        if (Takes.HasFlag(Bitstreams.Ac3)) codecs.Add("ac3");
        if (Takes.HasFlag(Bitstreams.Eac3)) codecs.Add("eac3");
        // dts-hd carries plain DTS too; mpv takes either, not both.
        if (Takes.HasFlag(Bitstreams.DtsHd)) codecs.Add("dts-hd");
        else if (Takes.HasFlag(Bitstreams.Dts)) codecs.Add("dts");
        if (Takes.HasFlag(Bitstreams.TrueHd)) codecs.Add("truehd");
        return string.Join(',', codecs);
    }

    /// <summary>mpv's output format while it passes a track through untouched ("spdif-truehd"): the receiver plays it.</summary>
    public static bool IsPassthrough(string? audioOutFormat) => audioOutFormat?.StartsWith("spdif-", StringComparison.Ordinal) == true;

    /// <summary>
    /// Clear dialogue (C16): surround (5.1 or 7.1) played on two channels (headphones, stereo speakers) is folded down with
    /// the centre, where films put the voices, ahead of the rest, then gently levelled so quiet speech and loud scenes sit
    /// closer, as Apple TV's Enhance Dialogue and Infuse's night mode do; mpv's own fold-down leaves the voices under the
    /// music and effects. "" leaves mpv's own: stereo or mono films, a surround output (it plays every channel), a track
    /// passed through, or a layout this doesn't know. <paramref name="layout"/> is mpv's audio-params/channels.
    /// </summary>
    public static string DialogueFilter(string? layout, long outputChannels, bool passthrough)
    {
        if (passthrough || outputChannels != 2) return "";
        // The surround channels each layout has (FFmpeg's names): back pair, side pair, or both.
        var rear = layout switch
        {
            "5.1" => "BL|BR",
            "5.1(side)" => "SL|SR",
            "7.1" or "7.1(wide)" => "BL|BR|SL|SR",
            _ => null,
        };
        if (rear is null) return "";
        var r = rear.Split('|');
        string Side(string front, int k) => string.Join("+", new[] { $"0.6*{front}", "0.85*FC" }.Concat(r.Where((_, i) => i % 2 == k).Select(c => $"0.4*{c}")));
        return $"@exo-dialogue:lavfi=[pan=stereo|FL={Side("FL", 0)}|FR={Side("FR", 1)},dynaudnorm=f=250:g=15:m=6:p=0.9]";
    }
}

/// <summary>Surround formats an output takes as they are (IEC 61937 over HDMI or S/PDIF), as Windows reports them.</summary>
[Flags]
internal enum Bitstreams
{
    None = 0,
    Ac3 = 1,
    Eac3 = 2,
    Dts = 4,
    DtsHd = 8,
    TrueHd = 16,
}
