using Microsoft.UI.Xaml;

namespace ExoCinema;

public partial class App : Application
{
    public static MainWindow? MainAppWindow { get; private set; }

    /// <summary>%LOCALAPPDATA%\ExoCinema\crash.log: what ended Exo Cinema, when something did.</summary>
    private static readonly string CrashLog = Path.Combine(AppInfo.Data, "crash.log");

    public App()
    {
        InitializeComponent();
        UiGuard.Sink = WriteCrash;
        UnhandledException += (_, e) => WriteCrash("xaml", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash("domain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => WriteCrash("task", e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainAppWindow = new MainWindow(LaunchArgs.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray()));
        MainAppWindow.Show();
    }

    internal static void WriteCrash(string where, Exception? e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.AppendAllText(CrashLog, AppLog.Clean($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {where}: {AppLog.Trace(e)}\n\n"));
        }
        catch (IOException) { }
        // The report for the next start (CrashReport): the log's last lines and this error, scrubbed. Nothing is sent from a crash.
        try
        {
            CrashReport.Keep(null, $"{where}: {AppLog.Trace(e)}", AppLog.Shared.Tail(200).Split('\n'), AppInfo.Data);
        }
        catch (Exception) { }
    }
}

#if DEBUG
/// <summary>The test-only crash for the crash-report check (EXO_CINEMA_TEST_CRASH on a test copy). Not in a release build.</summary>
internal sealed class SmokeTestException(string message) : Exception(message);
#endif
