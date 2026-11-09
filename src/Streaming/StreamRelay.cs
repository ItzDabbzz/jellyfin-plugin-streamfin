using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

public sealed record Playback(string Id, string Url, string Secret, IReadOnlyList<string> Subtitles);

public sealed class StreamRelay(IServerApplicationHost host, SafeHttp http, IMediaEncoder encoder, MusicCache songs)
{
    // Jellyfin forwards User-Agent to both FFprobe and FFmpeg; arbitrary headers are not forwarded by FFprobe.
    // FFmpeg's HLS reader repeats it on every playlist and segment request.
    public const string Header = "User-Agent";
    private readonly string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly ConcurrentDictionary<string, Ticket> tickets = new();
    // A song's file from one source never changes, and the catalog throttles bursts, so each one is probed once.
    // Kept as JSON so every playback gets its own copy of the streams to adjust.
    private readonly MemoryCache audioProbes = new(new MemoryCacheOptions { SizeLimit = 5000 });
    private sealed record AudioProbe(string Streams, long? RunTimeTicks, int? Bitrate, long? Size, string? Container);

    // Live is rechecked on every request and between chunks, so revoking a user, a source,
    // or a content filter stops playback that is already running.
    private sealed class Ticket(Guid userId, bool hls, Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> resolve, Func<bool> live)
    {
        public Guid UserId { get; } = userId;
        public bool Hls { get; } = hls;
        public Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> Resolve { get; } = resolve;
        public Func<bool> Live { get; } = live;
        public DateTime Created { get; } = DateTime.UtcNow;
        public DateTime Expires { get; } = DateTime.UtcNow.AddHours(12);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ResolvedVideo? Video { get; set; }
        public HlsLinks Links { get; } = new();
        public required ResolvedVideo First { get; init; }
        // Jellyfin's own HTTP client fetches external subtitles and cannot send the secret header.
        public string SubtitleKey { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    }

    // A session starts from the resolution Jellyfin just probed; resolving again costs several
    // page loads for film sources and can land on a different server or file.
    public Playback Create(Guid userId, ResolvedVideo resolved, Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> resolve, Func<bool> live)
    {
        var hls = resolved.Hls;
        foreach (var pair in tickets.Where(p => p.Value.Expires < DateTime.UtcNow)) tickets.TryRemove(pair.Key, out _);
        // Failed and abandoned attempts pile up, so make room by dropping the oldest instead of refusing.
        foreach (var (limit, scope) in new[] { (32, tickets.Where(p => p.Value.UserId == userId)), (256, tickets) })
            foreach (var pair in scope.OrderBy(p => p.Value.Created).SkipLast(limit - 1).ToList()) tickets.TryRemove(pair.Key, out _);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var ticket = tickets[id] = new(userId, hls, resolve, live) { Video = resolved, First = resolved };
        var root = host.GetLocalApiUrl("127.0.0.1", "http", host.HttpPort).TrimEnd('/') + "/StreamFin/Internal/Stream/" + id;
        // The extension lets FFmpeg pick its HLS reader before reading a byte.
        return new(id, root + (hls ? "/master.m3u8" : ""), secret,
            (resolved.Subtitles ?? []).Select((_, i) => $"{root}/sub/{i}.vtt?key={ticket.SubtitleKey}").ToArray());
    }

    // Jellyfin only extracts embedded subtitles from local files, so remote ones must be external.
    public async Task<MediaSourceInfo> MediaSourceAsync(Guid userId, ResolvedVideo resolved, string name,
        Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> resolve, Func<bool> live, CancellationToken ct)
    {
        var playback = Create(userId, resolved, resolve, live);
        try { return await ProbeAsync(playback, resolved, name, ct); }
        catch { tickets.TryRemove(playback.Id, out _); throw; }
    }

    private async Task<MediaSourceInfo> ProbeAsync(Playback playback, ResolvedVideo resolved, string name, CancellationToken ct)
    {
        // Audio is direct played so the FLAC reaches the client untouched: with a required header, clients
        // ask Jellyfin's static stream, which fetches the loopback URL itself and forwards User-Agent and Range.
        // Jellyfin 12 disables direct stream for every HTTP source, and a transcode follows the client's
        // profile, which is often lossy.
        var media = new MediaSourceInfo
        {
            Path = playback.Url, Protocol = MediaProtocol.Http, Container = resolved.Ticket?.Container ?? (resolved.Hls ? "hls" : "mp4"), IsRemote = true,
            Name = name, SupportsDirectPlay = resolved.Audio, SupportsDirectStream = resolved.Audio, SupportsTranscoding = true, SupportsProbing = true,
            RequiredHttpHeaders = new Dictionary<string, string> { [Header] = playback.Secret }
        };
        // YouTube's addresses change on every lookup, so probes are kept per song and format instead.
        if (resolved.Ticket is { } song) return await ProbeAudioAsync(media, song.TrackId + ":" + song.Container, song.Container, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var info = await encoder.GetMediaInfo(new MediaInfoRequest { MediaSource = media, MediaType = DlnaProfileType.Video, ExtractChapters = false }, timeout.Token);
        var streams = info.MediaStreams.Where(s => s.Type != MediaStreamType.Subtitle).ToList();
        var subtitles = resolved.Subtitles ?? [];
        for (var i = 0; i < subtitles.Count; i++)
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Subtitle, Index = streams.Count, Codec = "vtt", Path = playback.Subtitles[i],
                IsExternal = true, SupportsExternalStream = true, Title = subtitles[i].Label, IsDefault = i == 0 && subtitles[i].Language == "en",
                Language = subtitles[i].Language == "und" ? null : subtitles[i].Language
            });
        media.MediaStreams = streams;
        media.RunTimeTicks = info.RunTimeTicks;
        media.Bitrate = info.Bitrate;
        media.Size = info.Size;
        media.Container = info.Container;
        return media;
    }

    private async Task<MediaSourceInfo> ProbeAudioAsync(MediaSourceInfo media, string key, string container, CancellationToken ct)
    {
        if (!audioProbes.TryGetValue<AudioProbe>(key, out var probe))
        {
            try
            {
                // Well inside the minute a player or proxy waits: a song that needs longer to describe plays undescribed.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var info = await encoder.GetMediaInfo(new MediaInfoRequest { MediaSource = media, MediaType = DlnaProfileType.Audio, ExtractChapters = false }, timeout.Token);
                var audio = info.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
                if (audio.Count == 0) throw new ProviderException("The probe found no audio.");
                probe = new AudioProbe(System.Text.Json.JsonSerializer.Serialize(audio), info.RunTimeTicks, info.Bitrate, info.Size, info.Container);
                audioProbes.Set(key, probe, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromDays(1) });
            }
            // Each source sends one known format, so a probe it throttled must not stop playback; the player reads the details itself.
            // The plain description is kept a few minutes, so replaying a song whose probe failed does not wait again.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var plain = new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = container == "flac" ? "flac" : "aac", Channels = 2, ChannelLayout = "stereo", IsDefault = true };
                probe = new AudioProbe(System.Text.Json.JsonSerializer.Serialize(new List<MediaStream> { plain }), null, null, null, container);
                audioProbes.Set(key, probe, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
            }
        }
        media.MediaStreams = System.Text.Json.JsonSerializer.Deserialize<List<MediaStream>>(probe!.Streams) ?? [];
        media.RunTimeTicks = probe.RunTimeTicks;
        media.Bitrate = probe.Bitrate;
        media.Size = probe.Size;
        media.Container = probe.Container ?? container;
        return media;
    }

    private Ticket? Authorized(HttpContext context, string id)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is not null && IPAddress.IsLoopback(address)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers[Header].ToString()), Encoding.UTF8.GetBytes(secret))
            && tickets.TryGetValue(id, out var ticket) && ticket.Expires > DateTime.UtcNow && Plugin.Allows(ticket.UserId) && ticket.Live()
            ? ticket : null;
    }

    // Resolve once per HLS session: the playlist tokens are tied to that resolution.
    private static async Task<ResolvedVideo> VideoAsync(Ticket ticket, CancellationToken ct)
    {
        if (ticket.Video is { } known) return known;
        await ticket.Gate.WaitAsync(ct);
        try { return ticket.Video ??= (await ticket.Resolve(ct))[0]; }
        finally { ticket.Gate.Release(); }
    }

    public async Task ServeFileAsync(HttpContext context, string id)
    {
        if (Authorized(context, id) is not { Hls: false } ticket) { context.Response.StatusCode = 403; return; }
        if (!ValidRange(context, out var range)) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        // Keep the probed file. Jellyfin holds the stable loopback URL, so when the CDN link expires
        // (403/404/410) the episode is resolved again, once, and only another MP4 is accepted.
        var video = ticket.Video!;
        if (video.Ticket is { } song)
        {
            songs.Warm(song);
            context.Response.Headers.CacheControl = "no-store";
            if (await songs.ServeAsync(context, song, range, () => Plugin.Allows(ticket.UserId) && ticket.Live())) return;
            // A song kept on disk with no address to fall back to was evicted, so look again.
            if (song.Local) MusicAudio.Forget(song.TrackId);
        }
        // Reviewed anime hosts keep their strict rules; film players live on rotating hosts.
        if (!(video.Ticket is { Local: true })
            && await ProxyAsync(context, ticket, video.Url, video.Headers, range, open: !SafeHttp.Allowed(video.Url, true), timeout.Token, retryable: true, audio: video.Ticket?.ContentType)) return;
        await ticket.Gate.WaitAsync(timeout.Token);
        try
        {
            if (ReferenceEquals(ticket.Video, video))
            {
                if (video.Ticket is { } expired) MusicAudio.Forget(expired.TrackId);
                var fresh = (await ticket.Resolve(timeout.Token))[0];
                // A song never changes format partway through: the player has already read part of the other one.
                if (fresh.Hls || fresh.Ticket?.Container != video.Ticket?.Container) { context.Response.StatusCode = 502; return; }
                ticket.Video = fresh;
            }
            video = ticket.Video!;
        }
        finally { ticket.Gate.Release(); }
        if (video.Ticket is { } local && songs.Kept(local.TrackId) == local.Container && await songs.ServeAsync(context, local, range, () => Plugin.Allows(ticket.UserId) && ticket.Live())) return;
        await ProxyAsync(context, ticket, video.Url, video.Headers, range, open: !SafeHttp.Allowed(video.Url, true), timeout.Token, audio: video.Ticket?.ContentType);
    }

    public async Task ServeMasterAsync(HttpContext context, string id)
    {
        if (Authorized(context, id) is not { Hls: true } ticket) { context.Response.StatusCode = 403; return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var video = await VideoAsync(ticket, timeout.Token);
        var text = await http.TextAsync(video.Url, timeout.Token, headers: video.Headers, open: true);
        var master = Hls.IsMaster(text) ? Hls.RewriteMaster(text, video.Url, ticket.Links) : Hls.WrapMedia(ticket.Links.Add(video.Url, playlist: true));
        await WriteAsync(context, master, "application/vnd.apple.mpegurl");
    }

    public async Task ServePartAsync(HttpContext context, string id, int index)
    {
        if (Authorized(context, id) is not { Hls: true } ticket || ticket.Links.Get(index) is not { } upstream) { context.Response.StatusCode = 403; return; }
        if (!ValidRange(context, out var range)) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var video = await VideoAsync(ticket, timeout.Token);
        if (ticket.Links.IsPlaylist(index))
        {
            var text = await http.TextAsync(upstream, timeout.Token, headers: video.Headers, open: true);
            await WriteAsync(context, Hls.RewriteMedia(text, upstream, ticket.Links), "application/vnd.apple.mpegurl");
            return;
        }
        await ProxyAsync(context, ticket, upstream, video.Headers, range, open: true, timeout.Token, segment: true);
    }

    public async Task ServeSubtitleAsync(HttpContext context, string id, int index, string key)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null || !IPAddress.IsLoopback(address) || !tickets.TryGetValue(id, out var ticket) || ticket.Expires <= DateTime.UtcNow
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(ticket.SubtitleKey))
            || !Plugin.Allows(ticket.UserId) || !ticket.Live() || ticket.First.Subtitles is not { } subtitles || index < 0 || index >= subtitles.Count)
        { context.Response.StatusCode = 403; return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var text = await http.TextAsync(subtitles[index].Url, timeout.Token, headers: ticket.First.Headers, open: true);
        await WriteAsync(context, Hls.WebVtt(text), "text/vtt");
    }

    private static bool ValidRange(HttpContext context, out string range)
    {
        range = context.Request.Headers.Range.ToString();
        if (range.Length == 0 || (System.Net.Http.Headers.RangeHeaderValue.TryParse(range, out var parsed) && parsed.Unit == "bytes" && parsed.Ranges.Count == 1)) return true;
        context.Response.StatusCode = 416;
        return false;
    }

    private static async Task WriteAsync(HttpContext context, string body, string type)
    {
        context.Response.ContentType = type;
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.WriteAsync(body, context.RequestAborted);
    }

    // Returns false, without writing anything, when a retryable request finds the link expired.
    private async Task<bool> ProxyAsync(HttpContext context, Ticket ticket, Uri upstream, IReadOnlyDictionary<string, string> headers, string range, bool open, CancellationToken ct, bool retryable = false, bool segment = false, string? audio = null)
    {
        using var response = await SafeHttp.PatientAsync(() => http.OpenAsync(upstream, true, ct, range.Length == 0 ? null : range,
            head: HttpMethods.IsHead(context.Request.Method), headers: headers, open: open), ct, attempts: audio is null ? 1 : 3);
        if (retryable && (int)response.StatusCode is 403 or 404 or 410) return false;
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.Headers.CacheControl = "no-store";
        foreach (var name in new[] { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges" })
        {
            if (response.Content.Headers.TryGetValues(name, out var contentValues)) context.Response.Headers[name] = contentValues.ToArray();
            else if (response.Headers.TryGetValues(name, out var values)) context.Response.Headers[name] = values.ToArray();
        }
        // Jellyfin's static stream passes this on to the client; the catalog labels FLAC as octet-stream.
        if (audio is not null && response.IsSuccessStatusCode) context.Response.ContentType = audio;
        if (HttpMethods.IsHead(context.Request.Method)) return true;
        await using var input = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        var buffer = new byte[65536];
        var first = true;
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                var read = await input.ReadAsync(buffer, idle.Token);
                if (read == 0) break;
                // A "segment" that is really a playlist would make FFmpeg fetch its addresses directly.
                if (segment && first && Encoding.UTF8.GetString(buffer, 0, Math.Min(read, 64)).TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith('#'))
                {
                    if (!context.Response.HasStarted) context.Response.StatusCode = 502;
                    break;
                }
                first = false;
                if (!Plugin.Allows(ticket.UserId) || !ticket.Live()) break;
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }
        }
        // FFprobe and seeking FFmpeg hang up mid-segment all the time; that is not an error.
        catch (Exception ex) when (context.RequestAborted.IsCancellationRequested && ex is OperationCanceledException or IOException) { }
        return true;
    }
}
