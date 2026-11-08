using System.Globalization;
using System.Text.Json;
using AngleSharp.Html.Parser;

namespace StreamFin.Manga.Sources;

// The same public Next.js page data observed in the site's normal browser navigation.
public sealed class FlameSource(SourceDefinition definition, ISourceHttp http) : IMangaSource
{
    public SourceDefinition Definition => definition;
    private async Task<JsonDocument> PageData(string url, CancellationToken ct)
    {
        var uri = MangaHttp.Resolve(url, definition.BaseUrl, definition);
        var document = await new HtmlParser().ParseDocumentAsync(await http.Text(uri, definition, ct), ct);
        var json = document.QuerySelector("script#__NEXT_DATA__")?.TextContent ?? throw new InvalidDataException("Flame page data was not found; the source may have changed.");
        return JsonDocument.Parse(json);
    }
    private static JsonElement Props(JsonDocument data) => data.RootElement.GetProperty("props").GetProperty("pageProps");
    private Series MakeSeries(JsonElement item)
    {
        var url = definition.BaseUrl.TrimEnd('/') + "/series/" + item.GetProperty("series_id").ToString();
        var description = item.TryGetProperty("description", out var text) ? new HtmlParser().ParseDocument(text.GetString() ?? "").Body?.TextContent ?? "" : "";
        var cover = item.TryGetProperty("cover", out var file) && file.ValueKind == JsonValueKind.String
            ? "https://cdn.flamecomics.xyz/uploads/images/series/" + item.GetProperty("series_id") + "/" + Uri.EscapeDataString(file.GetString()!) : "";
        return new(Identity.For(definition.Id, url), definition.Id, url, item.GetProperty("title").GetString()!, description, cover);
    }
    public async Task<IReadOnlyList<Series>> Search(string query, CancellationToken ct)
    {
        using var data = await PageData("/browse", ct);
        return Props(data).GetProperty("series").EnumerateArray()
            .Where(s => s.TryGetProperty("series_id", out _) && (!s.TryGetProperty("language", out var language) || language.GetString() == "English") &&
                (s.GetProperty("title").GetString()!.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 s.TryGetProperty("altTitles", out var aliases) && aliases.ValueKind == JsonValueKind.Array && aliases.EnumerateArray().Any(a => a.GetString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)))
            .Select(MakeSeries).ToList();
    }
    public async Task<Series> Details(string url, CancellationToken ct)
    {
        using var data = await PageData(url, ct); return MakeSeries(Props(data).GetProperty("series"));
    }
    public async Task<IReadOnlyList<Chapter>> Chapters(Series series, CancellationToken ct)
    {
        using var data = await PageData(series.Url, ct); var result = new List<Chapter>();
        foreach (var chapter in Props(data).GetProperty("chapters").EnumerateArray())
        {
            if (chapter.TryGetProperty("notice", out var notice) && notice.GetInt32() != 0) continue;
            var numberText = chapter.GetProperty("chapter").GetString()!;
            if (!decimal.TryParse(numberText, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)) continue;
            var token = chapter.GetProperty("token").GetString()!;
            var url = series.Url.TrimEnd('/') + "/" + Uri.EscapeDataString(token);
            var title = "Chapter " + numberText + " " + (chapter.TryGetProperty("title", out var t) ? t.GetString() : "");
            result.Add(new(Identity.For(series.Id, url), series.Id, url, title.Trim(), number));
        }
        if (result.Count == 0) throw new InvalidDataException("No public Flame chapters found.");
        return result.DistinctBy(c => c.Id).OrderBy(c => c.Number).ToList();
    }
    public async Task<IReadOnlyList<PageImage>> Pages(Chapter chapter, CancellationToken ct)
    {
        var uri = MangaHttp.Resolve(chapter.Url, definition.BaseUrl, definition);
        var document = await new HtmlParser().ParseDocumentAsync(await http.Text(uri, definition, ct), ct);
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "series") throw new ArgumentException("Invalid Flame chapter URL.");
        var prefix = "/uploads/images/series/" + parts[1] + "/" + parts[2] + "/";
        var pages = document.QuerySelectorAll("img[src]").Select(i => i.GetAttribute("src")!)
            .Where(src => Uri.TryCreate(src, UriKind.Absolute, out var image) && image.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            .Select(src => new PageImage(MangaHttp.Resolve(src, chapter.Url, definition).ToString(), chapter.Url)).DistinctBy(p => p.Url).ToList();
        if (pages.Count is 0 or > 2000) throw new InvalidDataException("No public Flame chapter images found.");
        return pages;
    }
}
