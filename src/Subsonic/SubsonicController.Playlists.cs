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
    private SubsonicNode Playlist(MusicPlaylist p, Starred stars, bool entries) => new SubsonicNode("playlist").Set("id", "pl-" + p.Id.ToString("N"))
        .Set("name", p.Name).Set("comment", p.Comment).Set("owner", user.Name).Set("public", false).Set("songCount", p.Entries.Count)
        .Set("duration", (int)Math.Round(p.Entries.Sum(e => e.Track.DurationMs ?? 0) / 1000.0)).Set("created", p.Created).Set("changed", p.Changed)
        .Set("coverArt", p.Entries.Count > 0 ? "al-" + p.Entries[0].Track.ReleaseId : null)
        .AddRange(entries ? p.Entries.Select(e => Song("entry", e.Track, e.Album, stars)) : []);

    private async Task<SubsonicNode?> PlaylistsAsync()
    {
        var (library, stars) = await LibraryAsync();
        return Success().One(new SubsonicNode("playlists").AddRange(library.Playlists.OrderByDescending(p => p.Changed).Select(p => Playlist(p, stars, false))));
    }

    private async Task<SubsonicNode?> PlaylistAsync()
    {
        var id = ParsePlaylist(One("id") ?? throw Missing("id")) ?? throw NotFound("Playlist");
        var (library, stars) = await LibraryAsync();
        return Success().One(Playlist(library.Playlists.FirstOrDefault(p => p.Id == id) ?? throw NotFound("Playlist"), stars, true));
    }

    private async Task<List<MusicPlay>> EntriesAsync(IEnumerable<string> ids)
    {
        var entries = new List<MusicPlay>();
        foreach (var id in ids.Take(MusicLibraryStore.PlaylistEntryLimit))
        {
            var (track, release) = await TrackAsync(id);
            entries.Add(new MusicPlay(track, release.Title, DateTime.UtcNow));
        }
        return entries;
    }

    private async Task<SubsonicNode?> CreatePlaylistAsync()
    {
        var existing = One("playlistId") is { } given ? ParsePlaylist(given) ?? throw NotFound("Playlist") : (Guid?)null;
        var name = One("name");
        if (existing is null && string.IsNullOrWhiteSpace(name)) throw Missing("name");
        if (All("songId").Length > MusicLibraryStore.PlaylistEntryLimit) throw TooMany();
        var entries = await EntriesAsync(All("songId"));
        var now = DateTime.UtcNow;
        var id = existing ?? Guid.NewGuid();
        bool missing = false, full = false;
        await store.UpdateAsync(user.Id, library =>
        {
            var old = library.Playlists.FirstOrDefault(p => p.Id == id);
            if (existing is not null && old is null) { missing = true; return; }
            if (Full(library, id, entries.Count)) { full = true; return; }
            library.Playlists.RemoveAll(p => p.Id == id);
            library.Playlists.Insert(0, new MusicPlaylist(id, name ?? old!.Name, old?.Comment ?? "", old?.Created ?? now, now, entries));
        }, Ct);
        if (missing) throw NotFound("Playlist");
        if (full) throw TooMany();
        var (library, stars) = await LibraryAsync();
        return Success().One(Playlist(library.Playlists.First(p => p.Id == id), stars, true));
    }

    private static bool Full(MusicLibrary library, Guid id, int count) => count > MusicLibraryStore.PlaylistEntryLimit
        || library.Playlists.All(p => p.Id != id) && library.Playlists.Count >= MusicLibraryStore.PlaylistLimit
        || library.Playlists.Where(p => p.Id != id).Sum(p => p.Entries.Count) + count > MusicLibraryStore.PlaylistTotalLimit;
    private static SubsonicError TooMany() => new(0, $"Up to {MusicLibraryStore.PlaylistLimit} playlists, {MusicLibraryStore.PlaylistEntryLimit} songs each and {MusicLibraryStore.PlaylistTotalLimit} in all.");

    private async Task<SubsonicNode?> UpdatePlaylistAsync()
    {
        var id = ParsePlaylist(One("playlistId") ?? throw Missing("playlistId")) ?? throw NotFound("Playlist");
        var added = await EntriesAsync(All("songIdToAdd"));
        var removed = All("songIndexToRemove").Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1).Where(n => n >= 0).ToHashSet();
        bool missing = false, full = false;
        await store.UpdateAsync(user.Id, library =>
        {
            var index = library.Playlists.FindIndex(p => p.Id == id);
            if (index < 0) { missing = true; return; }
            var p = library.Playlists[index];
            var kept = p.Entries.Where((_, i) => !removed.Contains(i)).Concat(added).ToArray();
            if (Full(library, id, kept.Length)) { full = true; return; }
            library.Playlists[index] = p with
            {
                Name = One("name") is { Length: > 0 } name ? name : p.Name,
                Comment = One("comment") ?? p.Comment,
                Changed = DateTime.UtcNow,
                Entries = kept
            };
        }, Ct);
        if (full) throw TooMany();
        return missing ? throw NotFound("Playlist") : Success();
    }

    private async Task<SubsonicNode?> DeletePlaylistAsync()
    {
        var id = ParsePlaylist(One("id") ?? throw Missing("id")) ?? throw NotFound("Playlist");
        var removed = 0;
        await store.UpdateAsync(user.Id, library => removed = library.Playlists.RemoveAll(p => p.Id == id), Ct);
        return removed == 0 ? throw NotFound("Playlist") : Success();
    }

    private string Client() => One("c") is { Length: > 0 } client ? (client.Length > 60 ? client[..60] : client) : "Subsonic";

    private async Task<SubsonicNode?> RatingAsync()
    {
        var id = One("id") ?? throw Missing("id");
        if (!MusicLibraryStore.IsRatingKey(id)) throw NotFound("Item");
        if (!int.TryParse(One("rating") ?? throw Missing("rating"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rating) || rating is < 0 or > 5)
            throw new SubsonicError(0, "A rating is 0 (none) to 5.");
        var full = false;
        await store.UpdateAsync(user.Id, library =>
        {
            if (rating == 0) library.Ratings.Remove(id);
            else if (library.Ratings.ContainsKey(id) || library.Ratings.Count < MusicLibraryStore.StarLimit) library.Ratings[id] = rating;
            else full = true;
        }, Ct);
        return full ? throw new SubsonicError(0, $"Up to {MusicLibraryStore.StarLimit} ratings are kept.") : Success();
    }

}
