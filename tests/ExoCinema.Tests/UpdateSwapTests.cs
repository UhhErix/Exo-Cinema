using System.Diagnostics;
using ExoCinema.Updates;
using Microsoft.Win32;

namespace ExoCinema.Tests;

/// <summary>
/// The swap of a file-level update (src\ExoCinema\Updates\UpdateSwap.cs), run for real: the script the app writes, in
/// Windows PowerShell, on a throwaway folder, with its registry entries under a throwaway key.
/// How it could fail: a swap that is cut short at any step leaves no app folder, or half of one; a file held open, or a
/// staged folder that was changed after it was checked, still gets swapped in; the previous version isn't put back; a
/// failed version is tried the same way again instead of through Setup; or leftovers and the RunOnce entry stay behind.
/// </summary>
public sealed class UpdateSwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-cinema-swap-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _key = @"Software\ExoCinemaTests\" + Guid.NewGuid().ToString("N");
    private string App => Path.Combine(_root, "app");
    private string Staged => Path.Combine(_root, "app.incoming-u4242");
    private static readonly string[] Required = ["ExoCinema.exe"];

    private static readonly Dictionary<string, byte[]> Old = new()
    {
        ["ExoCinema.exe"] = FileUpdateTests.Bytes("host 1"),
        ["ExoCinema.dll"] = FileUpdateTests.Bytes("app code, version one"),
        ["wwwroot/index.html"] = FileUpdateTests.Bytes("<html>one</html>"),
        ["wwwroot/old.js"] = FileUpdateTests.Bytes("gone in two"),
    };

    private static readonly Dictionary<string, byte[]> New = new()
    {
        ["ExoCinema.exe"] = FileUpdateTests.Bytes("host 1"),
        ["ExoCinema.dll"] = FileUpdateTests.Bytes("app code, version two!"),
        ["wwwroot/index.html"] = FileUpdateTests.Bytes("<html>one</html>"),
    };

    public UpdateSwapTests()
    {
        Put(App, Old, "1.0.0");
        Put(Staged, New, "2.0.0");
        using var uninstall = Registry.CurrentUser.CreateSubKey($@"{_key}\Uninstall\ExoCinemaTest");
        uninstall.SetValue("InstallLocation", App);
        uninstall.SetValue("DisplayVersion", "1.0.0");
        using var other = Registry.CurrentUser.CreateSubKey($@"{_key}\Uninstall\SomethingElse");
        other.SetValue("InstallLocation", @"C:\Elsewhere\app");
        other.SetValue("DisplayVersion", "7.7.7");
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false); } catch (UnauthorizedAccessException) { }
        // The shared parent goes too once no test's key is left under it.
        try { Registry.CurrentUser.DeleteSubKey(@"Software\ExoCinemaTests", throwOnMissingSubKey: false); } catch (InvalidOperationException) { } catch (UnauthorizedAccessException) { }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Put(string folder, IReadOnlyDictionary<string, byte[]> files, string version)
    {
        FileUpdateTests.WriteTree(folder, files);
        File.WriteAllBytes(Path.Combine(folder, PayloadManifest.FileName), FileUpdateTests.ManifestOf(version, files));
        File.WriteAllText(Path.Combine(folder, "Uninstall.exe"), "uninstaller");
    }

    /// <summary>Which release the app folder is, whole: "1.0.0", "2.0.0", or what is wrong with it.</summary>
    private string AppIs()
    {
        if (!Directory.Exists(App)) return "missing";
        foreach (var (version, files) in new[] { ("1.0.0", Old), ("2.0.0", New) })
        {
            var bytes = FileUpdateTests.ManifestOf(version, files);
            if (PayloadManifest.Parse(bytes, Required).Mismatch(App, bytes) is null) return version;
        }
        return "neither release";
    }

    private string[] Leftovers() => Directory.EnumerateDirectories(_root).Select(Path.GetFileName).Where(n => n != "app").ToArray()!;

    private string? RunOnce()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{_key}\RunOnce");
        return key?.GetValueNames().Select(n => key.GetValue(n) as string).FirstOrDefault();
    }

    private string? ShownVersion(string entry = "ExoCinemaTest")
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{_key}\Uninstall\{entry}");
        return key?.GetValue("DisplayVersion") as string;
    }

    private string[] TestKeys => ["-RunOnceKey", $@"HKCU:\{_key}\RunOnce", "-UninstallKey", $@"HKCU:\{_key}\Uninstall"];

    private int Run(params string[] arguments)
    {
        var script = Path.Combine(_root, UpdateSwap.ScriptName);
        File.WriteAllText(script, UpdateSwap.Script, System.Text.Encoding.ASCII);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root };
        foreach (var a in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Root", _root, "-NoLaunch", .. TestKeys, .. arguments]) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        Assert.True(p.WaitForExit(60_000), "the swap script didn't finish");
        return p.ExitCode;
    }

    private int Swap(params string[] more) => Run(["-Version", "2.0.0", "-Incoming", "app.incoming-u4242", .. more]);

    [Fact]
    public void A_staged_release_is_swapped_in_whole_and_nothing_is_left_beside_it()
    {
        Assert.Equal(0, Swap());
        Assert.Equal("2.0.0", AppIs());
        Assert.False(File.Exists(Path.Combine(App, "wwwroot", "old.js"))); // a file the release dropped is gone
        Assert.Empty(Leftovers());
        Assert.Equal(new UpdateState("2.0.0", UpdateState.Done, ""), UpdateState.Read(_root));
        Assert.Null(RunOnce());
        Assert.Equal("2.0.0", ShownVersion()); // Installed apps shows the new version
        Assert.Equal("7.7.7", ShownVersion("SomethingElse")); // and no other app's entry is touched
        Assert.False(File.Exists(Path.Combine(_root, UpdateSwap.ReadyName)));
    }

    // Each step the script can be cut off after, and the release the app must be once -Recover has run.
    [Theory]
    [InlineData("ready", "1.0.0", UpdateState.Failed)]
    [InlineData("verified", "1.0.0", UpdateState.Failed)]
    [InlineData("journal", "1.0.0", UpdateState.Failed)]
    [InlineData("renamed-old", "1.0.0", UpdateState.Failed)]
    [InlineData("renamed-new", "2.0.0", UpdateState.Done)]
    public void A_swap_cut_short_at_any_step_recovers_to_a_whole_app(string step, string then, string state)
    {
        new UpdateState("2.0.0", UpdateState.Staged, "").Write(_root); // as Arm leaves it
        Assert.NotEqual(0, Swap("-StopAfter", step)); // killed, as a crash would
        if (step == "renamed-old")
        {
            // The one moment with no app folder: the previous one is kept beside it, and Windows' next sign-in runs -Recover.
            Assert.Equal("missing", AppIs());
            Assert.Contains("-Recover", RunOnce());
            Assert.Contains(_root, RunOnce());
        }
        else Assert.Contains(AppIs(), new[] { "1.0.0", "2.0.0" }); // whole at every other moment, before any recovery

        Assert.Equal(0, Run("-Recover"));
        Assert.Equal(then, AppIs());
        Assert.Empty(Leftovers());
        Assert.Null(RunOnce());
        var after = UpdateState.Read(_root);
        Assert.Equal("2.0.0", after?.Version);
        Assert.Equal(state, after?.State);
        if (then == "2.0.0") Assert.Equal("2.0.0", ShownVersion());
        // -Recover again changes nothing.
        Assert.Equal(0, Run("-Recover"));
        Assert.Equal(then, AppIs());
    }

    [Fact]
    public void A_file_held_open_stops_the_swap_with_the_installed_app_untouched()
    {
        using (new FileStream(Path.Combine(App, "ExoCinema.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(1, Swap("-RenameTries", "2"));
        }
        Assert.Equal("1.0.0", AppIs());
        Assert.Empty(Leftovers());
        Assert.Equal(new UpdateState("2.0.0", UpdateState.Failed, "in-use"), UpdateState.Read(_root));
        Assert.Null(RunOnce());
        Assert.Equal("1.0.0", ShownVersion());
    }

    [Theory]
    [InlineData("altered")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("other-version")]
    public void A_staged_folder_that_is_not_the_release_is_never_swapped_in(string fault)
    {
        switch (fault)
        {
            case "altered": File.WriteAllBytes(Path.Combine(Staged, "ExoCinema.dll"), FileUpdateTests.Bytes("app code, version TWO!")); break;
            case "extra": File.WriteAllText(Path.Combine(Staged, "wwwroot", "planted.js"), "x"); break;
            case "missing": File.Delete(Path.Combine(Staged, "wwwroot", "index.html")); break;
            case "other-version": File.WriteAllBytes(Path.Combine(Staged, PayloadManifest.FileName), FileUpdateTests.ManifestOf("2.0.1", New)); break;
        }
        Assert.Equal(1, Swap());
        Assert.Equal("1.0.0", AppIs());
        Assert.Empty(Leftovers());
        var state = UpdateState.Read(_root);
        Assert.Equal(UpdateState.Failed, state?.State);
        Assert.StartsWith("staged-mismatch", state?.Reason);
    }

    [Fact]
    public void An_app_folder_that_went_missing_is_put_back_from_the_kept_previous_one()
    {
        // As a power cut between the two renames leaves it, with no state file to go by.
        Directory.Move(App, Path.Combine(_root, "app.previous-u77"));
        Assert.Equal(0, Run("-Recover"));
        Assert.Equal("1.0.0", AppIs());
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void Recovery_with_no_app_and_nothing_kept_says_so_and_removes_nothing()
    {
        Directory.Move(App, Path.Combine(_root, "elsewhere"));
        Assert.Equal(1, Run("-Recover"));
        Assert.True(Directory.Exists(Staged)); // not ours to delete while there is no app
    }

    [Fact]
    public void The_app_arms_the_swap_and_gets_a_token_that_is_not_a_Setup_to_run()
    {
        var staged = new StagedUpdate(_root, Staged, "2.0.0", 0, 1, 2);
        var token = UpdateSwap.Arm(staged, appPid: 0, launch: false, more: TestKeys);
        Assert.True(UpdateSwap.IsToken(token));
        Assert.False(UpdateSwap.IsToken(Path.Combine(_root, "updates", "ExoCinema-Setup-2.0.0.exe")));
        var until = Stopwatch.StartNew();
        while (UpdateState.Read(_root)?.State != UpdateState.Done && until.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(100);
        Assert.Equal(new UpdateState("2.0.0", UpdateState.Done, ""), UpdateState.Read(_root));
        Assert.Equal("2.0.0", AppIs());
    }

    [Fact]
    public void The_next_start_tidies_up_a_swap_that_never_ran()
    {
        new UpdateState("2.0.0", UpdateState.Staged, "").Write(_root);
        File.WriteAllText(Path.Combine(_root, UpdateSwap.ReadyName), "999999"); // the script that wrote it is long gone
        var log = new List<string>();
        UpdateSwap.OnStart(App, log.Add);
        var until = Stopwatch.StartNew();
        while (UpdateState.Read(_root)?.State != UpdateState.Failed && until.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(100);
        Assert.Equal(new UpdateState("2.0.0", UpdateState.Failed, "interrupted"), UpdateState.Read(_root));
        Assert.Equal("1.0.0", AppIs());
        while (Leftovers().Length > 0 && until.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(100);
        Assert.Empty(Leftovers());
        Assert.Single(log);
    }

    [Fact]
    public void A_start_with_nothing_unfinished_runs_nothing()
    {
        Directory.Delete(Staged, recursive: true);
        var log = new List<string>();
        UpdateSwap.OnStart(App, log.Add);
        Assert.Empty(log);
        Assert.False(File.Exists(Path.Combine(_root, UpdateSwap.ScriptName)));
    }
}
