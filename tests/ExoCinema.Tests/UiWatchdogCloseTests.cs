namespace ExoCinema.Tests;

/// <summary>The window closing under the freeze watch ends the watch; it never ends the app.</summary>
public sealed class UiWatchdogCloseTests
{
    [Theory]
    [InlineData(typeof(NullReferenceException))]   // the closed window's dispatcher is null
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(System.Runtime.InteropServices.COMException))]
    public void A_dispatcher_that_is_gone_ends_the_watch_quietly(Type thrown)
    {
        var froze = 0;
        using var stop = new CancellationTokenSource();
        var thread = UiWatchdog.Start(
            _ => throw (Exception)Activator.CreateInstance(thrown, "gone")!,
            () => "nothing",
            (_, _) => Interlocked.Increment(ref froze),
            stop.Token);
        // The first ask comes after UiWatchdog.Every; the thread must end by itself, without the token.
        Assert.True(thread.Join(UiWatchdog.Every + TimeSpan.FromSeconds(4)), "the watch kept running after its dispatcher was gone");
        Assert.Equal(0, froze);
    }
}

/// <summary>A test copy's reports stay on its disk.</summary>
public sealed class ReportSendingTests
{
    [Theory]
    [InlineData(false, null, null, false)]  // the installed app sends
    [InlineData(false, "1", null, true)]    // unless told never to
    [InlineData(true, null, null, true)]    // a test copy sends nothing
    [InlineData(true, null, "1", false)]    // unless a test asks for the send itself
    [InlineData(true, "1", "1", true)]      // and never wins
    public void Only_the_installed_app_sends_reports_by_itself(bool testCopy, string? never, string? testSends, bool off) =>
        Assert.Equal(off, CrashReport.SendingOffFor(testCopy, never, testSends));
}
