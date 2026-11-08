using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace StreamFin;

// The settings page reads the cache totals here and can clear or re-warm them without leaving the page. Warming is
// queued as a Jellyfin task, so its progress stays visible under Dashboard → Scheduled Tasks and it cannot run twice.
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("StreamFin/Cache")]
public sealed class StreamFinCacheController(CatalogCache cache, ITaskManager tasks) : ControllerBase
{
    [HttpGet]
    public ActionResult<CacheStatus> Status() => Ok(cache.Status());

    [HttpPost("Clear")]
    public ActionResult<CacheStatus> Clear()
    {
        cache.Clear();
        Plugin.InvalidateChannels();
        return Ok(cache.Status());
    }

    [HttpPost("Warm")]
    public ActionResult<CacheRefreshResponse> Warm() => Ok(new CacheRefreshResponse(Queue<WarmCatalogTask>(), cache.Status()));

    [HttpPost("Refresh")]
    public ActionResult<CacheRefreshResponse> Refresh()
    {
        cache.Clear();
        Plugin.InvalidateChannels();
        return Ok(new CacheRefreshResponse(Queue<WarmCatalogTask>(), cache.Status()));
    }

    private bool Queue<T>() where T : IScheduledTask
    {
        // A task is only usable once Jellyfin has added it at startup; a very early request must not throw.
        if (!tasks.ScheduledTasks.Any(worker => worker.ScheduledTask is T)) return false;
        tasks.QueueIfNotRunning<T>();
        return true;
    }
}

public sealed record CacheRefreshResponse(bool Queued, CacheStatus Status);
