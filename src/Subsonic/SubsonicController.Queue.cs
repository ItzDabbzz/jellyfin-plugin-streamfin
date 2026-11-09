using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;


namespace StreamFin;

public sealed partial class SubsonicController
{
    // Songs for a queue, from what is already known: a stored album, this user's library, or a song the catalog
    // described in an earlier answer. Apps save the queue on every skip, so this never waits on the catalog; a song
    // nothing describes yet is kept by its id alone and described when the queue is read.
    private async Task<List<MusicPlay>> ResolveAsync(IEnumerable<string> ids)
    {
        var found = new List<MusicPlay>();
        foreach (var id in ids)
        {
            if (ParseTrack(id) is not var (releaseId, trackId)) continue;
            try
            {
                if (catalog.HasRelease(releaseId))
                {
                    var (track, release) = await TrackAsync(id);
                    found.Add(new MusicPlay(track, release.Title, DateTime.UtcNow));
                }
                else if (await KnownAsync(releaseId, trackId) is var (mine, album)) found.Add(new MusicPlay(mine, album.Title, DateTime.UtcNow));
                else if (catalog.KnownTrack(trackId) is { } seen && seen.ReleaseId == releaseId) found.Add(new MusicPlay(seen, "", DateTime.UtcNow));
                else found.Add(new MusicPlay(new MusicTrack(trackId, "", [], releaseId, null, null, null, false, null, null), "", DateTime.UtcNow));
            }
            catch (Exception ex) when (ex is ProviderException or SubsonicError or HttpRequestException or JsonException) { }
        }
        return found;
    }

    // Apps save the queue on every skip and some on a timer. The latest one is kept here and written to the user's
    // library when its songs change, or at most every 30 seconds when only the position moved.
    private sealed record LiveQueue(MusicQueue Queue, DateTime Written);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, LiveQueue> LiveQueues = new();

    private async Task<SubsonicNode?> SavePlayQueueAsync()
    {
        var ids = All("id").Take(MusicLibraryStore.QueueLimit).ToArray();
        if (ids.Length == 0)
        {
            LiveQueues.TryRemove(user.Id, out _);
            await store.UpdateAsync(user.Id, library => library.Queue = null, Ct, content: false);
            return Success();
        }
        var entries = await ResolveAsync(ids);
        var current = One("current") is { } given && entries.Any(e => TrackId(e.Track) == given) ? given : null;
        var position = long.TryParse(One("position"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? Math.Max(0, ms) : 0;
        var queue = new MusicQueue(entries, current, position, DateTime.UtcNow, Client());
        LiveQueues.TryGetValue(user.Id, out var live);
        var same = live is not null && live.Queue.Current == current && live.Queue.Entries.Select(e => TrackId(e.Track)).SequenceEqual(entries.Select(e => TrackId(e.Track)));
        if (same && DateTime.UtcNow - live!.Written < TimeSpan.FromSeconds(30))
        {
            LiveQueues[user.Id] = live with { Queue = queue };
            return Success();
        }
        LiveQueues[user.Id] = new LiveQueue(queue, DateTime.UtcNow);
        await store.UpdateAsync(user.Id, library => library.Queue = queue, Ct, content: false);
        return Success();
    }

    private async Task<SubsonicNode?> PlayQueueAsync()
    {
        var (library, stars) = await LibraryAsync();
        if ((LiveQueues.TryGetValue(user.Id, out var live) ? live.Queue : library.Queue) is not { } queue) return Success();
        var node = new SubsonicNode("playQueue").Set("current", queue.Current).Set("position", queue.Position).Set("username", user.Name)
            .Set("changed", queue.Changed).Set("changedBy", queue.ChangedBy);
        foreach (var entry in queue.Entries)
        {
            if (entry.Track.Title.Length > 0) { node.Add(Song("entry", entry.Track, entry.Album, stars)); continue; }
            // Kept by id only: described from a copy the server has by now, otherwise left out of this answer. Reading
            // the queue never waits on the catalog either.
            if (!catalog.HasRelease(entry.Track.ReleaseId) && catalog.KnownTrack(entry.Track.Id) is null) continue;
            if ((await ResolveAsync([TrackId(entry.Track)])).FirstOrDefault(p => p.Track.Title.Length > 0) is { } known)
                node.Add(Song("entry", known.Track, known.Album, stars));
        }
        return Success().One(node);
    }

    private async Task<SubsonicNode?> BookmarksAsync()
    {
        var (library, stars) = await LibraryAsync();
        return Success().One(new SubsonicNode("bookmarks").AddRange(library.Bookmarks.Select(b => new SubsonicNode("bookmark").Set("position", b.Position)
            .Set("username", user.Name).Set("comment", b.Comment).Set("created", b.Created).Set("changed", b.Changed)
            .One(Song("entry", b.Entry.Track, b.Entry.Album, stars)))));
    }

    private async Task<SubsonicNode?> CreateBookmarkAsync()
    {
        var id = One("id") ?? throw Missing("id");
        var position = long.TryParse(One("position") ?? throw Missing("position"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? Math.Max(0, ms) : 0;
        var (track, release) = await TrackAsync(id);
        var now = DateTime.UtcNow;
        await store.UpdateAsync(user.Id, library =>
        {
            var old = library.Bookmarks.FirstOrDefault(b => b.Entry.Track.Id == track.Id);
            library.Bookmarks.RemoveAll(b => b.Entry.Track.Id == track.Id);
            library.Bookmarks.Insert(0, new MusicBookmark(new MusicPlay(track, release.Title, now), position, One("comment") ?? old?.Comment ?? "", old?.Created ?? now, now));
        }, Ct);
        return Success();
    }

    private async Task<SubsonicNode?> DeleteBookmarkAsync()
    {
        if (ParseTrack(One("id") ?? throw Missing("id")) is not var (_, trackId)) throw NotFound("Bookmark");
        var removed = 0;
        await store.UpdateAsync(user.Id, library => removed = library.Bookmarks.RemoveAll(b => b.Entry.Track.Id == trackId), Ct);
        return removed == 0 ? throw NotFound("Bookmark") : Success();
    }

    // ---- Lyrics

    private async Task<SubsonicNode?> LyricsBySongAsync()
    {
        var (track, release) = await TrackAsync(One("id"));
        var list = new SubsonicNode("lyricsList");
        if (await lyrics.TrackAsync(track, release.Title, Ct) is { } found)
        {
            var synced = found.Metadata.IsSynced == true;
            list.Add(new SubsonicNode("structuredLyrics").Set("displayArtist", Names(track.Artists)).Set("displayTitle", track.Title).Set("lang", "und")
                .Set("offset", 0).Set("synced", synced)
                .AddRange(found.Lyrics.Select(l => new SubsonicNode("line") { Text = l.Text }.Set("start", synced && l.Start is long s ? s / TimeSpan.TicksPerMillisecond : null))));
        }
        return Success().One(list);
    }

    private async Task<SubsonicNode?> LyricsAsync()
    {
        var artist = One("artist") ?? "";
        var title = One("title") ?? "";
        var node = new SubsonicNode("lyrics").Set("artist", artist).Set("title", title);
        if (title.Length == 0) return Success().One(node);
        var hit = (await catalog.SearchAsync((title + " " + artist).Trim(), Ct)).Tracks
            .FirstOrDefault(t => string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase)
                && (artist.Length == 0 || t.Artists.Any(a => string.Equals(a.Name, artist, StringComparison.OrdinalIgnoreCase))));
        if (hit is not null)
        {
            var release = await catalog.ReleaseAsync(hit.ReleaseId, Ct);
            if (await lyrics.TrackAsync(hit, release.Title, Ct) is { } found) node.Text = string.Join('\n', found.Lyrics.Select(l => l.Text));
        }
        return Success().One(node);
    }

    // ---- Media

}
