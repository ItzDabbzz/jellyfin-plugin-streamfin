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
    private sealed record Played(int Count, DateTime Last);
    private sealed record Starred(Dictionary<string, DateTime> Tracks, Dictionary<string, DateTime> Albums, Dictionary<string, DateTime> Artists,
        Dictionary<string, int> Ratings, Dictionary<string, Played> TrackPlays, Dictionary<string, Played> AlbumPlays)
    {
        public int? Rating(string id) => Ratings.TryGetValue(id, out var rating) ? rating : null;
    }
    private static Starred Stars(MusicLibrary library) => new(
        library.StarredTracks.GroupBy(s => s.Track.Id).ToDictionary(g => g.Key, g => g.First().At),
        library.StarredAlbums.GroupBy(s => s.Release.Id).ToDictionary(g => g.Key, g => g.First().At),
        library.StarredArtists.GroupBy(s => s.Artist.Id).ToDictionary(g => g.Key, g => g.First().At),
        library.Ratings,
        library.Plays.GroupBy(p => p.Track.Id).ToDictionary(g => g.Key, g => new Played(g.Count(), g.Max(p => p.At))),
        library.Plays.GroupBy(p => p.Track.ReleaseId).ToDictionary(g => g.Key, g => new Played(g.Count(), g.Max(p => p.At))));

    private static IEnumerable<MusicPlay> Songs(MusicLibrary library) => library.Plays.Concat(library.StarredTracks)
        .Concat(library.Playlists.SelectMany(p => p.Entries)).DistinctBy(p => p.Track.Id);

    private sealed record LibraryAlbum(MusicRelease Release, DateTime Added, DateTime? Played, int Plays);
    private static List<LibraryAlbum> Albums(MusicLibrary library)
    {
        var albums = new Dictionary<string, LibraryAlbum>();
        foreach (var play in library.Plays)
        {
            var t = play.Track;
            albums[t.ReleaseId] = albums.TryGetValue(t.ReleaseId, out var known)
                ? known with { Added = play.At < known.Added ? play.At : known.Added, Played = known.Played > play.At ? known.Played : play.At, Plays = known.Plays + 1 }
                : new LibraryAlbum(new MusicRelease(t.ReleaseId, play.Album, t.Artists, "ALBUM", null, null, t.Artwork, null, []), play.At, play.At, 1);
        }
        foreach (var star in library.StarredAlbums)
            albums[star.Release.Id] = albums.TryGetValue(star.Release.Id, out var known)
                ? known with { Release = star.Release, Added = star.At < known.Added ? star.At : known.Added }
                : new LibraryAlbum(star.Release, star.At, null, 0);
        foreach (var song in library.StarredTracks.Concat(library.Playlists.SelectMany(p => p.Entries)))
            albums.TryAdd(song.Track.ReleaseId, new LibraryAlbum(new MusicRelease(song.Track.ReleaseId, song.Album, song.Track.Artists, "ALBUM", null, null,
                song.Track.Artwork, null, []), song.At, null, 0));
        return [.. albums.Values];
    }

    // The library grows with taste: every album of an artist this user starred or keeps playing is listed too,
    // so apps that show "all albums" or sync the library are not limited to what was played. Only artist pages
    // the server already keeps are used; missing ones are fetched in the background while the catalog has room.
    private const int GrowthArtists = 50;
    private const int GrowthAlbums = 100;
    private const int GrowthPlays = 3;

    private static IEnumerable<string> GrowthArtistIds(MusicLibrary library) => library.StarredArtists.OrderByDescending(s => s.At).Select(s => s.Artist.Id)
        .Concat(library.Plays.Where(p => p.Track.Artists.Count > 0).GroupBy(p => p.Track.Artists[0].Id).Where(g => g.Count() >= GrowthPlays)
            .OrderByDescending(g => g.Count()).Select(g => g.Key))
        .Distinct().Take(GrowthArtists);

    // Built once per user and library version, and again every few minutes while missing artist pages arrive. Changed
    // moves only when the set of albums does, for apps that compare the index date.
    private sealed record Grown(long Version, IReadOnlyList<LibraryAlbum> Albums, DateTime Changed, DateTime Built);
    private static readonly MemoryCache Growth = new(new MemoryCacheOptions { SizeLimit = 1000 });

    private async Task<Grown> GrowthAsync(MusicLibrary library)
    {
        var version = store.Version(user.Id);
        Growth.TryGetValue<Grown>(user.Id, out var known);
        if (known is not null && known.Version == version && DateTime.UtcNow - known.Built < TimeSpan.FromMinutes(2)) return known;
        var listed = Albums(library).Select(a => a.Release.Id).ToHashSet();
        var grown = new List<LibraryAlbum>();
        var started = 0;
        foreach (var artistId in GrowthArtistIds(library))
        {
            if (!catalog.HasArtist(artistId))
            {
                if (started++ < 2 && catalog.SpareFor(artistId) && !catalog.DownFor(artistId)) _ = WarmArtistAsync(artistId);
                continue;
            }
            MusicArtistPage page;
            try { page = await catalog.ArtistAsync(artistId, Ct); }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or IOException or RegexMatchTimeoutException
                or OperationCanceledException && !Ct.IsCancellationRequested) { continue; }
            // The catalog lists a clean edit next to the explicit album; one entry per title and length is enough.
            foreach (var release in page.Releases.GroupBy(r => (r.Title.ToLowerInvariant(), r.Type, r.TrackCount)).Select(g => g.First()).Take(GrowthAlbums))
                if (listed.Add(release.Id))
                    grown.Add(new LibraryAlbum(release, release.Date ?? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, 0));
        }
        var changed = known is not null && known.Albums.Select(a => a.Release.Id).ToHashSet().SetEquals(grown.Select(a => a.Release.Id)) ? known.Changed : DateTime.UtcNow;
        var result = new Grown(version, grown, changed, DateTime.UtcNow);
        Growth.Set(user.Id, result, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(30) });
        return result;
    }

    private async Task<List<LibraryAlbum>> AlbumsAsync(MusicLibrary library)
    {
        var albums = Albums(library);
        var listed = albums.Select(a => a.Release.Id).ToHashSet();
        albums.AddRange((await GrowthAsync(library)).Albums.Where(a => listed.Add(a.Release.Id)));
        return albums;
    }

    // Albums per artist, counted once for a whole listing.
    private static Dictionary<string, int> AlbumCounts(List<LibraryAlbum> albums) => albums.SelectMany(al => al.Release.Artists.Select(a => a.Id).Distinct())
        .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

    private async Task WarmArtistAsync(string artistId)
    {
        try { await catalog.ArtistAsync(artistId, CancellationToken.None); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or OperationCanceledException or IOException
            or RegexMatchTimeoutException) { }
    }

    private static List<MusicArtistRef> Artists(MusicLibrary library) => library.StarredArtists.Select(s => s.Artist)
        .Concat(Songs(library).SelectMany(p => p.Track.Artists)).Concat(library.StarredAlbums.SelectMany(s => s.Release.Artists))
        .DistinctBy(a => a.Id).ToList();

    // ---- Nodes

    private static string Names(IEnumerable<MusicArtistRef> artists) => string.Join(", ", artists.Select(a => a.Name));
    private static string Kind(string type) => type switch { "SINGLE" => "Single", "EP" => "EP", "COMPILATION" => "Compilation", _ => "Album" };

    private static SubsonicNode Song(string element, MusicTrack t, string album, Starred stars, MusicRelease? release = null)
    {
        // Before a song plays nobody knows its source; once it has, the details say what a player will get.
        var container = MusicAudio.AllowLossy ? MusicCache.Container(t.Id) ?? MusicAudio.Known(t.Id)?.Container ?? "flac" : "flac";
        var suffix = container == "flac" ? "flac" : "m4a";
        var node = new SubsonicNode(element).Set("id", TrackId(t)).Set("parent", "al-" + t.ReleaseId).Set("isDir", false).Set("title", t.Title)
            .Set("album", album).Set("artist", Names(t.Artists)).Set("track", t.Number).Set("discNumber", t.Disc).Set("coverArt", "al-" + t.ReleaseId)
            .Set("contentType", MusicAudio.ContentType(container)).Set("suffix", suffix).Set("duration", t.DurationMs is > 0 ? (int)Math.Round(t.DurationMs.Value / 1000.0) : null)
            .Set("path", $"{MusicPaths.Part(t.Artists.FirstOrDefault()?.Name ?? "Unknown")}/{MusicPaths.Part(album)}/{t.Number ?? 0:00} {MusicPaths.Part(t.Title)}.{suffix}")
            .Set("albumId", "al-" + t.ReleaseId).Set("artistId", t.Artists.Count > 0 ? "ar-" + t.Artists[0].Id : null).Set("type", "music")
            .Set("mediaType", "song").Set("isVideo", false).Set("displayArtist", Names(t.Artists))
            .Set("isrc", t.Isrc is null ? null : new[] { t.Isrc }).Set("explicitStatus", t.Explicit ? "explicit" : "clean")
            .Set("starred", stars.Tracks.TryGetValue(t.Id, out var at) ? at : null)
            .Set("userRating", stars.Rating(TrackId(t))).Set("playCount", stars.TrackPlays.GetValueOrDefault(t.Id)?.Count)
            .Set("played", stars.TrackPlays.GetValueOrDefault(t.Id)?.Last).Set("year", release?.Date?.Year)
            .Set("displayAlbumArtist", release is { Artists.Count: > 0 } ? Names(release.Artists) : null);
        // Known once the song has passed through here: its header says how it was mastered.
        if (container == "flac" && MusicGain.FormatFor(t.Id) is { } format)
            node.Set("samplingRate", format.SampleRate).Set("bitDepth", format.BitDepth).Set("channelCount", format.Channels);
        // Amperfy hides a song whose size is unknown, and the catalog JSON has none: the real one once the song
        // has played here, otherwise an estimate from the length (lossless averages about 70% of CD rate).
        var duration = t.DurationMs is > 0 ? t.DurationMs.Value / 1000.0 : 0;
        var known = MusicCache.Size(t.Id) ?? (container == "flac"
            ? MusicCatalog.IsMonochrome(t.Id) && FlacHeads.TryGetValue<FlacHead>(MusicCatalog.StreamUri(t.Id).AbsoluteUri, out var head) ? head?.Size : null
            : MusicAudio.Known(t.Id)?.Size);
        node.Set("size", known ?? (duration > 0 ? (long)(duration * 1411 * 125 * 0.7) : null))
            .Set("bitRate", known is long bytes && duration > 0 ? (int)Math.Round(bytes * 8 / duration / 1000) : null);
        if (MusicGain.For(t.Id) is { } gain)
            node.One(new SubsonicNode("replayGain").Set("trackGain", gain.TrackGain).Set("albumGain", gain.AlbumGain)
                .Set("trackPeak", gain.TrackPeak).Set("albumPeak", gain.AlbumPeak));
        node.AddRange(t.Artists.Select(a => new SubsonicNode("artists").Set("id", "ar-" + a.Id).Set("name", a.Name)));
        return node.AddRange((release?.Artists ?? []).Select(a => new SubsonicNode("albumArtists").Set("id", "ar-" + a.Id).Set("name", a.Name)));
    }

    private static SubsonicNode Album(string element, MusicRelease r, Starred stars, bool directory = false)
    {
        var songs = r.Tracks.Count > 0 ? r.Tracks.Count : r.TrackCount ?? 0;
        var node = new SubsonicNode(element).Set("id", "al-" + r.Id).Set("name", r.Title).Set("title", r.Title).Set("album", r.Title)
            .Set("artist", Names(r.Artists)).Set("artistId", r.Artists.Count > 0 ? "ar-" + r.Artists[0].Id : null).Set("coverArt", "al-" + r.Id)
            .Set("songCount", songs).Set("duration", (int)Math.Round(r.Tracks.Sum(t => t.DurationMs ?? 0) / 1000.0)).Set("year", r.Date?.Year)
            .Set("created", r.Date ?? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Set("isDir", true)
            .Set("parent", directory && r.Artists.Count > 0 ? "ar-" + r.Artists[0].Id : null)
            .Set("starred", stars.Albums.TryGetValue(r.Id, out var at) ? at : null).Set("displayArtist", Names(r.Artists))
            .Set("releaseTypes", new[] { Kind(r.Type) }).Set("isCompilation", r.Type == "COMPILATION")
            .Set("explicitStatus", r.Tracks.Count == 0 ? null : r.Tracks.Any(t => t.Explicit) ? "explicit" : "clean")
            .Set("userRating", stars.Rating("al-" + r.Id)).Set("playCount", stars.AlbumPlays.GetValueOrDefault(r.Id)?.Count)
            .Set("played", stars.AlbumPlays.GetValueOrDefault(r.Id)?.Last).Set("sortName", SortName(r.Title));
        if (r.Date is { } date) node.One(new SubsonicNode("originalReleaseDate").Set("year", date.Year).Set("month", date.Month).Set("day", date.Day));
        if (r.Label is { Length: > 0 } label) node.Add(new SubsonicNode("recordLabels").Set("name", label));
        return node.AddRange(r.Artists.Select(a => new SubsonicNode("artists").Set("id", "ar-" + a.Id).Set("name", a.Name)));
    }

    private static SubsonicNode Artist(string element, MusicArtistRef a, int albums, Starred stars) => new SubsonicNode(element).Set("id", "ar-" + a.Id)
        .Set("name", a.Name).Set("coverArt", "ar-" + a.Id).Set("albumCount", albums).Set("starred", stars.Artists.TryGetValue(a.Id, out var at) ? at : null)
        .Set("userRating", stars.Rating("ar-" + a.Id)).Set("sortName", SortName(a.Name));

    private async Task<(MusicLibrary Library, Starred Stars)> LibraryAsync()
    {
        var library = await store.ReadAsync(user.Id, Ct);
        return (library, Stars(library));
    }

    // The catalog budget is shared by everyone, so one request (a big playlist, a mass star) may only send
    // so many lookups to the catalog. What is already cached costs nothing.
    private const int FetchLimit = 40;
    private int fetches;
    private bool Fetch(bool cached) => cached || ++fetches <= FetchLimit
        ? true : throw new SubsonicError(0, "Too many albums at once for the music catalog. Try again in a minute: the ones already fetched stay cached.");

    // A song the catalog has answered for before comes from that copy. While the catalog cannot answer, one this
    // user played, starred or put in a playlist is described from their own library, so scrobbles, stars, lyrics
    // and playback carry on. Only a song neither knows has to wait for the catalog.
    private async Task<(MusicTrack Track, MusicRelease Release)> TrackAsync(string? id)
    {
        if (ParseTrack(id ?? throw Missing("id")) is not var (releaseId, trackId)) throw NotFound("Song");
        if (!catalog.HasRelease(releaseId) && catalog.DownFor(releaseId) && await KnownAsync(releaseId, trackId) is { } known) return known;
        Fetch(catalog.HasRelease(releaseId));
        MusicRelease release;
        try { release = await catalog.ReleaseAsync(releaseId, Ct); }
        catch (Exception ex) when (ex is ProviderException or JsonException)
        {
            if (await KnownAsync(releaseId, trackId) is { } fallback) return fallback;
            throw;
        }
        var track = release.Tracks.FirstOrDefault(t => t.Id == trackId) ?? throw NotFound("Song");
        return (track with { Artwork = track.Artwork ?? release.Artwork }, release);
    }

    private Dictionary<(string, string), MusicPlay>? knownSongs;
    private async Task<(MusicTrack Track, MusicRelease Release)?> KnownAsync(string releaseId, string trackId)
    {
        knownSongs ??= Songs(await store.ReadAsync(user.Id, Ct)).Where(p => p.Track.Title.Length > 0)
            .GroupBy(p => (p.Track.ReleaseId, p.Track.Id)).ToDictionary(g => g.Key, g => g.First());
        if (!knownSongs.TryGetValue((releaseId, trackId), out var play)) return null;
        return (play.Track, new MusicRelease(releaseId, play.Album, play.Track.Artists, "ALBUM", null, null, play.Track.Artwork, null, []));
    }

    private async Task<IReadOnlyList<MusicRelease>> Releases(MusicArtistPage artist) => MusicCatalog.PreferExplicit
        ? await catalog.WithoutCleanCopiesAsync(artist.Releases, Ct) : artist.Releases;

    // ---- Browsing

    private static readonly string[] Articles = ["The", "El", "La", "Los", "Las", "Le", "Les"];
    private static string SortName(string name)
    {
        foreach (var article in Articles)
            if (name.StartsWith(article + " ", StringComparison.OrdinalIgnoreCase) && name.Length > article.Length + 1) return name[(article.Length + 1)..];
        return name;
    }

    private async Task<SubsonicNode?> ArtistsAsync(string element)
    {
        var (library, stars) = await LibraryAsync();
        var grown = await GrowthAsync(library);
        var changed = new DateTimeOffset(library.Changed > grown.Changed ? library.Changed : grown.Changed).ToUnixTimeMilliseconds();
        var root = new SubsonicNode(element).Set("ignoredArticles", string.Join(' ', Articles)).Set("lastModified", element == "indexes" ? changed : null);
        // An app that already has this version of the index gets an empty answer, as the API allows.
        if (element == "indexes" && long.TryParse(One("ifModifiedSince"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var since) && since >= changed)
            return Success().One(root);
        var counts = AlbumCounts(await AlbumsAsync(library));
        foreach (var group in Artists(library).GroupBy(a => SortName(a.Name) is { Length: > 0 } s && char.IsLetter(s[0]) ? char.ToUpperInvariant(s[0]).ToString() : "#")
                     .OrderBy(g => g.Key == "#" ? "~" : g.Key, StringComparer.Ordinal))
            root.Add(new SubsonicNode("index").Set("name", group.Key).AddRange(group.OrderBy(a => SortName(a.Name), StringComparer.OrdinalIgnoreCase)
                .Select(a => Artist("artist", a, counts.GetValueOrDefault(a.Id), stars))));
        return Success().One(root);
    }

    private async Task<SubsonicNode?> ArtistAsync()
    {
        var id = ParseId(One("id") ?? throw Missing("id"), "ar") ?? throw NotFound("Artist");
        var artist = await catalog.ArtistAsync(id, Ct);
        var (_, stars) = await LibraryAsync();
        var releases = await Releases(artist);
        return Success().One(Artist("artist", new MusicArtistRef(artist.Id, artist.Name), releases.Count, stars)
            .AddRange(releases.OrderByDescending(r => r.Date).Select(r => Album("album", r, stars))));
    }

    private async Task<SubsonicNode?> AlbumAsync()
    {
        var id = ParseId(One("id") ?? throw Missing("id"), "al") ?? throw NotFound("Album");
        var release = await catalog.ReleaseAsync(id, Ct);
        var (_, stars) = await LibraryAsync();
        return Success().One(Album("album", release, stars).AddRange(release.Tracks.Select(t => Song("song", t with { Artwork = t.Artwork ?? release.Artwork }, release.Title, stars, release))));
    }

    private async Task<SubsonicNode?> SongAsync()
    {
        var (track, release) = await TrackAsync(One("id"));
        var (_, stars) = await LibraryAsync();
        return Success().One(Song("song", track, release.Title, stars, release.Tracks.Count > 0 ? release : null));
    }

    // Folder browsing for older clients: an artist holds albums, an album holds songs.
    private async Task<SubsonicNode?> DirectoryAsync()
    {
        var id = One("id") ?? throw Missing("id");
        var (_, stars) = await LibraryAsync();
        if (ParseId(id, "ar") is { } artistId)
        {
            var artist = await catalog.ArtistAsync(artistId, Ct);
            return Success().One(new SubsonicNode("directory").Set("id", id).Set("name", artist.Name)
                .AddRange((await Releases(artist)).OrderByDescending(r => r.Date).Select(r => Album("child", r, stars, directory: true))));
        }
        if (ParseId(id, "al") is { } releaseId)
        {
            var release = await catalog.ReleaseAsync(releaseId, Ct);
            return Success().One(new SubsonicNode("directory").Set("id", id).Set("name", release.Title)
                .Set("parent", release.Artists.Count > 0 ? "ar-" + release.Artists[0].Id : null)
                .AddRange(release.Tracks.Select(t => Song("child", t, release.Title, stars, release))));
        }
        if (id == "1")
        {
            var (library, _) = await LibraryAsync();
            return Success().One(new SubsonicNode("directory").Set("id", "1").Set("name", "JellySteamHub")
                .AddRange(Artists(library).OrderBy(a => SortName(a.Name), StringComparer.OrdinalIgnoreCase)
                    .Select(a => new SubsonicNode("child").Set("id", "ar-" + a.Id).Set("title", a.Name).Set("isDir", true).Set("coverArt", "ar-" + a.Id))));
        }
        throw NotFound("Directory");
    }

    private async Task<SubsonicNode?> ArtistInfoAsync(string element)
    {
        var id = ParseId(One("id") ?? throw Missing("id"), "ar") ?? throw NotFound("Artist");
        var count = Int("count", 20, 0, 50);
        var artist = await catalog.ArtistAsync(id, Ct);
        var (_, stars) = await LibraryAsync();
        // Apps load this with the artist page: what is known in a few seconds now, the rest from the cache next time.
        var similar = count == 0 ? [] : await radio.SimilarArtistsAsync(artist, Ct, DateTime.UtcNow.AddSeconds(4));
        return Success().One(new SubsonicNode(element).Set("biography", artist.Bio)
            .AddRange(similar.Take(count).Select(a => Artist("similarArtist", a, 0, stars))));
    }

    private async Task<SubsonicNode?> TopSongsAsync()
    {
        var name = One("artist") ?? throw Missing("artist");
        var count = Int("count", 50, 1, 50);
        var (_, stars) = await LibraryAsync();
        var found = (await catalog.SearchAsync(name, Ct)).Artists.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found is null) return Success().One(new SubsonicNode("topSongs"));
        var artist = await catalog.ArtistAsync(found.Id, Ct);
        return Success().One(new SubsonicNode("topSongs").AddRange(await SongsWithAlbumsAsync((await catalog.ExplicitTracksAsync(artist.TopTracks, Ct)).Take(count).ToArray(), stars, "song")));
    }

    // Radio: the artist's top tracks mixed with similar artists'. Without similarity data, the artist's own, shuffled.
    private async Task<SubsonicNode?> SimilarAsync(string element)
    {
        var id = One("id") ?? throw Missing("id");
        var count = Int("count", 50, 1, 50);
        string? artistId = ParseId(id, "ar");
        if (artistId is null && ParseId(id, "al") is { } releaseId) artistId = (await catalog.ReleaseAsync(releaseId, Ct)).Artists.FirstOrDefault()?.Id;
        if (artistId is null && ParseTrack(id) is not null) artistId = (await TrackAsync(id)).Track.Artists.FirstOrDefault()?.Id;
        if (artistId is null) throw NotFound("Item");
        var (_, stars) = await LibraryAsync();
        var mix = await radio.RadioAsync(artistId, count, Ct);
        if (mix.Count == 0) mix = (await catalog.ExplicitTracksAsync((await catalog.ArtistAsync(artistId, Ct)).TopTracks, Ct)).OrderBy(_ => Random.Shared.Next()).Take(count).ToArray();
        return Success().One(new SubsonicNode(element).AddRange(await SongsWithAlbumsAsync(mix, stars, "song")));
    }

    // Search hits and top tracks name their release but not its title. Titles come from the release cache, a
    // handful of lookups at most; the rest go out without an album name rather than stalling the response.
    private async Task<IEnumerable<SubsonicNode>> SongsWithAlbumsAsync(IReadOnlyList<MusicTrack> tracks, Starred stars, string element,
        IReadOnlyList<MusicRelease>? known = null, MusicLibrary? library = null)
    {
        var titles = (known ?? []).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Title);
        foreach (var play in library is null ? [] : Songs(library)) titles.TryAdd(play.Track.ReleaseId, play.Album);
        var lookups = 0;
        var deadline = DateTime.UtcNow + MusicCatalog.ExtraTime;
        foreach (var releaseId in tracks.Select(t => t.ReleaseId).Distinct().Where(r => !titles.ContainsKey(r)))
        {
            var cached = catalog.HasRelease(releaseId);
            var left = deadline - DateTime.UtcNow;
            if (lookups++ >= 8 || !cached && (!catalog.SpareFor(releaseId) || catalog.DownFor(releaseId) || left <= TimeSpan.Zero)) break;
            try { titles[releaseId] = (await catalog.ReleaseAsync(releaseId, Ct).WaitAsync(cached ? Timeout.InfiniteTimeSpan : left, Ct)).Title; }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or TimeoutException
                or OperationCanceledException && !Ct.IsCancellationRequested) { break; }
        }
        return tracks.Select(t => Song(element, t, titles.GetValueOrDefault(t.ReleaseId, ""), stars));
    }

    private async Task<SubsonicNode?> SearchAsync(string element)
    {
        var query = (One("query") ?? "").Trim().Trim('"').Trim();
        var (library, stars) = await LibraryAsync();
        int Count(string name) => Int(name + "Count", 20, 0, 500);
        int Offset(string name) => Int(name + "Offset", 0, 0, 100_000);
        var root = new SubsonicNode(element);
        // An empty query is how Symfonium and others sync "the whole library": hand them this user's own.
        if (query.Length == 0 || query == "*")
        {
            var albums = await AlbumsAsync(library);
            var counts = AlbumCounts(albums);
            root.AddRange(Artists(library).Skip(Offset("artist")).Take(Count("artist")).Select(a => Artist("artist", a, counts.GetValueOrDefault(a.Id), stars)));
            root.AddRange(albums.OrderByDescending(a => a.Added).Skip(Offset("album")).Take(Count("album")).Select(a => Album("album", a.Release, stars)));
            root.AddRange(Songs(library).Skip(Offset("song")).Take(Count("song")).Select(p => Song("song", p.Track, p.Album, stars)));
            return Success().One(root);
        }
        if (query.Length < 2) return Success().One(root);
        var results = await catalog.SearchAsync(query, Ct);
        var tracks = Count("song") > 0 ? await catalog.ExplicitTracksAsync(results.Tracks, Ct) : results.Tracks;
        var releases = MusicCatalog.PreferExplicit && Count("album") > 0 ? await catalog.WithoutCleanCopiesAsync(results.Releases, Ct) : results.Releases;
        root.AddRange(results.Artists.Skip(Offset("artist")).Take(Count("artist")).Select(a => Artist("artist", new MusicArtistRef(a.Id, a.Name), a.Releases.Count, stars)));
        root.AddRange(releases.Skip(Offset("album")).Take(Count("album")).Select(r => Album("album", r, stars)));
        root.AddRange(await SongsWithAlbumsAsync(tracks.Skip(Offset("song")).Take(Count("song")).ToArray(), stars, "song", results.Releases, library));
        return Success().One(root);
    }

    private async Task<SubsonicNode?> AlbumListAsync(string element)
    {
        var type = One("type") ?? throw Missing("type");
        var size = Int("size", 10, 1, 500);
        var offset = Int("offset", 0, 0, 100_000);
        var (library, stars) = await LibraryAsync();
        var albums = await AlbumsAsync(library);
        if (type == "highest")
        {
            // An album rated from a search may be in no other list; a stored copy describes it.
            var listed = albums.Select(a => a.Release.Id).ToHashSet();
            foreach (var id in stars.Ratings.Keys.Select(k => ParseId(k, "al")).OfType<string>().Where(id => !listed.Contains(id) && catalog.HasRelease(id)).Take(200))
                try { albums.Add(new LibraryAlbum((await catalog.ReleaseAsync(id, Ct)) with { Tracks = [] }, DateTime.UtcNow, null, 0)); }
                catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or RegexMatchTimeoutException) { }
        }
        IEnumerable<LibraryAlbum> ordered = type switch
        {
            "random" => albums.OrderBy(_ => Random.Shared.Next()),
            "newest" => albums.OrderByDescending(a => a.Added),
            "recent" => albums.Where(a => a.Played is not null).OrderByDescending(a => a.Played),
            "frequent" => albums.Where(a => a.Plays > 0).OrderByDescending(a => a.Plays).ThenByDescending(a => a.Played),
            "highest" => albums.Where(a => stars.Rating("al-" + a.Release.Id) is not null).OrderByDescending(a => stars.Rating("al-" + a.Release.Id))
                .ThenByDescending(a => a.Plays),
            "starred" => albums.Where(a => stars.Albums.ContainsKey(a.Release.Id)).OrderByDescending(a => stars.Albums[a.Release.Id]),
            "alphabeticalByName" => albums.OrderBy(a => a.Release.Title, StringComparer.OrdinalIgnoreCase),
            "alphabeticalByArtist" => albums.OrderBy(a => SortName(Names(a.Release.Artists)), StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Release.Title, StringComparer.OrdinalIgnoreCase),
            "byYear" => ByYear(albums),
            "byGenre" => [],
            _ => throw new SubsonicError(0, "Unknown album list type: " + type)
        };
        return Success().One(new SubsonicNode(element).AddRange(ordered.Skip(offset).Take(size).Select(a => Album("album", a.Release, stars, directory: element == "albumList"))));
    }

    private IEnumerable<LibraryAlbum> ByYear(List<LibraryAlbum> albums)
    {
        var from = Int("fromYear", 0, 0, 9999);
        var to = Int("toYear", 9999, 0, 9999);
        var (low, high) = from <= to ? (from, to) : (to, from);
        var inRange = albums.Where(a => a.Release.Date?.Year is int y && y >= low && y <= high);
        return from <= to ? inRange.OrderBy(a => a.Release.Date) : inRange.OrderByDescending(a => a.Release.Date);
    }

    private async Task<SubsonicNode?> RandomSongsAsync()
    {
        var (library, stars) = await LibraryAsync();
        var size = Int("size", 10, 1, 500);
        return Success().One(new SubsonicNode("randomSongs").AddRange(Songs(library).OrderBy(_ => Random.Shared.Next()).Take(size).Select(p => Song("song", p.Track, p.Album, stars))));
    }

    private async Task<SubsonicNode?> StarredAsync(string element)
    {
        var (library, stars) = await LibraryAsync();
        var counts = AlbumCounts(await AlbumsAsync(library));
        return Success().One(new SubsonicNode(element)
            .AddRange(library.StarredArtists.Select(s => Artist("artist", s.Artist, counts.GetValueOrDefault(s.Artist.Id), stars)))
            .AddRange(library.StarredAlbums.Select(s => Album("album", s.Release, stars, directory: element == "starred")))
            .AddRange(library.StarredTracks.Select(s => Song("song", s.Track, s.Album, stars))));
    }

    // ---- Changes

}
