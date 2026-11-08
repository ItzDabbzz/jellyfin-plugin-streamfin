using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamFin.Manga;

// Volume metadata from ComicVine. A miss is stored with an empty title so it is not searched again.
// Creator fields are comma-separated names, most frequent first. ComicVine has no genre field, so none is stored.
public sealed record VolumeMetadata(string Title = "", string Summary = "", string Publisher = "", int? Year = null, string Url = "", int? VolumeId = null, string Writers = "", string Pencillers = "", string Inkers = "", string Colorists = "", string CoverArtists = "", string Genres = "", string Tags = "");

// ComicVine needs a free API key entered in the StreamFin manga settings. Only ComicVine's host is contacted.
public static class ComicVine
{
    private static readonly HttpClient Client = CreateClient();
    // Comic Vine allows about 200 requests an hour and one per second; stay under both.
    private static readonly RateGate Gate = new(TimeSpan.FromMilliseconds(1100));

    // Returns null without sending when the service has asked us to slow down, or when it answers with its rate-limit code.
    private static async Task<HttpResponseMessage?> Fetch(string url, CancellationToken ct)
    {
        if (Gate.IsPaused) return null;
        await Gate.WaitAsync(ct);
        var response = await Client.GetAsync(url, ct);
        if (response.StatusCode is TooManyRequests || (int)response.StatusCode == 420)
        {
            Gate.Pause(TimeSpan.FromMinutes(15));
            response.Dispose();
            return null;
        }
        return response;
    }

    private const HttpStatusCode TooManyRequests = HttpStatusCode.TooManyRequests;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamFin-Jellyfin-Plugin/0.12");
        return client;
    }

    // Source titles carry scan tags such as "(Official Colored)"; ComicVine knows the series by its plain name.
    public static string CleanTitle(string title)
    {
        var plain = Regex.Replace(title ?? "", @"\s*[\(\[\{][^\)\]\}]*[\)\]\}]\s*", " ").Trim();
        return plain.Length > 0 ? plain : (title ?? "").Trim();
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();

    public static async Task<VolumeMetadata?> Lookup(Store store, Series series, string apiKey, CancellationToken ct, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        if (!force)
        {
            var cached = store.Get<VolumeMetadata>("comicvine", series.Id);
            if (cached is not null) return cached.Title == "" ? null : cached;
        }

        var query = CleanTitle(series.Title);
        var url = "https://comicvine.gamespot.com/api/search/?format=json&resources=volume&limit=10&field_list=id,name,start_year,publisher,deck,description,site_detail_url"
            + "&api_key=" + Uri.EscapeDataString(apiKey) + "&query=" + Uri.EscapeDataString(query);
        using var response = await Fetch(url, ct);
        if (response is null) return null;
        // Failures are not cached, so a bad key or outage is retried on the next download.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new InvalidOperationException("ComicVine rejected the API key.");
        if (!response.IsSuccessStatusCode) return null;

        using var document = await ReadJson(response, ct);
        if (document is null) return null;
        var results = Array(document.RootElement, "results").ToList();
        // Only an exact name match is accepted; a loose match would attach the wrong series to the files.
        var wanted = Normalize(query);
        var match = results.FirstOrDefault(item => Normalize(Text(item, "name")) == wanted);
        VolumeMetadata metadata;
        if (match.ValueKind == JsonValueKind.Object)
        {
            var volumeId = match.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id) ? id : (int?)null;
            var credits = volumeId is int vid ? await FetchCredits(vid, apiKey, ct) : null;
            metadata = new VolumeMetadata(
                Title: Text(match, "name"),
                Summary: Plain(Text(match, "deck")) is { Length: > 0 } deck ? deck : Plain(Text(match, "description")),
                Publisher: match.TryGetProperty("publisher", out var pub) && pub.ValueKind == JsonValueKind.Object ? Text(pub, "name") : "",
                Year: int.TryParse(Text(match, "start_year"), out var year) ? year : null,
                Url: Text(match, "site_detail_url"),
                VolumeId: volumeId,
                Writers: Top(credits, "writer"), Pencillers: Top(credits, "penciller"), Inkers: Top(credits, "inker"),
                Colorists: Top(credits, "colorist"), CoverArtists: Top(credits, "cover"));
        }
        else metadata = new VolumeMetadata();
        store.Put("comicvine", series.Id, metadata);
        return metadata.Title == "" ? null : metadata;
    }

    // Person credits come per issue; one call for up to 100 issues gives the creators for the series.
    private static async Task<Dictionary<string, Dictionary<string, int>>?> FetchCredits(int volumeId, string apiKey, CancellationToken ct)
    {
        var url = "https://comicvine.gamespot.com/api/issues/?format=json&limit=100&field_list=person_credits&filter=volume:" + volumeId
            + "&api_key=" + Uri.EscapeDataString(apiKey);
        using var response = await Fetch(url, ct);
        if (response is null || !response.IsSuccessStatusCode) return null;
        using var document = await ReadJson(response, ct);
        if (document is null) return null;
        var counts = new Dictionary<string, Dictionary<string, int>>();
        foreach (var issue in Array(document.RootElement, "results"))
        {
            foreach (var person in Array(issue, "person_credits"))
            {
                var name = Text(person, "name");
                if (name.Length == 0) continue;
                foreach (var role in Text(person, "role").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var key = role.ToLowerInvariant();
                    if (!counts.TryGetValue(key, out var byName)) counts[key] = byName = new Dictionary<string, int>();
                    byName[name] = byName.GetValueOrDefault(name) + 1;
                }
            }
        }
        return counts;
    }

    // Top five names for a role; "cover" also matches "cover artist" style roles.
    private static string Top(Dictionary<string, Dictionary<string, int>>? credits, string role)
    {
        if (credits is null) return "";
        var names = credits.Where(entry => entry.Key == role || (role == "cover" && entry.Key.Contains("cover")))
            .SelectMany(entry => entry.Value)
            .GroupBy(pair => pair.Key).Select(group => (Name: group.Key, Count: group.Sum(pair => pair.Value)))
            .OrderByDescending(entry => entry.Count).Take(5).Select(entry => entry.Name);
        return string.Join(", ", names);
    }

    // Only reads a property when the element is an object and the property is an array; anything else is empty.
    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().ToList();
    }

    // Comic Vine sometimes answers with an HTML page (throttling or an outage). That is treated as a pause, not a crash.
    private static async Task<JsonDocument?> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!text.TrimStart().StartsWith('{'))
        {
            Gate.Pause(TimeSpan.FromMinutes(15));
            return null;
        }
        return JsonDocument.Parse(text);
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Plain(string html)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
