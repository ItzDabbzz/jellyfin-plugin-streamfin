using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace StreamFin.Manga;

public interface ISourceHttp
{
    Task<string> Text(Uri uri, SourceDefinition source, CancellationToken ct);
    Task<byte[]> Image(Uri uri, SourceDefinition source, string referer, CancellationToken ct);
    Task<byte[]> Data(Uri uri, SourceDefinition source, CancellationToken ct) => throw new NotSupportedException("Binary API not supported by this transport.");
    Task<byte[]> Data(Uri uri, SourceDefinition source, IReadOnlyDictionary<string, string> headers, CancellationToken ct) => throw new NotSupportedException("Session API requests are not supported by this transport.");
    Task<string> FormText(Uri uri, SourceDefinition source, CancellationToken ct) => throw new NotSupportedException("Form API not supported by this transport.");
}

public sealed class MangaHttp : ISourceHttp, IDisposable
{
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new();
    internal MangaHttp(HttpMessageHandler handler) { _client = new(handler) { Timeout = TimeSpan.FromSeconds(60) }; }
    public MangaHttp()
    {
        _client = new(new SocketsHttpHandler {
            AllowAutoRedirect = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a))) throw new HttpRequestException("Private/reserved destination is not allowed.");
                Exception? last = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, true); }
                    catch (Exception ex) { socket.Dispose(); last = ex; }
                }
                throw new HttpRequestException("Unable to connect.", last);
            }
        }) { Timeout = TimeSpan.FromSeconds(60) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("JellyManga/0.1 (+https://jellyfin.org)");
    }
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99) || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100) || b[0] == 203 && b[1] == 0 && b[2] == 113);
        // Restrict IPv6 to globally routable unicast, excluding documentation and transition ranges.
        return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] == 0x0d && b[3] == 0xb8 || b[2] == 0 || b[2] == 0x00)) && !(b[0] == 0x20 && b[1] == 0x02);
    }
    public static void ValidateUri(Uri uri, SourceDefinition source)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0) throw new ArgumentException("Sources require HTTPS on the default port, without credentials.");
        var root = new Uri(source.BaseUrl).IdnHost;
        if (!uri.IdnHost.Equals(root, StringComparison.OrdinalIgnoreCase) && !source.AllowedHosts.Any(h => string.Equals(h, uri.IdnHost, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Host not in source allowlist: " + uri.IdnHost);
        if (IPAddress.TryParse(uri.Host, out var ip) && !IsPublic(ip)) throw new ArgumentException("Private destination is not allowed.");
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !uri.Host.Contains('.')) throw new ArgumentException("A public host is required.");
    }
    public static Uri Resolve(string value, string parent, SourceDefinition source)
    {
        var uri = new Uri(new Uri(parent), value.Trim()); ValidateUri(uri, source); return uri;
    }
    public async Task<string> Text(Uri uri, SourceDefinition source, CancellationToken ct)
        => System.Text.Encoding.UTF8.GetString(await Read(uri, source, "", 8 * 1024 * 1024, false, ct));
    public Task<byte[]> Image(Uri uri, SourceDefinition source, string referer, CancellationToken ct) => Read(uri, source, referer, 32 * 1024 * 1024, true, ct);
    public Task<byte[]> Data(Uri uri, SourceDefinition source, CancellationToken ct) => Read(uri, source, "", 8 * 1024 * 1024, false, ct);
    public Task<byte[]> Data(Uri uri, SourceDefinition source, IReadOnlyDictionary<string, string> headers, CancellationToken ct) => Read(uri, source, "", 8 * 1024 * 1024, false, ct, extraHeaders: headers);
    public async Task<string> FormText(Uri uri, SourceDefinition source, CancellationToken ct) => System.Text.Encoding.UTF8.GetString(await Read(uri, source, "", 8 * 1024 * 1024, false, ct, true));
    private async Task<byte[]> Read(Uri uri, SourceDefinition source, string referer, int limit, bool image, CancellationToken ct, bool form = false, IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        ct = deadline.Token;
        var originalHost = uri.IdnHost;
        var mutex = _locks.GetOrAdd(source.Id, _ => new(1)); var held = false;
        try
        {
            for (int redirect = 0; redirect < 6; redirect++)
            {
                await mutex.WaitAsync(ct); held = true;
                ValidateUri(uri, source);
                if (_last.TryGetValue(source.Id, out var last))
                {
                    var wait = last.AddMilliseconds(Math.Clamp(source.DelayMilliseconds, 250, 60000)) - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                }
                using var request = new HttpRequestMessage(form ? HttpMethod.Post : HttpMethod.Get, uri);
                if (extraHeaders != null && string.Equals(uri.IdnHost, originalHost, StringComparison.OrdinalIgnoreCase))
                    foreach (var header in extraHeaders)
                    {
                        if (!header.Key.Equals("SESSION-TOKEN", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Unsupported source session header.");
                        request.Headers.Add(header.Key, header.Value);
                    }
                if (form) request.Content = new FormUrlEncodedContent([]);
                if (!string.IsNullOrEmpty(referer)) { var r = new Uri(referer); ValidateUri(r, source); request.Headers.Referrer = r; }
                _last[source.Id] = DateTimeOffset.UtcNow;
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location != null) { uri = new Uri(uri, response.Headers.Location); if (response.StatusCode is HttpStatusCode.SeeOther or HttpStatusCode.MovedPermanently or HttpStatusCode.Found) form = false; mutex.Release(); held = false; continue; }
                if ((int)response.StatusCode == 429)
                {
                    var until = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
                    _last[source.Id] = until;
                }
                response.EnsureSuccessStatusCode();
                // Pace request starts and honor Retry-After under the lock. Allow
                // response bodies from separate chapter jobs to transfer concurrently.
                mutex.Release(); held = false;
                if (response.Content.Headers.ContentLength > limit) throw new HttpRequestException("Source response exceeds size limit.");
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (image && mediaType != null && mediaType != "application/octet-stream" && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) throw new HttpRequestException("Expected an image; source may require authentication or a challenge.");
                await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var chunk = new byte[32768];
                int n; while ((n = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + n > limit) throw new HttpRequestException("Source response exceeds size limit."); await buffer.WriteAsync(chunk.AsMemory(0, n), ct); }
                return buffer.ToArray();
            }
            throw new HttpRequestException("Too many redirects.");
        }
        finally { if (held) mutex.Release(); }
    }
    public void Dispose() { _client.Dispose(); foreach (var mutex in _locks.Values) mutex.Dispose(); }
}
