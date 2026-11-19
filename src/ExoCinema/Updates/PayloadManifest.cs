using System.Security.Cryptography;
using System.Text.Json;

namespace ExoCinema.Updates;

/// <summary>One file of the app as a release has it: where it sits under the app folder, its size and its SHA-256.</summary>
internal sealed record PayloadFile(string Path, long Size, string Sha256);

/// <summary>
/// A release's payload-manifest.json (tools\installer\New-PayloadManifest.ps1 writes it as Setup is built): every file of
/// the app folder with its size and SHA-256. Setup checks its staged copy against it; a file-level update reads the new
/// release's to learn which files it already has (misc\updates\file-level-updates.md).
/// </summary>
internal sealed class PayloadManifest
{
    public const string FileName = "payload-manifest.json";
    public const int MaxBytes = 8 * 1024 * 1024;
    private const int MaxFiles = 20_000;

    /// <summary>What an install adds beside the payload: the manifest itself and the uninstaller's two files.</summary>
    public static readonly IReadOnlyList<string> InstallExtras = [FileName, "Uninstall.exe", "ExoSetup.exe"];

    /// <summary>What Exo Cinema can't start without (tools\installer\Verify-Payload.ps1 holds the same list).</summary>
    public static readonly IReadOnlyList<string> CinemaRequired =
        ["ExoCinema.exe", "ExoCinema.dll", "ExoCinema.deps.json", "ExoCinema.runtimeconfig.json", "wwwroot/index.html", "libmpv-2.dll"];

    public required string Version { get; init; }
    public required IReadOnlyList<PayloadFile> Files { get; init; }
    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>Reads a manifest, refusing anything a release build would not write. Throws InvalidDataException.</summary>
    public static PayloadManifest Parse(ReadOnlySpan<byte> json, IReadOnlyList<string> required)
    {
        if (json.Length is 0 or > MaxBytes) throw new InvalidDataException("The file list is empty or too large.");
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1)
                throw new InvalidDataException("The file list isn't one this version reads.");
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            if (!System.Version.TryParse(version, out var parsed) || parsed.ToString(3) != version) throw new InvalidDataException("The file list names no version.");
            if (!root.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > MaxFiles)
                throw new InvalidDataException("The file list has no files.");
            var files = new List<PayloadFile>(list.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in list.EnumerateArray())
            {
                var path = entry.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
                var sha = entry.TryGetProperty("sha256", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
                if (!entry.TryGetProperty("size", out var z) || z.ValueKind != JsonValueKind.Number || !z.TryGetInt64(out var size) || size < 0)
                    throw new InvalidDataException("A file in the list has no size.");
                if (!SafePath(path) || !IsSha256(sha)) throw new InvalidDataException("A file in the list has a path or hash that can't be used.");
                if (InstallExtras.Contains(path, StringComparer.OrdinalIgnoreCase) || !seen.Add(path)) throw new InvalidDataException("A file is listed twice, or is one Setup writes itself.");
                files.Add(new PayloadFile(path, size, sha.ToLowerInvariant()));
            }
            foreach (var need in required)
                if (!seen.Contains(need)) throw new InvalidDataException($"The file list is missing {need}.");
            return new PayloadManifest { Version = version, Files = files };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The file list couldn't be read.", e);
        }
    }

    /// <summary>A relative path with forward slashes, plain names only: nothing rooted, no "..", no drive or stream colon.</summary>
    public static bool SafePath(string path)
    {
        if (path.Length is 0 or > 240 || path.Contains('\\')) return false;
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) return false;
            if (part.AsSpan().IndexOfAny(Invalid) >= 0 || part.Any(char.IsControl)) return false;
        }
        return true;
    }

    private static readonly char[] Invalid = [':', '*', '?', '"', '<', '>', '|'];

    public static bool IsSha256(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);

    public static string Sha256Of(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is exactly this release: every listed file at its size and SHA-256, the manifest
    /// itself as <paramref name="manifestBytes"/>, nothing else but what an install adds, and no links. Returns what is
    /// wrong, or null when nothing is.
    /// </summary>
    public string? Mismatch(string folder, ReadOnlySpan<byte> manifestBytes)
    {
        var root = System.IO.Path.GetFullPath(folder).TrimEnd('\\');
        if (!Directory.Exists(root)) return "the folder is missing";
        var expected = new Dictionary<string, PayloadFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files) expected[f.Path] = f;
        var found = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "a link is in the folder";
                if (entry is DirectoryInfo) { pending.Push(entry.FullName); continue; }
                var relative = entry.FullName[(root.Length + 1)..].Replace('\\', '/');
                if (InstallExtras.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
                if (!expected.TryGetValue(relative, out var want)) return $"an unexpected file ({relative})";
                if (((FileInfo)entry).Length != want.Size) return $"a file of the wrong size ({relative})";
                if (!string.Equals(Sha256Of(entry.FullName), want.Sha256, StringComparison.Ordinal)) return $"a file that doesn't match ({relative})";
                found++;
            }
        }
        if (found != expected.Count) return "a file is missing";
        var own = System.IO.Path.Combine(root, FileName);
        if (!File.Exists(own) || !File.ReadAllBytes(own).AsSpan().SequenceEqual(manifestBytes)) return "the file list in the folder isn't the release's";
        return null;
    }
}
