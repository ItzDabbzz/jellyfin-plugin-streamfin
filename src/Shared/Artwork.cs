using MediaBrowser.Model.Entities;

namespace StreamFin;

// Every image Jellyfin can show for one title. Channel listings carry a single ImageUrl, which the
// ChannelManager writes as Primary only — that is why a card poster used to be the only artwork
// anywhere, including behind the video player where a 2:3 poster gets stretched across the screen.
public sealed record Artwork(string? Poster = null, string? Backdrop = null, string? Logo = null, string? Banner = null, string? Thumb = null);

// Resolves artwork from ids alone, so a listing path that never saw the provider object (Jellyfin's own
// ChannelManager creates most channel items) can still fill in the wide art from what the cache holds.
public static class ArtworkResolver
{
    public static Artwork For(Film film) => new(film.Image, film.Backdrop, film.Logo);

    public static Artwork For(Anime anime) => new(anime.Image, anime.Banner, Banner: anime.Banner);

    public static Artwork? ForFilm(TmdbCatalog films, string? externalId) =>
        film(films, externalId) is { } value ? For(value) : null;

    public static Artwork? ForAnime(AniListCatalog anime, string? externalId) =>
        id(externalId) is { } value && anime.Cached(value) is { } title ? For(title) : null;

    // movie/<scope>/<tmdb>, tv/<scope>/<tmdb>, season/<scope>/<tmdb>/<n>, ep/<scope>/<tmdb>/<s>/<e>
    private static Film? film(TmdbCatalog films, string? externalId)
    {
        var parts = externalId?.Split('/');
        if (parts is not { Length: >= 3 } || parts[0] is not ("movie" or "tv" or "season" or "ep")) return null;
        if (!int.TryParse(parts[2], out var id) || id <= 0) return null;
        var film = films.Cached(parts[0] is "movie" ? FilmKind.Movie : FilmKind.Tv, id);
        return film is null ? null : films.WithCachedDetails(film);
    }

    // title/<scope>/<anilist>, source/<scope>/<anilist>/<provider>, episode/<scope>/<anilist>/...
    private static int? id(string? externalId)
    {
        var parts = externalId?.Split('/');
        if (parts is not { Length: >= 3 } || parts[0] is not ("title" or "source" or "episode")) return null;
        return int.TryParse(parts[2], out var id) && id > 0 ? id : null;
    }
}
