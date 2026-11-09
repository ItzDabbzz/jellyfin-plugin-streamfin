using StreamFin.Manga.Sources;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace StreamFin.Manga;

// Rewrites ComicInfo, series art and volume covers for every manga StreamFin knows about. Chapters are not downloaded
// again. It appears under Dashboard > Scheduled Tasks > StreamFin, where it can run on a schedule.
public sealed class MangaMetadataTask(Store store, LibraryStorage storage, CoverService covers, MangaSourceRegistry sources, ILibraryManager libraries, ILogger<MangaMetadataTask> logger) : IScheduledTask
{
    public string Name => "Refresh manga metadata";
    public string Key => "StreamFinMangaMetadata";
    public string Description => "Rewrites the ComicInfo details, series art and volume covers of every downloaded manga from Comic Vine, AniList and MangaDex. Chapters are not downloaded again.";
    public string Category => "StreamFin";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var series = store.All<Subscription>("series").ToList();
        var failed = 0;
        progress.Report(0);
        for (var i = 0; i < series.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await SeriesMetadata.RefreshSeries(store, storage, covers, sources, series[i].Series.Id, MangaHost.Options.ComicVineApiKey, cancellationToken, libraries); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex, "StreamFin could not refresh manga metadata for {Series}", series[i].Series.Title);
            }
            progress.Report(100.0 * (i + 1) / series.Count);
        }
        logger.LogInformation("StreamFin refreshed manga metadata for {Count} series; {Failed} failed", series.Count, failed);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
