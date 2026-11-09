using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;

namespace StreamFin.Manga;

// chapter number -> volume, and volume -> cover image, both taken from MangaDex for one series.
public sealed record VolumeIndex(Dictionary<string, string> ChapterVolume, Dictionary<string, string> VolumeCover);

// Puts each chapter's volume cover in front of its pages, so Jellyfin shows that cover on the chapter tile.
public static class VolumeArt
{
    private static readonly HttpClient Client = CreateClient();
    // MangaDex allows about 5 requests a second; one every 250 ms stays well under that.
    private static readonly RateGate Gate = new(TimeSpan.FromMilliseconds(250));
    private const long MaxCoverBytes = 10L * 1024 * 1024;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamFin-Jellyfin-Plugin/0.12");
        return client;
    }

    // "1", "1.0" and 1.00 all become "1"; "10.5" stays "10.5".
    public static string Key(decimal number)
    {
        var text = number.ToString(CultureInfo.InvariantCulture);
        return text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;
    }

    private static string? KeyFromText(string text) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? Key(value) : null;

    private static async Task<JsonDocument?> GetJson(string url, CancellationToken ct)
    {
        if (Gate.IsPaused) return null;
        await Gate.WaitAsync(ct);
        using var response = await Client.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            Gate.Pause(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            return null;
        }
        if (!response.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    // Reads the MangaDex title id from the series URL, e.g. https://mangadex.org/title/{id}.
    private static string? MangaDexId(Series series)
    {
        const string marker = "mangadex.org/title/";
        var at = series.Url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var id = series.Url[(at + marker.Length)..].Split('?', '/', '#')[0];
        return Guid.TryParse(id, out _) ? id : null;
    }

    public static async Task<VolumeIndex?> Index(Store store, Series series, CancellationToken ct, bool force = false)
    {
        var id = MangaDexId(series);
        if (id is null) return null;
        if (!force)
        {
            var cached = store.Get<VolumeIndex>("volume-index", series.Id);
            if (cached is not null) return cached;
        }

        var chapterVolume = new Dictionary<string, string>();
        for (var offset = 0; ; offset += 500)
        {
            using var feed = await GetJson($"https://api.mangadex.org/manga/{id}/feed?translatedLanguage[]=en&limit=500&offset={offset}&order[chapter]=asc", ct);
            if (feed is null) return null;
            var items = feed.RootElement.GetProperty("data").EnumerateArray().ToList();
            foreach (var item in items)
            {
                var attributes = item.GetProperty("attributes");
                var number = KeyFromText(Text(attributes, "chapter"));
                var volume = Text(attributes, "volume");
                if (number is not null && volume.Length > 0) chapterVolume.TryAdd(number, volume);
            }
            if (items.Count < 500) break;
        }

        var volumeCover = new Dictionary<string, string>();
        for (var offset = 0; ; offset += 100)
        {
            using var covers = await GetJson($"https://api.mangadex.org/cover?manga[]={id}&limit=100&offset={offset}&order[volume]=asc", ct);
            if (covers is null) return null;
            var items = covers.RootElement.GetProperty("data").EnumerateArray().ToList();
            foreach (var item in items)
            {
                var attributes = item.GetProperty("attributes");
                var volume = Text(attributes, "volume");
                var file = Text(attributes, "fileName");
                if (volume.Length > 0 && file.Length > 0)
                    volumeCover.TryAdd(volume, "https://uploads.mangadex.org/covers/" + id + "/" + Uri.EscapeDataString(file) + ".512.jpg");
            }
            if (items.Count < 100) break;
        }

        var index = new VolumeIndex(chapterVolume, volumeCover);
        store.Put("volume-index", series.Id, index);
        return index;
    }

    private static async Task<byte[]?> Cover(string url, CancellationToken ct)
    {
        if (Gate.IsPaused) return null;
        await Gate.WaitAsync(ct);
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength ?? 0) > MaxCoverBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxCoverBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // Rewrites one chapter archive with its volume cover first. Chapters already done are skipped.
    public static async Task<bool> Apply(Store store, LibraryStorage storage, Chapter chapter, string relativePath, VolumeIndex index, CancellationToken ct)
    {
        if (!string.Equals(Path.GetExtension(relativePath), ".cbz", StringComparison.OrdinalIgnoreCase)) return false;
        if (store.Get<string>("chapter-cover", chapter.Id) is not null) return false;
        var volume = index.ChapterVolume.GetValueOrDefault(Key(chapter.Number));
        if (volume is null || !index.VolumeCover.TryGetValue(volume, out var url)) return false;
        var bytes = await Cover(url, ct);
        if (bytes is null) return false;

        var path = storage.Absolute(relativePath);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var source = ZipFile.OpenRead(path))
            using (var target = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var pages = source.Entries.Where(entry => entry.Name != "ComicInfo.xml").OrderBy(entry => entry.FullName, StringComparer.Ordinal).ToList();
                var cover = target.CreateEntry("00000" + LibraryStorage.ImageExtension(bytes), CompressionLevel.NoCompression);
                await using (var coverStream = cover.Open()) await coverStream.WriteAsync(bytes, ct);
                for (var i = 0; i < pages.Count; i++)
                {
                    var entry = target.CreateEntry((i + 1).ToString("D5") + Path.GetExtension(pages[i].Name), CompressionLevel.NoCompression);
                    await using var output = entry.Open();
                    await using var input = pages[i].Open();
                    await input.CopyToAsync(output, ct);
                }
                var info = source.GetEntry("ComicInfo.xml");
                if (info is not null)
                {
                    XDocument document;
                    await using (var infoStream = info.Open()) document = await XDocument.LoadAsync(infoStream, LoadOptions.None, ct);
                    document.Root?.Element("PageCount")?.SetValue(pages.Count + 1);
                    var infoEntry = target.CreateEntry("ComicInfo.xml");
                    await using var infoOut = infoEntry.Open();
                    await document.SaveAsync(infoOut, SaveOptions.None, ct);
                }
            }
            File.Move(temporary, path, overwrite: true);
            store.Put("chapter-cover", chapter.Id, volume);
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
