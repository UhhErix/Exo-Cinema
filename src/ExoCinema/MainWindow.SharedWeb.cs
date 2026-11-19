using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace ExoCinema;

/// <summary>
/// One WebView2 for Exo's apps (L1, Exo Launcher's research/shared-webview-spec.md, v1): Launcher, Cinema and Exo OS
/// share one user data folder, %LOCALAPPDATA%\Exo\WebView2, so one browser process and one GPU process serve all three;
/// each keeps its own profile ("cinema" here), so their data stays apart.
/// <list type="bullet">
/// <item>Joined only while Launcher's marker (exo-shared.json, "v":1) is there: Launcher moves its own folder in first
/// (its cookie keys live in the folder's Local State). Cinema never makes the folder itself.</item>
/// <item>The environment exactly as the spec says, or WebView2 refuses the second app: <see cref="SharedArguments"/>,
/// extensions on, the folder not exclusive. A test run adds only the shared debugging port, first.</item>
/// <item>Cinema's localStorage (its library cache, Play next episode, each show's languages) is carried from its old
/// folder into the "cinema" profile once, before the profile is first made.</item>
/// <item>Anything failing: Cinema uses its old folder for that run and says so in app.log; it never retries the shared
/// folder with other options.</item>
/// <item>Lean lowers only Cinema's own renderer, never the shared browser, GPU or utility processes.</item>
/// </list>
/// A test copy on its own data never shares, unless EXO_SHARED_WEBVIEW_DIR names a test group's folder.
/// </summary>
public sealed partial class MainWindow
{
    internal const string SharedArguments = "--disable-features=msWebOOUI,msPdfOOUI,SpareRendererForSitePerProcess,AIPromptAPI,AISummarizationAPI,AIWriterAPI,AIRewriterAPI,AIProofreadingAPI,AIEmbeddingsAPI,LanguageDetectionAPI,TranslationAPI,msEdgeLanguageDetectionAPI,msEdgeTranslationAPI --process-per-site";
    private const string SharedProfile = "cinema";

    private static readonly string SharedFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exo", "WebView2");

    /// <summary>Whether this run's WebView2 is the shared one (and Lean must touch only Cinema's renderer).</summary>
    private bool _sharedWeb;
    private int[] _ownRenderers = [];

    /// <summary>A test group's own shared folder (Browser's stress test, never the live one): with it, a test copy joins
    /// that folder under the same marker rule. Without it, test copies never share.</summary>
    private static string? TestSharedFolder => Environment.GetEnvironmentVariable("EXO_SHARED_WEBVIEW_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary>The shared folder when its marker says it's ready (v1), else null.</summary>
    private static string? SharedFolder()
    {
        var folder = TestSharedFolder ?? (AppInfo.OwnData ? null : SharedFolderPath);
        if (folder is null) return null;
        try
        {
            var marker = Path.Combine(folder, "exo-shared.json");
            return File.Exists(marker) && JsonNode.Parse(File.ReadAllText(marker))?["v"] is JsonValue v && v.TryGetValue<int>(out var n) && n == 1 ? folder : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// Cinema's localStorage, from its old folder into the shared profile's folder (WebView2 names it "WV2Profile_cinema"),
    /// once, before the profile is opened. Marked done by a file beside it. A first build copied it into "cinema" (the
    /// profile's name, not its folder): that copy goes, and the carry runs again over the profile WebView2 made, which
    /// held nothing yet that the old folder hadn't.
    /// </summary>
    private void CarryLocalStorage(string oldFolder, string shared)
    {
        var profile = Path.Combine(shared, "EBWebView", "WV2Profile_" + SharedProfile);
        var done = Path.Combine(shared, "EBWebView", "exo-cinema-carried.json");
        var from = Path.Combine(oldFolder, "EBWebView", "Default", "Local Storage");
        if (File.Exists(done) || !Directory.Exists(from)) return;
        try
        {
            var wrong = Path.Combine(shared, "EBWebView", SharedProfile);
            if (Directory.Exists(wrong)) Directory.Delete(wrong, recursive: true);
            var to = Path.Combine(profile, "Local Storage");
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            File.WriteAllText(done, "{\"v\":1}");
            _log.Write("web: Cinema's saved page data carried into the shared WebView2");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.Write($"web: page data not carried ({AppLog.Kind(e)})"); }
    }

    /// <summary>The environment and controller: the shared ones when Launcher's marker is there, else (or on any failure)
    /// Cinema's own folder, as before.</summary>
    private async Task<(CoreWebView2Environment Env, CoreWebView2Controller Controller)> CreateWebAsync(string oldFolder)
    {
        var cdp = Environment.GetEnvironmentVariable("EXO_CINEMA_CDP") is { Length: > 0 } port ? port : null;
        if (SharedFolder() is { } shared)
        {
            try
            {
                CarryLocalStorage(oldFolder, shared);
                var options = new CoreWebView2EnvironmentOptions
                {
                    // A live run's debugging port is the shared 9333; a test group's is its own.
                    AdditionalBrowserArguments = cdp is null ? SharedArguments : $"--remote-debugging-port={(TestSharedFolder is null ? "9333" : cdp)} " + SharedArguments,
                    AreBrowserExtensionsEnabled = true,
                    ExclusiveUserDataFolderAccess = false,
                };
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, shared, options);
                var controller = await env.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_hwnd), ControllerOptions(env, SharedProfile));
                _sharedWeb = true;
                _log.Write("web: the shared Exo WebView2 (profile cinema)");
                return (env, controller);
            }
            catch (Exception e) { _log.Write($"web: the shared WebView2 refused ({AppLog.Kind(e)}); Cinema's own folder this run"); }
        }
        var own = new CoreWebView2EnvironmentOptions();
        if (cdp is not null) own.AdditionalBrowserArguments = $"--remote-debugging-port={cdp}";
        var ownEnv = await CoreWebView2Environment.CreateWithOptionsAsync(null, oldFolder, own);
        return (ownEnv, await ownEnv.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_hwnd), ControllerOptions(ownEnv, null)));
    }

    // A windowed WebView2, a child of this window: WinUI's WebView2 control always paints an opaque base color
    // (microsoft-ui-xaml#6527, still open; its transparent becomes the theme's page color), which would hide mpv's
    // picture. A windowed one honors a transparent background. Per controller, not environment.
    private static CoreWebView2ControllerOptions ControllerOptions(CoreWebView2Environment env, string? profile)
    {
        var o = env.CreateCoreWebView2ControllerOptions();
        o.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        if (profile is not null) o.ProfileName = profile;
        return o;
    }

    /// <summary>Cinema's own renderers in the shared WebView2 (those drawing https://cinema.exo), read on the UI thread
    /// whenever Lean is about to step back.</summary>
    private async Task ReadOwnRenderersAsync()
    {
        if (!_sharedWeb || _core is not { } core) return;
        try
        {
            var infos = await core.Environment.GetProcessExtendedInfosAsync();
            _ownRenderers = [.. infos.Where(i => i.ProcessInfo.Kind == CoreWebView2ProcessKind.Renderer
                    && i.AssociatedFrameInfos.Any(f => f.Source.StartsWith("https://cinema.exo", StringComparison.Ordinal)))
                .Select(i => i.ProcessInfo.ProcessId)];
            // Read after Lean stepped back: they step back now.
            if (_leanOn) foreach (var web in LeanWebProcesses()) using (web) Step(web, true);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
    }

    /// <summary>The WebView2 processes Lean may lower: the browser process in Cinema's own folder; in the shared one,
    /// only Cinema's renderers.</summary>
    private List<Process> LeanWebProcesses()
    {
        var list = new List<Process>();
        if (!_sharedWeb)
        {
            if (BrowserProcess() is { } browser) list.Add(browser);
            return list;
        }
        foreach (var pid in _ownRenderers)
            try { list.Add(Process.GetProcessById(pid)); }
            catch (ArgumentException) { }
        return list;
    }
}
