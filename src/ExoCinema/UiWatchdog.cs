using System.Diagnostics;
namespace ExoCinema;

/// <summary>
/// Freezes: the UI thread not answering for five seconds. A thread of its own asks the UI thread to answer every second;
/// when the last answer is older than <see cref="Limit"/>, <c>onFreeze</c> is called once for that freeze, with how long
/// it has lasted and what was running (CrashReport.Running). A freeze that ends re-arms the watch.
/// </summary>
internal static class UiWatchdog
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    /// <summary>Whether the UI thread has been silent long enough to call it a freeze.</summary>
    public static bool Stuck(TimeSpan sinceAnswer) => sinceAnswer > Limit;

    private sealed class Beat
    {
        public long Answered = Stopwatch.GetTimestamp();
        public int Asking;
        public int Reported;
    }

    /// <param name="post">Runs an action on the UI thread (a dispatcher's TryEnqueue).</param>
    /// <param name="running">What the UI thread was doing, by name.</param>
    /// <param name="onFreeze">Called once per freeze, with its length so far.</param>
    public static Thread Start(Action<Action> post, Func<string> running, Action<TimeSpan, string> onFreeze, CancellationToken stop)
    {
        var beat = new Beat();
        var thread = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Thread.Sleep(Every);
                if (Interlocked.CompareExchange(ref beat.Asking, 1, 0) == 0)
                {
                    try
                    {
                        post(() =>
                        {
                            Volatile.Write(ref beat.Answered, Stopwatch.GetTimestamp());
                            Volatile.Write(ref beat.Reported, 0);
                            Volatile.Write(ref beat.Asking, 0);
                        });
                    }
                    // The window closed under the watch (its dispatcher is gone, disposed or null): the watch ends.
                    // Nothing thrown here may leave this thread, where it would end the app as a crash.
                    catch (Exception e) when (e is not OutOfMemoryException) { return; }
                }
                var since = Stopwatch.GetElapsedTime(Volatile.Read(ref beat.Answered));
                if (Stuck(since) && Interlocked.CompareExchange(ref beat.Reported, 1, 0) == 0)
                {
                    try { onFreeze(since, running()); }
                    catch (Exception e) when (e is not OutOfMemoryException) { }
                }
            }
        }) { IsBackground = true, Name = "UI watchdog" };
        thread.Start();
        return thread;
    }
}
