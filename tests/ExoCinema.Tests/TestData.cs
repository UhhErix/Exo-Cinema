using System.Runtime.CompilerServices;

namespace ExoCinema.Tests;

/// <summary>
/// The tests' own data folder. The app's code keeps its log, reports and caches under AppInfo.Data, which is the real
/// %LOCALAPPDATA%\ExoCinema unless EXO_CINEMA_DATA names another. Until 2026-11-12 the suite ran on the real one: every
/// run started app.log afresh there, so the owner's last two logs were replaced by test lines each time (found when a
/// "refused a server request" line from a test turned up in his app.prev.log). The variable is set here before any test
/// or any of the app's code runs, and the folder goes when the run ends.
/// </summary>
internal static class TestData
{
    internal static readonly string Folder = Path.Combine(Path.GetTempPath(), "exo-cinema-tests", "data-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Use()
    {
        Directory.CreateDirectory(Folder);
        Environment.SetEnvironmentVariable("EXO_CINEMA_DATA", Folder);
        // A test copy sends no reports by itself (CrashReport.SendingOffFor). The suite's sends go to a fake transport,
        // so it asks for them.
        Environment.SetEnvironmentVariable("EXO_CINEMA_REPORT_SEND", "1");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Folder, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        };
    }
}

public sealed class TestDataTests
{
    [Fact]
    public void The_suite_never_runs_on_the_real_data_folder()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoCinema");
        Assert.True(AppInfo.OwnData);
        Assert.Equal(Path.GetFullPath(TestData.Folder), AppInfo.Data);
        Assert.False(AppInfo.Data.StartsWith(real, StringComparison.OrdinalIgnoreCase), AppInfo.Data);
        // The shared log is under it: writing a line makes the file there, and nothing in the real folder.
        AppLog.Shared.Write("test: the suite's own log");
        AppLog.Shared.Flush();
        Assert.True(File.Exists(Path.Combine(TestData.Folder, "app.log")));
    }
}
