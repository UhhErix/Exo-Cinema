using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ExoCinema.Mpv;

/// <summary>
/// The seek bar's pictures made for a video (FramePeek), kept at %LOCALAPPDATA%\ExoCinema\cache\scrub so the next time
/// it plays (resumed tomorrow, watched again) they're all there at once, with nothing read from its host. One file per
/// video, named by a hash of its size and length, each picture added to its end as it's made; past 256 MB the files
/// played longest ago go. Settings › Storage › Clear empties it with the images.
/// </summary>
internal static class ScrubStore
{
    private static readonly string Dir = Path.Combine(AppInfo.Data, "cache", "scrub");
    private const long Keep = 256L << 20;
    private static ReadOnlySpan<byte> Magic => "EXS1"u8;

    /// <summary>The name a video's pictures are kept under; null when its size or length isn't known.</summary>
    public static string? Key(long fileSize, double duration) =>
        fileSize > 0 && duration > 0
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"scrub|{fileSize}|{duration:0.0}"))))[..32]
            : null;

    /// <summary>
    /// Opens a video's file for its new pictures, reading the ones it holds into <paramref name="made"/> (the time on the
    /// page's grid, the time the picture really shows, the JPEG); null when it can't be written.
    /// </summary>
    public static FileStream? Open(string key, Dictionary<double, (double At, byte[] Jpeg)> made)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Trim(key);
            var file = new FileStream(Path.Combine(Dir, key), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var whole = Read(file, made);
            // A picture cut short (Exo Cinema closed while writing it) is dropped, and new ones go after the whole ones.
            if (whole == 0)
            {
                file.SetLength(0);
                file.Write(Magic);
            }
            else
            {
                file.SetLength(whole);
                file.Seek(whole, SeekOrigin.Begin);
            }
            return file;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // The length of the whole pictures at the file's start, read into made; 0 when it isn't one of these files.
    private static long Read(FileStream file, Dictionary<double, (double At, byte[] Jpeg)> made)
    {
        if (file.Length < Magic.Length) return 0;
        Span<byte> head = stackalloc byte[4];
        file.ReadExactly(head);
        if (!head.SequenceEqual(Magic)) return 0;
        using var r = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        var whole = file.Position;
        while (file.Length - file.Position >= 20)
        {
            var t = r.ReadDouble();
            var at = r.ReadDouble();
            var n = r.ReadInt32();
            if (n <= 0 || n > 4 << 20 || file.Length - file.Position < n) break;
            made[t] = (at, r.ReadBytes(n));
            whole = file.Position;
        }
        return whole;
    }

    /// <summary>Adds a picture to a video's file.</summary>
    public static void Add(FileStream file, double t, double at, byte[] jpeg)
    {
        try
        {
            using var w = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);
            w.Write(t);
            w.Write(at);
            w.Write(jpeg.Length);
            w.Write(jpeg);
            w.Flush();
        }
        catch (IOException) { }
    }

    // The files played longest ago go until the rest fit in Keep; the one opening stays.
    private static void Trim(string opening)
    {
        var files = new DirectoryInfo(Dir).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var total = files.Sum(f => f.Length);
        foreach (var f in Enumerable.Reverse(files))
        {
            if (total <= Keep) break;
            if (f.Name == opening) continue;
            try
            {
                f.Delete();
                total -= f.Length;
            }
            catch (IOException) { }
        }
    }

    /// <summary>Bytes the kept pictures take on disk.</summary>
    public static long SizeOnDisk()
    {
        try { return new DirectoryInfo(Dir).EnumerateFiles().Sum(f => f.Length); }
        catch (IOException) { return 0; }
    }

    /// <summary>Removes every video's pictures but the playing one's; returns what is left.</summary>
    public static long Clear()
    {
        try
        {
            foreach (var f in new DirectoryInfo(Dir).EnumerateFiles())
                try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        return SizeOnDisk();
    }
}
