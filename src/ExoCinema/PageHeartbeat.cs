namespace ExoCinema;

/// <summary>
/// Page freezes: the page's own event loop not answering. app.js posts a heartbeat every second over the page bridge; the
/// host records when the last one came. A page silent for more than five seconds while the window is in view is a freeze,
/// reported once as <see cref="ErrorType"/> (UiWatchdog covers the UI thread itself, as "Hang.Ui").
/// </summary>
internal static class PageHeartbeat
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    /// <summary>The report type of a page freeze (CrashReport.Keep); the UI thread's is "Hang.Ui".</summary>
    public const string ErrorType = "Hang.Page";

    /// <summary>Whether the page has been silent long enough to call it a freeze. Never while the window is away.</summary>
    public static bool Stuck(TimeSpan sinceBeat, bool windowVisible) => windowVisible && sinceBeat > Limit;
}
