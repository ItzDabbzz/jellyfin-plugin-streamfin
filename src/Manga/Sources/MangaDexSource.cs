using System.Globalization;
using System.Text.Json;

namespace StreamFin.Manga.Sources;

public sealed class MangaDexSource(SourceDefinition definition, ISourceHttp http) : IMangaSource
{
    public SourceDefinition Definition => definition;
    private async Task<JsonDocument> Get(string path, CancellationToken ct) => JsonDocument.Parse(await http.Text(new Uri("https://api.mangadex.org" + path), definition, ct));
    private Series Series(JsonElement item)
    {
        var id = item.GetProperty("id").GetString()!; var attributes = item.GetProperty("attributes"); var titles = attributes.GetProperty("title");
        var title = titles.TryGetProperty("en", out var en) ? en.GetString()! : titles.EnumerateObject().First().Value.GetString()!;
        var description = attributes.GetProperty("description").TryGetProperty("en", out var d) ? d.GetString()! : "";
        var cover = "";
        if (item.TryGetProperty("relationships", out var relationships))
            foreach (var relationship in relationships.EnumerateArray())
                if (relationship.GetProperty("type").GetString() == "cover_art" && relationship.TryGetProperty("attributes", out var coverAttributes) && coverAttributes.TryGetProperty("fileName", out var fileName))
                { cover = "https://uploads.mangadex.org/covers/" + id + "/" + Uri.EscapeDataString(fileName.GetString()!) + ".256.jpg"; break; }
        return new(Identity.For(definition.Id, id), definition.Id, "https://mangadex.org/title/" + id, title, description, cover);
    }
    private static string ExternalId(string url, string segment)
    {
        var uri = new Uri(url); var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host != "mangadex.org" || parts.Length < 2 || parts[0] != segment || !Guid.TryParse(parts[1], out _)) throw new ArgumentException("Invalid MangaDex URL.");
        return parts[1];
    }
    public async Task<IReadOnlyList<Series>> Search(string query, CancellationToken ct)
    {
        using var json = await Get("/manga?limit=50&includes[]=cover_art&availableTranslatedLanguage[]=en&title=" + Uri.EscapeDataString(query), ct);
        return json.RootElement.GetProperty("data").EnumerateArray().Select(Series).ToList();
    }
    public async Task<Series> Details(string url, CancellationToken ct)
    {
        using var json = await Get("/manga/" + ExternalId(url, "title") + "?includes[]=cover_art", ct);
        var data = json.RootElement.GetProperty("data");
        var series = Series(data);
        var art = await FirstVolumeCover(data.GetProperty("id").GetString()!, ct);
        return art is null ? series : series with { CoverUrl = art };
    }

    // A manga's cover relationships come in no fixed order, so ask for the first volume's cover, the usual series art.
    private async Task<string?> FirstVolumeCover(string mangaId, CancellationToken ct)
    {
        try
        {
            using var json = await Get("/cover?manga[]=" + mangaId + "&order[volume]=asc&limit=1", ct);
            var first = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
            if (first.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            var file = first.GetProperty("attributes").GetProperty("fileName").GetString();
            return string.IsNullOrEmpty(file) ? null : "https://uploads.mangadex.org/covers/" + mangaId + "/" + Uri.EscapeDataString(file) + ".256.jpg";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
    public async Task<IReadOnlyList<Chapter>> Chapters(Series series, CancellationToken ct)
    {
        var output = new List<Chapter>();
        for (int offset = 0; offset < 10000; offset += 500)
        {
            using var json = await Get($"/manga/{ExternalId(series.Url, "title")}/feed?translatedLanguage[]=en&limit=500&offset={offset}&order[chapter]=asc&includeExternalUrl=0&includeEmptyPages=0", ct);
            foreach (var item in json.RootElement.GetProperty("data").EnumerateArray())
            {
                var a = item.GetProperty("attributes");
                if (a.TryGetProperty("externalUrl", out var external) && external.ValueKind == JsonValueKind.String) continue;
                if (a.TryGetProperty("pages", out var pages) && pages.GetInt32() == 0) continue;
                var id = item.GetProperty("id").GetString()!; var number = a.GetProperty("chapter").GetString() ?? "0";
                decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out var n);
                var published = DateTimeOffset.TryParse(Extraction.Value(a, "publishAt"), out var date) ? date : (DateTimeOffset?)null;
                output.Add(new(Identity.For(series.Id, id), series.Id, "https://mangadex.org/chapter/" + id, "Chapter " + number + " " + (a.GetProperty("title").GetString() ?? ""), n, SourceDate: published));
            }
            if (offset + 500 >= json.RootElement.GetProperty("total").GetInt32()) break;
            if (offset == 9500) throw new InvalidDataException("MangaDex feed exceeds the supported 10,000-entry pagination window.");
        }
        // Keep distinct scanlation releases explicit; do not merge by chapter number.
        return output.DistinctBy(c => c.Id).OrderBy(c => c.Number).ToList();
    }
    public async Task<IReadOnlyList<PageImage>> Pages(Chapter chapter, CancellationToken ct)
    {
        using var json = await Get("/at-home/server/" + ExternalId(chapter.Url, "chapter"), ct);
        var root = json.RootElement; var baseUrl = root.GetProperty("baseUrl").GetString()!;
        var info = root.GetProperty("chapter"); var hash = info.GetProperty("hash").GetString();
        // At-home hosts are dynamic. The registry explicitly trusts MangaDex's returned HTTPS host,
        // while MangaHttp still rejects private destinations and pins DNS at connect time.
        return info.GetProperty("data").EnumerateArray().Select(p => new PageImage(baseUrl + "/data/" + hash + "/" + p.GetString(), chapter.Url)).ToList();
    }
}
