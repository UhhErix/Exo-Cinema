using System.Globalization;
using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace ExoCinema.Net;

/// <summary>
/// The page's private files (the profile's pictures and loops), each on its own host, served here rather than by a
/// folder mapping (Exo Launcher's Helpers/LocalFileHost.cs, the same rules). A mapping answers any frame in the page
/// that asks, and with Allow it could read the bytes: a request carries no frame of its own to check and WebView2 hands
/// this handler no Sec-Fetch-Site. So every URL carries <see cref="Token"/>, a secret made each run that only the page is
/// given (in the addresses the app posts to it): a frame can't ask for a file it can't name. A request that names another
/// page as its Origin or Referer is refused too. Refusals and misses are the same 404, so nothing tells a page which files
/// exist. Ranges are answered from the file as it is read, a video at most <see cref="MaxVideoAnswer"/> bytes per answer.
/// </summary>
internal sealed partial class LocalFileHost
{
    internal const long MaxVideoAnswer = 8L << 20;
    private const string Refused = "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n";

    /// <summary>What Exo.Identity's ProfileMedia keeps (its Extension).</summary>
    internal static readonly IReadOnlyDictionary<string, string> ProfileTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp", [".avif"] = "image/avif",
        [".mp4"] = "video/mp4", [".webm"] = "video/webm",
    };

    internal sealed record Site(Func<string?> Root, IReadOnlyDictionary<string, string> Types);

    private readonly CoreWebView2 _core;
    private readonly DispatcherQueue _queue;
    private readonly Dictionary<string, Site> _sites = new(StringComparer.OrdinalIgnoreCase);

    internal LocalFileHost(CoreWebView2 core, DispatcherQueue queue)
    {
        _core = core;
        _queue = queue;
        core.WebResourceRequested += (s, e) => UiGuard.Run(() => OnRequested(s, e), "page");
    }

    /// <summary>Every request to the host comes here, from every frame and worker: one not answered here would go to the network.</summary>
    internal void Serve(string host, Func<string?> root, IReadOnlyDictionary<string, string> types)
    {
        _sites[host] = new Site(root, types);
        _core.AddWebResourceRequestedFilter($"https://{host}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
    }

    private void OnRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !_sites.TryGetValue(uri.IdnHost, out var site)) return;
        var environment = sender.Environment;
        var headers = e.Request.Headers;
        string? Header(string name) => headers.Contains(name) ? headers.GetHeader(name) : null;
        var origin = Header("Origin");
        var method = e.Request.Method;
        if (method is not ("GET" or "HEAD") || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) || !IsExoRequest(origin, Header("Referer")))
        {
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", Refused);
            return;
        }
        var range = Header("Range");
        var deferral = e.GetDeferral();
        _ = Task.Run(() => Answer(site, uri.AbsolutePath, origin, range, method == "HEAD")).ContinueWith(task =>
        {
            var answer = task.IsCompletedSuccessfully ? task.Result : (404, "Not Found", Refused, null);
            if (_queue.Post(() =>
            {
                try { e.Response = environment.CreateWebResourceResponse(answer.Body?.AsRandomAccessStream(), answer.Status, answer.Reason, answer.Headers); }
                catch { answer.Body?.Dispose(); }
                finally { deferral.Complete(); }
            }, DispatcherQueuePriority.High)) return;
            answer.Body?.Dispose();
            deferral.Complete();
        }, TaskScheduler.Default);
    }

    private static (int Status, string Reason, string Headers, Stream? Body) Answer(Site site, string absolutePath, string? origin, string? range, bool head)
    {
        if (site.Root() is not { } root || ResolveFile(root, absolutePath, Token, site.Types) is not { } found)
            return (404, "Not Found", Refused, null);
        FileStream file;
        try { file = new FileStream(found.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (404, "Not Found", Refused, null); }
        var total = file.Length;
        var kind = ParseRange(range, total, out var first, out var last);
        var video = found.Type.StartsWith("video/", StringComparison.Ordinal);
        // The shell's own CORS reads (the snip editor's canvas, a profile video's still) name it as their Origin.
        var cors = origin is null ? "" : $"Access-Control-Allow-Origin: {origin}\r\nVary: Origin\r\n";
        if (kind == RangeKind.Unsatisfiable)
        {
            file.Dispose();
            return (416, "Range Not Satisfiable", string.Create(CultureInfo.InvariantCulture, $"Content-Range: bytes */{total}\r\n{cors}{Refused}"), null);
        }
        // A video's first ask carries a range (bytes=0-); one without is cut to its start the same way.
        if (kind == RangeKind.Whole && video && total > MaxVideoAnswer) kind = RangeKind.Part;
        if (video) last = Math.Min(last, first + MaxVideoAnswer - 1);
        var length = total == 0 ? 0 : last - first + 1;
        var text = string.Create(CultureInfo.InvariantCulture,
            $"Content-Type: {found.Type}\r\nContent-Length: {length}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nCross-Origin-Resource-Policy: same-site\r\n{cors}");
        if (kind == RangeKind.Part) text += string.Create(CultureInfo.InvariantCulture, $"Content-Range: bytes {first}-{last}/{total}\r\n");
        if (head)
        {
            file.Dispose();
            return (kind == RangeKind.Part ? 206 : 200, kind == RangeKind.Part ? "Partial Content" : "OK", text, null);
        }
        return (kind == RangeKind.Part ? 206 : 200, kind == RangeKind.Part ? "Partial Content" : "OK", text, new Slice(file, found.Path, first, length));
    }

    /// <summary>
    /// <paramref name="length"/> bytes of a file from <paramref name="start"/>, read as WebView2 asks. The file closes
    /// once the slice is read, so a capture can be trimmed, moved or deleted without waiting for the stream's finalizer;
    /// a later seek back opens it again.
    /// </summary>
    private sealed class Slice(FileStream file, string path, long start, long length) : Stream
    {
        private FileStream? _file = file;
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, length); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var want = (int)Math.Min(buffer.Length, length - _position);
            if (want <= 0) { Release(); return 0; }
            _file ??= new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            _file.Position = start + _position;
            var read = _file.Read(buffer[..want]);
            _position += read;
            if (read == 0 || _position >= length) Release();
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch { SeekOrigin.Current => _position + offset, SeekOrigin.End => length + offset, _ => offset };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Release()
        {
            _file?.Dispose();
            _file = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Release();
            base.Dispose(disposing);
        }
    }
}
