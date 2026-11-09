using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace StreamFin.Manga;

// Lists chapters for series requested from search. Queued when a request is made; it does nothing when nothing is pending.
public sealed class MangaRequestTask(Store store, MangaService manga, ILogger<MangaRequestTask> logger) : IScheduledTask
{
    public string Name => "Process manga requests";
    public string Key => "StreamFinMangaRequests";
    public string Description => "Lists the English chapters of manga requested from search and queues them for download.";
    public string Category => "StreamFin";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var pending = store.All<PendingRequest>("request-pending").Where(request => request.Error is null).OrderBy(request => request.RequestedAt).ToList();
        progress.Report(0);
        for (var i = 0; i < pending.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = pending[i];
            var id = Identity.For(request.SourceId, request.Url);
            try
            {
                await manga.Request(request.SourceId, request.Url, request.User, cancellationToken);
                store.Delete("request-pending", id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The error stays on the request so the page can show it instead of retrying forever.
                store.Put("request-pending", id, request with { Error = ex.Message });
                logger.LogWarning(ex, "JellySteamHub could not process a manga request");
            }
            progress.Report(100.0 * (i + 1) / pending.Count);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}

// Checks monitored series for new English chapters. Runs on a schedule; change the interval in Scheduled Tasks.
public sealed class MangaMonitorTask(MangaService manga, ILogger<MangaMonitorTask> logger) : IScheduledTask
{
    public string Name => "Check manga releases";
    public string Key => "StreamFinMangaMonitor";
    public string Description => "Looks for new English chapters of monitored manga and queues them for download.";
    public string Category => "StreamFin";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        if (MangaHost.Options.Paused) { logger.LogInformation("JellySteamHub manga monitoring is paused"); return; }
        await manga.Monitor(cancellationToken);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(30).Ticks }
    ];
}
