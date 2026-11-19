using System.Text.Json;
using ExoCinema.Mpv;

namespace ExoCinema;

/// <summary>
/// Auto HDR for films (HdrFilms.cs rules, DisplayHdr Windows calls): a title whose picture is HDR (mpv's transfer is PQ or
/// HLG) turns HDR on for the screen the window is on, when that screen can show HDR and has it off, and no game runs.
/// Playback holds its first frame until the screen has taken HDR, so the picture doesn't flash wrong. When a title ends, HDR
/// stays on for a while, for a next HDR title, and goes back off if none follows; closing the player or Exo restores it at
/// once. A marker written before each switch lets the next start turn it off after a crash.
/// </summary>
public sealed partial class MainWindow
{
    private readonly object _filmHdrLock = new();
    private DisplayHdr.Screen? _filmHdrOn;
    private ushort[]? _filmRamp;
    private int _filmSwitching;
    private FilmHdrHold? _filmHold;

    private static string FilmMarkerPath => Path.Combine(AppInfo.Data, HdrFilms.MarkerFile);

    /// <summary>A title's end, with HDR held for a next one (HdrFilms.Hold decides it each quarter second).</summary>
    private sealed class FilmHdrHold
    {
        public readonly long SinceMs = Environment.TickCount64;
        public bool NextIsHdr;
    }

    /// <summary>mpv reports a title's transfer and its end: an HDR picture may switch its screen, and the end of a title holds HDR for the next.</summary>
    private void FollowFilmHdr(string name, object? value)
    {
        if (name == "eof-reached" && value is true) { EndFilmTitle(); return; }
        if (name != "video-params/gamma" || value is not string gamma || !PictureRules.Hdr(gamma)) return;
        TitleWantsHdr(sdrToHdr: false);
    }

    /// <summary>
    /// A title wants its screen in HDR: its picture is HDR, or RTX Video HDR turns its SDR picture into HDR
    /// (<paramref name="sdrToHdr"/>, from MainWindow.Player.cs › ApplyVideoProcessor, which puts the filter in once the
    /// screen is in HDR). The same rules either way: HdrFilms.ShouldSwitchOn, held before the first frame, back off after.
    /// </summary>
    private void TitleWantsHdr(bool sdrToHdr)
    {
        // An HDR title starting during a hold keeps HDR on for it: the hold ends at its next decision.
        lock (_filmHdrLock)
        {
            if (_filmHold is not null) _filmHold.NextIsHdr = true;
        }
        if (_filmHdrOn is not null || Interlocked.Exchange(ref _filmSwitching, 1) == 1) return;
        // Held before the first frame, so the screen takes HDR before anything is drawn; released once it has, or after the wait.
        var mpv = _mpv;
        var pausedHere = mpv is not null && !_firstFrameSeen && mpv.Get("pause") == "no";
        if (pausedHere) mpv!.Set("pause", "yes");
        Task.Run(() =>
        {
            try { SwitchFilmHdrOn(mpv, pausedHere, sdrToHdr); }
            finally
            {
                // RTX Video HDR goes in while the first frame is still held, so the film starts in HDR.
                if (sdrToHdr) ApplyPicture();
                if (pausedHere) mpv!.Set("pause", "no");
                Interlocked.Exchange(ref _filmSwitching, 0);
            }
        });
    }

    private void SwitchFilmHdrOn(MpvPlayer? mpv, bool held, bool sdrToHdr)
    {
        var screen = DisplayHdr.ForWindow(_hwnd);
        var gaming = GameRunning();
        if (!HdrFilms.ShouldSwitchOn(titleIsHdr: true, screenSupportsHdr: screen?.Supported == true, screenHdrOn: screen?.Enabled == true, gameRunning: gaming))
        {
            if (screen is { Supported: true, Enabled: true }) _log.Write("hdr: this screen is already in HDR; left as it is");
            else if (gaming) _log.Write("hdr: not switched, a game is running");
            else if (screen is { Supported: false }) _log.Write("hdr: this screen can't show HDR; the title plays in SDR");
            return;
        }
        lock (_filmHdrLock)
        {
            var ramp = DisplayHdr.ReadRamp(screen!.GdiName);
            // The marker goes down before the switch, so a crash between the two still gets HDR turned off at the next start.
            Directory.CreateDirectory(AppInfo.Data);
            File.WriteAllText(FilmMarkerPath, HdrFilms.MarkerJson(screen.DevicePath, screen.GdiName));
            if (!DisplayHdr.Set(screen, true))
            {
                DeleteFilmMarker();
                _log.Write("hdr: Windows did not switch HDR on for this screen");
                return;
            }
            _filmHdrOn = screen;
            _filmRamp = ramp;
        }
        var reached = WaitForFilmHdr(screen.DevicePath, true);
        _log.Write(reached
            ? $"hdr: HDR on for this film's screen ({screen.RefreshHz:0} Hz), before the first frame{(sdrToHdr ? ", for RTX Video HDR" : "")}"
            : "hdr: the screen did not report HDR in time; the film starts anyway and HDR goes off when it ends");
    }

    private static bool WaitForFilmHdr(string devicePath, bool want)
    {
        var until = Environment.TickCount64 + HdrFilms.SwitchWaitMs;
        while (true)
        {
            if (HdrFilms.Reached(want, DisplayHdr.Find(devicePath)?.Enabled)) return true;
            if (Environment.TickCount64 >= until) return false;
            Thread.Sleep(200);
        }
    }

    /// <summary>The title reached its end: HDR Exo switched on stays on for HdrFilms.RestoreHoldMs, for a next HDR title, then goes off.</summary>
    private void EndFilmTitle()
    {
        lock (_filmHdrLock)
        {
            if (_filmHdrOn is null) return;
            var hold = new FilmHdrHold();
            _filmHold = hold;
            _log.Write("hdr: the title ended; HDR stays on a moment, for a next HDR title");
            _ = WatchFilmHold(hold);
        }
    }

    /// <summary>Decides a hold until it ends: an HDR title that is playing (or starts) keeps HDR on, and when the time runs out HDR goes back off.</summary>
    private async Task WatchFilmHold(FilmHdrHold hold)
    {
        try
        {
            while (true)
            {
                await Task.Delay(250);
                // Read on every tick, not only from mpv's change event: two HDR files in a row can report the same gamma, and no change fires.
                var playingHdr = HdrTitlePlayingNow();
                lock (_filmHdrLock)
                {
                    // Restored at once (the player closed, or Exo), or a newer title's hold replaced this one.
                    if (!ReferenceEquals(_filmHold, hold)) return;
                    switch (HdrFilms.Hold(hold.NextIsHdr || playingHdr, hold.SinceMs, Environment.TickCount64))
                    {
                        case HdrFilms.HoldStep.Keep:
                            _filmHold = null;
                            _log.Write("hdr: the next title is HDR too; HDR stays on");
                            return;
                        case HdrFilms.HoldStep.Restore:
                            RestoreFilmHdr("no HDR title followed");
                            return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Write("hdr: hold: " + ex.GetType().Name);
            // Never leave HDR on without a hold to end it.
            try { RestoreFilmHdr("the hold failed"); } catch (Exception) { }
        }
    }

    /// <summary>Whether an HDR title is playing now, by mpv's own state: a file loaded and not at its end, with an HDR picture.</summary>
    private bool HdrTitlePlayingNow()
    {
        try
        {
            var mpv = _mpv;
            return mpv is not null && HdrFilms.HdrTitlePlaying(mpv.Get("idle-active"), mpv.Get("eof-reached"), mpv.Get("video-params/gamma"), _vppHdr);
        }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>HDR off on the screen Exo switched it on for. When Windows refuses, the restore is tried again (RetryRestoreFilmHdr),
    /// and the marker stays until HDR is off, so a later start turns it off too.</summary>
    private void RestoreFilmHdr(string why)
    {
        if (TryRestoreFilmHdr(why)) return;
        _ = RetryRestoreFilmHdr(why);
    }

    /// <summary>One try at HDR off for the owned screen. False when Windows refused it (the marker and the owned screen stay).</summary>
    private bool TryRestoreFilmHdr(string why)
    {
        lock (_filmHdrLock)
        {
            _filmHold = null;
            if (_filmHdrOn is not { } owned) return true;
            var now = DisplayHdr.Find(owned.DevicePath);
            if (now is not null && now.Enabled && !DisplayHdr.Set(now, false))
            {
                _log.Write($"hdr: Windows did not turn HDR off ({why}); will try again");
                return false;
            }
            if (now is not null && _filmRamp is { } ramp) DisplayHdr.WriteRamp(now.GdiName, ramp);
            _filmHdrOn = null;
            _filmRamp = null;
            DeleteFilmMarker();
            _log.Write($"hdr: HDR back off for this screen, {why}");
            return true;
        }
    }

    /// <summary>The retries after a refused restore, a second apart. An HDR title playing by then keeps HDR on for itself (its end holds or restores).</summary>
    private async Task RetryRestoreFilmHdr(string why)
    {
        try
        {
            for (var attempt = 2; HdrFilms.HasRetryLeft(attempt); attempt++)
            {
                await Task.Delay(HdrFilms.RestoreRetryMs);
                if (HdrTitlePlayingNow())
                {
                    _log.Write("hdr: an HDR title is playing; HDR stays on until it ends");
                    return;
                }
                if (TryRestoreFilmHdr($"{why}, try {attempt} of {HdrFilms.RestoreAttempts}")) return;
            }
            _log.Write("hdr: Windows still refuses to turn HDR off; the marker stays and the next start tries again");
        }
        catch (Exception ex) { _log.Write("hdr: restore: " + ex.GetType().Name); }
    }

    /// <summary>At start: a marker from a run that ended without switching HDR back turns it off, if that screen is still in HDR.</summary>
    private void RecoverFilmHdr()
    {
        try
        {
            if (!File.Exists(FilmMarkerPath)) return;
            var marker = HdrFilms.ParseMarker(File.ReadAllText(FilmMarkerPath));
            var screen = marker is null ? null : DisplayHdr.Find(marker.DevicePath);
            var action = HdrFilms.AtStart(true, screen is not null, screen?.Enabled == true);
            if (action == HdrFilms.StartAction.TurnOff)
            {
                // The gamma ramp from before the crash is not in the marker, so it is not put back (Windows keeps the one it has).
                if (!DisplayHdr.Set(screen!, false))
                {
                    _log.Write("hdr: Windows did not turn HDR off at start; tried again next start");
                    return;
                }
                _log.Write("hdr: HDR turned back off; the last run ended before it could");
            }
            DeleteFilmMarker();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _log.Write("hdr: marker: " + ex.GetType().Name); }
    }

    private static void DeleteFilmMarker()
    {
        try { File.Delete(FilmMarkerPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>What this play's PC and connection decide, as one line in app.log (PlaybackPlan): read before the video loads.</summary>
    private void LogPlan()
    {
        try
        {
            var (total, free) = SystemMemory.Read();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var measured = LinkSpeed.MeasuredAt;
            var screen = DisplayHdr.ForWindow(_hwnd);
            var plan = PlaybackPlan.Decide(new PlaybackPlan.Input(
                PlayerTuning.Card, PlayerTuning.OnBattery(), GameRunning(), total, free,
                LinkSpeed.Fresh(now), measured is > 0 and var at ? (now - at) / 86_400_000.0 : null,
                NeedMbps: null, BytesPerSecond: null, RemainingSeconds: 0,
                RefreshHz: screen?.RefreshHz, DisplayHdr: screen?.Supported));
            _log.Write(plan.Line);
        }
        catch (Exception e) { _log.Write("plan: " + AppLog.Kind(e)); }
    }
}
