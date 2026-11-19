using System.Text.RegularExpressions;
using ExoCinema.Net;

namespace ExoCinema.Tests;

/// <summary>Everywhere Exo Cinema connects (Net/Egress.cs): said in PRIVACY.md, and every client made through it.</summary>
[Collection("Shared log")]
public sealed partial class EgressTests
{
    private static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PRIVACY.md")) && Directory.Exists(Path.Combine(dir.FullName, "src", "ExoCinema"))) return dir.FullName;
        throw new InvalidOperationException("The repository isn't above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_fixed_destination_is_named_in_PRIVACY_md()
    {
        var privacy = File.ReadAllText(Path.Combine(Repo(), "PRIVACY.md"));
        foreach (var (host, _) in Egress.Fixed)
            Assert.True(privacy.Contains($"`{host}", StringComparison.OrdinalIgnoreCase), $"{host} is a destination PRIVACY.md doesn't name");
    }

    [Fact]
    public void Updates_go_only_to_the_update_address_over_https()
    {
        Assert.True(Egress.Allowed(new Uri("https://updates.getexo.app/v1/cinema/latest"), Egress.Purpose.Updates));
        Assert.False(Egress.Allowed(new Uri("https://release-assets.githubusercontent.com/x"), Egress.Purpose.Updates));
        Assert.False(Egress.Allowed(new Uri("https://github.com/UhhErix/ExoCinema/releases/latest"), Egress.Purpose.Updates));
        Assert.False(Egress.Allowed(new Uri("http://updates.getexo.app/v1/cinema/latest"), Egress.Purpose.Updates));
        Assert.False(Egress.Allowed(new Uri("https://evil.example/ExoCinema-Setup.exe"), Egress.Purpose.Updates));
        Assert.False(Egress.Allowed(new Uri("https://github.com.evil.example/x"), Egress.Purpose.Updates));
    }

    [Fact]
    public void A_film_s_link_goes_anywhere_on_the_web_but_Exo_and_the_fixed_hosts()
    {
        Assert.True(Egress.Allowed(new Uri("http://192.168.1.20:8096/Videos/1/stream"), Egress.Purpose.Stream));
        Assert.True(Egress.Allowed(new Uri("https://files.example.org/dl/abc.mkv?token=1"), Egress.Purpose.Stream));
        Assert.False(Egress.Allowed(new Uri("https://updates.getexo.app/x"), Egress.Purpose.Stream));
        Assert.False(Egress.Allowed(new Uri("https://getexo.app/x"), Egress.Purpose.Stream));
        Assert.False(Egress.Allowed(new Uri("file:///C:/Windows/win.ini"), Egress.Purpose.Stream));
        Assert.False(Egress.Allowed(new Uri("ftp://media.example.org/x"), Egress.Purpose.Stream));
    }

    // Jellyfin only (1.5): the catalog, picture and lookup hosts Cinema asked before are no destination any more, and no
    // purpose is left that could carry a request to them. A film's link is the one thing that may lead anywhere.
    [Fact]
    public void The_hosts_Cinema_asked_before_1_5_are_not_destinations_any_more()
    {
        string[] old = ["v3-cinemeta.strem.io", "cinemeta-live.strem.io", "cinemeta-catalogs.strem.io", "images.metahub.space", "episodes.metahub.space", "image.tmdb.org", "query.wikidata.org"];
        Assert.DoesNotContain(Egress.Fixed, f => old.Contains(f.Host));
        Assert.DoesNotContain(Enum.GetNames<Egress.Purpose>(), n => n is "Addon" or "Metadata");
        Egress.SignedInTo("https://media.example.org");
        try
        {
            foreach (var host in old)
                foreach (var purpose in Enum.GetValues<Egress.Purpose>().Where(p => p != Egress.Purpose.Stream))
                    Assert.False(Egress.Allowed(new Uri($"https://{host}/meta/movie/tt0111161.json"), purpose), $"{host} is still reached for {purpose}");
        }
        finally { Egress.SignedInTo(null); }
    }
    [Fact]
    public void The_Exo_account_reaches_its_service_over_https_and_a_local_one_on_this_PC()
    {
        Assert.True(Egress.Allowed(new Uri("https://accounts.getexo.app/v1/session"), Egress.Purpose.Account));
        Assert.True(Egress.Allowed(new Uri("http://127.0.0.1:8792/v1/session"), Egress.Purpose.Account));
        Assert.True(Egress.Allowed(new Uri("http://localhost:8792/"), Egress.Purpose.Account));
        Assert.False(Egress.Allowed(new Uri("http://accounts.getexo.app/v1/session"), Egress.Purpose.Account));
        Assert.False(Egress.Allowed(new Uri("https://account.example/v1/session"), Egress.Purpose.Account));
        Assert.False(Egress.Allowed(new Uri("https://accounts.getexo.app.evil.example/v1/session"), Egress.Purpose.Account));
        Assert.False(Egress.Allowed(new Uri("http://192.168.1.20:8792/"), Egress.Purpose.Account));
        Assert.False(Egress.Allowed(new Uri("http://127.0.0.1:8792/v1/start?provider=google"), Egress.Purpose.Account));
        // The sync pull and the export page by cursor, so they take a query; no other account route does.
        Assert.True(Egress.Allowed(new Uri("https://accounts.getexo.app/v1/sync?after=0"), Egress.Purpose.Account));
        Assert.True(Egress.Allowed(new Uri("https://accounts.getexo.app/v1/export?after=40"), Egress.Purpose.Account));
        Assert.False(Egress.Allowed(new Uri("https://accounts.getexo.app/v1/session?x=1"), Egress.Purpose.Account));
        Assert.False(Egress.Allowed(new Uri("http://me:secret@127.0.0.1:8792/"), Egress.Purpose.Account));
    }

    [Fact]
    public void Crash_reports_go_only_to_the_report_service_over_https_for_the_report_purpose()
    {
        var report = new Uri("https://reports.getexo.app/v1/report");
        Assert.True(Egress.Allowed(report, Egress.Purpose.Reports));
        Assert.False(Egress.Allowed(new Uri("http://reports.getexo.app/v1/report"), Egress.Purpose.Reports));
        Assert.False(Egress.Allowed(new Uri("https://reports.getexo.app/v1/report?x=1"), Egress.Purpose.Reports));
        Assert.False(Egress.Allowed(new Uri("https://me:x@reports.getexo.app/v1/report"), Egress.Purpose.Reports));
        Assert.False(Egress.Allowed(new Uri("https://evil.example/v1/report"), Egress.Purpose.Reports));
        Assert.False(Egress.Allowed(new Uri("https://updates.getexo.app/x"), Egress.Purpose.Reports));
        foreach (var purpose in new[] { Egress.Purpose.Stream, Egress.Purpose.Updates, Egress.Purpose.Server, Egress.Purpose.Account })
            Assert.False(Egress.Allowed(report, purpose), $"the report host reached {purpose}");
    }

    [Fact]
    public async Task A_refused_connection_is_logged_by_purpose_and_never_by_address()
    {
        var folder = Path.Combine(Path.GetTempPath(), "exo-cinema-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previous = AppLog.Shared;
        AppLog.Shared = new AppLog(Path.Combine(folder, "app.log"));
        try
        {
            using var client = new HttpClient(Egress.Checkpoint(Egress.Purpose.Reports, new ThrowingHandler()));
            await Assert.ThrowsAsync<Egress.Refusal>(() => client.GetAsync("https://evil.example/secret-path?token=1"));
            var lines = AppLog.Shared.Tail(100);
            Assert.Contains("egress: refused a reports request", lines);
            Assert.DoesNotContain("evil.example", lines);
            Assert.DoesNotContain("secret-path", lines);
        }
        finally
        {
            AppLog.Shared = previous;
        }
    }

    /// <summary>An inner handler that must never be reached: a refused request stops at the checkpoint.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("the request got past the checkpoint");
    }

    [Fact]
    public void Exo_s_dev_account_service_only_while_dev_json_is_on_and_only_its_host()
    {
        var account = new Uri("https://" + Exo.Identity.DevServers.AccountHost + "/v1/session");
        Assert.True(Egress.Allowed(account, Egress.Purpose.Account, devServers: true));
        Assert.False(Egress.Allowed(account, Egress.Purpose.Account, devServers: false));
        Assert.False(Egress.Allowed(new Uri("http://" + Exo.Identity.DevServers.AccountHost + "/v1/session"), Egress.Purpose.Account, devServers: true));
        Assert.False(Egress.Allowed(new Uri("https://other-dev.exo-erix.workers.dev/v1/session"), Egress.Purpose.Account, devServers: true));
    }

    // The mailbox sync was retired in 1.4.10 and its code removed in 1.5: no purpose reaches a mailbox, on this PC or the
    // dev one, and nothing in the app opens a socket of that kind.
    [Fact]
    public void Nothing_reaches_a_mailbox_any_more()
    {
        Assert.DoesNotContain(Enum.GetNames<Egress.Purpose>(), n => n is "Mailbox" or "MediaStore");
        foreach (var purpose in Enum.GetValues<Egress.Purpose>())
        {
            Assert.False(Egress.Allowed(new Uri("ws://127.0.0.1:8787/v1/box/0123456789abcdef0123456789abcdef"), purpose), purpose.ToString());
            Assert.False(Egress.Allowed(new Uri("wss://" + Exo.Identity.DevServers.MailboxHost + "/v1/box/0123456789abcdef0123456789abcdef"), purpose, devServers: true), purpose.ToString());
        }
        foreach (var file in new[] { "MainWindow.Sync.cs", "MainWindow.xaml.cs", "MainWindow.Account.cs" })
            Assert.DoesNotMatch(@"CinemaSync|ExoAccount\b|MailboxSocket|IMailboxSocket|MailboxConnect", File.ReadAllText(Path.Combine(Repo(), "src", "ExoCinema", file)));
    }
    [Fact]
    public void Every_web_client_in_the_app_is_made_through_the_checkpoint()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(Repo(), "src", "ExoCinema"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        var made = 0;
        foreach (var file in sources)
            foreach (var line in File.ReadLines(file))
            {
                if (line.Contains("new SocketsHttpHandler") || line.Contains("new HttpClientHandler"))
                {
                    made++;
                    Assert.True(line.Contains("Egress.Checkpoint("), $"{Path.GetFileName(file)} makes a web client that skips Net/Egress.cs: {line.Trim()}");
                }
                Assert.False(BareClient().IsMatch(line), $"{Path.GetFileName(file)} makes a web client with .NET's own handler: {line.Trim()}");
                // No socket of its own either: the only one the app ever made was the mailbox's, gone in 1.5.
                Assert.False(line.Contains("new ClientWebSocket"), $"{Path.GetFileName(file)} opens a socket that skips Net/Egress.cs: {line.Trim()}");
            }
        Assert.True(made >= 7, $"only {made} web clients found: the scan isn't seeing the app");
    }

    // new HttpClient() and new HttpMessageInvoker() with no handler of their own use .NET's, which skips the checkpoint.
    [GeneratedRegex(@"new\s+(HttpClient|HttpMessageInvoker)\s*\(\s*\)")]
    private static partial Regex BareClient();
}
