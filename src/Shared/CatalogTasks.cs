using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Empties every catalog page, title lookup and search answer the plugin keeps in memory, then rotates the channel
// cache key so Jellyfin asks for its channel items again. Manual only: it is a repair action, not housekeeping.
public sealed class ClearCacheTask(CatalogCache cache, ILogger<ClearCacheTask> logger) : IScheduledTask
{
    public string Name => "Clear JellySteamHub caches";
    public string Key => "StreamFinCacheClear";
    public string Description => "Empties the catalog pages, title details, lyrics and radio mixes JellySteamHub keeps in memory, and makes Jellyfin refresh its channel items. Playback, watch progress, favourites and settings are not touched.";
    public string Category => "StreamFin";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        cache.Clear();
        Plugin.InvalidateChannels();
        logger.LogInformation("JellySteamHub caches cleared; channels will be fetched again");
        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}

// Fetches the first page of each enabled catalog into the shared cache, one request at a time and with a short pause
// between them. It never bypasses the catalogs' own rate windows or outage breakers, so a warm-up cannot flood a
// provider; when one category fails the rest still run and the task reports how many worked.
public sealed class WarmCatalogTask(AniListCatalog anime, TmdbCatalog films, MusicCatalog music, CatalogWarmLibrary library, ILogger<WarmCatalogTask> logger) : IScheduledTask
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(400);

    public string Name => "Refresh JellySteamHub catalogs";
    public string Key => "StreamFinCatalogWarm";
    public string Description => "Fetches the first few pages of each enabled JellySteamHub catalog (popular anime, this season, A–Z, popular movies, popular TV, trending and music charts) into the in-memory cache, one request at a time. Providers keep their own rate limits, so this cannot overload them.";
    public string Category => "StreamFin";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var steps = new List<Func<CancellationToken, Task>>();
        if (Plugin.Instance?.Configuration is { Enabled: true })
        {
            // A category whose channel is off would only spend a provider's request budget.
            if (SourceRegistry.AnyEnabled)
                foreach (var category in new[] { "popular", "season", "az" })
                    foreach (var page in new[] { 1, 2, 3, 4 })
                        steps.Add(token => anime.BrowseAsync(category, page, token));
            if (FilmChannel.IsAvailable)
                foreach (var category in new[] { "movies", "tv", "trending" })
                    foreach (var page in new[] { 1, 2, 3 })
                        steps.Add(token => films.BrowseAsync(category, page, token));
            if (MusicChannel.IsAvailable)
                steps.Add(token => music.PopularAsync(token));
            // Titles are written into Jellyfin's item table here, not on the request path, so cards
            // already have ratings, genres and ids the first time a page is opened.
            steps.Add(async token =>
            {
                var titles = await library.WarmAsync(token);
                if (titles > 0) logger.LogInformation("JellySteamHub library warm-up wrote {Titles} titles", titles);
            });
        }
        var warmed = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / steps.Count);
            try
            {
                await steps[i](cancellationToken);
                warmed++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                && ex is ProviderException or HttpRequestException or JsonException or RegexMatchTimeoutException or OperationCanceledException)
            {
                logger.LogWarning("JellySteamHub catalog warm-up skipped a category ({ErrorType})", ex.GetType().Name);
            }
            if (i + 1 < steps.Count) await Task.Delay(Pause, cancellationToken);
        }
        progress.Report(100);
        logger.LogInformation("JellySteamHub catalog warm-up finished: {Warmed} of {Total} categories", warmed, steps.Count);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks }
    ];
}
