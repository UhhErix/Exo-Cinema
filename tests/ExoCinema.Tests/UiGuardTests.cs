namespace ExoCinema.Tests;

/// <summary>
/// The UI thread's callbacks (posted actions, WebView2 events, timer ticks) run through UiGuard, so a throw reaches the
/// crash writer before it ends the process. A source guard keeps a raw enqueue or an unguarded handler from coming back.
/// </summary>
[Collection("Shared log")]
public sealed class UiGuardTests
{
    private static readonly string[] UnguardedHandlers =
    [
        ".Tick += ", ".WebMessageReceived += ", ".NavigationCompleted += ", ".WebResourceRequested += ", ".ContainsFullScreenElementChanged += ",
    ];

    [Fact]
    public void A_throw_on_the_UI_thread_is_handed_to_the_sink_with_its_place_and_thrown_on()
    {
        var seen = new List<(string Where, Exception Error)>();
        var previous = UiGuard.Sink;
        UiGuard.Sink = (where, error) => seen.Add((where, error));
        try
        {
            var boom = new InvalidOperationException("boom");

            var thrown = Assert.Throws<InvalidOperationException>(() => UiGuard.Run(() => throw boom, "page"));

            Assert.Same(boom, thrown);
            var one = Assert.Single(seen);
            Assert.Equal("page", one.Where);
            Assert.Same(boom, one.Error);
        }
        finally
        {
            UiGuard.Sink = previous;
        }
    }

    [Fact]
    public void A_callback_that_returns_normally_writes_nothing()
    {
        var seen = 0;
        var previous = UiGuard.Sink;
        UiGuard.Sink = (_, _) => seen++;
        try
        {
            var ran = false;
            UiGuard.Run(() => ran = true);

            Assert.True(ran);
            Assert.Equal(0, seen);
        }
        finally
        {
            UiGuard.Sink = previous;
        }
    }

    [Fact]
    public void No_raw_dispatcher_enqueue_is_left_and_every_UI_handler_runs_through_UiGuard()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "ExoCinema", "UiDispatch.cs"))) dir = dir.Parent;
        Assert.True(dir is not null, "the source folder src/ExoCinema wasn't found beside the tests");

        var rawEnqueue = new List<string>();
        var unguarded = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src", "ExoCinema"), "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("///")) continue;
                var where = $"{name}:{i + 1}";
                if (line.Contains(".TryEnqueue(") && name != "UiDispatch.cs") rawEnqueue.Add(where);
                if (UnguardedHandlers.Any(h => line.Contains(h)) && !line.Contains("UiGuard.Run(")) unguarded.Add(where);
            }
        }

        Assert.True(rawEnqueue.Count == 0, "raw TryEnqueue (use DispatcherQueue.Post): " + string.Join(", ", rawEnqueue));
        Assert.True(unguarded.Count == 0, "UI handler not run through UiGuard.Run: " + string.Join(", ", unguarded));
    }
}
