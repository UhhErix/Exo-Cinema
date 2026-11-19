using System.Diagnostics;
using ExoCinema.Mpv;

namespace ExoCinema;

/// <summary>
/// Surround sound passed through to a receiver (SoundOutput): TrueHD Atmos, DTS:X, Dolby Digital Plus and the rest go
/// out as they are, so the receiver plays their height channels. Chosen for each video before mpv opens its sound. While
/// it passes through, mpv has the device to itself (WASAPI exclusive), so an Exo voice call or a game starting gets it
/// back within 2 s: mpv opens its sound again, decoded.
/// </summary>
public sealed partial class MainWindow
{
    private string _spdif = "";
    private string _soundSaid = "";
    private Timer? _soundWatch;

    private void ChoosePassthrough(MpvPlayer mpv)
    {
        var started = Stopwatch.GetTimestamp();
        var output = SoundOutput.Read();
        var (call, game) = (VoiceCall(), GameRunning());
        var spdif = output?.Passthrough(call, game) ?? "";
        if (spdif != _spdif && mpv.Set("audio-spdif", spdif) >= 0) _spdif = spdif;
        if (output is null) return;
        var why = output.Takes == Bitstreams.None ? "" : output.OthersPlaying ? ", another app is playing on it" : call ? ", a voice call is on" : game ? ", a game is running" : "";
        var said = $"sound: {output.Name} · {(_spdif.Length > 0 ? $"passes {_spdif} through" : "decoded" + why)}";
        if (said == _soundSaid) return;
        _soundSaid = said;
        _log.Write($"{said} ({Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms)");
    }

    private string _dialogue = "";

    /// <summary>Clear dialogue for surround on two channels (SoundOutput.DialogueFilter), set or taken off as the film's
    /// layout and the output change. The output's count is the device's, from Windows: with the filter on, mpv's own
    /// reads 2 by our doing, so a switch to a surround output would never take it off.</summary>
    private void ApplyDialogue()
    {
        if (_mpv is not { } mpv || _closingWindow) return;
        var layout = mpv.Get("audio-params/channels");
        long outputs = SoundOutput.MixChannels();
        if (outputs == 0 && _dialogue.Length == 0) outputs = long.TryParse(mpv.Get("audio-out-params/channel-count"), out var n) ? n : 0;
        var want = SoundOutput.DialogueFilter(layout, outputs, SoundOutput.IsPassthrough(mpv.Get("audio-out-params/format")));
        if (want == _dialogue) return;
        if (_dialogue.Length > 0) mpv.Command("af", "remove", "@exo-dialogue");
        if (want.Length > 0 && mpv.Command("af", "add", want) < 0) want = "";
        _dialogue = want;
        _log.Write(want.Length > 0 ? $"sound: clear dialogue ({layout} on 2 channels)" : "sound: mpv's own fold-down");
    }

    /// <summary>mpv's output format changed: while a track passes through, the device is watched for a call or a game.</summary>
    private void OnSoundFormat(string? format)
    {
        if (SoundOutput.IsPassthrough(format)) (_soundWatch ??= new Timer(_ => OnUi(WatchSound))).Change(2000, 2000);
        else _soundWatch?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void WatchSound()
    {
        if (_mpv is not { } mpv || _closingWindow || _spdif.Length == 0) return;
        var call = VoiceCall();
        if (!call && !GameRunning()) return;
        // mpv opens its sound again by itself when audio-spdif changes, decoded this time.
        if (mpv.Set("audio-spdif", "") < 0) return;
        _spdif = "";
        _soundSaid = "";
        _log.Write($"sound: decoded from here on, {(call ? "a voice call" : "a game")} started");
    }

    // An Exo voice call is on: Exo Launcher signals it with a named event while a call's sound runs (its
    // Services/VoiceCallSignal.cs, agreed with it), as it does for games. Without Exo Launcher the event doesn't exist,
    // which reads as no call.
    private static bool VoiceCall()
    {
        var handle = OpenEventW(0x00100000 /* SYNCHRONIZE */, false, @"Local\ExoLauncher.VoiceCall");
        if (handle == IntPtr.Zero) return false;
        try { return WaitForSingleObject(handle, 0) == 0; }
        finally { CloseHandle(handle); }
    }
}
