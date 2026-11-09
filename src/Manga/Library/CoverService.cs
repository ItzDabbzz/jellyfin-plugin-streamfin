using StreamFin.Manga.Sources;

namespace StreamFin.Manga;

public sealed record CoverFailure(DateTimeOffset RetryAt);
public sealed class CoverService
{
    private readonly Store _store; private readonly MangaSourceRegistry _sources; private readonly ISourceHttp _http; private readonly LibraryStorage _storage; private readonly string _folder;
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly SemaphoreSlim _fetches = new(3);
    public CoverService(Store store, MangaSourceRegistry sources, ISourceHttp http, LibraryStorage storage)
        : this(store, sources, http, storage, Path.Combine(MangaHost.DataFolder, "covers")) { }
    internal CoverService(Store store, MangaSourceRegistry sources, ISourceHttp http, LibraryStorage storage, string folder)
    { _store = store; _sources = sources; _http = http; _storage = storage; _folder = folder; }
    public void Remember(Series series)
    {
        var previous = _store.Get<Series>("cover-series", series.Id);
        _store.Put("cover-series", series.Id, string.IsNullOrWhiteSpace(series.CoverUrl) && !string.IsNullOrWhiteSpace(previous?.CoverUrl) ? series with { CoverUrl = previous.CoverUrl } : series);
    }
    // Drops the cached image so the next request fetches the series cover again.
    public void Forget(string id)
    {
        Identity.ValidId(id);
        var path = Path.Combine(_folder, id + ".image");
        if (File.Exists(path)) File.Delete(path);
        _store.Delete("cover-failure", id);
    }
    public async Task<byte[]> Get(string id, CancellationToken ct)
    {
        Identity.ValidId(id); Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, id + ".image");
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct);
        var gate = _locks[Convert.ToInt32(id[..2], 16) % _locks.Length]; await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct);
            var subscription = _store.Get<Subscription>("series", id);
            var series = subscription?.Series ?? _store.Get<Series>("cover-series", id) ?? throw new KeyNotFoundException("Unknown manga cover.");
            var failure = _store.Get<CoverFailure>("cover-failure", id);
            if (failure == null || failure.RetryAt <= DateTimeOffset.UtcNow)
            {
                await _fetches.WaitAsync(ct);
                try
                {
                    var definition = _sources.Get(series.SourceId, false);
                    async Task Refresh()
                    {
                        var details = await _sources.Adapter(series.SourceId, false).Details(series.Url, ct);
                        if (details.Id != id || details.SourceId != series.SourceId) throw new InvalidDataException("Cover metadata returned a different manga.");
                        series = series with { CoverUrl = details.CoverUrl }; Remember(series);
                        if (!string.IsNullOrWhiteSpace(series.CoverUrl) && subscription != null)
                            _store.UpdateSubscription(id, current => current with { Series = current.Series with { CoverUrl = series.CoverUrl } });
                    }
                    async Task<byte[]> DownloadCover()
                    {
                        if (string.IsNullOrWhiteSpace(series.CoverUrl)) throw new FileNotFoundException("The source has no cover image.");
                        var imageDefinition = MangaSourceRegistry.CoverDefinition(definition, series.CoverUrl);
                        var uri = MangaHttp.Resolve(series.CoverUrl, series.Url, imageDefinition);
                        return await _http.Image(uri, imageDefinition, series.Url, ct);
                    }
                    if (string.IsNullOrWhiteSpace(series.CoverUrl)) await Refresh();
                    byte[] bytes;
                    try { bytes = await DownloadCover(); }
                    catch (HttpRequestException)
                    {
                        var previous = series.CoverUrl; await Refresh();
                        if (series.CoverUrl == previous) throw;
                        bytes = await DownloadCover();
                    }
                    if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Cover exceeds 8 MiB.");
                    LibraryStorage.ImageExtension(bytes);
                    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { await File.WriteAllBytesAsync(temporary, bytes, ct); File.Move(temporary, path, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    _store.Delete("cover-failure", id); return bytes;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _store.Put("cover-failure", id, new CoverFailure(DateTimeOffset.UtcNow.AddMinutes(5)));
                    _store.Event("Cover unavailable", series.Title + ": " + ex.Message);
                }
                finally { _fetches.Release(); }
            }
            // Offline or coverless sources can still show a thumbnail from a downloaded chapter.
            foreach (var job in _store.All<DownloadJob>("job").Where(j => j.Chapter.SeriesId == id && j.Status == "Complete").OrderBy(j => j.Chapter.Number))
            {
                try { return await _storage.Page(job, 0, ct); }
                catch (IOException) { }
                catch (InvalidDataException) { }
            }
            throw new FileNotFoundException("No cover or downloaded thumbnail is available.");
        }
        finally { gate.Release(); }
    }
}
