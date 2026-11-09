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
    private async Task<SubsonicNode?> StarAsync(bool star)
    {
        var ids = All("id").Concat(All("albumId")).Concat(All("artistId")).Distinct().Take(500).ToArray();
        if (ids.Length == 0) throw Missing("id");
        var tracks = new List<MusicPlay>();
        var albums = new List<MusicStarredAlbum>();
        var artists = new List<MusicStarredArtist>();
        var now = DateTime.UtcNow;
        foreach (var id in ids)
        {
            if (!star)
            {
                if (ParseTrack(id) is var (_, trackId)) tracks.Add(new MusicPlay(new MusicTrack(trackId, "", [], "", null, null, null, false, null, null), "", now));
                else if (ParseId(id, "al") is { } releaseId) albums.Add(new MusicStarredAlbum(new MusicRelease(releaseId, "", [], "ALBUM", null, null, null, null, []), now));
                else if (ParseId(id, "ar") is { } artistId) artists.Add(new MusicStarredArtist(new MusicArtistRef(artistId, ""), now));
                else throw NotFound("Item");
                continue;
            }
            // An id that names nothing is skipped. One the catalog cannot describe right now fails the request
            // instead, before anything is saved, so the app keeps it and sends it again.
            try
            {
                if (ParseTrack(id) is not null)
                {
                    var (track, release) = await TrackAsync(id);
                    tracks.Add(new MusicPlay(track, release.Title, now));
                }
                else if (ParseId(id, "al") is { } releaseId) albums.Add(new MusicStarredAlbum(await StarredReleaseAsync(releaseId) with { Tracks = [] }, now));
                else if (ParseId(id, "ar") is { } artistId) artists.Add(new MusicStarredArtist(await StarredArtistAsync(artistId), now));
                else throw NotFound("Item");
            }
            catch (SubsonicError error) when (error.Code == 70 && ids.Length > 1) { }
        }
        if (star && tracks.Count + albums.Count + artists.Count == 0) throw NotFound("Item");
        await store.UpdateAsync(user.Id, library =>
        {
            library.StarredTracks.RemoveAll(s => tracks.Any(t => t.Track.Id == s.Track.Id));
            library.StarredAlbums.RemoveAll(s => albums.Any(a => a.Release.Id == s.Release.Id));
            library.StarredArtists.RemoveAll(s => artists.Any(a => a.Artist.Id == s.Artist.Id));
            if (!star) return;
            library.StarredTracks.InsertRange(0, tracks);
            library.StarredAlbums.InsertRange(0, albums);
            library.StarredArtists.InsertRange(0, artists);
        }, Ct);
        if (!star) await saver.CancelAsync(user.Id, albums.Select(a => a.Release.Id).ToArray(), Ct);
        else foreach (var album in albums) await saver.QueueAsync(user.Id, album.Release, Ct);
        await saver.MirrorAsync(user.Id, albums.Select(a => a.Release.Id).ToArray(), star, Ct);
        return Success();
    }

    private async Task<MusicRelease> StarredReleaseAsync(string releaseId)
    {
        if (!catalog.HasRelease(releaseId) && catalog.DownFor(releaseId) && Albums(await store.ReadAsync(user.Id, Ct)).FirstOrDefault(a => a.Release.Id == releaseId) is { } mine
            && mine.Release.Title.Length > 0) return mine.Release;
        Fetch(catalog.HasRelease(releaseId));
        return await catalog.ReleaseAsync(releaseId, Ct);
    }

    private async Task<MusicArtistRef> StarredArtistAsync(string artistId)
    {
        if (!catalog.HasArtist(artistId) && catalog.DownFor(artistId) && Artists(await store.ReadAsync(user.Id, Ct)).FirstOrDefault(a => a.Id == artistId) is { Name.Length: > 0 } mine) return mine;
        Fetch(catalog.HasArtist(artistId));
        var artist = await catalog.ArtistAsync(artistId, Ct);
        return new MusicArtistRef(artist.Id, artist.Name);
    }

}
