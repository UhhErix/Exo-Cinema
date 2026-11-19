namespace ExoCinema;

/// <summary>
/// Every callback that runs on the UI thread goes through <see cref="Run"/>: a posted action (UiDispatch), a WebView2
/// event, a timer's tick. A throw is handed to <see cref="Sink"/> (App's crash writer: crash.log and the report for the
/// next start) and then thrown on, so the process ends as it did. The dispatcher's own handler does not always run for
/// a throw from a posted callback, so the writing is not left to it.
/// </summary>
internal static class UiGuard
{
    /// <summary>Where a UI-thread throw goes first, with the place it came from. App sets it to its crash writer.</summary>
    internal static Action<string, Exception>? Sink { get; set; }

    internal static void Run(Action action, string where = "ui")
    {
        try { action(); }
        catch (Exception e)
        {
            Sink?.Invoke(where, e);
            throw;
        }
    }
}
