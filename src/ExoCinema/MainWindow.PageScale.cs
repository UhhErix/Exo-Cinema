using System.Text.Json;
using System.Text.Json.Nodes;
using ExoCinema.Ui;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;

namespace ExoCinema;

/// <summary>
/// One layout on every screen, and the look every Exo app shares.
/// <list type="bullet">
/// <item>Page scale (as Exo Launcher's MainWindow.PageScale): the page is laid out as on a 1080p screen and drawn larger
/// to fill the window, with Chromium's device emulation (input is mapped back), at the window's size over the zoom, so
/// resizing still reflows it. 1 on a 1080p screen at 100%, 1.33 on 1440p at 100% or 4K at 150%, 2 on 4K at 100%. The
/// shared Text size multiplies it (small 0.9, large 1.14, Launcher's factors). The caption's regions take the same zoom.</item>
/// <item>Appearance: %LOCALAPPDATA%\Exo\appearance.json, written by Exo Launcher alone (Launcher's AppearanceShare):
/// {"appearance","material","reduceMotion","textColour","textSize"}. Given to the page before its first frame
/// (window.exoAppearance; boot.js applies it) and posted again whenever the file changes. No file: Windows decides.</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private static readonly string AppearanceFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exo", "appearance.json");

    /// <summary>The page's CSS pixels to the window's scaled pixels: 1 unless the screen is bigger than 1080p.</summary>
    private double PageZoom => _pageScaled.Zoom;

    private (int Width, int Height, double Zoom) _pageScaled = (0, 0, 1);
    private FileSystemWatcher? _appearanceWatch;
    private JsonObject? _appearance;

    /// <summary>The shared look as the file has it, only the fields and values it may hold; null with no file.</summary>
    private static JsonObject? ReadAppearance()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(AppearanceFile)) is not JsonObject o) return null;
            static string? Pick(JsonObject o, string name, params string[] allowed) =>
                o[name] is JsonValue v && v.TryGetValue<string>(out var s) && allowed.Contains(s) ? s : null;
            return new JsonObject
            {
                ["appearance"] = Pick(o, "appearance", "system", "light", "dark", "void") ?? "system",
                ["material"] = Pick(o, "material", "glass", "solid") ?? "glass",
                ["reduceMotion"] = o["reduceMotion"] is JsonValue r && r.TryGetValue<bool>(out var b) ? b : null,
                ["textSize"] = Pick(o, "textSize", "small", "normal", "large") ?? "normal",
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static double TextScale(JsonObject? a) => (string?)a?["textSize"] switch { "small" => 0.9, "large" => 1.14, _ => 1 };

    /// <summary>Before the page is navigated to: what it applies before its first frame, and the watch for changes.</summary>
    private async Task StartAppearanceAsync(CoreWebView2 core)
    {
        _appearance = ReadAppearance();
        await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.exoAppearance = {_appearance?.ToJsonString() ?? "null"}");
        try
        {
            var folder = Path.GetDirectoryName(AppearanceFile)!;
            Directory.CreateDirectory(folder);
            _appearanceWatch = new FileSystemWatcher(folder, "appearance.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, _) => OnUi(AppearanceChanged);
            _appearanceWatch.Changed += changed;
            _appearanceWatch.Created += changed;
            _appearanceWatch.Deleted += changed;
            _appearanceWatch.Renamed += (_, _) => OnUi(AppearanceChanged);
            _appearanceWatch.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { _log.Write($"appearance: not watched ({AppLog.Kind(e)})"); }
    }

    private void AppearanceChanged()
    {
        if (_closingWindow) return;
        var now = ReadAppearance();
        if (JsonNode.DeepEquals(now, _appearance)) return;
        var size = TextScale(now) != TextScale(_appearance);
        _appearance = now;
        PostJson(new JsonObject { ["type"] = "appearance", ["appearance"] = now?.DeepClone() }.ToJsonString());
        if (size) ApplyPageScale(again: true);
    }

    /// <summary>On a size, screen or scale change, after the page loads and when Text size changes.</summary>
    private void ApplyPageScale(bool again = false)
    {
        if (_core is not { } core || _controller is null) return;
        double zoom = 1;
        (double Width, double Height) screen = default;
        var scale = GetWindowScale();
        try
        {
            var bounds = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).OuterBounds;
            screen = (bounds.Width / scale, bounds.Height / scale);
            // EXO_PAGE_SCREEN=1440 (or 1080x1920) tries another screen's zoom on this one.
            if (PageScale.TryScreen(Environment.GetEnvironmentVariable("EXO_PAGE_SCREEN"), out var tried)) screen = tried;
            zoom = Math.Round(PageScale.ZoomFor(screen.Width, screen.Height) * TextScale(_appearance), 3);
        }
        catch { /* no screen to read while the window closes: the page stays as it is */ }
        var width = (int)Math.Round(_controller.Bounds.Width / scale / zoom);
        var height = (int)Math.Round(_controller.Bounds.Height / scale / zoom);
        if (width <= 0 || height <= 0) return;
        var was = _pageScaled.Zoom;
        if (!again && (width, height, zoom) == _pageScaled) return;
        _pageScaled = (width, height, zoom);
        if (zoom == 1 && was == 1 && !again) return;
        if (zoom != was) _log.Write($"page scale: {zoom:0.###}, laid out at {width}x{height} on a screen of {screen.Width:0}x{screen.Height:0}");
        // dontSetVisibleSize: the view stays the window's own size and the zoomed page fills it (Launcher's finding).
        var call = zoom == 1
            ? core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}")
            : core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                JsonSerializer.Serialize(new { width, height, deviceScaleFactor = 0, mobile = false, scale = zoom, dontSetVisibleSize = true }));
        _ = call.AsTask().ContinueWith(t => _log.Write("page scale: " + AppLog.Kind(t.Exception!.GetBaseException())), TaskContinuationOptions.OnlyOnFaulted);
        // The player's glass and pictures are sized in device pixels from the page's own (player.js › dpr).
        PostJson(new JsonObject { ["type"] = "page-zoom", ["zoom"] = zoom }.ToJsonString());
        if (zoom != was) UpdateCaptionDragRegions();
    }
}
