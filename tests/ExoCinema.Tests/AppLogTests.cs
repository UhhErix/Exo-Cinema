using System.Text;
namespace ExoCinema.Tests;

/// <summary>app.log: the last run kept as app.prev.log, the size cap, and file names kept out of it.</summary>
[Collection("Shared log")]
public sealed class AppLogTests
{
    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "exo-cinema-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void The_last_run_is_kept_as_app_prev_log_and_replaces_the_one_before()
    {
        var folder = Folder();
        var path = Path.Combine(folder, "app.log");
        File.WriteAllText(path, "this run's start\n");
        File.WriteAllText(Path.Combine(folder, "app.prev.log"), "the run before that\n");

        AppLog.RotatePrevious(path);

        Assert.False(File.Exists(path));
        Assert.Equal("this run's start\n", File.ReadAllText(Path.Combine(folder, "app.prev.log")));
    }

    [Fact]
    public void A_first_run_with_no_log_leaves_no_previous_one()
    {
        var folder = Folder();
        AppLog.RotatePrevious(Path.Combine(folder, "app.log"));
        Assert.False(File.Exists(Path.Combine(folder, "app.prev.log")));
    }

    [Fact]
    public void The_log_stays_under_its_byte_cap_and_keeps_the_newest_lines()
    {
        var folder = Folder();
        var path = Path.Combine(folder, "app.log");
        var log = new AppLog(path, maxBytes: 4000, maxLines: 100);
        for (var i = 0; i < 300; i++) log.Write($"line {i:D4} " + new string('x', 150));

        var lines = File.ReadAllLines(path);
        Assert.True(Encoding.UTF8.GetByteCount(string.Join("\n", lines)) <= 4000, "app.log is over its byte cap");
        Assert.True(lines.Length <= 100, "app.log is over its line cap");
        Assert.Contains(lines, line => line.Contains("line 0299 "));
        Assert.DoesNotContain(lines, line => line.Contains("line 0000 "));
    }

    [Fact]
    public void FirstKept_counts_both_the_byte_cap_and_the_line_cap()
    {
        var lines = Enumerable.Range(0, 10).Select(_ => new string('a', 10)).ToList();
        // Each line is 10 bytes plus its line break (2): 12 each.
        Assert.Equal(2, AppLog.FirstKept(lines, maxBytes: 100, maxLines: 100));
        Assert.Equal(2, AppLog.FirstKept(lines, maxBytes: 100_000, maxLines: 8));
        Assert.Equal(0, AppLog.FirstKept(lines, maxBytes: 100_000, maxLines: 100));
    }

    [Fact]
    public void A_file_name_is_taken_out_of_the_log_and_an_address_keeps_only_its_kind()
    {
        Assert.Equal("<file> from the library", AppLog.Clean("Playing Quantum Night (2010).mkv from the library"));
        Assert.Equal("https://…/*.mkv", AppLog.Clean("https://aiostreams.example.org/stremio/4cdf0e6a/x/*.mkv"));
    }

    [Fact]
    public void Kind_names_a_failure_without_its_address_or_message()
    {
        Assert.Equal("HTTP 500", AppLog.Kind(new HttpRequestException("boom at https://k.example/x?key=1", null, System.Net.HttpStatusCode.InternalServerError)));
        Assert.Equal("no answer", AppLog.Kind(new HttpRequestException("connect to k.example failed")));
        Assert.Equal("not JSON", AppLog.Kind(new System.Text.Json.JsonException("at key")));
    }

    [Fact]
    public void A_local_path_keeps_only_its_drive_its_user_folder_or_its_server()
    {
        // A folder can carry a title, so only the root of a path reaches the log.
        Assert.Equal(@"could not open D:\…", AppLog.Clean(@"could not open D:\Films\Quantum Night (2010)\cut.nfo"));
        Assert.Equal(@"read \\NAS\…", AppLog.Clean(@"read \\NAS\Media\Quantum Night\cut.nfo"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(@"kept in %USERPROFILE%\…", AppLog.Clean($@"kept in {home}\Videos\Quantum Night\cut.nfo"));
    }

    [Fact]
    public void Trace_names_the_types_and_stack_and_never_a_message()
    {
        string text;
        try { throw new InvalidOperationException("Quantum Night is not on the server"); }
        catch (Exception e) { text = AppLog.Trace(e); }
        Assert.Contains("System.InvalidOperationException", text);
        Assert.DoesNotContain("Quantum Night", text);
        Assert.Equal("", AppLog.Trace(null));
    }

    /// <summary>Every log call in the source writes a kind, a count or a fact. It never writes an exception's message or
    /// the whole exception, a title, a file name, or what the user typed (a search). Reads the source beside the tests.</summary>
    [Fact]
    public void No_log_call_in_the_source_writes_a_message_a_whole_exception_or_a_title()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "ExoCinema", "AppLog.cs"))) dir = dir.Parent;
        Assert.True(dir is not null, "the source folder src/ExoCinema wasn't found beside the tests");
        var logCall = new System.Text.RegularExpressions.Regex(@"(_log\??\.?Invoke\(|_log\.Write\(|_log\(|Log\?\.Invoke\(|\blog\(|AppLog\.Shared\.Write\(|_o\.Log\(|\bLog\()");
        var leak = new System.Text.RegularExpressions.Regex(@"\.Message\b|\{e\}|\{ex\}|\{t\.Exception\}|\bTitle\b|\bFileName\b|\bQuery\b|\{query\}|\{q\}|\{search\}");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src", "ExoCinema"), "*.cs", SearchOption.AllDirectories))
            foreach (var (line, number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
                if (logCall.IsMatch(line) && leak.IsMatch(line)) found.Add($"{Path.GetFileName(file)}:{number}");
        Assert.True(found.Count == 0, "log calls that write a message, a whole exception or a title: " + string.Join(", ", found));
    }
}
