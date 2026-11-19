// A walk-through of one update on a test install, with the app's own update code (misc\updates\file-level-updates.md):
//   dotnet run --project tools\update-walkthrough -- --app <test root>\app --base http://127.0.0.1:8787/v1/cinema --version X.Y.Z [--with-app <data folder>]
// It does what AppUpdater.DownloadAsync does for an installed copy: the file-level update first, and the full Setup
// (downloaded, checked against its .sha256, run silently) when that can't be used. It prints which way the update went,
// the bytes it downloaded and how long each part took. Only a loopback address is taken, and never the real install.
//   --with-app <data folder>   the test install is opened first (on that data folder, never yours) and asked to close once
//                              the swap is waiting for it, as the app does itself; then the new version is opened for a
//                              few seconds to see that it starts.
//   --stop-after <step>        the swap is cut off after that step (ready, verified, journal, renamed-old, renamed-new),
//                              as a power cut would; then the RunOnce entry it left is run as Windows would at the next
//                              sign-in, and the app folder must be a whole version again.
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoCinema.Updates;

string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var app = Path.GetFullPath(Option("--app") ?? throw new ArgumentException("--app <folder> is needed."));
var address = new Uri(Option("--base") ?? throw new ArgumentException("--base <address> is needed."));
var version = Option("--version") ?? throw new ArgumentException("--version X.Y.Z is needed.");
var data = Option("--with-app");
var stopAfter = Option("--stop-after");
if (!address.IsLoopback) throw new ArgumentException("Only an update service on this PC (a loopback address).");
var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoCinema");
if (app.StartsWith(real, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Not on the real Exo Cinema: use a -TestRoot install.");
var root = Path.GetDirectoryName(app)!;
var exe = Path.Combine(app, "ExoCinema.exe");
var baseUrl = address.ToString().TrimEnd('/');

long wire = 0;
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(5) };
async Task<HttpResponseMessage> Get(string url, CancellationToken ct)
{
    var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
    if (r.IsSuccessStatusCode) return r;
    r.Dispose();
    throw new HttpRequestException($"{new Uri(url).Host} answered {(int)r.StatusCode}.");
}

Process? Open(string folder)
{
    var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = app, WindowStyle = ProcessWindowStyle.Minimized };
    start.Environment["EXO_CINEMA_DATA"] = folder;
    return Process.Start(start);
}

string InstalledVersion() => (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(app, PayloadManifest.FileName)))?["version"] ?? "?";

Console.WriteLine($"installed: {InstalledVersion()}  ->  {version}   ({app})");
long? setupBytes = null;
try
{
    var latest = JsonNode.Parse(await http.GetStringAsync($"{baseUrl}/latest"));
    setupBytes = (long?)latest?["checks"]?["setup"]?["size"];
}
catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException) { }

Process? running = null;
if (data is not null)
{
    running = Open(Path.GetFullPath(data));
    Thread.Sleep(8000); // its page and player are up: every file it holds open is held
    Console.WriteLine($"test install opened (pid {running?.Id}), {(running is { HasExited: false } ? "running" : "NOT running")}");
}

var clock = Stopwatch.StartNew();
var way = "file-level";
try
{
    var staged = await new FileUpdater(app, baseUrl, Get, Console.WriteLine).StageAsync(version, setupBytes, _ => { });
    wire = staged.BytesDownloaded;
    var stagedIn = clock.Elapsed;
    Console.WriteLine($"staged in {stagedIn.TotalSeconds:n1}s: {staged.FilesDownloaded} file(s) downloaded ({staged.BytesDownloaded:n0} bytes on the wire), {staged.FilesReused} kept");
    if (stopAfter is not null)
    {
        var was = InstalledVersion();
        try { UpdateSwap.Arm(staged, 0, launch: false, more: ["-StopAfter", stopAfter]); } catch (FileUpdateUnavailable) { } // cut off before it could say it was running
        Thread.Sleep(6000);
        Console.WriteLine($"cut off after '{stopAfter}': app folder {(File.Exists(exe) ? "present" : "MISSING")}, state {UpdateState.Read(root)?.State}");
        using var runOnce = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", writable: true)!;
        var name = runOnce.GetValueNames().FirstOrDefault(n => n.StartsWith("ExoUpdate-") && (runOnce.GetValue(n) as string ?? "").Contains(root, StringComparison.OrdinalIgnoreCase));
        if (name is null) Console.WriteLine("no RunOnce entry (none is set before the journal step); the next start's check is what runs");
        else
        {
            var command = (string)runOnce.GetValue(name)!;
            Console.WriteLine($"RunOnce: {command}");
            runOnce.DeleteValue(name); // Windows removes the entry before it runs it
            using var recover = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{command}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            recover.WaitForExit();
        }
        if (name is null) { UpdateSwap.OnStart(app, Console.WriteLine); Thread.Sleep(6000); }
        var after = UpdateState.Read(root);
        var leftovers = Directory.EnumerateDirectories(root, "app.*").Select(Path.GetFileName).Where(n => n != "app").ToArray();
        Console.WriteLine($"RESULT way=recovered installed={(File.Exists(exe) ? InstalledVersion() : "MISSING")} was={was} state={after?.State} {after?.Reason} leftovers={leftovers.Length}");
        return File.Exists(exe) && leftovers.Length == 0 ? 0 : 1;
    }
    UpdateSwap.Arm(staged, running?.Id ?? 0, launch: false);
    running?.CloseMainWindow(); // as the app does after it starts the swap
    while (UpdateState.Read(root) is not { State: UpdateState.Done or UpdateState.Failed } && clock.Elapsed < TimeSpan.FromMinutes(3)) Thread.Sleep(50);
    var state = UpdateState.Read(root);
    Console.WriteLine($"swap: {state?.State} {state?.Reason} in {(clock.Elapsed - stagedIn).TotalSeconds:n1}s");
    if (state?.State != UpdateState.Done) throw new FileUpdateUnavailable("swap-" + state?.Reason, "The swap didn't finish.");
}
catch (Exception e) when (FileUpdater.FallsBack(e))
{
    way = "setup";
    Console.WriteLine($"Setup is used instead of a file-level update ({(e as FileUpdateUnavailable)?.Reason ?? e.GetType().Name}: {e.Message})");
    // As AppUpdater does: Setup and its .sha256 from the address, checked, then run with no window.
    var sum = Regex.Match(await http.GetStringAsync($"{baseUrl}/download/v{version}/ExoCinema-Setup.exe.sha256"), "^[0-9a-fA-F]{64}").Value;
    var folder = Path.Combine(root, "updates");
    Directory.CreateDirectory(folder);
    var setup = Path.Combine(folder, $"ExoCinema-Setup-{version}.exe");
    using (var r = await Get($"{baseUrl}/download/v{version}/ExoCinema-Setup.exe", default))
    await using (var to = File.Create(setup)) await r.Content.CopyToAsync(to);
    wire += new FileInfo(setup).Length;
    await using (var check = File.OpenRead(setup))
        if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(check)), sum, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Setup doesn't match its checksum.");
    Console.WriteLine($"Setup downloaded and checked in {clock.Elapsed.TotalSeconds:n1}s ({new FileInfo(setup).Length:n0} bytes)");
    using var install = Process.Start(new ProcessStartInfo(setup, "/S /NOLAUNCH") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root })!;
    install.WaitForExit();
    Console.WriteLine($"Setup exit {install.ExitCode}");
    File.Delete(setup);
}
clock.Stop();

if (data is not null)
{
    using var again = Open(Path.GetFullPath(data));
    Thread.Sleep(8000);
    var alive = again is { HasExited: false };
    Console.WriteLine($"new version opened: {(alive ? "running" : "NOT running")}, {FileVersionInfo.GetVersionInfo(exe).ProductVersion}");
    if (alive) { again!.CloseMainWindow(); if (!again.WaitForExit(15000)) again.Kill(); }
}
Console.WriteLine($"RESULT way={way} installed={InstalledVersion()} bytes={wire} seconds={clock.Elapsed.TotalSeconds:n1}");
return InstalledVersion() == version ? 0 : 1;
