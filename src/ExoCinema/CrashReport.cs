using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoCinema.Net;
namespace ExoCinema;

/// <summary>
/// Crash, freeze and playback error reports, sent by default (cloud/reports; PRIVACY.md). A report is the
/// <see cref="Payload"/> and nothing else: the app, its version, the Windows build, an error's name and a few log lines.
/// The lines are scrubbed here, on this PC, before anything leaves it: addresses (the Jellyfin server's, a film's link,
/// any web address), file paths and file names, titles, the user and machine names, emails, ids and tokens. A line that
/// still looks personal after that is replaced whole. The service refuses what this lets through (cloud/reports/src/validate.js),
/// so the two lists match. A report is kept on disk and sent at the next start, or straight away for a freeze or a
/// playback error; at most once a day per signature, and five a day in all. No install id is sent.
/// </summary>
internal static partial class CrashReport
{
    internal const string Endpoint = "https://reports.getexo.app/v1/report";
    internal const int MaxExcerpt = 8192;
    internal const int MaxPerDay = 5;
    internal const int MaxPending = 20;
    internal const string AppName = "cinema";

    /// <summary>What a report holds. Nothing else is ever put in one.</summary>
    internal sealed record Payload(string App, string Version, string OsBuild, string ErrorType, string Excerpt);

    /// <summary>Tests only: the transport a post goes through instead of the network (still through Egress).</summary>
    internal static Func<HttpMessageHandler>? FakeTransport;

    private static readonly object Gate = new();
    private static readonly List<string> Known = [];
    private static readonly HashSet<string> KeptThisRun = [];
    private static readonly SemaphoreSlim Sending = new(1, 1);
    private const int MaxKnown = 64;

    /// <summary>What the page or the player is doing now (an operation's name, never its arguments): a freeze's report says it.</summary>
    internal static volatile string Running = "start";

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]{1,15}://[^\s""'<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    // Windows drive, %USERPROFILE% or <user> followed by a path; a UNC share; a Unix home folder; a drive written with a slash.
    [GeneratedRegex(@"(?:%[A-Za-z_]+%\\|<user>\\|\b[A-Za-z]:\\|\b[A-Za-z]:/)[^\r\n""'|()\[\]]*", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"\\\\[^\s\\""'<>|]+(?:\\[^\r\n""'|()\[\]]*)?", RegexOptions.CultureInvariant)]
    private static partial Regex SharePath();

    [GeneratedRegex(@"(?<![A-Za-z0-9])/(?:home|Users|root)/[^\r\n""'|()\[\]]*", RegexOptions.CultureInvariant)]
    private static partial Regex UnixHome();

    [GeneratedRegex(@"\b(?:[a-z0-9-]+\.)+(?:com|net|org|io|gg|app|dev|local|lan|gl|tv|de|co|uk|us|xyz|ru|jp|me|edu|gov)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostName();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IpAddress();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GuidId();

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlChar();

    // A three-part token (JWT-like) logged with no label; the same rule as the Launcher's log redaction.
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{16,}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex DottedToken();

    [GeneratedRegex(@"\d{9,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumber();

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9_]{0,70}(?:Exception|Error)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorName();

    [GeneratedRegex(@"^\d{1,4}(?:\.\d{1,4}){2}", RegexOptions.CultureInvariant)]
    private static partial Regex VersionHead();

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionShape();

    [GeneratedRegex(@"^\d{1,5}\.\d{1,6}(\.\d{1,6}){0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex OsBuildShape();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorTypeShape();

    [GeneratedRegex(@"[A-Za-z]:\\|\b[A-Za-z]:/", RegexOptions.CultureInvariant)] private static partial Regex WindowsPathSweep();
    [GeneratedRegex(@"\\\\[^\s\\]+\\", RegexOptions.CultureInvariant)] private static partial Regex SharePathSweep();
    [GeneratedRegex(@"(^|[^A-Za-z0-9])/(home|Users)/", RegexOptions.CultureInvariant)] private static partial Regex UnixHomeSweep();
    [GeneratedRegex(@"[a-z][a-z0-9+.-]{1,15}://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex UrlSweep();
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)] private static partial Regex EmailSweep();
    [GeneratedRegex(@"\d{15,}", RegexOptions.CultureInvariant)] private static partial Regex LongRun();
    [GeneratedRegex(@"[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{16,}", RegexOptions.CultureInvariant)] private static partial Regex TokenSweep();

    /// <summary>
    /// A media file's name (Cinema's own: a title's file), from the last dot-name before a line break. A line holding one
    /// goes whole, and app.log takes the name out too (AppLog.Clean).
    /// </summary>
    [GeneratedRegex(AppLog.MediaFilePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaFile();

    /// <summary>What the service also refuses (cloud/reports/src/validate.js › PERSONAL), and the media file names. A line matching any is removed whole.</summary>
    private static readonly Regex[] PersonalPatterns =
    [
        WindowsPathSweep(), SharePathSweep(), UnixHomeSweep(), UrlSweep(), EmailSweep(), LongRun(), TokenSweep(), MediaFile(),
    ];

    /// <summary>Remembers a text that must never reach a report: an address (its host and path too), a sign-in's token, a title.</summary>
    internal static void Note(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        lock (Gate)
        {
            if (Known.Contains(secret.Trim(), StringComparer.OrdinalIgnoreCase)) return;
            Known.Add(secret.Trim());
            if (Known.Count > MaxKnown) Known.RemoveAt(0);
        }
    }

    private static string[] KnownSecrets()
    {
        lock (Gate) return [.. Known];
    }

    /// <summary>The secrets with the parts an address leaks by (its host, its path), longest first.</summary>
    internal static IEnumerable<string> Expand(IEnumerable<string> secrets)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in secrets)
        {
            var s = raw?.Trim() ?? "";
            if (s.Length < 4) continue;
            all.Add(s);
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Host.Length >= 4)
            {
                all.Add(uri.Host);
                var path = uri.AbsolutePath.Trim('/');
                if (path.Length >= 4)
                {
                    all.Add(path);
                    all.Add(uri.Host + "/" + path);
                }
            }
        }
        return all.OrderByDescending(x => x.Length);
    }

    /// <summary>
    /// The report for an error, as the service takes it. <paramref name="report"/> is the log's lines and the error's text;
    /// <paramref name="errorType"/> names a report type (Hang.Ui, Hang.Page, PlaybackFailed, ServerUnreachable), or null to
    /// take the first exception or error name in the text.
    /// </summary>
    internal static Payload Build(string error, string report, string version, string windows, string userName, string profile, string machineName, IEnumerable<string> secrets, string? errorType = null)
    {
        var raw = report.Length > MaxExcerpt * 4 ? report[..(MaxExcerpt * 4)] : report;
        var excerpt = Scrub(raw, userName, profile, machineName, secrets);
        excerpt = string.Join('\n', excerpt.Split('\n').Select(line => PersonalPatterns.Any(p => p.IsMatch(line)) ? "<removed>" : line));
        // Cut after the lines are replaced: "<removed>" is longer than some lines, so the cap is checked last.
        if (excerpt.Length > MaxExcerpt) excerpt = excerpt[..MaxExcerpt];
        var type = errorType ?? (ErrorName().Match(error + "\n" + raw) is { Success: true } named ? named.Value : "AppError");
        var head = VersionHead().Match(version);
        return new Payload(AppName, head.Success ? head.Value : "0.0.0", OsBuild(windows), type, excerpt);
    }

    /// <summary>The personal parts out of a report's text: addresses and the secrets, paths, hosts and machine names, addresses, emails, ids.</summary>
    internal static string Scrub(string text, string userName, string profile, string machineName, IEnumerable<string> secrets)
    {
        // Control characters but tab and newline are refused by the service: take them out first, so a report never fails on one.
        text = ControlChar().Replace(text.Replace("\r", ""), "");
        foreach (var secret in Expand(secrets))
            text = Regex.Replace(text, Regex.Escape(secret), "<hidden>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = ReplaceUser(AppLog.Clean(text), userName, profile);
        text = DottedToken().Replace(text, "<token>");
        text = Url().Replace(text, "<url>");
        text = SharePath().Replace(text, "<path>");
        text = WindowsPath().Replace(text, "<path>");
        text = UnixHome().Replace(text, "<path>");
        text = Email().Replace(text, "<email>");
        text = GuidId().Replace(text, "<id>");
        text = IpAddress().Replace(text, "<address>");
        text = HostName().Replace(text, "<host>");
        if (machineName.Length >= 3)
            text = Regex.Replace(text, $@"(?<![A-Za-z0-9-]){Regex.Escape(machineName)}(?![A-Za-z0-9-])", "<host>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return LongNumber().Replace(text, "<id>");
    }

    private static string ReplaceUser(string text, string userName, string profile)
    {
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (userName.Length >= 3) text = Regex.Replace(text, $@"(?<![A-Za-z0-9]){Regex.Escape(userName)}(?![A-Za-z0-9])", "<user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }

    /// <summary>Whether the service would take this report: the same shapes and the same personal-data refusals.</summary>
    internal static bool Acceptable(Payload p) =>
        p.App == AppName && VersionShape().IsMatch(p.Version) && OsBuildShape().IsMatch(p.OsBuild)
        && ErrorTypeShape().IsMatch(p.ErrorType) && p.Excerpt.Length <= MaxExcerpt
        && !Regex.IsMatch(p.Excerpt, "[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")
        && !PersonalPatterns.Any(x => x.IsMatch(p.Excerpt) || x.IsMatch(p.ErrorType));

    /// <summary>The report as the service takes it, now: <paramref name="headline"/> is its last line.</summary>
    internal static Payload Current(string headline, IEnumerable<string> lines, string? errorType = null) =>
        Build(headline, string.Join('\n', lines.Append(headline)), AppInfo.Version, Environment.OSVersion.Version.ToString(),
            Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.MachineName, KnownSecrets(), errorType);

    /// <summary>The JSON a report is posted as: exactly its five fields.</summary>
    public static string ToJson(Payload payload) => new JsonObject
    {
        ["app"] = payload.App,
        ["version"] = payload.Version,
        ["osBuild"] = payload.OsBuild,
        ["errorType"] = payload.ErrorType,
        ["excerpt"] = payload.Excerpt,
    }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    private static Payload? FromJson(string text)
    {
        try
        {
            var n = JsonNode.Parse(text)?.AsObject();
            if (n is null) return null;
            return new Payload((string?)n["app"] ?? "", (string?)n["version"] ?? "", (string?)n["osBuild"] ?? "", (string?)n["errorType"] ?? "", (string?)n["excerpt"] ?? "");
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or InvalidCastException) { return null; }
    }

    /// <summary>The signature for the once-a-day rule: the error type and its line's words, without times, numbers or brackets. Kept here; never sent.</summary>
    internal static string Signature(Payload p)
    {
        var lines = p.Excerpt.Split('\n');
        var first = lines.FirstOrDefault(l => l.Contains(p.ErrorType, StringComparison.Ordinal)) ?? lines.FirstOrDefault() ?? "";
        var norm = Regex.Replace(first, @"\[[^\]]*\]|\d+", "").Trim();
        return p.ErrorType + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm)))[..12];
    }

    internal static string PendingDir(string folder) => Path.Combine(folder, "reports");

    private static string StatePath(string folder) => Path.Combine(folder, "reports-state.json");

    /// <summary>
    /// Keeps one report for the next start (<paramref name="sendNow"/> false: a crash, which ends the process), or sends it
    /// in the background straight away (a freeze or a playback error). The same signature is kept once a run.
    /// </summary>
    internal static void Keep(string? errorType, string headline, IEnumerable<string> lines, string folder, bool sendNow = false)
    {
        try
        {
            var payload = Current(headline, lines, errorType);
            if (!Acceptable(payload))
            {
                AppLog.Shared.Write("report: not kept (it didn't pass the check)");
                return;
            }
            var signature = Signature(payload);
            lock (Gate) { if (!KeptThisRun.Add(signature)) return; }
            var dir = PendingDir(folder);
            Directory.CreateDirectory(dir);
            if (Directory.GetFiles(dir, "*.json").Length >= MaxPending) return;
            File.WriteAllText(Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json"), ToJson(payload), Encoding.UTF8);
            AppLog.Shared.Write($"report: {payload.ErrorType} kept for sending");
            if (sendNow && !SendingOff) _ = Task.Run(() => SendPendingAsync(folder, DateTime.UtcNow));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Test copies only: EXO_CINEMA_NO_REPORT_SEND=1 keeps every report on disk and sends none (a freeze's or a start's),
    /// so a live run on a test copy cannot send a stray report to the service.
    /// </summary>
    internal static bool SendingOff => SendingOffFor(AppInfo.OwnData, Environment.GetEnvironmentVariable("EXO_CINEMA_NO_REPORT_SEND"), Environment.GetEnvironmentVariable("EXO_CINEMA_REPORT_SEND"));

    /// <summary>A test copy (its own data folder, EXO_CINEMA_DATA) sends nothing unless EXO_CINEMA_REPORT_SEND=1 asks for
    /// it: a test's freeze or crash is not a person's, and would count and alert as one. Any copy sends nothing with
    /// EXO_CINEMA_NO_REPORT_SEND=1.</summary>
    internal static bool SendingOffFor(bool testCopy, string? never, string? testSends) => never == "1" || testCopy && testSends != "1";

    /// <summary>Tests only: a run starts with no signature kept.</summary>
    internal static void ForgetKeptThisRun()
    {
        lock (Gate) KeptThisRun.Clear();
    }

    private sealed class SendState
    {
        public string Day { get; set; } = "";
        public int Count { get; set; }
        public Dictionary<string, string> Sent { get; set; } = [];
    }

    private static SendState ReadState(string folder, DateTime utcNow)
    {
        var today = utcNow.ToString("yyyy-MM-dd");
        SendState? saved = null;
        try
        {
            var n = JsonNode.Parse(File.ReadAllText(StatePath(folder)))?.AsObject();
            if (n is not null)
            {
                saved = new SendState { Day = (string?)n["day"] ?? "", Count = (int?)n["count"] ?? 0 };
                if (n["sent"] is JsonObject sent) foreach (var pair in sent) saved.Sent[pair.Key] = (string?)pair.Value ?? "";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or InvalidCastException) { }
        // A new day starts the count again, and any signature may be sent once more.
        if (saved is null || saved.Day != today) return new SendState { Day = today };
        return saved;
    }

    private static void WriteState(string folder, SendState state)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var n = new JsonObject { ["day"] = state.Day, ["count"] = state.Count };
            var sent = new JsonObject();
            foreach (var pair in state.Sent) sent[pair.Key] = pair.Value;
            n["sent"] = sent;
            File.WriteAllText(StatePath(folder), n.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Sends the kept reports, within the day's limits: each signature once a day and <see cref="MaxPerDay"/> in all. Every
    /// file is deleted whether it went or not; a refusal is logged. Returns how many the service stored.
    /// </summary>
    internal static async Task<int> SendPendingAsync(string folder, DateTime utcNow, CancellationToken ct = default)
    {
        var dir = PendingDir(folder);
        if (SendingOff || !Directory.Exists(dir)) return 0;
        await Sending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = ReadState(folder, utcNow);
            var stored = 0;
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                // The day's five are sent: the rest stay where they are, for tomorrow (the folder's cap keeps them bounded).
                if (state.Count >= MaxPerDay)
                {
                    AppLog.Shared.Write("report: today's five are sent; the rest wait for tomorrow");
                    break;
                }
                Payload? payload = null;
                try { payload = FromJson(File.ReadAllText(file)); File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                if (payload is null || !Acceptable(payload)) continue;
                var signature = Signature(payload);
                if (state.Sent.ContainsKey(signature)) continue;
                state.Count++;
                state.Sent[signature] = state.Day;
                if (await PostAsync(payload, ct).ConfigureAwait(false)) stored++;
            }
            WriteState(folder, state);
            return stored;
        }
        finally
        {
            Sending.Release();
        }
    }

    private static async Task<bool> PostAsync(Payload payload, CancellationToken ct)
    {
        try
        {
            using var handler = FakeTransport is null
                ? Egress.Checkpoint(Egress.Purpose.Reports, new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
                : Egress.Checkpoint(Egress.Purpose.Reports, FakeTransport());
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var content = new StringContent(ToJson(payload), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                AppLog.Shared.Write($"report: {payload.ErrorType} stored by the service (202)");
                return true;
            }
            AppLog.Shared.Write($"report: the service answered {(int)response.StatusCode}");
            return false;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Shared.Write($"report: not sent ({e.GetType().Name})");
            return false;
        }
    }

    /// <summary>"10.0.26300.0" as the service takes it: the first four parts, digits only.</summary>
    private static string OsBuild(string windows)
    {
        var parts = windows.Split('.').Where(part => part.Length > 0 && part.All(char.IsAsciiDigit)).Take(4).ToArray();
        return parts.Length >= 2 ? string.Join('.', parts) : "0.0";
    }
}
