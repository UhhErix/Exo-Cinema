using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ExoCinema;

/// <summary>
/// Timings, mpv's warnings and what fails quietly, kept for the page and written to %LOCALAPPDATA%\ExoCinema\app.log.
/// Each run starts a new app.log; the run before is kept as app.prev.log. Each file stops at 512 KB (the newest lines
/// stay). One log is shared by the whole app (<see cref="Shared"/>), so the network, sync and the player write to it too.
/// </summary>
internal sealed class AppLog
{
    public const long DefaultMaxBytes = 512 * 1024;
    public const int DefaultMaxLines = 4000;

    /// <summary>A media file's name: the text before its extension, up to a quote, a bracket-free path or a line break. Not a "*", so an address keeps its kind.</summary>
    public const string MediaFilePattern = @"[^\\/\r\n""'<>|*]+?\.(?:mkv|mp4|m4v|avi|mov|wmv|webm|mpg|mpeg|ts|m2ts|iso|srt|ass|ssa|vtt|sub|idx)\b";
    private static readonly Regex MediaFile = new(MediaFilePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalPath = new(@"(%USERPROFILE%|\b[A-Za-z]:|\\\\[^\\\s""'<>|]+)\\[^\r\n""'<>|*]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly object SharedGate = new();
    private static AppLog? s_shared;

    private readonly List<string> _lines = [];
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxLines;

    public Dictionary<string, double> Marks { get; } = [];

    /// <summary>The app's one log. The first use of this run starts app.log afresh (the last run's becomes app.prev.log).</summary>
    internal static AppLog Shared
    {
        get
        {
            lock (SharedGate)
            {
                if (s_shared is null)
                {
                    var path = Path.Combine(AppInfo.Data, "app.log");
                    RotatePrevious(path);
                    s_shared = new AppLog(path);
                }
                return s_shared;
            }
        }
        set { lock (SharedGate) s_shared = value; }
    }

    /// <summary>A log at <paramref name="path"/>. The caps are for tests; the app uses the defaults.</summary>
    internal AppLog(string path, long maxBytes = DefaultMaxBytes, int maxLines = DefaultMaxLines)
    {
        _path = path;
        _maxBytes = maxBytes;
        _maxLines = maxLines;
    }

    /// <summary>This run starts app.log afresh: the file the last run left becomes app.prev.log (replacing the one before it).</summary>
    internal static void RotatePrevious(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, PreviousPath(path), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal static string PreviousPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".prev.log");

    /// <summary>The first line to keep of <paramref name="lines"/> so the rest fit both caps: the newest lines win.</summary>
    internal static int FirstKept(IReadOnlyList<string> lines, long maxBytes, int maxLines)
    {
        long bytes = 0;
        var first = lines.Count;
        while (first > 0 && lines.Count - first < maxLines)
        {
            bytes += Encoding.UTF8.GetByteCount(lines[first - 1]) + 2;
            if (bytes > maxBytes) break;
            first--;
        }
        return first;
    }

    /// <summary>What a failure is, in a word, for the log and a report: an HTTP status, no answer, a timeout, bad data. Never its message.</summary>
    internal static string Kind(Exception e) => e switch
    {
        HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
        HttpRequestException => "no answer",
        TaskCanceledException => "timed out",
        JsonException => "not JSON",
        InvalidDataException => "bad data",
        _ => e.GetType().Name,
    };

    /// <summary>A failure's types and stack, for crash.log and a report: never its messages, which can carry a title, an address or a path.</summary>
    internal static string Trace(Exception? e)
    {
        var text = new StringBuilder();
        for (var x = e; x is not null; x = x.InnerException)
            text.Append(x.GetType().FullName).Append('\n').Append(x.StackTrace).Append('\n');
        return text.ToString().TrimEnd();
    }

    /// <summary>Blanks this server's host in every line from now on.</summary>
    public void Hide(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        if (!address.Contains("://")) address = "http://" + address.Trim();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return;
        var pattern = $@"(?<![\w.-]){Regex.Escape(uri.Host)}(?![\w-])";
        lock (_lines)
            if (!_hidden.Exists(r => r.ToString() == pattern)) _hidden.Add(new Regex(pattern, RegexOptions.IgnoreCase));
    }

    /// <summary>Blanks this exact text (a sign-in's token) in every line from now on.</summary>
    public void HideText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var pattern = Regex.Escape(text);
        lock (_lines)
            if (!_hidden.Exists(r => r.ToString() == pattern)) _hidden.Add(new Regex(pattern, RegexOptions.IgnoreCase));
    }

    public void Mark(string what, double ms)
    {
        lock (_lines)
        {
            Marks[what] = Math.Round(ms, 1);
            _lines.Add($"{DateTime.Now:HH:mm:ss.fff} {what} {ms:0.0} ms");
        }
        Flush();
    }

    public void Write(string text)
    {
        lock (_lines) _lines.Add($"{DateTime.Now:HH:mm:ss.fff} {Redact(text)}");
        Flush();
    }

    // The log gets attached to public issues, so nothing personal goes in it: every web address keeps only its scheme
    // and its kind of file (https://…/*.mkv), which drops the server, its access token, a file's link and the
    // title; the server in use is blanked wherever else it's named (a "host not found" error); a media
    // file's name is blanked; and the Windows user's folder reads %USERPROFILE%.
    private readonly List<Regex> _hidden = [];
    private static readonly Regex Address = new(@"\b[a-zA-Z][a-zA-Z0-9+.-]*://[^\s""'<>]+", RegexOptions.Compiled);
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private string Redact(string text)
    {
        text = Clean(text);
        lock (_lines) foreach (var host in _hidden) text = host.Replace(text, "…");
        return text;
    }

    /// <summary>The addresses, media file names, local paths and the user's folder taken out, for app.log, crash.log and reports alike.</summary>
    internal static string Clean(string text)
    {
        text = Address.Replace(text, m =>
        {
            var at = m.Value.IndexOf("://", StringComparison.Ordinal);
            var path = m.Value[(at + 3)..].Split('?', '#')[0];
            var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : "";
            var dot = name.LastIndexOf('.');
            var kind = dot > 0 && name.Length - dot is > 1 and <= 6 && name[(dot + 1)..].All(char.IsAsciiLetterOrDigit) ? $"/*{name[dot..]}" : "";
            return $"{m.Value[..at]}://…{kind}";
        });
        text = MediaFile.Replace(text, "<file>");
        text = Profile.Length > 0 ? text.Replace(Profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase) : text;
        // A folder can carry a title too ("D:\Films\Quantum Night (2010)\"), so a path keeps only its drive, its user folder or its server.
        return LocalPath.Replace(text, m => m.Groups[1].Value + "\\…");
    }

    /// <summary>The last lines, as written (already cleaned), for Copy logs and reports.</summary>
    public string Tail(int count)
    {
        lock (_lines) return string.Join(Environment.NewLine, _lines.Skip(Math.Max(0, _lines.Count - count)));
    }

    public void Flush()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            lock (_lines)
            {
                var first = FirstKept(_lines, _maxBytes, _maxLines);
                if (first > 0) _lines.RemoveRange(0, first);
                File.WriteAllLines(_path, _lines);
            }
        }
        catch (IOException) { }
    }
}
