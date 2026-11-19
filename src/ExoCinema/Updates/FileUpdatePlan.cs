namespace ExoCinema.Updates;

/// <summary>A file-level update can't be used this time; the full Setup is. <see cref="Reason"/> is a short word for the log.</summary>
internal sealed class FileUpdateUnavailable(string reason, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// What a file-level update has to do to turn the installed app folder into a release: which of the release's files the
/// folder already holds (by size and SHA-256 of the bytes on disk, never by what an old manifest says) and which have to
/// be downloaded. A file the release no longer has is simply not carried over.
/// </summary>
internal sealed class FileUpdatePlan
{
    /// <summary>Release files already on disk, each with the installed file that holds its bytes.</summary>
    public required IReadOnlyList<(PayloadFile File, string Source)> Reuse { get; init; }
    /// <summary>Release files whose bytes aren't installed: changed, new, or altered since they were installed.</summary>
    public required IReadOnlyList<PayloadFile> Download { get; init; }
    /// <summary>Installed files the release doesn't have.</summary>
    public required IReadOnlyList<string> Removed { get; init; }

    /// <summary>Bytes to fetch, each distinct file once (the same bytes at two paths are downloaded once).</summary>
    public long DownloadBytes => Download.DistinctBy(f => f.Sha256).Sum(f => f.Size);

    /// <summary>
    /// Compares the installed folder with the release. Every installed file whose size some release file has is hashed, so
    /// an installed file that was altered is downloaded again, and a file the release moved is found where it is now.
    /// </summary>
    public static FileUpdatePlan Make(string appFolder, PayloadManifest target)
    {
        var root = Path.GetFullPath(appFolder).TrimEnd('\\');
        var sizes = target.Files.Select(f => f.Size).ToHashSet();
        var wanted = target.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var have = new Dictionary<string, string>(StringComparer.Ordinal); // size:sha256 -> installed file
        var samePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // relative path -> size:sha256
        var removed = new List<string>();
        if (Directory.Exists(root))
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var relative = file.FullName[(root.Length + 1)..].Replace('\\', '/');
                if (PayloadManifest.InstallExtras.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
                if (!wanted.Contains(relative)) removed.Add(relative);
                if (!sizes.Contains(file.Length)) continue;
                string key;
                try { key = $"{file.Length}:{PayloadManifest.Sha256Of(file.FullName)}"; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; } // unreadable: downloaded instead
                samePath[relative] = key;
                have.TryAdd(key, file.FullName);
            }
        }
        var reuse = new List<(PayloadFile, string)>();
        var download = new List<PayloadFile>();
        foreach (var f in target.Files)
        {
            var key = $"{f.Size}:{f.Sha256}";
            if (samePath.TryGetValue(f.Path, out var here) && here == key) reuse.Add((f, Path.Combine(root, f.Path.Replace('/', '\\'))));
            else if (have.TryGetValue(key, out var elsewhere)) reuse.Add((f, elsewhere));
            else download.Add(f);
        }
        removed.Sort(StringComparer.OrdinalIgnoreCase);
        return new FileUpdatePlan { Reuse = reuse, Download = download, Removed = removed };
    }

    /// <summary>
    /// Why this plan should not be used, or null when it should. The full Setup is the better download once the changed
    /// files weigh as much as it does (they travel compressed, so this errs towards Setup), and it is the only way when
    /// the disk has no room for a second copy of the app.
    /// </summary>
    public FileUpdateUnavailable? WhyNot(PayloadManifest target, long? setupBytes, long freeBytes)
    {
        if (setupBytes is { } setup && DownloadBytes >= setup)
            return new("larger-than-setup", "The changed files are as large as Setup, so Setup is downloaded.");
        if (freeBytes < target.TotalBytes + SpareBytes)
            return new("no-room", "There isn't room for the new files beside the app.");
        return null;
    }

    public const long SpareBytes = 64L * 1024 * 1024;
}
