using System.Security.Claims;
using Jellyfin.Database.Implementations.Enums;
using StreamFin.Manga.Sources;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace StreamFin.Manga;

public sealed class MangaExceptionFilter : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        // A page that stopped waiting (closed or navigated away) is not an error worth logging.
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested) { context.ExceptionHandled = true; context.Result = new StatusCodeResult(499); return; }
        var status = context.Exception switch { UnauthorizedAccessException => 403, KeyNotFoundException or FileNotFoundException => 404, ArgumentException or InvalidDataException => 400, HttpRequestException => 502, OperationCanceledException => 408, _ => 500 };
        context.Result = new ObjectResult(new { error = status == 500 ? "Operation failed. See Jellyfin logs for diagnostics." : context.Exception.Message }) { StatusCode = status };
        context.ExceptionHandled = true;
        // A series with no cover yet is normal; answer 404 without filling the log.
        if (context.Exception is not (FileNotFoundException or KeyNotFoundException))
            context.HttpContext.RequestServices.GetService<Microsoft.Extensions.Logging.ILogger<MangaExceptionFilter>>()?.LogWarning(context.Exception, "StreamFin manga request failed");
    }
}

public sealed record RequestBody(string SourceId, string Url);
public sealed record MonitorBody(bool Monitoring);
public sealed record PendingRequest(string SourceId, string Url, string User, DateTimeOffset RequestedAt, string? Error = null);
public sealed record TestBody(string Query, string? SeriesUrl = null, bool Download = false);
public sealed record ImportBody(string Path, string? Title = null);

public sealed class MangaJsonFilter : ResultFilterAttribute
{
    public override void OnResultExecuting(ResultExecutingContext context)
    {
        // Jellyfin's global API casing is PascalCase; keep this plugin's documented wire format stable.
        if (context.Result is ObjectResult result) context.Result = new JsonResult(result.Value, Store.Json) { StatusCode = result.StatusCode ?? 200 };
    }
}

[ApiController, Route("StreamFin/Manga"), Authorize, MangaExceptionFilter, MangaJsonFilter]
public sealed class MangaController(Store store, MangaSourceRegistry sources, CoverService covers, MangaService manga, LibraryStorage storage, QueueService queue, SourceTester tester, IUserManager users, ILibraryManager libraries, MediaBrowser.Model.Tasks.ITaskManager tasks) : ControllerBase
{
    private Guid UserId()
    {
        var claims = User.Claims.Where(c => c.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase)).Select(c => Guid.TryParse(c.Value, out var id) ? id : Guid.Empty).Distinct().ToArray();
        if (claims.Length != 1 || claims[0] == Guid.Empty || users.GetUserById(claims[0]) == null) throw new UnauthorizedAccessException("A Jellyfin user session is required.");
        return claims[0];
    }
    private bool Admin() => users.GetUserById(UserId())!.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value);
    private IEnumerable<Jellyfin.Database.Implementations.Entities.User> ListUsers()
    {
        // 10.11.11 changed IUserManager.Users into GetUsers without changing the assembly version.
        var type = typeof(IUserManager);
        var value = type.GetMethod("GetUsers", Type.EmptyTypes)?.Invoke(users, null) ?? type.GetProperty("Users")?.GetValue(users);
        return value is System.Collections.IEnumerable list ? list.Cast<Jellyfin.Database.Implementations.Entities.User>() : [];
    }
    private void Access()
    {
        var id = UserId(); var user = users.GetUserById(id)!; if (user.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value)) return;
        var config = MangaHost.Options;
        if (!config.Enabled) throw new UnauthorizedAccessException("StreamFin Manga is not enabled yet. An administrator can turn it on in the Manga settings.");
        if (config.DeniedUsers.Contains(id.ToString("N"), StringComparer.OrdinalIgnoreCase) || config.DeniedUsers.Contains(id.ToString(), StringComparer.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Manga access is disabled for your account.");
        if (!Guid.TryParse(config.LibraryId, out var libraryId) || libraries.GetItemById(libraryId) is not { } library || !library.IsVisible(user)) throw new UnauthorizedAccessException("You need access to the configured manga library.");
    }
    [HttpGet("assets/{name}"), AllowAnonymous]
    public ActionResult Asset(string name) => name switch {
        "app.js" => Content(MangaHost.ReadResource("Web.app.js"), "application/javascript"),
        "app.css" => Content(MangaHost.ReadResource("Web.app.css"), "text/css"),
        _ => NotFound()
    };
    [HttpGet("session")]
    public ActionResult Session() { Access(); return Ok(new { userId = UserId(), admin = Admin(), version = typeof(Plugin).Assembly.GetName().Version?.ToString() }); }
    [HttpGet("sources")]
    public ActionResult Sources() { Access(); return Ok(sources.All().Where(s => s.Enabled && !s.Removed).Select(s => new { s.Id, s.Name, s.Status, s.Priority })); }
    [HttpGet("search")]
    public async Task<ActionResult> Search([FromQuery] string query, [FromQuery] string? sourceId, CancellationToken ct)
    {
        Access(); if (query.Length is < 2 or > 200) throw new ArgumentException("Enter between 2 and 200 characters.");
        var selected = sources.All().Where(s => s.Enabled && !s.Removed && (sourceId == null || s.Id == sourceId)).ToArray();
        var results = new List<Series>(); var errors = new List<object>();
        using var concurrency = new SemaphoreSlim(3);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        await Task.WhenAll(selected.Select(async source => {
            var acquired = false;
            try
            {
                await concurrency.WaitAsync(token); acquired = true;
                var found = await sources.Adapter(source.Id).Search(query, token);
                lock (results) results.AddRange(found);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lock (errors) errors.Add(new { sourceId = source.Id, error = "Timed out while searching." });
            }
            catch (Exception ex) { lock (errors) errors.Add(new { sourceId = source.Id, error = ex.Message }); }
            finally { if (acquired) concurrency.Release(); }
        }));
        foreach (var series in results) covers.Remember(series);
        var resultIds = results.Select(series => series.Id).ToHashSet();
        var downloadedCounts = store.All<DownloadJob>("job")
            .Where(job => job.Status == "Complete" && resultIds.Contains(job.Chapter.SeriesId))
            .GroupBy(job => job.Chapter.SeriesId)
            .ToDictionary(group => group.Key, group => group.Count());
        return Ok(new { results, errors, downloadedCounts });
    }
    [HttpGet("search/{id}/details")]
    public async Task<ActionResult> SearchDetails(string id, CancellationToken ct)
    {
        Access();
        Identity.ValidId(id);
        var series = store.Get<Series>("cover-series", id) ?? throw new KeyNotFoundException("Search result not found.");
        var adapter = sources.Adapter(series.SourceId);
        var details = await adapter.Details(series.Url, ct);
        var chapters = (await adapter.Chapters(details, ct))
            .Where(chapter => chapter.Language == "en")
            .OrderByDescending(chapter => chapter.Number)
            .ThenByDescending(chapter => chapter.SourceDate)
            .ToArray();
        var firstNumberedChapter = chapters.LastOrDefault(chapter => chapter.Number > 0);
        return Ok(new
        {
            details.Description,
            chapterCount = chapters.Length,
            firstChapterNumber = firstNumberedChapter?.Number,
            lastChapterNumber = chapters.FirstOrDefault(chapter => chapter.Number > 0)?.Number,
            latestChapter = chapters.FirstOrDefault()?.Title,
            latestChapterDate = chapters.FirstOrDefault()?.SourceDate,
            missingStart = firstNumberedChapter?.Number > 1
        });
    }
    [HttpPost("requests")]
    public ActionResult CreateRequest([FromBody] RequestBody request)
    {
        Access();
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Request a series by its https link.");
        var id = Identity.For(request.SourceId, request.Url);
        store.Put("request-pending", id, new PendingRequest(request.SourceId, request.Url, UserId().ToString("N"), DateTimeOffset.UtcNow));
        // The chapter list is fetched by the scheduled task, so the page does not wait on the source.
        if (!tasks.ScheduledTasks.Any(worker => worker.ScheduledTask is MangaRequestTask)) return Accepted(new { queued = false, message = "Request saved. It will be processed when Jellyfin's scheduled tasks start." });
        tasks.QueueIfNotRunning<MangaRequestTask>();
        return Accepted(new { queued = true, message = "Request queued. The series appears in your library once its chapters are listed." });
    }
    [HttpGet("library")]
    public ActionResult Library()
    {
        Access();
        var downloads = store.All<DownloadJob>("job").ToLookup(job => job.Chapter.SeriesId);
        var progress = store.All<ReadingProgress>("progress:" + UserId().ToString("N")).ToLookup(entry => entry.SeriesId);
        return Ok(store.All<Subscription>("series").Select(subscription =>
        {
            var seriesJobs = downloads[subscription.Series.Id];
            var jobs = seriesJobs.Where(job => job.Status == "Complete");
            var latest = jobs.Select(job => new { Job = job, Date = LibraryUpdateDate(job) })
                .Where(entry => entry.Date != null)
                .OrderByDescending(entry => entry.Date)
                .ThenByDescending(entry => entry.Job.Chapter.Number)
                .FirstOrDefault();
            return new
            {
                subscription.Series,
                subscription.Monitoring,
                subscription.LastChecked,
                subscription.Error,
                downloaded = jobs.Count(),
                downloadStatus = seriesJobs.GroupBy(job => job.Status).ToDictionary(group => group.Key, group => group.Count()),
                activeDownloads = seriesJobs.Where(job => job.Status == "Downloading").Select(job => new { title = job.Chapter.Title, progress = DownloadProgress.Get(job.Chapter.Id) is { } p ? new { done = p.Done, total = p.Total } : null }).ToArray(),
                progress = progress[subscription.Series.Id].MaxBy(entry => entry.UpdatedAt),
                lastUpdatedAt = latest?.Date,
                latestUpdatedChapter = latest?.Job.Chapter,
                archiveDateOnly = latest != null && latest.Job.CompletedAt == null
            };
        }).ToArray());
    }

    private DateTimeOffset? LibraryUpdateDate(DownloadJob job)
    {
        if (job.CompletedAt != null) return job.CompletedAt;
        if (job.RelativePath == null) return null;
        try
        {
            var archive = new FileInfo(storage.Absolute(job.RelativePath));
            return archive.Exists ? new DateTimeOffset(archive.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
    [HttpGet("covers/{id}")]
    public async Task<ActionResult> Cover(string id, CancellationToken ct)
    {
        Access();
        // Art already saved in the series folder is served from disk, so the library grid never waits on a source.
        if (store.Get<Subscription>("series", id)?.Series is { } known)
        {
            var folder = storage.Absolute(MangaNames.FolderFor(store, known));
            var art = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "folder.*").FirstOrDefault() : null;
            if (art is not null)
            {
                Response.Headers.CacheControl = "private, max-age=300";
                return PhysicalFile(art, Path.GetExtension(art).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", ".avif" => "image/avif", _ => "image/jpeg" });
            }
        }
        var bytes = await covers.Get(id, ct); var extension = LibraryStorage.ImageExtension(bytes);
        Response.Headers.CacheControl = "private, max-age=300";
        return File(bytes, extension switch { ".jpg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", ".avif" => "image/avif", _ => "image/gif" });
    }
    [HttpDelete("library/{id}"), Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> RemoveSeries(string id, CancellationToken ct)
    {
        Access();
        await manga.Remove(id, queue, storage, ct);
        return NoContent();
    }
    [HttpGet("library/{id}/chapters")]
    public ActionResult Chapters(string id) { Access(); return Ok(store.All<DownloadJob>("job").Where(j => j.Chapter.SeriesId == id).OrderBy(j => j.Chapter.Number)); }
    [HttpGet("downloads")]
    public ActionResult Downloads([FromQuery] string? seriesId = null)
    {
        Access();
        var titles = store.All<Subscription>("series").ToDictionary(subscription => subscription.Series.Id, subscription => subscription.Series.Title);
        return Ok(store.All<DownloadJob>("job").Where(job => seriesId == null || job.Chapter.SeriesId == seriesId).Select(j => new
        {
            j.Chapter, j.SourceId, j.Status, j.Attempts, j.Error, j.RetryAt, j.Pages,
            seriesTitle = titles.GetValueOrDefault(j.Chapter.SeriesId),
            progress = DownloadProgress.Get(j.Chapter.Id) is { } p ? new { done = p.Done, total = p.Total } : null
        }));
    }
    [HttpGet("chapters/{id}/manifest")]
    public ActionResult Manifest(string id)
    {
        Access(); var job = Complete(id);
        var pdf = string.Equals(Path.GetExtension(job.RelativePath), ".pdf", StringComparison.OrdinalIgnoreCase);
        return Ok(new { job.Chapter, pages = pdf ? 0 : storage.Entries(job).Count, pdf });
    }
    // PDF chapters are read by the browser's viewer; images inside CBZ files use the page endpoint.
    [HttpGet("chapters/{id}/file")]
    public ActionResult Document(string id)
    {
        Access(); var job = Complete(id);
        if (!string.Equals(Path.GetExtension(job.RelativePath), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("That chapter is not a PDF.");
        var path = storage.Absolute(job.RelativePath ?? throw new FileNotFoundException());
        if (!System.IO.File.Exists(path)) throw new FileNotFoundException("Chapter file is missing.");
        Response.Headers.CacheControl = "private, max-age=300";
        return PhysicalFile(path, "application/pdf", enableRangeProcessing: true);
    }
    [HttpGet("chapters/{id}/pages/{page:int}")]
    public async Task<ActionResult> Page(string id, int page, CancellationToken ct)
    {
        Access(); var bytes = await storage.Page(Complete(id), page, ct); var extension = LibraryStorage.ImageExtension(bytes);
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(bytes, extension switch { ".jpg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", ".avif" => "image/avif", _ => "image/gif" });
    }
    private DownloadJob Complete(string id) { var job = store.Get<DownloadJob>("job", id); return job?.Status == "Complete" ? job : throw new KeyNotFoundException("Chapter is not downloaded."); }
    [HttpGet("progress")]
    public ActionResult Progress() { Access(); return Ok(store.All<ReadingProgress>("progress:" + UserId().ToString("N"))); }
    [HttpPut("progress")]
    public ActionResult Progress([FromBody] ReadingProgress progress)
    {
        Access(); var chapter = Complete(progress.ChapterId);
        if (progress.SeriesId != chapter.Chapter.SeriesId || progress.Page < 0 || progress.Page >= chapter.Pages) throw new ArgumentException("Invalid reading progress.");
        store.Put("progress:" + UserId().ToString("N"), progress.ChapterId, progress with { UpdatedAt = DateTimeOffset.UtcNow }); return NoContent();
    }
    // Registers a folder that was copied into manga storage by hand, so it appears in the library and
    // can be enriched (ComicVine, AniList) without being tied to a streaming source.
    [HttpPost("admin/import"), Authorize(Policy = "RequiresElevation")]
    public ActionResult Import([FromBody] ImportBody body)
    {
        var relative = (body.Path ?? "").Trim().Replace('\\', '/').Trim('/');
        if (relative.Length == 0 || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Enter a folder inside the manga storage folder.");
        var folder = storage.Absolute(relative);
        if (!Directory.Exists(folder)) throw new FileNotFoundException("That folder does not exist in manga storage.");
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is ".cbz" or ".cbr" or ".zip" or ".pdf")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new ArgumentException("That folder has no .cbz, .cbr or .pdf chapter files.");
        var seriesId = Identity.For("local-series", relative);
        if (store.Get<Subscription>("series", seriesId) is not null) throw new ArgumentException("That folder is already in the manga library.");
        var title = string.IsNullOrWhiteSpace(body.Title) ? Path.GetFileName(folder) : body.Title.Trim();
        var series = new Series(seriesId, "local", relative, title);
        store.Put("series", seriesId, new Subscription(series, false, null, null));
        store.Put("series-folder", seriesId, relative);
        var index = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var chapter = new Chapter(Identity.For("local-chapter", relative, file), seriesId, file,
                LocalChapterTitle(name), LocalChapterNumber(name, ++index));
            store.Put("job", chapter.Id, new DownloadJob(chapter, "local", "import", "Complete", 0, null, null,
                relative + "/" + Path.GetFileName(file), 0, System.IO.File.GetLastWriteTimeUtc(file)));
        }
        store.Event("Library", title + " · imported " + files.Length + " chapters from disk");
        return Ok(new { series = title, chapters = files.Length });
    }

    private static string LocalChapterTitle(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"issue[\s_-]*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? "Issue " + match.Groups[1].Value : MangaNames.CleanChapterTitle(name);
    }

    // "Rick and Morty 003 (2015)" is chapter 3: years and volume markers must not win.
    private static decimal LocalChapterNumber(string name, int fallback)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(name, @"[\(\[](?:19|20)\d{2}[)\] ]", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\bvol(?:ume)?\.?\s*\d+", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var matches = System.Text.RegularExpressions.Regex.Matches(cleaned, @"\d+(?:\.\d+)?");
        return matches.Count > 0 && decimal.TryParse(matches[^1].Value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : fallback;
    }

    [HttpGet("admin/overview"), Authorize(Policy = "RequiresElevation")]
    public ActionResult Overview()
    {
        var config = MangaHost.Options; string? storageError = null; long? used = null;
        try { used = storage.Used(); } catch (Exception ex) { storageError = ex.Message; }
        return Ok(new { injection = MangaInjection.Registered, config.Paused, usedBytes = used, storageError, downloads = store.All<DownloadJob>("job").GroupBy(j => j.Status).ToDictionary(g => g.Key, g => g.Count()), verifiedSources = sources.All().Count(source => !source.Retired && !source.Removed && source.Status.StartsWith("Verified", StringComparison.Ordinal)), monitored = store.All<Subscription>("series"), checks = store.All<SourceCheck>("check"), libraries = libraries.GetVirtualFolders().Select(v => new { v.Name, v.ItemId, v.Locations }), users = ListUsers().Select(u => new { u.Id, u.Username }) });
    }
    [HttpGet("admin/settings"), Authorize(Policy = "RequiresElevation")]
    public ActionResult Settings() => Ok(MangaHost.Options);
    [HttpGet("admin/logs"), Authorize(Policy = "RequiresElevation")]
    public ActionResult Logs() => Ok(store.All<DiagnosticEvent>("event").OrderByDescending(e => e.At).Take(200));
    [HttpPut("admin/settings"), Authorize(Policy = "RequiresElevation")]
    public ActionResult Settings([FromBody] MangaOptions config)
    {
        if (config.AvifQuality is < 1 or > 100) throw new ArgumentException("AVIF quality must be 1–100.");
        if (config.DownloadConcurrency is < 1 or > 8 || config.MonitorMinutes is < 15 or > 10080 || config.StorageLimitBytes < 0) throw new ArgumentException("Concurrency must be 1–8, monitoring 15–10080 minutes, and storage limit nonnegative.");
        if (!Guid.TryParse(config.LibraryId, out var libraryId) || !libraries.GetVirtualFolders().Any(v => Guid.TryParse(v.ItemId, out var id) && id == libraryId)) throw new ArgumentException("Select an existing Jellyfin library.");
        if (string.IsNullOrWhiteSpace(config.StoragePath) || !Path.IsPathFullyQualified(config.StoragePath)) throw new ArgumentException("An absolute storage path is required.");
        var active = store.All<DownloadJob>("job").Any(j => j.Status == "Downloading");
        if (!string.Equals(config.StoragePath, MangaHost.Options.StoragePath, StringComparison.Ordinal))
        {
            if (store.All<DownloadJob>("job").Any(j => j.Status == "Complete") || active) throw new ArgumentException("Storage cannot change while downloads exist. Move the library offline before migrating storage.");
        }
        var probeStorage = new LibraryStorage(() => config); var probe = probeStorage.Absolute(".write-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config.StoragePath); System.IO.File.WriteAllText(probe, "ok"); System.IO.File.Delete(probe);
        config.DeniedUsers = config.DeniedUsers.Select(id => Guid.TryParse(id, out var value) ? value.ToString("N") : throw new ArgumentException("Invalid user identifier.")).Distinct().ToArray();
        MangaHost.SaveOptions(config); store.Event("Settings", "Administrator updated manga settings."); return NoContent();
    }
    [HttpGet("admin/sources"), Authorize(Policy = "RequiresElevation")]
    public ActionResult AdminSources() => Ok(sources.All().Where(source => !source.Retired && !source.Removed));
    [HttpPut("admin/sources/{id}/enabled"), Authorize(Policy = "RequiresElevation")]
    public ActionResult SourceEnabled(string id, [FromBody] bool enabled) => Ok(sources.SetEnabled(id, enabled));
    [HttpPut("admin/sources/{id}"), Authorize(Policy = "RequiresElevation")]
    public ActionResult Source(string id, [FromBody] SourceDefinition source) { if (id != source.Id) throw new ArgumentException("Source identifier mismatch."); sources.Save(source); return NoContent(); }
    [HttpPut("admin/sources/{id}/downloads"), Authorize(Policy = "RequiresElevation")]
    public ActionResult SourceDownloads(string id, [FromBody] SourceDownloadSettings settings) { sources.SetDownloadLimit(id, settings.MaxConcurrentDownloads); return NoContent(); }
    [HttpDelete("admin/sources/{id}"), Authorize(Policy = "RequiresElevation")]
    public ActionResult RemoveSource(string id) { sources.Remove(id); return NoContent(); }
    [HttpPost("admin/sources/import"), Authorize(Policy = "RequiresElevation")]
    public ActionResult Import([FromBody] SourceDefinition[] definitions)
    {
        if (definitions.Length > 100) throw new ArgumentException("Import at most 100 sources at once.");
        foreach (var definition in definitions) MangaSourceRegistry.Validate(definition);
        foreach (var definition in definitions) sources.Save(definition with { Enabled = false, Removed = false }); return NoContent();
    }
    [HttpPost("admin/sources/{id}/test"), Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Test(string id, [FromBody] TestBody body, CancellationToken ct) => Ok(await tester.Test(id, body, ct));
    [HttpPost("admin/downloads/{id}/retry"), Authorize(Policy = "RequiresElevation")]
    public ActionResult RetryDownload(string id) => DownloadAction(id, "retry");
    [HttpPost("admin/downloads/{id}/pause"), Authorize(Policy = "RequiresElevation")]
    public ActionResult PauseDownload(string id) => DownloadAction(id, "pause");
    [HttpPost("admin/downloads/{id}/cancel"), Authorize(Policy = "RequiresElevation")]
    public ActionResult CancelDownload(string id) => DownloadAction(id, "cancel");
    private ActionResult DownloadAction(string id, string action)
    {
        store.Action(id, action);
        if (action is "cancel" or "pause") queue.Cancel(id);
        return NoContent();
    }
    [HttpPut("admin/series/{id}"), Authorize(Policy = "RequiresElevation")]
    public ActionResult Monitor(string id, [FromBody] MonitorBody body)
    {
        if (store.Get<Subscription>("series", id) == null) throw new KeyNotFoundException(); store.UpdateSubscription(id, current => current with { Monitoring = body.Monitoring }); return NoContent();
    }
    [HttpPost("admin/series/{id}/metadata"), Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> RefreshMetadata(string id, CancellationToken ct)
    {
        Identity.ValidId(id);
        var updated = await SeriesMetadata.RefreshSeries(store, storage, covers, sources, id, MangaHost.Options.ComicVineApiKey, ct, libraries);
        return Ok(new { updated });
    }
    [HttpPost("admin/series/{id}/check"), Authorize(Policy = "RequiresElevation")]
    public ActionResult CheckNow(string id)
    {
        if (store.Get<Subscription>("series", id) == null) throw new KeyNotFoundException(); store.UpdateSubscription(id, current => current with { LastChecked = null }); return Accepted(new { message = "Series scheduled for the next monitor pass (within one minute when monitoring is enabled)." });
    }
}
