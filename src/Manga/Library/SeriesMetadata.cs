using MediaBrowser.Controller.Library;
using StreamFin.Manga.Sources;
using System.IO.Compression;

namespace StreamFin.Manga;

// Rewrites ComicInfo.xml inside chapters that are already downloaded, so no pages are fetched again.
public static class SeriesMetadata
{
    // Used by the Refresh metadata button and by the Jellyfin scheduled task.
    public static async Task<int> RefreshSeries(Store store, LibraryStorage storage, CoverService covers, MangaSourceRegistry sources, string seriesId, string apiKey, CancellationToken ct, ILibraryManager? libraries = null)
    {
        var subscription = store.Get<Subscription>("series", seriesId);
        if (subscription is not null)
        {
            try
            {
                var fresh = await sources.Adapter(subscription.Series.SourceId, false).Details(subscription.Series.Url, ct);
                store.UpdateSubscription(seriesId, current => current with { Series = current.Series with { CoverUrl = fresh.CoverUrl } });
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        covers.Forget(seriesId);
        return await Refresh(store, storage, covers, seriesId, apiKey, ct, libraries);
    }

    public static async Task<int> Refresh(Store store, LibraryStorage storage, CoverService covers, string seriesId, string apiKey, CancellationToken ct, ILibraryManager? libraries = null)
    {
        var series = store.Get<Subscription>("series", seriesId)?.Series ?? throw new KeyNotFoundException("Unknown series.");
        // Refresh always searches again, so a miss cached by an older version does not stick.
        var comic = await MangaMetadata.Resolve(store, series, apiKey, ct, force: true);
        // Tell Jellyfin the Comic Vine id so the series' External IDs are filled in. A failure here must not stop the refresh.
        if (libraries is not null && comic?.VolumeId is int volumeId)
        {
            try { await JellyfinIds.SetComicVineAsync(libraries, storage.Absolute(MangaNames.FolderFor(store, series)), volumeId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        RenameStatusNamedChapters(store, storage, seriesId);
        var jobs = store.All<DownloadJob>("job").Where(j => j.Chapter.SeriesId == seriesId && j.Status == "Complete" && j.RelativePath != null).ToList();
        var updated = 0;
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            var path = storage.Absolute(job.RelativePath!);
            if (!File.Exists(path)) continue;
            // Only archives whose ComicInfo would actually change are rewritten; rewriting every file makes Jellyfin rescan the whole library.
            if (!await ComicInfoChanged(path, job.Chapter, series, comic, ct)) continue;
            // Work on a copy and swap it in, so Jellyfin never reads a half-written archive.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(path, temporary);
            try
            {
                using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Update))
                {
                    var pages = archive.Entries.Count(e => e.Name != "ComicInfo.xml");
                    archive.GetEntry("ComicInfo.xml")?.Delete();
                    var entry = archive.CreateEntry("ComicInfo.xml");
                    await using var stream = entry.Open();
                    await ComicInfoFile.Build(job.Chapter, series, pages, comic).SaveAsync(stream, System.Xml.Linq.SaveOptions.None, ct);
                }
                File.Move(temporary, path, overwrite: true);
                updated++;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        // The cached volume list is reused; only a first run (or a cache miss) asks MangaDex for it.
        var volumes = await VolumeArt.Index(store, series, ct);
        var covered = 0;
        if (volumes is not null)
        {
            foreach (var job in jobs)
            {
                ct.ThrowIfCancellationRequested();
                try { if (await VolumeArt.Apply(store, storage, job.Chapter, job.RelativePath!, volumes, ct)) covered++; }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
        }
        await WriteSeriesInfo(store, storage, series, comic, ct);
        // The source cover becomes folder.<ext> in the series folder, which Jellyfin uses as the series artwork.
        var art = await SaveFolderArt(covers, storage, store, series, ct);
        store.Event("Metadata", series.Title + " · refreshed ComicInfo for " + updated + " chapters" + (art is null ? "" : " · series art saved") + (comic is null ? " (no ComicVine match; source details only)" : ""));
        return updated;
    }

    // True when the archive's ComicInfo would differ from what is stored now (compared as XML, ignoring formatting).
    // Files named from source status text (for example "Last Read 2024-09-07…") get the clean chapter name.
    // The stored path follows the file, so the reader and the downloads tab keep working.
    // Writes ComicInfo.xml into the series folder (not into a chapter). Skipped when the content is already current.
    private static async Task WriteSeriesInfo(Store store, LibraryStorage storage, Series series, VolumeMetadata? comic, CancellationToken ct)
    {
        var folder = storage.Absolute(MangaNames.FolderFor(store, series));
        if (!Directory.Exists(folder)) return;
        var path = Path.Combine(folder, "ComicInfo.xml");
        var wanted = ComicInfoFile.BuildSeries(series, comic);
        if (File.Exists(path))
        {
            try
            {
                var current = System.Xml.Linq.XDocument.Load(path);
                if (System.Xml.Linq.XNode.DeepEquals(current.Root, wanted.Root)) return;
            }
            catch (System.Xml.XmlException) { }
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary)) await wanted.SaveAsync(stream, System.Xml.Linq.SaveOptions.None, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RenameStatusNamedChapters(Store store, LibraryStorage storage, string seriesId)
    {
        foreach (var job in store.All<DownloadJob>("job").Where(j => j.Chapter.SeriesId == seriesId && j.Status == "Complete" && j.RelativePath != null).ToList())
        {
            try
            {
                var rawTitle = job.Chapter.Title ?? "";
                var cleanTitle = MangaNames.CleanChapterTitle(rawTitle);
                if (cleanTitle == rawTitle) continue;
                var chapter = job.Chapter with { Title = cleanTitle };
                var folder = Path.GetDirectoryName(job.RelativePath!) ?? "";
                var target = MangaNames.UniqueChapterFile(store, job.Chapter.Id, folder, MangaNames.ChapterFile(chapter));
                var from = storage.Absolute(job.RelativePath!);
                var to = storage.Absolute(target);
                if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
                else if (!File.Exists(to)) { store.Put("job", job.Chapter.Id, job with { Chapter = chapter }); continue; }
                store.Put("job", job.Chapter.Id, job with { Chapter = chapter, RelativePath = target });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A file that cannot be renamed keeps its old name; the rest of the series still updates.
            }
        }
    }

    private static async Task<bool> ComicInfoChanged(string path, Chapter chapter, Series series, VolumeMetadata? comic, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(path);
        var pages = archive.Entries.Count(e => e.Name != "ComicInfo.xml");
        var wanted = ComicInfoFile.Build(chapter, series, pages, comic);
        var existing = archive.GetEntry("ComicInfo.xml");
        if (existing is null) return true;
        try
        {
            using var reader = new StreamReader(existing.Open());
            var current = System.Xml.Linq.XDocument.Parse(await reader.ReadToEndAsync(ct));
            return !System.Xml.Linq.XNode.DeepEquals(current.Root, wanted.Root);
        }
        catch (System.Xml.XmlException) { return true; }
    }

    private static async Task<string?> SaveFolderArt(CoverService covers, LibraryStorage storage, Store store, Series series, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = await covers.Get(series.Id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        var folder = storage.Absolute(MangaNames.FolderFor(store, series));
        if (!Directory.Exists(folder)) return null;
        var extension = LibraryStorage.ImageExtension(bytes);
        foreach (var old in Directory.EnumerateFiles(folder, "folder.*")) File.Delete(old);
        await File.WriteAllBytesAsync(Path.Combine(folder, "folder" + extension), bytes, ct);
        return extension;
    }
}
