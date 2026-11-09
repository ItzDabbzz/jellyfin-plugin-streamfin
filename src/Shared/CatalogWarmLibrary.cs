using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Channels;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Puts the titles a browse would discover into Jellyfin's item table ahead of time, so the first
// Movies/Anime page renders from items that already carry ratings, genres, studios and ids instead of
// empty rows that only fill in after the user opens them. Runs from the scheduled warm task, never
// from a request.
public sealed class CatalogWarmLibrary(AniListCatalog anime, TmdbCatalog films, ChannelItemWriter writer,
    ExternalTagCache externalCache, ILogger<CatalogWarmLibrary> logger)
{
    private static readonly IReadOnlyDictionary<string, string> FilmFolders = new Dictionary<string, string>
    {
        ["movies"] = "Popular movies", ["tv"] = "Popular TV", ["trending"] = "Trending this week"
    };

    public async Task<int> WarmAsync(CancellationToken ct)
    {
        var titles = 0;
        try
        {
            if (SourceRegistry.AnyEnabled) titles += await WarmAnimeAsync(ct);
            if (FilmChannel.IsAvailable) titles += await WarmFilmsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is ProviderException or HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning("JellySteamHub library warm-up stopped early ({ErrorType})", ex.GetType().Name);
        }

        if (titles > 0) externalCache.QueueIfPresent();
        return titles;
    }

    private async Task<int> WarmAnimeAsync(CancellationToken ct)
    {
        if (writer.FindChannel("Anime") is not { } channel) return 0;
        var folder = writer.EnsureFolder(channel, "catalog/popular/1", "Popular",
            "Popular anime on AniList. Source availability is checked when you open a title.");
        var data = await anime.BrowseAsync("popular", 1, ct);
        var infos = data.Items.Select(item => AnimeChannel.Item(item, "popular")).ToList();
        var saved = await writer.UpsertAsync(channel, folder, infos, ct, id => ArtworkResolver.ForAnime(anime, id));
        return saved.Count;
    }

    private async Task<int> WarmFilmsAsync(CancellationToken ct)
    {
        if (writer.FindChannel(FilmChannel.ChannelName) is not { } channel) return 0;
        var titles = 0;
        foreach (var (category, name) in FilmFolders)
        {
            ct.ThrowIfCancellationRequested();
            var folder = writer.EnsureFolder(channel, $"list/{category}/1", name);
            var data = await films.BrowseAsync(category, 1, ct);
            // Details are what carry IMDb ids, studios, cast and certification; fetch the page's worth
            // once here so the write below and every later browse read them from the cache.
            foreach (var film in data.Items) await films.WarmDetailsAsync(film, ct);
            var infos = data.Items.Select(film => FilmChannel.Item(films.WithCachedDetails(film), category)).ToList();
            titles += (await writer.UpsertAsync(channel, folder, infos, ct, id => ArtworkResolver.ForFilm(films, id))).Count;
        }

        return titles;
    }
}
