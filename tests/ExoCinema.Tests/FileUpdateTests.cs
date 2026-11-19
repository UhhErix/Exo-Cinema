using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoCinema.Updates;

namespace ExoCinema.Tests;

/// <summary>
/// File-level updates up to the swap (src\ExoCinema\Updates): reading a release's file list, working out which files are
/// already installed, downloading and checking the rest, and putting the new app folder together beside the old one.
/// How it could fail: a list that names a path outside the app folder is accepted; an installed file that was altered is
/// trusted because an old list said so; a download with the wrong bytes is kept; a staged folder that isn't the release
/// is offered for the swap; or a failure leaves something behind or touches the installed app instead of falling back.
/// </summary>
public sealed class FileUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-cinema-fileupdate-" + Guid.NewGuid().ToString("N"));
    private string App => Path.Combine(_root, "app");
    private static readonly string[] Required = ["ExoCinema.exe"];
    private const string Base = "https://updates.test/v1/cinema";

    public FileUpdateTests() => Directory.CreateDirectory(App);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    internal static byte[] ManifestOf(string version, IReadOnlyDictionary<string, byte[]> files) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            version,
            files = files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new { path = f.Key, size = f.Value.Length, sha256 = Sha(f.Value) }).ToArray(),
        });

    internal static void WriteTree(string folder, IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (path, bytes) in files)
        {
            var full = Path.Combine(folder, path.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
    }

    internal static Dictionary<string, byte[]> ReadTree(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f[(folder.Length + 1)..].Replace('\\', '/'), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static byte[] Gzip(byte[] bytes)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Optimal)) gzip.Write(bytes);
        return memory.ToArray();
    }

    private static readonly Dictionary<string, byte[]> Old = new()
    {
        ["ExoCinema.exe"] = Bytes("host 1"),
        ["ExoCinema.dll"] = Bytes("app code, version one"),
        ["libmpv-2.dll"] = Bytes(new string('m', 4000)),
        ["wwwroot/index.html"] = Bytes("<html>one</html>"),
        ["wwwroot/old.js"] = Bytes("gone in two"),
    };

    private static readonly Dictionary<string, byte[]> New = new()
    {
        ["ExoCinema.exe"] = Bytes("host 1"),
        ["ExoCinema.dll"] = Bytes("app code, version two!"),
        ["libmpv-2.dll"] = Bytes(new string('m', 4000)),
        ["wwwroot/index.html"] = Bytes("<html>one</html>"),
    };

    /// <summary>The update address for one release: its file list and its files by hash, with every request recorded.</summary>
    private sealed class Service(string version, IReadOnlyDictionary<string, byte[]> files)
    {
        public List<string> Asked { get; } = [];
        public Dictionary<string, byte[]> Objects { get; } = files.Values.DistinctBy(Sha).ToDictionary(b => $"{Base}/file/{Sha(b)}", Gzip);
        public byte[] Manifest { get; set; } = ManifestOf(version, files);
        public string ListUrl { get; } = $"{Base}/payload/v{version}";

        public Task<HttpResponseMessage> Get(string url, CancellationToken ct)
        {
            lock (Asked) Asked.Add(url);
            var bytes = url == ListUrl ? Manifest : Objects.GetValueOrDefault(url);
            if (bytes is null) throw new HttpRequestException("updates.test answered 404.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private FileUpdater Updater(Service service) => new(App, Base, service.Get, _ => { }) { Required = Required };

    private void Install(IReadOnlyDictionary<string, byte[]> files, string version = "1.0.0")
    {
        WriteTree(App, files);
        File.WriteAllBytes(Path.Combine(App, PayloadManifest.FileName), ManifestOf(version, files));
        File.WriteAllText(Path.Combine(App, "Uninstall.exe"), "uninstaller");
        File.WriteAllText(Path.Combine(App, "ExoSetup.exe"), "setup window");
    }

    // ---- the file list

    [Fact]
    public void A_release_s_file_list_is_read_with_every_file_s_path_size_and_hash()
    {
        var m = PayloadManifest.Parse(ManifestOf("2.0.0", New), Required);
        Assert.Equal("2.0.0", m.Version);
        Assert.Equal(New.Count, m.Files.Count);
        Assert.Equal(New.Values.Sum(b => (long)b.Length), m.TotalBytes);
        Assert.Contains(m.Files, f => f.Path == "wwwroot/index.html" && f.Sha256 == Sha(New["wwwroot/index.html"]));
    }

    [Theory]
    [InlineData("""{"schema":2,"version":"2.0.0","files":[{"path":"ExoCinema.exe","size":1,"sha256":"HASH"}]}""")]
    [InlineData("""{"schema":1,"version":"two","files":[{"path":"ExoCinema.exe","size":1,"sha256":"HASH"}]}""")]
    [InlineData("""{"schema":1,"version":"2.0.0","files":[]}""")]
    [InlineData("""{"schema":1,"version":"2.0.0","files":[{"path":"ExoCinema.exe","size":-1,"sha256":"HASH"}]}""")]
    [InlineData("""{"schema":1,"version":"2.0.0","files":[{"path":"ExoCinema.exe","size":1,"sha256":"abc"}]}""")]
    [InlineData("""{"schema":1,"version":"2.0.0","files":[{"path":"ExoCinema.exe","size":1,"sha256":"HASH"},{"path":"../evil.dll","size":1,"sha256":"HASH"}]}""")]
    [InlineData("""{"schema":1,"version":"2.0.0","files":[{"path":"ExoCinema.exe","size":1,"sha256":"HASH"},{"path":"C:/evil.dll","size":1,"sha256":"HASH"}]}""")]
    [InlineData("""{"schema":1,"version":"2.0.0","files":[{"path":"ExoCinema.exe","size":1,"sha256":"HASH"},{"path":"/evil.dll","size":1,"sha256":"HASH"}]}""")]
    [InlineData("""{"schema":1,"version":"2.0.0","files":[{"path":"ExoCinema.exe","size":1,"sha256":"HASH"},{"path":"a\\evil.dll","size":1,"sha256":"HASH"}]}""")]
    [InlineData("""{"schema":1,"version":"2.0.0","files":[{"path":"ExoCinema.exe","size":1,"sha256":"HASH"},{"path":"x.dll:stream","size":1,"sha256":"HASH"}]}""")]
    [InlineData("""{"schema":1,"version":"2.0.0","files":[{"path":"ExoCinema.exe","size":1,"sha256":"HASH"},{"path":"exocinema.EXE","size":1,"sha256":"HASH"}]}""")]
    [InlineData("""{"schema":1,"version":"2.0.0","files":[{"path":"ExoCinema.exe","size":1,"sha256":"HASH"},{"path":"Uninstall.exe","size":1,"sha256":"HASH"}]}""")]
    [InlineData("""{"schema":1,"version":"2.0.0","files":[{"path":"Other.exe","size":1,"sha256":"HASH"}]}""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public void A_file_list_a_release_build_would_not_write_is_refused(string json) =>
        Assert.Throws<InvalidDataException>(() => PayloadManifest.Parse(Bytes(json.Replace("HASH", new string('a', 64))), Required));

    [Fact]
    public void A_folder_is_the_release_only_when_every_file_matches_and_nothing_else_is_there()
    {
        var bytes = ManifestOf("2.0.0", New);
        var m = PayloadManifest.Parse(bytes, Required);
        Install(New, "2.0.0");
        Assert.Null(m.Mismatch(App, bytes)); // with what an install adds: the uninstaller's two files

        File.WriteAllText(Path.Combine(App, "extra.dll"), "x");
        Assert.Contains("unexpected", m.Mismatch(App, bytes));
        File.Delete(Path.Combine(App, "extra.dll"));

        File.WriteAllBytes(Path.Combine(App, "ExoCinema.dll"), Bytes("app code, version TWO!")); // same size, other bytes
        Assert.Contains("doesn't match", m.Mismatch(App, bytes));
        File.WriteAllBytes(Path.Combine(App, "ExoCinema.dll"), Bytes("short"));
        Assert.Contains("wrong size", m.Mismatch(App, bytes));
        File.Delete(Path.Combine(App, "ExoCinema.dll"));
        Assert.Contains("missing", m.Mismatch(App, bytes));
        File.WriteAllBytes(Path.Combine(App, "ExoCinema.dll"), New["ExoCinema.dll"]);
        Assert.Null(m.Mismatch(App, bytes));

        File.WriteAllBytes(Path.Combine(App, PayloadManifest.FileName), ManifestOf("2.0.1", New));
        Assert.Contains("file list", m.Mismatch(App, bytes));
    }

    // ---- what is already installed

    [Fact]
    public void Only_the_files_whose_bytes_are_not_installed_are_downloaded()
    {
        Install(Old);
        var plan = FileUpdatePlan.Make(App, PayloadManifest.Parse(ManifestOf("2.0.0", New), Required));
        Assert.Equal(["ExoCinema.dll"], plan.Download.Select(f => f.Path));
        Assert.Equal(3, plan.Reuse.Count);
        Assert.Equal(["wwwroot/old.js"], plan.Removed);
        Assert.Equal(New["ExoCinema.dll"].Length, plan.DownloadBytes);
    }

    [Fact]
    public void An_installed_file_that_was_altered_is_downloaded_again_whatever_the_old_list_says()
    {
        Install(New, "2.0.0"); // the installed list says it is already this release
        File.WriteAllBytes(Path.Combine(App, "libmpv-2.dll"), Bytes(new string('x', 4000))); // same size, other bytes
        File.Delete(Path.Combine(App, "wwwroot", "index.html"));
        var plan = FileUpdatePlan.Make(App, PayloadManifest.Parse(ManifestOf("2.0.0", New), Required));
        Assert.Equal(["libmpv-2.dll", "wwwroot/index.html"], plan.Download.Select(f => f.Path).Order());
    }

    [Fact]
    public void A_file_the_release_moved_or_repeats_is_taken_from_where_its_bytes_are()
    {
        Install(Old);
        var next = new Dictionary<string, byte[]>(New) { ["lib/mpv.dll"] = Old["libmpv-2.dll"], ["a/new.bin"] = Bytes("same new bytes"), ["b/new.bin"] = Bytes("same new bytes") };
        next.Remove("libmpv-2.dll");
        var plan = FileUpdatePlan.Make(App, PayloadManifest.Parse(ManifestOf("2.0.0", next), Required));
        Assert.Contains(plan.Reuse, r => r.File.Path == "lib/mpv.dll" && r.Source.EndsWith("libmpv-2.dll"));
        Assert.Equal(["ExoCinema.dll", "a/new.bin", "b/new.bin"], plan.Download.Select(f => f.Path).Order(StringComparer.Ordinal));
        // The same bytes at two paths count, and travel, once.
        Assert.Equal(New["ExoCinema.dll"].Length + "same new bytes".Length, plan.DownloadBytes);
    }

    [Fact]
    public void A_first_file_level_release_needs_no_list_from_the_installed_version()
    {
        Install(Old);
        File.Delete(Path.Combine(App, PayloadManifest.FileName)); // installed before lists were kept
        var plan = FileUpdatePlan.Make(App, PayloadManifest.Parse(ManifestOf("2.0.0", New), Required));
        Assert.Equal(["ExoCinema.dll"], plan.Download.Select(f => f.Path));
    }

    [Fact]
    public void Setup_is_the_better_download_once_the_changed_files_weigh_as_much_and_the_only_one_without_room()
    {
        Install(Old);
        var target = PayloadManifest.Parse(ManifestOf("2.0.0", New), Required);
        var plan = FileUpdatePlan.Make(App, target);
        Assert.Null(plan.WhyNot(target, setupBytes: null, freeBytes: long.MaxValue));
        Assert.Null(plan.WhyNot(target, setupBytes: plan.DownloadBytes + 1, freeBytes: long.MaxValue));
        Assert.Equal("larger-than-setup", plan.WhyNot(target, setupBytes: plan.DownloadBytes, freeBytes: long.MaxValue)?.Reason);
        Assert.Equal("no-room", plan.WhyNot(target, setupBytes: null, freeBytes: target.TotalBytes + FileUpdatePlan.SpareBytes - 1)?.Reason);
    }

    // ---- staging

    [Fact]
    public async Task An_update_that_changes_one_file_downloads_only_that_file_and_stages_exactly_the_release()
    {
        Install(Old);
        var service = new Service("2.0.0", New);
        var progress = new List<double>();
        var staged = await Updater(service).StageAsync("2.0.0", setupBytes: null, progress.Add, TestContext.Current.CancellationToken);

        Assert.Equal([service.ListUrl, $"{Base}/file/{Sha(New["ExoCinema.dll"])}"], service.Asked);
        Assert.Equal(1, staged.FilesDownloaded);
        Assert.Equal(3, staged.FilesReused);
        Assert.Equal(service.Objects[$"{Base}/file/{Sha(New["ExoCinema.dll"])}"].Length, staged.BytesDownloaded);
        Assert.StartsWith(Path.Combine(_root, "app.incoming-u"), staged.Incoming);

        var tree = ReadTree(staged.Incoming);
        foreach (var (path, bytes) in New) Assert.Equal(bytes, tree[path]);
        Assert.Equal(service.Manifest, tree[PayloadManifest.FileName]); // the release's own list, byte for byte
        Assert.Equal("uninstaller", Encoding.UTF8.GetString(tree["Uninstall.exe"]));
        Assert.Equal(New.Count + 3, tree.Count); // nothing the release dropped, nothing extra
        Assert.Null(PayloadManifest.Parse(service.Manifest, Required).Mismatch(staged.Incoming, service.Manifest));
        Assert.Equal(1.0, progress[^1]);
        // The installed app is as it was.
        foreach (var (path, bytes) in Old) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(App, path.Replace('/', '\\'))));
    }

    [Fact]
    public async Task A_jump_over_several_versions_is_the_same_update_only_with_more_files()
    {
        Install(Old, "1.0.0");
        var far = new Dictionary<string, byte[]>(New) { ["ExoCinema.exe"] = Bytes("host 5"), ["wwwroot/index.html"] = Bytes("<html>five</html>"), ["new/feature.dll"] = Bytes("feature") };
        var service = new Service("5.0.0", far);
        var staged = await Updater(service).StageAsync("5.0.0", null, _ => { }, TestContext.Current.CancellationToken);
        Assert.Equal(4, staged.FilesDownloaded);
        Assert.Null(PayloadManifest.Parse(service.Manifest, Required).Mismatch(staged.Incoming, service.Manifest));
    }

    public static TheoryData<string> BadDownloads => ["other-bytes", "same-size-other-bytes", "truncated", "not-gzip", "larger", "missing"];

    [Theory]
    [MemberData(nameof(BadDownloads))]
    public async Task A_download_that_is_not_the_release_s_file_falls_back_to_Setup_and_leaves_nothing_behind(string fault)
    {
        Install(Old);
        var service = new Service("2.0.0", New);
        var url = $"{Base}/file/{Sha(New["ExoCinema.dll"])}";
        var good = service.Objects[url];
        switch (fault)
        {
            case "other-bytes": service.Objects[url] = Gzip(Bytes("something else entirely")); break;
            case "same-size-other-bytes": service.Objects[url] = Gzip(Bytes("app code, version TWO!")); break;
            case "truncated": service.Objects[url] = good[..(good.Length / 2)]; break;
            case "not-gzip": service.Objects[url] = New["ExoCinema.dll"]; break;
            case "larger": service.Objects[url] = Gzip(new byte[1 << 20]); break;
            case "missing": service.Objects.Remove(url); break;
        }
        var e = await Assert.ThrowsAsync<FileUpdateUnavailable>(() => Updater(service).StageAsync("2.0.0", null, _ => { }, TestContext.Current.CancellationToken));
        Assert.Equal(fault == "missing" ? "file-missing" : "download-mismatch", e.Reason);
        Assert.True(FileUpdater.FallsBack(e));
        Assert.Empty(Directory.EnumerateDirectories(_root, "app.incoming-*"));
        foreach (var (path, bytes) in Old) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(App, path.Replace('/', '\\'))));
    }

    [Fact]
    public async Task A_release_with_no_file_list_or_another_version_s_falls_back_to_Setup()
    {
        Install(Old);
        var none = new Service("2.0.0", New);
        var e = await Assert.ThrowsAsync<FileUpdateUnavailable>(() => Updater(none).StageAsync("2.0.1", null, _ => { }, TestContext.Current.CancellationToken));
        Assert.Equal("no-file-list", e.Reason);

        var other = new Service("2.0.0", New) { Manifest = ManifestOf("2.0.1", New) };
        e = await Assert.ThrowsAsync<FileUpdateUnavailable>(() => Updater(other).StageAsync("2.0.0", null, _ => { }, TestContext.Current.CancellationToken));
        Assert.Equal("bad-file-list", e.Reason);

        var escaping = new Service("2.0.0", New) { Manifest = Bytes($$"""{"schema":1,"version":"2.0.0","files":[{"path":"ExoCinema.exe","size":6,"sha256":"{{Sha(New["ExoCinema.exe"])}}"},{"path":"../../evil.dll","size":1,"sha256":"{{new string('a', 64)}}"}]}""") };
        e = await Assert.ThrowsAsync<FileUpdateUnavailable>(() => Updater(escaping).StageAsync("2.0.0", null, _ => { }, TestContext.Current.CancellationToken));
        Assert.Equal("bad-file-list", e.Reason);
        Assert.Single(escaping.Asked); // nothing was fetched for a list that can't be used
    }

    [Theory]
    [InlineData(UpdateState.Failed, "2.0.0", true)]
    [InlineData(UpdateState.Swapping, "2.0.0", true)] // a swap that never finished
    [InlineData(UpdateState.Staged, "2.0.0", true)]
    [InlineData(UpdateState.Done, "2.0.0", false)]
    [InlineData(UpdateState.Failed, "1.9.0", false)] // an older version's failure doesn't hold this one back
    public async Task A_version_whose_files_did_not_go_in_last_time_goes_through_Setup(string state, string version, bool setup)
    {
        Install(Old);
        new UpdateState(version, state, "in-use").Write(_root);
        var service = new Service("2.0.0", New);
        if (setup)
        {
            var e = await Assert.ThrowsAsync<FileUpdateUnavailable>(() => Updater(service).StageAsync("2.0.0", null, _ => { }, TestContext.Current.CancellationToken));
            Assert.Equal("failed-before", e.Reason);
            Assert.Empty(service.Asked);
        }
        else Assert.Equal("2.0.0", (await Updater(service).StageAsync("2.0.0", null, _ => { }, TestContext.Current.CancellationToken)).Version);
    }

    [Fact]
    public async Task Changed_files_as_large_as_Setup_go_through_Setup()
    {
        Install(Old);
        var service = new Service("2.0.0", New);
        var e = await Assert.ThrowsAsync<FileUpdateUnavailable>(() => Updater(service).StageAsync("2.0.0", setupBytes: 5, _ => { }, TestContext.Current.CancellationToken));
        Assert.Equal("larger-than-setup", e.Reason);
        Assert.Equal([service.ListUrl], service.Asked);
    }

    [Fact]
    public void Only_failures_that_left_the_app_untouched_fall_back_to_Setup()
    {
        Assert.True(FileUpdater.FallsBack(new FileUpdateUnavailable("x", "y")));
        Assert.True(FileUpdater.FallsBack(new HttpRequestException("down")));
        Assert.True(FileUpdater.FallsBack(new IOException("disk")));
        Assert.True(FileUpdater.FallsBack(new UnauthorizedAccessException()));
        Assert.True(FileUpdater.FallsBack(new TaskCanceledException("timed out")));
        Assert.True(FileUpdater.FallsBack(new TaskCanceledException("timed out", null, new CancellationToken(true)))); // HttpClient's timeout
        Assert.False(FileUpdater.FallsBack(new InvalidOperationException()));
    }
}
