using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamFin.Manga;

// Genres, tags, description and staff from AniList's public manga data. No API key is needed.
public sealed record AniListMetadata(string Title = "", string Summary = "", int? Year = null, string Url = "", string Genres = "", string Tags = "", string Writers = "", string Artists = "");

public static class AniListManga
{
    private static readonly HttpClient Client = CreateClient();
    // AniList allows about 90 requests a minute; one call every 800 ms keeps well under that.
    private static readonly RateGate Gate = new(TimeSpan.FromMilliseconds(800));
    private const string Query = "query ($search: String) { Page(perPage: 5) { media(search: $search, type: MANGA, sort: SEARCH_MATCH) { siteUrl title { romaji english native } description(asHtml: false) startDate { year } genres tags { name rank isMediaSpoiler } staff(perPage: 25) { edges { role node { name { full } } } } } } }";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamFin-Jellyfin-Plugin/0.12");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public static async Task<AniListMetadata?> Lookup(Store store, Series series, CancellationToken ct, bool force = false)
    {
        if (!force)
        {
            var cached = store.Get<AniListMetadata>("anilist-manga", series.Id);
            if (cached is not null) return cached.Title == "" ? null : cached;
        }

        var query = ComicVine.CleanTitle(series.Title);
        var body = JsonSerializer.Serialize(new { query = Query, variables = new { search = query } });
        if (Gate.IsPaused) return null;
        await Gate.WaitAsync(ct);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("https://graphql.anilist.co", content, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            Gate.Pause(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            return null;
        }
        // Outages are not cached, so the next download tries again.
        if (!response.IsSuccessStatusCode) return null;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var media = document.RootElement.GetProperty("data").GetProperty("Page").GetProperty("media").EnumerateArray().ToList();
        var wanted = Normalize(query);
        var match = media.FirstOrDefault(item =>
        {
            var titles = item.GetProperty("title");
            return new[] { Text(titles, "romaji"), Text(titles, "english"), Text(titles, "native") }.Any(title => Normalize(title) == wanted);
        });

        var result = match.ValueKind == JsonValueKind.Object ? Parse(match) : new AniListMetadata();
        store.Put("anilist-manga", series.Id, result);
        return result.Title == "" ? null : result;
    }

    private static AniListMetadata Parse(JsonElement item)
    {
        var titles = item.GetProperty("title");
        var title = Text(titles, "english") is { Length: > 0 } english ? english : Text(titles, "romaji");
        var genres = item.TryGetProperty("genres", out var genreList) && genreList.ValueKind == JsonValueKind.Array
            ? string.Join(", ", genreList.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()!)) : "";
        // Spoiler tags and weak tags (rank under 60) are left out; the rest are the most relevant themes.
        var tags = item.TryGetProperty("tags", out var tagList) && tagList.ValueKind == JsonValueKind.Array
            ? string.Join(", ", tagList.EnumerateArray()
                .Where(t => t.TryGetProperty("rank", out var rank) && rank.ValueKind == JsonValueKind.Number && rank.GetInt32() >= 60)
                .Where(t => !(t.TryGetProperty("isMediaSpoiler", out var spoiler) && spoiler.ValueKind == JsonValueKind.True))
                .Select(t => Text(t, "name")).Where(name => name.Length > 0).Take(10)) : "";
        var (writers, artists) = Staff(item);
        var year = item.TryGetProperty("startDate", out var start) && start.TryGetProperty("year", out var yearElement) && yearElement.ValueKind == JsonValueKind.Number ? yearElement.GetInt32() : (int?)null;
        return new AniListMetadata(
            Title: title,
            Summary: Plain(Text(item, "description")),
            Year: year,
            Url: Text(item, "siteUrl"),
            Genres: genres,
            Tags: tags,
            Writers: writers,
            Artists: artists);
    }

    // AniList roles are free text such as "Story & Art", "Story" or "Art".
    private static (string Writers, string Artists) Staff(JsonElement item)
    {
        var writers = new List<string>();
        var artists = new List<string>();
        if (!item.TryGetProperty("staff", out var staff) || !staff.TryGetProperty("edges", out var edges)) return ("", "");
        foreach (var edge in edges.EnumerateArray())
        {
            var role = Text(edge, "role");
            var name = edge.TryGetProperty("node", out var node) && node.TryGetProperty("name", out var names) ? Text(names, "full") : "";
            if (name.Length == 0) continue;
            if (role.Contains("Story", StringComparison.OrdinalIgnoreCase)) writers.Add(name);
            if (role.Contains("Art", StringComparison.OrdinalIgnoreCase)) artists.Add(name);
        }
        return (string.Join(", ", writers.Distinct()), string.Join(", ", artists.Distinct()));
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Plain(string text) => Regex.Replace(Regex.Replace(text, "<[^>]+>", " "), @"\s+", " ").Trim();

    private static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
}

// Combines ComicVine (identity and credits) with AniList (genres, tags, description). Either source may be missing.
public static class MangaMetadata
{
    public static async Task<VolumeMetadata?> Resolve(Store store, Series series, string comicVineKey, CancellationToken ct, bool force = false)
    {
        var comic = await ComicVine.Lookup(store, series, comicVineKey, ct, force);
        AniListMetadata? anilist = null;
        try { anilist = await AniListManga.Lookup(store, series, ct, force); }
        catch (Exception ex) when (ex is not OperationCanceledException) { anilist = null; }

        if (anilist is null) return comic;
        var baseline = comic ?? new VolumeMetadata();
        return baseline with
        {
            Summary = baseline.Summary is { Length: > 0 } ? baseline.Summary : anilist.Summary,
            Year = baseline.Year ?? anilist.Year,
            Url = baseline.Url is { Length: > 0 } ? baseline.Url : anilist.Url,
            Writers = baseline.Writers is { Length: > 0 } ? baseline.Writers : anilist.Writers,
            Pencillers = baseline.Pencillers is { Length: > 0 } ? baseline.Pencillers : anilist.Artists,
            Genres = anilist.Genres,
            Tags = anilist.Tags
        };
    }
}
