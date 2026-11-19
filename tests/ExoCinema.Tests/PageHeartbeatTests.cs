namespace ExoCinema.Tests;

/// <summary>
/// The page's freeze rule (PageHeartbeat.cs): five seconds of silence while the window is in view, reported as Hang.Page.
/// </summary>
public sealed class PageHeartbeatTests
{
    [Fact]
    public void A_silent_page_is_a_freeze_after_five_seconds_while_the_window_is_in_view()
    {
        Assert.True(PageHeartbeat.Stuck(TimeSpan.FromSeconds(5.1), windowVisible: true));
        Assert.False(PageHeartbeat.Stuck(TimeSpan.FromSeconds(4.9), windowVisible: true));
    }

    [Fact]
    public void A_silent_page_is_not_a_freeze_while_the_window_is_hidden_or_minimized()
    {
        Assert.False(PageHeartbeat.Stuck(TimeSpan.FromSeconds(60), windowVisible: false));
    }

    [Fact]
    public void The_page_freeze_report_is_typed_Hang_Page_and_the_service_takes_it()
    {
        Assert.Equal("Hang.Page", PageHeartbeat.ErrorType);
        var payload = CrashReport.Build("Hang.Page: the page didn't answer for 6 s", "[2026-11-05T19:42:10.1230000Z] WARN Page: silent.", "1.6.0", "10.0.26300.0", "tester", @"C:\Users\tester", "PC-TEST", [], PageHeartbeat.ErrorType);
        Assert.Equal("Hang.Page", payload.ErrorType);
        Assert.True(CrashReport.Acceptable(payload));
    }
}
