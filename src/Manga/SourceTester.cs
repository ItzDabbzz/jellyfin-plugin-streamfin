using StreamFin.Manga.Sources;

namespace StreamFin.Manga;

public sealed class SourceTester(Store store, MangaSourceRegistry sources, ChapterDownloader downloader, LibraryStorage storage)
{
    public async Task<SourceCheck> Test(string id, TestBody body, CancellationToken ct)
    {
        if (body.Query.Length is < 2 or > 200) throw new ArgumentException("A test query of 2–200 characters is required.");
        bool search = false, details = false, chapters = false, pages = false, download = false; string? title = null, error = null;
        string? sampleChapter = null; int pageCount = 0;
        try
        {
            var adapter = sources.Adapter(id, false); var found = await adapter.Search(body.Query, ct); search = found.Count > 0;
            var url = body.SeriesUrl ?? found.FirstOrDefault(s => s.Title.Contains(body.Query, StringComparison.OrdinalIgnoreCase))?.Url ?? found.FirstOrDefault()?.Url ?? throw new InvalidDataException("Search returned no titles.");
            var series = await adapter.Details(url, ct); title = series.Title; details = title.Length > 0;
            var list = await adapter.Chapters(series, ct); chapters = list.Count > 0;
            var chapter = list.FirstOrDefault(c => c.Number >= 1 && !c.Title.Contains("notice", StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault() ?? throw new InvalidDataException("No chapters found."); sampleChapter = chapter.Url; var images = await adapter.Pages(chapter, ct); pageCount = images.Count; pages = images.Count > 0;
            if (body.Download)
            {
                // Tests use their own archive ID and never overwrite a library chapter.
                var testChapter = chapter with { Id = Identity.For("source-test", Guid.NewGuid().ToString()) };
                var result = await downloader.Download(new(testChapter, id, "source-test"), ct, false);
                try
                {
                    var job = new DownloadJob(testChapter, id, "source-test", "Complete", RelativePath: result.Path, Pages: result.Pages);
                    var entries = storage.Entries(job); download = entries.Count == result.Pages && result.Pages > 0;
                    for (int i = 0; i < entries.Count; i++) LibraryStorage.ImageExtension(await storage.Page(job, i, ct));
                }
                finally { File.Delete(storage.Absolute(result.Path)); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { error = ex.Message; }
        var check = new SourceCheck(id, DateTimeOffset.UtcNow, body.Query, title, search, details, chapters, pages, download, error, sampleChapter, pageCount);
        store.Put("check", id, check);
        store.Event("Source test", id + ": " + (error ?? (download ? "Complete chapter validated." : "Extraction checked; download not tested.")));
        var definition = sources.Get(id, false);
        store.Put("source", id, definition with { Status = search && details && chapters && pages && download ? "Verified download" : error == null ? "Extraction checked; download untested" : "Failed: " + error });
        return check;
    }
}
