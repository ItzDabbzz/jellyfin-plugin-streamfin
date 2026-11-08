using System.Text;

namespace StreamFin.Manga.Sources;

// Public web API with the same anonymous session header used by the website.
public sealed class MangaPlusSource(SourceDefinition definition, ISourceHttp http, string sessionId) : IMangaSource
{
    public SourceDefinition Definition => definition;
    private readonly IReadOnlyDictionary<string, string> _headers = new Dictionary<string, string> { ["SESSION-TOKEN"] = sessionId };
    private async Task<WireMessage> Get(string path, CancellationToken ct)
    {
        var response = new WireMessage(await http.Data(new Uri("https://jumpg-webapi.tokyo-cdn.com/api/" + path), definition, _headers, ct));
        if (response.Children(1).FirstOrDefault() is { } success) return success;
        var error = response.Children(2).FirstOrDefault()?.Children(2).FirstOrDefault();
        if (error?.Text(1).Contains("banned", StringComparison.OrdinalIgnoreCase) == true)
            throw new HttpRequestException("MANGA Plus rejected the plugin's anonymous API session (provider response: " + error.Text(1) + "). This is not a check of your personal MANGA Plus account.");
        throw new HttpRequestException(error == null ? "MANGA Plus API returned no result; login or regional availability may be required." : error.Text(1) + ": " + error.Text(2));
    }
    private Series Series(WireMessage title, string description = "")
    {
        var url = "https://mangaplus.shueisha.co.jp/titles/" + title.Number(1);
        return new(Identity.For(definition.Id, url), definition.Id, url, title.Text(2), description, title.Text(4));
    }
    private static string Id(string url, string kind)
    {
        var uri = new Uri(url); var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host != "mangaplus.shueisha.co.jp" || parts.Length != 2 || parts[0] != kind || !ulong.TryParse(parts[1], out _)) throw new ArgumentException("Invalid MANGA Plus URL."); return parts[1];
    }
    public async Task<IReadOnlyList<Series>> Search(string query, CancellationToken ct)
    {
        var response = await Get("title_list/allV2?lang=eng&clang=eng", ct);
        var titles = response.Children(25).SelectMany(v => v.Children(1)).SelectMany(g => g.Children(2));
        return titles.Where(t => t.Number(7) == 0 && t.Text(2).Contains(query, StringComparison.OrdinalIgnoreCase)).Select(t => Series(t)).DistinctBy(t => t.Id).ToList();
    }
    private async Task<WireMessage> Detail(string url, CancellationToken ct) => (await Get("title_detailV3?title_id=" + Id(url, "titles") + "&clang=eng", ct)).Children(8).FirstOrDefault() ?? throw new InvalidDataException("MANGA Plus title details missing.");
    public async Task<Series> Details(string url, CancellationToken ct)
    {
        var detail = await Detail(url, ct); var title = detail.Children(1).FirstOrDefault() ?? throw new InvalidDataException("MANGA Plus title missing.");
        if (title.Number(7) != 0) throw new InvalidDataException("Only English titles are supported."); return Series(title, detail.Text(3));
    }
    public async Task<IReadOnlyList<Chapter>> Chapters(Series series, CancellationToken ct)
    {
        var detail = await Detail(series.Url, ct);
        return detail.Children(28).SelectMany(g => g.Children(2).Concat(g.Children(4))).Where(c => c.Number(2) > 0).Select(c => {
            var url = "https://mangaplus.shueisha.co.jp/viewer/" + c.Number(2); var title = c.Text(3) + " " + c.Text(4);
            return new Chapter(Identity.For(series.Id, url), series.Id, url, title, Extraction.ChapterNumber(title));
        }).DistinctBy(c => c.Id).OrderBy(c => c.Number).ToList();
    }
    public async Task<IReadOnlyList<PageImage>> Pages(Chapter chapter, CancellationToken ct)
    {
        var result = await Get("manga_viewer_v3?chapter_id=" + Id(chapter.Url, "viewer") + "&split=yes&img_quality=high&clang=eng", ct);
        var pages = result.Children(10).SelectMany(v => v.Children(1)).SelectMany(p => p.Children(1)).ToList();
        if (pages.Any(p => p.Text(5).Length > 0)) throw new InvalidDataException("MANGA Plus returned encrypted images. This adapter does not bypass image protection.");
        if (pages.Count == 0) throw new InvalidDataException("No accessible MANGA Plus pages; chapter may have expired or require a subscription.");
        return pages.Select(p => new PageImage(p.Text(1), chapter.Url)).ToList();
    }
}

internal sealed class WireMessage
{
    private readonly Dictionary<int, List<(ulong Number, byte[]? Data)>> _fields = new();
    public WireMessage(byte[] bytes)
    {
        int offset = 0;
        ulong Varint()
        {
            ulong n = 0;
            for (int shift = 0; shift < 64; shift += 7) { if (offset >= bytes.Length) throw new InvalidDataException("Truncated protobuf."); var b = bytes[offset++]; n |= (ulong)(b & 127) << shift; if ((b & 128) == 0) return n; }
            throw new InvalidDataException("Invalid protobuf integer.");
        }
        while (offset < bytes.Length)
        {
            var tag = Varint(); var field = checked((int)(tag >> 3)); if (field == 0) throw new InvalidDataException("Invalid protobuf field.");
            (ulong Number, byte[]? Data) value;
            switch (tag & 7)
            {
                case 0: value = (Varint(), null); break;
                case 2: var count = checked((int)Varint()); if (count < 0 || count > bytes.Length - offset) throw new InvalidDataException("Invalid protobuf length."); value = (0, bytes.AsSpan(offset, count).ToArray()); offset += count; break;
                case 1: if (bytes.Length - offset < 8) throw new InvalidDataException(); offset += 8; continue;
                case 5: if (bytes.Length - offset < 4) throw new InvalidDataException(); offset += 4; continue;
                default: throw new InvalidDataException("Unsupported protobuf wire type.");
            }
            if (!_fields.TryGetValue(field, out var list)) _fields[field] = list = []; list.Add(value);
        }
    }
    public IEnumerable<WireMessage> Children(int field) => _fields.TryGetValue(field, out var values) ? values.Where(v => v.Data != null).Select(v => new WireMessage(v.Data!)) : [];
    public string Text(int field) => _fields.TryGetValue(field, out var values) && values.FirstOrDefault().Data is { } bytes ? Encoding.UTF8.GetString(bytes) : "";
    public ulong Number(int field) => _fields.TryGetValue(field, out var values) ? values[0].Number : 0;
}
