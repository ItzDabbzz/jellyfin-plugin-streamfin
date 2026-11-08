using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace StreamFin.Manga.Sources;

public interface IMangaSource
{
    SourceDefinition Definition { get; }
    Task<IReadOnlyList<Series>> Search(string query, CancellationToken ct);
    Task<Series> Details(string url, CancellationToken ct);
    Task<IReadOnlyList<Chapter>> Chapters(Series series, CancellationToken ct);
    Task<IReadOnlyList<PageImage>> Pages(Chapter chapter, CancellationToken ct);
}

public static partial class Extraction
{
    [GeneratedRegex(@"(?:chapter|chap|ch\.?|episode|ep\.?|#)\s*[-:#]?\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterPattern();
    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();
    public static decimal ChapterNumber(string title)
    {
        var match = ChapterPattern().Match(title);
        var value = match.Success ? match.Groups[1].Value : NumberPattern().Match(title).Value;
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
    public static string Text(IElement? node) => Regex.Replace(node?.TextContent ?? "", @"\s+", " ").Trim();
    public static JsonElement Path(JsonElement element, string path)
    {
        if (string.IsNullOrEmpty(path) || path == "$") return element;
        foreach (var key in path.TrimStart('$', '.').Split('.'))
        {
            if (element.ValueKind == JsonValueKind.Array && int.TryParse(key, out var index) && index >= 0 && index < element.GetArrayLength()) element = element[index];
            else if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var child)) element = child;
            else return default;
        }
        return element;
    }
    public static string Value(JsonElement element, string path) { var v = Path(element, path); return v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ValueKind == JsonValueKind.Undefined ? "" : v.ToString(); }
    public static string Image(IElement image, string preferred) => new[] { preferred, "data-src", "data-lazy-src", "src" }.Select(a => image.GetAttribute(a)?.Trim()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) ?? "";
}

public sealed class RuleSource(SourceDefinition definition, ISourceHttp http) : IMangaSource
{
    public SourceDefinition Definition => definition;
    private readonly HtmlParser _parser = new();
    private Uri Resolve(string value, string? parent = null) => MangaHttp.Resolve(value, parent ?? definition.BaseUrl, definition);
    private Uri ResolveImage(string value, string parent)
    {
        // Some HTTPS readers retain legacy http image URLs. Try HTTPS only, never
        // send plaintext traffic or relax the source host/address restrictions.
        if (Uri.TryCreate(value, UriKind.Absolute, out var image) && image.Scheme == "http" && image.IsDefaultPort && string.IsNullOrEmpty(image.UserInfo))
            value = new UriBuilder(image) { Scheme = "https", Port = -1 }.Uri.ToString();
        return Resolve(value, parent);
    }
    private Series MakeSeries(string url, string title, string description = "", string cover = "") => new(Identity.For(definition.Id, url), definition.Id, url, title, description, cover);
    private static IElement? Select(IElement node, string selector) => string.IsNullOrEmpty(selector) ? node : node.QuerySelector(selector);
    private string Url(IElement? node, string parent) => node?.GetAttribute("href") is string href ? Resolve(href, parent).ToString() : "";
    public async Task<IReadOnlyList<Series>> Search(string query, CancellationToken ct)
    {
        var url = Resolve(definition.SearchPath.Replace("{query}", Uri.EscapeDataString(query)));
        var output = new List<Series>(); var visited = new HashSet<string>();
        for (int page = 0; page < definition.MaxPagination && visited.Add(url.ToString()); page++)
        {
            var text = await http.Text(url, definition, ct);
            if (definition.Kind == "json")
            {
                using var json = JsonDocument.Parse(text); var items = Extraction.Path(json.RootElement, definition.JsonItemsPath);
                if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Search JSON path must resolve to an array.");
                foreach (var item in items.EnumerateArray()) output.Add(MakeSeries(Resolve(Extraction.Value(item, definition.JsonUrlPath)).ToString(), Extraction.Value(item, definition.JsonTitlePath)));
                break;
            }
            var doc = await _parser.ParseDocumentAsync(text, ct);
            foreach (var item in doc.QuerySelectorAll(definition.SearchItems))
            {
                var link = Select(item, definition.SearchLink); var href = Url(link, url.ToString());
                if (href.Length == 0) continue;
                var image = item.QuerySelector(definition.SearchCover);
                var cover = image == null ? "" : Extraction.Image(image, definition.ImageAttribute);
                var coverUrl = "";
                if (cover.Length > 0)
                    try { coverUrl = MangaHttp.Resolve(cover, url.ToString(), MangaSourceRegistry.CoverDefinition(definition, new Uri(url, cover).ToString())).ToString(); }
                    catch (Exception ex) when (ex is ArgumentException or UriFormatException) { /* Optional cover hosts must not break title search. */ }
                output.Add(MakeSeries(href, Extraction.Text(link), cover: coverUrl));
            }
            if (string.IsNullOrEmpty(definition.NextSelector) || doc.QuerySelector(definition.NextSelector)?.GetAttribute("href") is not string next) break;
            url = Resolve(next, url.ToString());
        }
        var terms = Regex.Split(query, @"\W+").Where(t => t.Length > 0).ToArray();
        return output.Where(s => s.Title.Length != 0 && terms.All(t => s.Title.Contains(t, StringComparison.OrdinalIgnoreCase))).DistinctBy(s => s.Id).Take(200).ToList();
    }
    public async Task<Series> Details(string url, CancellationToken ct)
    {
        var uri = Resolve(url); var text = await http.Text(uri, definition, ct);
        if (definition.Kind == "json") { using var json = JsonDocument.Parse(text); return MakeSeries(uri.ToString(), Extraction.Value(json.RootElement, definition.JsonTitlePath)); }
        var doc = await _parser.ParseDocumentAsync(text, ct); var title = Extraction.Text(doc.QuerySelector(definition.TitleSelector));
        if (title.Length == 0) throw new InvalidDataException("Series title selector returned no title; source rules may have changed.");
        var image = doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content");
        if (string.IsNullOrWhiteSpace(image))
        {
            var node = doc.QuerySelector(".summary_image img, .manga-info-pic img, .thumb img");
            if (node != null) image = Extraction.Image(node, definition.ImageAttribute);
        }
        var cover = "";
        if (!string.IsNullOrWhiteSpace(image))
            try { cover = MangaHttp.Resolve(image, uri.ToString(), MangaSourceRegistry.CoverDefinition(definition, new Uri(uri, image).ToString())).ToString(); }
            catch (ArgumentException) { /* An optional cover must not prevent a request. */ }
        return MakeSeries(uri.ToString(), title, Extraction.Text(doc.QuerySelector(definition.DescriptionSelector)), cover);
    }
    public async Task<IReadOnlyList<Chapter>> Chapters(Series series, CancellationToken ct)
    {
        var seriesParts = new Uri(series.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var url = Resolve(definition.ChaptersPath.Replace("{series}", series.Url.TrimEnd('/')).Replace("{seriesId}", seriesParts.Length > 1 ? seriesParts[1] : ""));
        var text = definition.ChaptersMethod == "POST" ? await http.FormText(url, definition, ct) : await http.Text(url, definition, ct); var output = new List<Chapter>();
        if (definition.Kind == "json")
        {
            using var json = JsonDocument.Parse(text); var items = Extraction.Path(json.RootElement, definition.JsonChaptersPath);
            if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Chapter JSON path must resolve to an array.");
            foreach (var item in items.EnumerateArray()) { var href = Resolve(Extraction.Value(item, definition.JsonUrlPath), url.ToString()).ToString(); var title = Extraction.Value(item, definition.JsonTitlePath); output.Add(new(Identity.For(series.Id, href), series.Id, href, title, Extraction.ChapterNumber(title))); }
        }
        else
        {
            var visited = new HashSet<string>();
            for (int page = 0; page < definition.MaxPagination && visited.Add(url.ToString()); page++)
            {
                var doc = await _parser.ParseDocumentAsync(text, ct);
                foreach (var item in doc.QuerySelectorAll(definition.ChapterItems))
                {
                    var link = Select(item, definition.ChapterLink); var href = Url(link, url.ToString()); var title = Extraction.Text(link);
                    if (href.Length != 0) output.Add(new(Identity.For(series.Id, href), series.Id, href, title, Extraction.ChapterNumber(title)));
                }
                if (string.IsNullOrEmpty(definition.NextSelector) || doc.QuerySelector(definition.NextSelector)?.GetAttribute("href") is not string next) break;
                url = Resolve(next, url.ToString()); text = await http.Text(url, definition, ct);
            }
        }
        if (output.Count == 0) throw new InvalidDataException("No English chapters found. The site may require a specialized adapter, login, or updated rules.");
        return output.DistinctBy(c => c.Id).OrderBy(c => c.Number).ThenBy(c => c.Title, StringComparer.Ordinal).ToList();
    }
    private static string InlineScriptData(string html)
    {
        // WordPress performance plugins embed ordinary inline scripts as base64 data URLs.
        // Decode text only; never execute the script or fetch script destinations.
        var document = new HtmlParser().ParseDocument(html); var text = new System.Text.StringBuilder(html);
        foreach (var script in document.QuerySelectorAll("script[src]"))
        {
            const string prefix = "data:text/javascript;base64,";
            var src = script.GetAttribute("src")!;
            if (!src.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || src.Length > 4_000_000) continue;
            try { text.AppendLine(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(src[prefix.Length..]))); }
            catch (FormatException) { /* Malformed optional script data is not an image list. */ }
            if (text.Length > 16_000_000) break;
        }
        return text.ToString();
    }
    public async Task<IReadOnlyList<PageImage>> Pages(Chapter chapter, CancellationToken ct)
    {
        var parts = new Uri(chapter.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var url = Resolve(definition.PagesPath.Replace("{chapter}", chapter.Url).Replace("{chapterId}", parts.Length > 1 ? parts[1] : "")); var text = await http.Text(url, definition, ct); var output = new List<PageImage>();
        if (definition.Kind == "json")
        {
            using var json = JsonDocument.Parse(text); var items = Extraction.Path(json.RootElement, definition.JsonPagesPath);
            if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Page JSON path must resolve to an array.");
            foreach (var item in items.EnumerateArray()) output.Add(new(ResolveImage(item.ValueKind == JsonValueKind.String ? item.GetString()! : Extraction.Value(item, definition.JsonUrlPath), chapter.Url).ToString(), chapter.Url));
        }
        else if (definition.ScriptImagePattern.Length > 0 && Regex.Match(InlineScriptData(text), definition.ScriptImagePattern, RegexOptions.Singleline, TimeSpan.FromSeconds(1)) is { Success: true } match)
        {
            var array = Newtonsoft.Json.Linq.JArray.Parse(match.Groups["images"].Value);
            foreach (var item in array) output.Add(new(ResolveImage(item.ToString(), chapter.Url).ToString(), chapter.Url));
        }
        else
        {
            var visited = new HashSet<string>();
            for (int page = 0; page < definition.MaxPagination && visited.Add(url.ToString()); page++)
            {
                var doc = await _parser.ParseDocumentAsync(text, ct);
                foreach (var node in doc.QuerySelectorAll(definition.PagesSelector)) { var image = Extraction.Image(node, definition.ImageAttribute); if (image.Length > 0) output.Add(new(ResolveImage(image, url.ToString()).ToString(), chapter.Url)); }
                if (string.IsNullOrEmpty(definition.NextSelector) || doc.QuerySelector(definition.NextSelector)?.GetAttribute("href") is not string next) break;
                url = Resolve(next, url.ToString()); text = await http.Text(url, definition, ct);
            }
        }
        output = output.DistinctBy(p => p.Url).ToList(); if (definition.ReversePages) output.Reverse();
        if (output.Count is 0 or > 2000) throw new InvalidDataException("Invalid page count; source extraction failed.");
        return output;
    }
}
