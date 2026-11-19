using System.Security.Cryptography;
using Exo.Identity;

namespace ExoCinema.Tests;

/// <summary>
/// Exo.Identity's ProfileMedia, the profile's media folder every Exo app shares. Ways it could fail with no error: a file
/// is taken by its name rather than its bytes (a renamed program kept as a "picture"), two apps keeping one file at once
/// leave a half-written one, or a look's media is said to be here when its file isn't.
/// </summary>
public sealed class ProfileMediaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-cinema-media-" + Guid.NewGuid().ToString("N")[..8]);

    public ProfileMediaTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void A_file_is_known_by_its_first_bytes_never_its_name()
    {
        Assert.Equal("image/png", ProfileMedia.TypeOf([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A]));
        Assert.Equal("image/jpeg", ProfileMedia.TypeOf([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/gif", ProfileMedia.TypeOf("GIF89a"u8));
        Assert.Equal("image/webp", ProfileMedia.TypeOf("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Equal("image/avif", ProfileMedia.TypeOf("\0\0\0 ftypavif"u8));
        Assert.Equal("video/mp4", ProfileMedia.TypeOf("\0\0\0 ftypisom"u8));
        Assert.Equal("video/webm", ProfileMedia.TypeOf([0x1A, 0x45, 0xDF, 0xA3, .. "\u0093B\u0082\u0084webm"u8]));
        Assert.Null(ProfileMedia.TypeOf("MZ\u0090\0"u8));
        Assert.Null(ProfileMedia.Keep(Write("not-a-picture.png", "MZ this is a program"u8.ToArray()), Path.Combine(_root, "kept")));
    }

    [Fact]
    public void Kept_once_under_its_hash_and_found_again_by_the_look()
    {
        byte[] gif = [.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0];
        var folder = Path.Combine(_root, "kept");
        var kept = ProfileMedia.Keep(Write("anything.bin", gif), folder)!;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(gif)), kept.Sha);
        Assert.Equal(("image/gif", (long)gif.Length), (kept.Type, kept.Bytes));
        // The same bytes from elsewhere, twice at once, as two apps might: one file, whole.
        Parallel.For(0, 4, _ => ProfileMedia.Keep(Write($"again-{Guid.NewGuid():N}.gif", gif), folder));
        Assert.Equal([kept.Sha + ".gif"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.Equal(gif, File.ReadAllBytes(Path.Combine(folder, kept.Sha + ".gif")));

        var media = ProfileItems.Media(kept.Sha, kept.Type, kept.Bytes);
        Assert.Equal(Path.Combine(folder, kept.Sha + ".gif"), ProfileMedia.FileOf(media, folder));
        Assert.Null(ProfileMedia.FileOf(ProfileItems.Media(new string('0', 64), "image/png", 10), folder));
        Assert.Null(ProfileMedia.FileOf(media, Path.Combine(_root, "elsewhere")));
    }
}
