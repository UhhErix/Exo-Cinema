using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace ExoCinema.Library;

/// <summary>
/// Every picture the library shows (posters, backdrops, logos, episode stills), fetched by the app and kept on disk at
/// %LOCALAPPDATA%\ExoCinema\cache\images, so the second time a cover is shown it comes from disk at once. The page asks
/// for one as <c>https://art.cinema.exo/i?u=&lt;its address&gt;</c>; the fetch goes through Egress as a request to the
/// Jellyfin server, so only the server signed in to is ever asked, never from the page. Requests for the same picture at once
/// share one download. With <c>&amp;w=</c> (the box's width in device pixels, in the page's steps) a picture wider than
/// that comes made to it, kept on disk beside the original: a 300 px poster in a 160 px card decoded at twice the pixels
/// it shows, and after a long browse those decoded pictures were most of WebView2's memory.
/// </summary>
internal sealed class ArtCache
{
    public const string Host = "art.cinema.exo";

    /// <summary>
    /// Exo's one art store (C5, all four apps; Cinema keeps the format): %LOCALAPPDATA%\Exo\art, so a cover fetched by
    /// one app is there for the others. The format:
    /// <list type="bullet">
    /// <item>A picture as fetched: named by <see cref="Key"/> (the first 32 hex digits, upper case, of SHA-256 over the
    /// source's absolute URL in UTF-8), no extension; only image/* bodies, readers sniff the type from the bytes.</item>
    /// <item>Made to a width: "&lt;key&gt;-w&lt;width&gt;", the width one of <see cref="Steps"/>; JPEG at 0.9, or PNG where the
    /// source may hold transparency (PNG, WebP, SVG); made only when the source is over 1.15× that width.</item>
    /// <item>Every write to "&lt;name&gt;.tmp", then moved over the name, so a reader never sees half a file.</item>
    /// </list>
    /// A test copy on its own data keeps its own store under it, so it never adds to the real one.
    /// </summary>
    internal static readonly string Dir = AppInfo.OwnData ? Path.Combine(AppInfo.Data, "cache", "images")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exo", "art");

    // Cinema's own cache from before the shared store: its pictures move in once (same names), then it goes.
    private static void MoveOldCache()
    {
        if (AppInfo.OwnData) return;
        var old = Path.Combine(AppInfo.Data, "cache", "images");
        if (!Directory.Exists(old)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(old))
            {
                var to = Path.Combine(Dir, Path.GetFileName(f));
                try { if (!File.Exists(to)) File.Move(f, to); else File.Delete(f); } catch (IOException) { }
            }
            Directory.Delete(old, recursive: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Pictures are the signed-in server's and no one else's: a redirect anywhere else is refused at the checkpoint.
    private static readonly HttpClient ServerHttp = new(Net.Egress.Checkpoint(Net.Egress.Purpose.Server, new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer = 32,
        EnableMultipleHttp2Connections = true,
        AutomaticDecompression = DecompressionMethods.All,
        // A redirect is followed here, one hop at a time, each through Egress for where it leads.
        AllowAutoRedirect = false,
    }))
    {
        Timeout = TimeSpan.FromSeconds(45),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inFlight = new();
    private readonly CoreWebView2Environment _env;

    public ArtCache(CoreWebView2 core, CoreWebView2Environment env)
    {
        _env = env;
        Directory.CreateDirectory(Dir);
        _ = Task.Run(MoveOldCache);
        core.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (s, e) => UiGuard.Run(() => OnRequest(s, e), "page");
    }

    private async void OnRequest(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var asked) || asked.Host != Host) return;
        // Only Cinema's page: another page that says who it is (a frame's Origin or Referer) gets the same 404 as a miss.
        string? Header(string name) => e.Request.Headers.Contains(name) ? e.Request.Headers.GetHeader(name) : null;
        if (!Net.LocalFileHost.IsExoRequest(Header("Origin"), Header("Referer")))
        {
            e.Response = _env.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
            return;
        }
        var deferral = e.GetDeferral();
        try
        {
            var bytes = Source(asked) is { } url ? Width(asked) is { } width ? await Task.Run(() => GetFittedAsync(url, width)) : await GetAsync(url) : null;
            e.Response = bytes is null
                ? _env.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store")
                : _env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false).AsRandomAccessStream(), 200, "OK",
                    $"Content-Type: {Sniff(bytes)}\r\nCache-Control: public, max-age=31536000, immutable\r\nAccess-Control-Allow-Origin: https://cinema.exo\r\nX-Content-Type-Options: nosniff");
        }
        catch
        {
            e.Response = _env.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
        }
        finally
        {
            deferral.Complete();
        }
    }

    // The width asked for, from ?w=: one of the page's steps (app.js › ART_STEPS), else none.
    internal static readonly int[] Steps = [96, 160, 200, 240, 320, 480, 640, 960, 1280, 1920];

    private static int? Width(Uri asked)
    {
        foreach (var pair in asked.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv[0] == "w" && kv.Length == 2 && int.TryParse(kv[1], out var w) && Steps.Contains(w)) return w;
        }
        return null;
    }

    /// <summary>The picture at most <paramref name="width"/> wide: the original when it's no wider (or can't be read),
    /// else made to that width with Fant (a box filter: sharp and alias-free going down), as JPEG, or PNG where it
    /// may hold transparency (logos).</summary>
    internal async Task<byte[]?> GetFittedAsync(string url, int width)
    {
        var path = Path.Combine(Dir, $"{Key(url)}-w{width}");
        if (File.Exists(path))
        {
            try { return await File.ReadAllBytesAsync(path); } catch (IOException) { }
        }
        if (await GetAsync(url) is not { } original) return null;
        try
        {
            using var input = new MemoryStream(original, writable: false);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
            if (decoder.PixelWidth <= width * 1.15 || decoder.PixelWidth == 0) return original;
            var height = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * (double)width / decoder.PixelWidth));
            var png = Sniff(original) is "image/png" or "image/webp" or "image/svg+xml";
            using var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                png ? Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId : Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, output,
                png ? [] : new Dictionary<string, Windows.Graphics.Imaging.BitmapTypedValue>
                    { ["ImageQuality"] = new(0.9f, Windows.Foundation.PropertyType.Single) });
            using var bitmap = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                png ? Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied : Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = height, InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant },
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            var bytes = new byte[output.Size];
            using (var read = output.GetInputStreamAt(0).AsStreamForRead()) await read.ReadExactlyAsync(bytes);
            try
            {
                await File.WriteAllBytesAsync(path + ".tmp", bytes);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (IOException) { }
            return bytes;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return original; }
    }

    // The picture's own address, from ?u=: an http(s) one only.
    private static string? Source(Uri asked)
    {
        foreach (var pair in asked.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv[0] != "u" || kv.Length != 2) continue;
            var url = Uri.UnescapeDataString(kv[1]);
            return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.AbsoluteUri : null;
        }
        return null;
    }

    internal Task<byte[]?> GetAsync(string url)
    {
        var path = Path.Combine(Dir, Key(url));
        if (File.Exists(path))
        {
            try { return Task.FromResult<byte[]?>(File.ReadAllBytes(path)); } catch (IOException) { }
        }
        // Shared while it downloads, then let go: kept here for good, every picture ever fetched stayed in memory.
        return _inFlight.GetOrAdd(url, key => DownloadAsync(key, path).ContinueWith(t =>
        {
            _inFlight.TryRemove(url, out _);
            return t.Result;
        }, TaskScheduler.Default));
    }

    private async Task<byte[]?> DownloadAsync(string url, string path)
    {
        try
        {
            var at = new Uri(url);
            HttpResponseMessage res;
            for (var hop = 0; ; hop++)
            {
                res = await ServerHttp.GetAsync(at);
                if ((int)res.StatusCode is not (>= 300 and < 400) || res.Headers.Location is not { } next || hop == 3) break;
                res.Dispose();
                at = next.IsAbsoluteUri ? next : new Uri(at, next);
            }
            using var _ = res;
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentType?.MediaType?.StartsWith("image/") == false) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync();
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return bytes;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inFlight.TryRemove(url, out _);
        }
    }

    /// <summary>Bytes the cache holds on disk.</summary>
    public static long SizeOnDisk()
    {
        try { return new DirectoryInfo(Dir).EnumerateFiles().Sum(f => f.Length); } catch (IOException) { return 0; }
    }

    /// <summary>Removes every cached picture; returns what is left (files in use stay).</summary>
    public static long Clear()
    {
        try { foreach (var f in new DirectoryInfo(Dir).EnumerateFiles()) try { f.Delete(); } catch (IOException) { } } catch (IOException) { }
        return SizeOnDisk();
    }

    internal static string Key(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];

    private static string Sniff(byte[] b) =>
        b.Length > 12 && b[0] == 0x89 && b[1] == 0x50 ? "image/png"
        : b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[8] == 'W' && b[9] == 'E' ? "image/webp"
        : b.Length > 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p' ? "image/avif"
        : b.Length > 4 && b[0] == '<' ? "image/svg+xml"
        : "image/jpeg";
}
