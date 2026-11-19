using System.Runtime.InteropServices;
using ExoCinema.Mpv;

namespace ExoCinema;

/// <summary>
/// mpv's options, picked from the PC it runs on rather than from one machine: the graphics card it draws on
/// (GraphicsCard, chosen by PictureRules.AdapterPreference), the memory and the power.
/// </summary>
internal static class PlayerTuning
{
    // Read once, the first time the player needs it (MainWindow asks early, off the UI thread): the card stays the
    // player's until the app closes.
    private static readonly Lazy<GraphicsCard> TheCard = new(() => GraphicsCard.Read(PictureRules.AdapterPreference(OnBattery(), GraphicsCard.WindowsChoice())));

    public static GraphicsCard Card => TheCard.Value;
    public static string Gpu => Card.Name;
    public static string Vendor => Card.Vendor;

    // Integrated graphics get mpv's default scalers; dedicated cards get its high-quality profile.
    internal static bool Strong => Card.Strong;

    /// <summary>The card for mpv's d3d11-adapter, so mpv draws where the choices were made for; null leaves mpv its own.</summary>
    public static string? Adapter => Card == GraphicsCard.Unknown ? null : Card.Name;

    public static Dictionary<string, string> Options()
    {
        var o = new Dictionary<string, string>
        {
            ["vo"] = "gpu-next",
            ["gpu-api"] = "d3d11",
            ["gpu-context"] = "d3d11",
            ["hwdec"] = "auto-safe",
            // On an HDR display mpv switches the swapchain to HDR itself; on SDR it tone-maps.
            ["target-colorspace-hint"] = "yes",
            ["keep-open"] = "yes",
            ["idle"] = "yes",
            ["osc"] = "no",
            ["osd-level"] = "0",
            ["input-default-bindings"] = "no",
            ["input-vo-keyboard"] = "no",
            ["input-cursor"] = "no",
            ["cursor-autohide"] = "no",
            ["ytdl"] = "no",
            // Buffering: read as far ahead as memory allows, so a fast link banks minutes to ride out a slow spell;
            // after running dry, wait for 3 s of picture, so a link a little slower than the video pauses once, not
            // every second (1 s, mpv's own, paused 4 times where this pauses once); and a server that drops the
            // connection gets asked again from where it stopped.
            ["cache"] = "yes",
            // The bytes decide how far ahead and behind (ReadAhead.Size, re-sized live while a film plays); the seconds
            // are no limit: a whole film at most.
            ["demuxer-max-bytes"] = StartPlan.ForwardBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["demuxer-max-back-bytes"] = StartPlan.BackBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["demuxer-readahead-secs"] = "43200",
            // Before the first frame playback waits for the read-ahead the link needs (MainWindow.Buffer.cs, StartBuffer),
            // and after it runs dry it waits the same way; cache-pause-wait is that wait, set from the link's speed.
            ["cache-pause-initial"] = "yes",
            ["cache-pause-wait"] = "6",
            ["stream-lavf-o"] = "reconnect=1,reconnect_streamed=1,reconnect_on_network_error=1,reconnect_delay_max=5",
        };
        // Plain subtitles (SRT, WebVTT) in Exo's type, Geist, white with a dark edge and a soft shadow; styled ones (the
        // ASS of most anime) keep the look they were made with, their own fonts included.
        var fonts = Path.Combine(AppContext.BaseDirectory, "wwwroot", "fonts");
        if (Directory.Exists(fonts))
        {
            o["sub-fonts-dir"] = fonts;
            o["sub-font"] = "Geist";
        }
        o["sub-bold"] = "yes";
        o["sub-font-size"] = "40";
        o["sub-border-size"] = "2.4";
        o["sub-shadow-offset"] = "1.5";
        o["sub-shadow-color"] = "#80000000";
        // Compiled shaders are kept: libmpv has no folder of its own for them, so every start and every switch to the
        // anime upscaler compiled from scratch (3 s with the picture frozen; 0.06 s from the cache).
        try { Directory.CreateDirectory(Path.Combine(ShaderCache, "peek")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        o["gpu-shader-cache-dir"] = ShaderCache;
        o["icc-cache-dir"] = ShaderCache;
        // The player's glass: mpv blurs the picture under the page's glass itself (MainWindow.Glass.cs).
        if (File.Exists(MainWindow.GlassShader)) o["glsl-shaders"] = MainWindow.GlassShader;
        if (Adapter is { } adapter) o["d3d11-adapter"] = adapter;
        if (Strong)
        {
            o["profile"] = "high-quality";
            o["dscale"] = HighDownscale;
        }
        return o;
    }

    /// <summary>
    /// High quality's downscaler: Catmull-Rom, sharper than mpv's default (hermite) when a picture is shrunk, as a 4K
    /// film on a 1080p screen or the anime upscaler's 2:1 is; hermite's softness cost what the upscaler had gained on
    /// the picture bench. The high-quality profile sets only the upscaler.
    /// </summary>
    public const string HighDownscale = "catmull_rom";

    /// <summary>Where mpv keeps the shaders it has compiled for this PC's graphics card.</summary>
    public static string ShaderCache { get; } = Path.Combine(AppInfo.Data, "cache", "shaders");

    /// <summary>The read-ahead and read-behind for mpv's start, from the memory free now (ReadAhead.Size); the player
    /// re-sizes them while a film plays (MainWindow.Player.cs, ReadAheadTick).</summary>
    public static ReadAhead.Plan StartPlan
    {
        get
        {
            var (total, available) = SystemMemory.Read();
            return ReadAhead.Size(total, available);
        }
    }

    /// <summary>Running on battery: the picture goes lighter (GetSystemPowerStatus; a desktop is always on mains).</summary>
    public static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static object Describe() => new { gpu = Gpu, vendor = Vendor };
}
