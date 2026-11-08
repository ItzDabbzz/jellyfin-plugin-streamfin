using System.Text.Json;

namespace StreamFin.Manga.Sources;

public sealed class ZinSource(SourceDefinition definition, ISourceHttp http) : IMangaSource
{
    private readonly RuleSource _html = new(definition, http);
    public SourceDefinition Definition => definition;
    public Task<IReadOnlyList<Series>> Search(string query, CancellationToken ct) => _html.Search(query, ct);
    public Task<Series> Details(string url, CancellationToken ct) => _html.Details(url, ct);
    public async Task<IReadOnlyList<PageImage>> Pages(Chapter chapter, CancellationToken ct)
    {
        var pages = await _html.Pages(chapter, ct);
        // The public reader declares referrerpolicy="origin" on its page images.
        var origin = new Uri(chapter.Url).GetLeftPart(UriPartial.Authority) + "/";
        return pages.Select(page => page with { Referer = origin }).ToArray();
    }

    public async Task<IReadOnlyList<Chapter>> Chapters(Series series, CancellationToken ct)
    {
        var seriesUri = MangaHttp.Resolve(series.Url, definition.BaseUrl, definition);
        var parts = seriesUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] != "manga") throw new ArgumentException("Invalid ZinManga series URL.");
        var slug = Uri.EscapeDataString(Uri.UnescapeDataString(parts[1]));
        var chapters = new List<Chapter>();
        for (var page = 1; page <= definition.MaxPagination; page++)
        {
            var endpoint = MangaHttp.Resolve($"/api/comics/{slug}/chapters?page={page}&per_page=50&order=desc", definition.BaseUrl, definition);
            using var json = JsonDocument.Parse(await http.Text(endpoint, definition, ct));
            var data = json.RootElement.GetProperty("data");
            var lastPage = data.GetProperty("last_page").GetInt32();
            if (lastPage > definition.MaxPagination) throw new InvalidDataException("ZinManga chapter list exceeds the configured pagination limit.");
            if (data.GetProperty("current_page").GetInt32() != page) throw new InvalidDataException("ZinManga returned the wrong chapter page.");
            foreach (var item in data.GetProperty("chapters").EnumerateArray())
            {
                var title = item.GetProperty("chapter_name").GetString() ?? "";
                var chapterSlug = Uri.EscapeDataString(item.GetProperty("chapter_slug").GetString() ?? "");
                if (chapterSlug.Length == 0) continue;
                var url = MangaHttp.Resolve($"/manga/{slug}/{chapterSlug}", definition.BaseUrl, definition).ToString();
                var updated = DateTimeOffset.TryParse(Extraction.Value(item, "updated_at"), out var date) ? date : (DateTimeOffset?)null;
                chapters.Add(new(Identity.For(series.Id, url), series.Id, url, title, Extraction.ChapterNumber(title), SourceDate: updated));
            }
            if (page >= lastPage) break;
        }
        return chapters.DistinctBy(chapter => chapter.Id).OrderBy(chapter => chapter.Number).ToList();
    }
}
