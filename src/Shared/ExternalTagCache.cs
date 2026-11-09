using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Jellyfin never raises ItemUpdated for channel items, so nothing downstream of the library event
// stream learns that a channel title gained a rating, genres or a provider id. Jellyfin Enhanced builds
// its poster tags from a server-side snapshot that only refreshes on those events (or a delta pass by
// DateLastSaved), which is why a StreamFin card shows the custom "Streamable" rule and nothing else.
// Queueing its own cache task is the supported way to make it re-read the items.
public sealed class ExternalTagCache(ITaskManager tasks, ILogger<ExternalTagCache> logger)
{
    private const string EnhancedTaskKey = "JellyfinEnhancedBuildTagCache";
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(5);
    private long _queuedAt;

    public void QueueIfPresent()
    {
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref _queuedAt);
        if (previous != 0 && now - previous < MinimumGap.Ticks) return;

        var worker = tasks.ScheduledTasks.FirstOrDefault(task =>
            string.Equals(task.ScheduledTask?.Key, EnhancedTaskKey, StringComparison.Ordinal));
        if (worker?.ScheduledTask is null) return;
        if (worker.State == TaskState.Running) { Interlocked.Exchange(ref _queuedAt, now); return; }

        Interlocked.Exchange(ref _queuedAt, now);
        try
        {
            tasks.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
            logger.LogInformation("Queued Jellyfin Enhanced's tag cache refresh for updated channel titles");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not queue the Jellyfin Enhanced tag cache refresh");
        }
    }
}
