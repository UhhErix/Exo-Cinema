using System.IO.Compression;
using System.Security.Cryptography;

namespace ExoCinema.Updates;

/// <summary>A release staged beside the app, complete and checked, waiting for the swap.</summary>
internal sealed record StagedUpdate(string Root, string Incoming, string Version, long BytesDownloaded, int FilesDownloaded, int FilesReused);

/// <summary>
/// A file-level update (misc\updates\file-level-updates.md): the new release's file list is read from Exo's update
/// address, the files the installed app already has are copied, only the others are downloaded (each checked by size and
/// SHA-256 as it arrives), and the whole new app folder is built beside the installed one and checked against the list,
/// file by file, before anything installed is touched. The swap itself is UpdateSwap's. Whatever goes wrong here throws
/// <see cref="FileUpdateUnavailable"/> (or a network or disk error) with nothing installed changed, and the caller
/// downloads the full Setup instead.
/// </summary>
internal sealed class FileUpdater(string appFolder, string baseUrl, Func<string, CancellationToken, Task<HttpResponseMessage>> get, Action<string> log)
{
    /// <summary>Downloads at once. Most updates fetch a handful of files.</summary>
    private const int Parallel = 4;
    public const string IncomingPrefix = "app.incoming-";
    public const string PreviousPrefix = "app.previous-";

    private readonly string _app = Path.GetFullPath(appFolder).TrimEnd('\\');
    public IReadOnlyList<string> Required { get; init; } = PayloadManifest.CinemaRequired;

    private string Root => Path.GetDirectoryName(_app) ?? throw new FileUpdateUnavailable("no-root", "The app folder has no parent.");

    /// <summary>Stages <paramref name="version"/> beside the app. <paramref name="setupBytes"/> is the full Setup's size when known.</summary>
    public async Task<StagedUpdate> StageAsync(string version, long? setupBytes, Action<double> progress, CancellationToken ct = default)
    {
        var root = Root;
        // Failed, or started and never finished: either way this version is not tried the same way twice.
        if (UpdateState.Read(root) is { State: not UpdateState.Done } last && last.Version == version)
            throw new FileUpdateUnavailable("failed-before", "This version's files didn't go in last time, so Setup is used.");
        if (!File.Exists(Path.Combine(_app, Required[0]))) throw new FileUpdateUnavailable("not-installed", "The app folder isn't an installed copy.");

        byte[] manifestBytes;
        using (var r = await GetOrUnavailableAsync($"{baseUrl}/payload/v{version}", "no-file-list", ct))
        {
            if (r.Content.Headers.ContentLength > PayloadManifest.MaxBytes) throw new FileUpdateUnavailable("bad-file-list", "The file list is too large.");
            manifestBytes = await ReadLimitedAsync(await r.Content.ReadAsStreamAsync(ct), PayloadManifest.MaxBytes, ct);
        }
        PayloadManifest target;
        try { target = PayloadManifest.Parse(manifestBytes, Required); }
        catch (InvalidDataException e) { throw new FileUpdateUnavailable("bad-file-list", e.Message, e); }
        if (target.Version != version) throw new FileUpdateUnavailable("bad-file-list", "The file list is for another version.");

        var plan = await Task.Run(() => FileUpdatePlan.Make(_app, target), ct);
        long free;
        try { free = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace; } catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { free = long.MaxValue; }
        if (plan.WhyNot(target, setupBytes, free) is { } no) throw no;

        // Nothing of an earlier try is reused: every staged folder starts empty and is checked whole.
        RemoveLeftovers(root, IncomingPrefix);
        var incoming = Path.Combine(root, $"{IncomingPrefix}u{Environment.ProcessId}");
        Directory.CreateDirectory(incoming);
        try
        {
            var total = Math.Max(1, target.TotalBytes);
            long done = 0, wire = 0;
            void Add(long n) => progress(Math.Min(1.0, (double)Interlocked.Add(ref done, n) / total));

            // The changed files first: each distinct one once, straight to its place in the staged folder.
            var groups = plan.Download.GroupBy(f => f.Sha256).ToList();
            using (var gate = new SemaphoreSlim(Parallel))
            {
                await Task.WhenAll(groups.Select(async group =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var first = group.First();
                        var path = Place(incoming, first.Path);
                        Interlocked.Add(ref wire, await DownloadAsync(first, path, Add, ct));
                        foreach (var twin in group.Skip(1))
                        {
                            File.Copy(path, Place(incoming, twin.Path));
                            Add(twin.Size);
                        }
                    }
                    finally { gate.Release(); }
                }));
            }

            // Then the files it already has, hashed again as they are copied (a file changed since the plan stops it here).
            await Task.Run(() =>
            {
                foreach (var (file, source) in plan.Reuse)
                {
                    ct.ThrowIfCancellationRequested();
                    if (CopyHashed(source, Place(incoming, file.Path), file.Size) != file.Sha256)
                        throw new FileUpdateUnavailable("changed-underneath", "An installed file changed while the update was being put together.");
                    Add(file.Size);
                }
                // What Setup wrote beside the payload stays with the app: the uninstaller and its window.
                foreach (var extra in PayloadManifest.InstallExtras)
                {
                    var from = Path.Combine(_app, extra);
                    if (extra != PayloadManifest.FileName && File.Exists(from)) File.Copy(from, Path.Combine(incoming, extra));
                }
                File.WriteAllBytes(Path.Combine(incoming, PayloadManifest.FileName), manifestBytes);
                // The whole staged folder against the release's list: every file, its size and SHA-256, nothing extra.
                if (target.Mismatch(incoming, manifestBytes) is { } wrong)
                    throw new FileUpdateUnavailable("staged-mismatch", $"The new files didn't check out: {wrong}.");
            }, ct);

            progress(1);
            log($"update: files for {version} staged ({groups.Count} downloaded, {wire} bytes; {plan.Reuse.Count} kept; {plan.Removed.Count} dropped)");
            return new StagedUpdate(root, incoming, version, wire, groups.Count, plan.Reuse.Count);
        }
        catch
        {
            TryDelete(incoming);
            throw;
        }
    }

    // One file from the update address: gzip of the exact bytes, named by their SHA-256. It is unpacked to its place and
    // counted and hashed on the way; more bytes than the list says, fewer, or another hash, and the file is refused.
    private async Task<long> DownloadAsync(PayloadFile file, string path, Action<long> add, CancellationToken ct)
    {
        using var r = await GetOrUnavailableAsync($"{baseUrl}/file/{file.Sha256}", "file-missing", ct);
        var counted = new CountingStream(await r.Content.ReadAsStreamAsync(ct));
        try
        {
            await using var from = new GZipStream(counted, CompressionMode.Decompress);
            await using var to = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long size = 0;
            int n;
            while ((n = await from.ReadAsync(buffer, ct)) > 0)
            {
                if ((size += n) > file.Size) throw new FileUpdateUnavailable("download-mismatch", "A downloaded file is larger than the release says.");
                await to.WriteAsync(buffer.AsMemory(0, n), ct);
                hash.AppendData(buffer, 0, n);
                add(n);
            }
            if (size != file.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(file.Sha256)))
                throw new FileUpdateUnavailable("download-mismatch", "A downloaded file doesn't match the release's checksum.");
        }
        catch (InvalidDataException e)
        {
            throw new FileUpdateUnavailable("download-mismatch", "A downloaded file couldn't be unpacked.", e);
        }
        return counted.Count;
    }

    private async Task<HttpResponseMessage> GetOrUnavailableAsync(string url, string reason, CancellationToken ct)
    {
        try { return await get(url, ct); }
        catch (HttpRequestException e) { throw new FileUpdateUnavailable(reason, "The update address doesn't have this version's files.", e); }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, CancellationToken ct)
    {
        await using var _ = stream;
        using var memory = new MemoryStream();
        var buffer = new byte[1 << 14];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + n > limit) throw new FileUpdateUnavailable("bad-file-list", "The file list is too large.");
            memory.Write(buffer, 0, n);
        }
        return memory.ToArray();
    }

    // The staged place for a release path. Paths were checked when the list was read; this holds them inside the folder anyway.
    private static string Place(string incoming, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(incoming, relative.Replace('/', '\\')));
        if (!path.StartsWith(incoming + "\\", StringComparison.OrdinalIgnoreCase)) throw new FileUpdateUnavailable("bad-file-list", "A file's path leaves the app folder.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string CopyHashed(string source, string target, long expected)
    {
        using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 17];
        long size = 0;
        int n;
        while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            to.Write(buffer, 0, n);
            hash.AppendData(buffer, 0, n);
            size += n;
        }
        return size == expected ? Convert.ToHexStringLower(hash.GetHashAndReset()) : "";
    }

    public static void RemoveLeftovers(string root, string prefix)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, prefix + "*")) TryDelete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The errors that mean "use Setup instead": nothing installed was changed when one of these is thrown.</summary>
    public static bool FallsBack(Exception e) =>
        e is FileUpdateUnavailable or HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException;

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); Count += n; return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var n = await inner.ReadAsync(buffer, ct); Count += n; return n; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
