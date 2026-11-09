using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

public enum FilmKind { Movie, Tv }
public sealed record Film(int Id, FilmKind Kind, string Title, int? Year, string Overview, string? Image, int? Runtime, bool IsAdult,
    double? VoteAverage = null, int[]? GenreIds = null, DateTime? PremiereDate = null,
    string? ImdbId = null, string? Certification = null, string[]? Studios = null, string[]? Cast = null, string[]? Directors = null,
    string? Backdrop = null, string? Logo = null, IReadOnlyDictionary<string, string>? Photos = null);
public sealed record FilmPage(IReadOnlyList<Film> Items, bool HasNext, int TotalCount = 0);
public sealed record FilmSeason(int Number, string Name, int Episodes, string? Image);
public sealed record FilmEpisode(int Season, int Number, string Name, string Overview, string? Image, int? Runtime, DateTime? Aired, int? TmdbId = null, double? VoteAverage = null);

public sealed class TmdbCatalog(SafeHttp http, CatalogCache store)
{
    private readonly MemoryCache cache = store.Collection("films-details");
    private readonly BrowseCache<object> browsing = new(store, "films", value => value switch
        { FilmPage page => page.Items.Count > 0, System.Collections.ICollection items => items.Count > 0, _ => true });
    private readonly RateWindow budget = new(40, TimeSpan.FromSeconds(10));
    private static readonly Uri Endpoint = new("https://api.themoviedb.org/3/");
    private const string Images = "https://image.tmdb.org/t/p/w500";
    // Wide artwork for backdrops and transparent title art for logos: both are served to the client at
    // poster-card size, so smaller variants keep the channel cache and the image cache honest.
    private const string Backdrops = "https://image.tmdb.org/t/p/w780";
    private const string Logos = "https://image.tmdb.org/t/p/w300";
    // Movie and TV ids share one table: the ids that differ (12 vs 10759, 878 vs 10765, 10752 vs 10768)
    // never mean two different genres, and every shared id means the same one.
    private static readonly IReadOnlyDictionary<int, string> GenreNames = new Dictionary<int, string>
    {
        [28] = "Action", [12] = "Adventure", [16] = "Animation", [35] = "Comedy", [80] = "Crime", [99] = "Documentary",
        [18] = "Drama", [10751] = "Family", [14] = "Fantasy", [36] = "History", [27] = "Horror", [10402] = "Music",
        [9648] = "Mystery", [10749] = "Romance", [878] = "Science Fiction", [10770] = "TV Movie", [53] = "Thriller",
        [10752] = "War", [37] = "Western", [10759] = "Action & Adventure", [10762] = "Kids", [10763] = "News",
        [10764] = "Reality", [10765] = "Sci-Fi & Fantasy", [10766] = "Soap", [10767] = "Talk", [10768] = "War & Politics"
    };
    private int Revision => Plugin.Instance?.Configuration.CacheRevision ?? 0;
    private static string Key => Plugin.Instance?.Configuration.TmdbApiKey?.Trim() ?? "";
    private TimeSpan Lifetime => TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440));
    public static bool HasKey => IsValidKey(Key);
    public static bool IsValidKey(string key) => Regex.IsMatch(key, @"\A(?:[a-f0-9]{32}|eyJ[A-Za-z0-9_.-]{20,1000})\z", RegexOptions.None, TimeSpan.FromSeconds(1));

    public Task<FilmPage> BrowseAsync(string category, int page, CancellationToken ct, string? search = null)
    {
        if (page is < 1 or > 500) throw new ProviderException("Invalid catalog page.");
        if (category == "search" && (string.IsNullOrWhiteSpace(search) || search.Trim().Length is < 2 or > 100)) return Task.FromResult(new FilmPage([], false));
        var path = category switch
        {
            "movies" => "movie/popular?",
            "tv" => "tv/popular?",
            "trending" => "trending/all/week?",
            "search" => "search/multi?query=" + Uri.EscapeDataString(search!.Trim()) + "&",
            _ => throw new ProviderException("Invalid catalog page.")
        };
        return CachedAsync($"{category}/{page}/{search?.Trim().ToLowerInvariant()}", category == "search" ? TimeSpan.FromMinutes(30) : Lifetime, async token =>
        {
            using var document = await QueryAsync($"{path}include_adult=false&language=en-US&page={page}", token);
            var root = document.RootElement;
            var fallback = category == "movies" ? FilmKind.Movie : FilmKind.Tv;
            var items = root.GetProperty("results").EnumerateArray()
                .Select(node => Parse(node, category is "movies" or "tv" ? fallback : null)).OfType<Film>().Where(f => !f.IsAdult).ToArray();
            foreach (var film in items) Store($"{film.Kind}/{film.Id}", film, Lifetime);
            var hasNext = root.TryGetProperty("total_pages", out var total) && total.TryGetInt32(out var pages) && page < pages;
            return new FilmPage(items, hasNext, root.TryGetProperty("total_results", out var resultCount) && resultCount.TryGetInt32(out var count)
                ? count : (page - 1) * 20 + items.Length + (hasNext ? 1 : 0));
        }, ct);
    }

    public Task<Film> GetAsync(FilmKind kind, int id, CancellationToken ct) => id <= 0 ? throw new ProviderException("Invalid title identifier.")
        : CachedAsync($"{kind}/{id}", Lifetime, async token =>
        {
            using var document = await QueryAsync($"{(kind == FilmKind.Movie ? "movie" : "tv")}/{id}?language=en-US", token);
            return Parse(document.RootElement, kind) ?? throw new ProviderException("This title is unavailable.");
        }, ct);

    public Task<IReadOnlyList<FilmSeason>> SeasonsAsync(int id, CancellationToken ct) => CachedAsync<IReadOnlyList<FilmSeason>>($"seasons/{id}", Lifetime, async token =>
    {
        using var document = await QueryAsync($"tv/{id}?language=en-US", token);
        // Season 0 holds specials, which streaming sites rarely carry under the same numbering.
        return document.RootElement.GetProperty("seasons").EnumerateArray()
            .Select(s => new FilmSeason(s.GetProperty("season_number").GetInt32(), Text(s, "name") ?? "Season", Number(s, "episode_count") ?? 0, Image(s, "poster_path")))
            .Where(s => s.Number > 0 && s.Episodes > 0).OrderBy(s => s.Number).ToArray();
    }, ct);

    public Task<IReadOnlyList<FilmEpisode>> EpisodesAsync(int id, int season, CancellationToken ct) => season is < 1 or > 500
        ? throw new ProviderException("Invalid season.")
        : CachedAsync<IReadOnlyList<FilmEpisode>>($"season/{id}/{season}", Lifetime, async token =>
        {
            using var document = await QueryAsync($"tv/{id}/season/{season}?language=en-US", token);
            // Unaired episodes are listed in advance; nothing can stream them yet.
            return document.RootElement.GetProperty("episodes").EnumerateArray()
                .Select(e => new FilmEpisode(season, e.GetProperty("episode_number").GetInt32(), Text(e, "name") ?? "Episode", Text(e, "overview") ?? "",
                    Image(e, "still_path"), Number(e, "runtime"),
                    DateTime.TryParse(Text(e, "air_date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var aired) ? aired : null))
                .Where(e => e.Aired is null || e.Aired <= DateTime.UtcNow.AddDays(1)).OrderBy(e => e.Number).ToArray();
        }, ct);

    private static string DetailKey(Film film) => $"details/{film.Kind}/{film.Id}";

    // Details are cache-only on the request path. The warm task fills this cache at startup,
    // so a browse never waits on twenty extra TMDB calls.
    public bool TryDetails(Film film, out Film detailed)
    {
        var slot = $"{Revision}/{DetailKey(film)}";
        if (cache.TryGetValue(slot, out detailed!) && detailed is not null) return true;
        detailed = film;
        return false;
    }

    public Film WithCachedDetails(Film film) => TryDetails(film, out var detailed) ? detailed : film;

    // Cache-only lookup used where an id is all that is known (ChannelManager-created items).
    public Film? Cached(FilmKind kind, int id) => cache.TryGetValue<Film>($"{Revision}/{kind}/{id}", out var found) ? found : null;

    public async Task WarmDetailsAsync(Film film, CancellationToken ct)
    {
        try
        {
            await CachedAsync(DetailKey(film), TimeSpan.FromHours(24), async token =>
            {
                var path = film.Kind == FilmKind.Movie
                    ? $"movie/{film.Id}?language=en-US&append_to_response=external_ids,credits,release_dates,images&include_image_language=en,null"
                    : $"tv/{film.Id}?language=en-US&append_to_response=external_ids,aggregate_credits,content_ratings,images&include_image_language=en,null";
                using var document = await QueryAsync(path, token);
                return Detailed(film, document.RootElement);
            }, ct);
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.Json.JsonException or OperationCanceledException) { }
    }

    private static Film Detailed(Film film, JsonElement root)
    {
        var imdb = root.TryGetProperty("external_ids", out var external) && external.ValueKind == JsonValueKind.Object ? Text(external, "imdb_id") : null;
        var studios = Names(root, film.Kind == FilmKind.Movie ? "production_companies" : "networks");
        var cast = Cast(root);
        var directors = film.Kind == FilmKind.Movie ? Crew(root, "Director") : Names(root, "created_by");
        var certification = Certification(root, film.Kind);
        return film with
        {
            ImdbId = imdb is { Length: > 0 } ? imdb : film.ImdbId,
            Studios = studios.Length > 0 ? studios : film.Studios,
            Cast = cast.Length > 0 ? cast : film.Cast,
            Directors = directors.Length > 0 ? directors : film.Directors,
            Certification = certification ?? film.Certification,
            VoteAverage = Vote(root) ?? film.VoteAverage,
            Backdrop = ImageAt(Backdrops, root, "backdrop_path") ?? film.Backdrop,
            Logo = Logo(root) ?? film.Logo,
            Photos = Photos(root) ?? film.Photos
        };
    }

    private static string[] Names(JsonElement root, string property) =>
        root.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(value => Text(value, "name")).OfType<string>().Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).Take(8).ToArray()
            : [];

    private static JsonElement Credits(JsonElement root) =>
        root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object ? credits
        : root.TryGetProperty("aggregate_credits", out var aggregate) && aggregate.ValueKind == JsonValueKind.Object ? aggregate
        : default;

    private static string[] Cast(JsonElement root)
    {
        var credits = Credits(root);
        return credits.ValueKind == JsonValueKind.Object && credits.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.Array
            ? cast.EnumerateArray().Select(value => Text(value, "name")).OfType<string>().Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).Take(8).ToArray()
            : [];
    }

    private static string[] Crew(JsonElement root, string job)
    {
        var credits = Credits(root);
        return credits.ValueKind == JsonValueKind.Object && credits.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array
            ? crew.EnumerateArray().Where(value => Text(value, "job") == job).Select(value => Text(value, "name")).OfType<string>()
                .Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).Take(4).ToArray()
            : [];
    }

    private static string? Certification(JsonElement root, FilmKind kind)
    {
        var property = kind == FilmKind.Movie ? "release_dates" : "content_ratings";
        if (!root.TryGetProperty(property, out var container) || container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        foreach (var country in results.EnumerateArray())
        {
            if (Text(country, "iso_3166_1") != "US") continue;
            if (kind == FilmKind.Movie && country.TryGetProperty("release_dates", out var dates) && dates.ValueKind == JsonValueKind.Array)
                return dates.EnumerateArray().Select(date => Text(date, "certification")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value != "NR");
            return Text(country, "rating") is { Length: > 0 } rating && rating != "NR" ? rating : null;
        }
        return null;
    }

    private async Task<T> CachedAsync<T>(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        var slot = $"{Revision}/{key}";
        if (cache.TryGetValue<T>(slot, out var found)) return found!;
        return (T)await browsing.GetAsync(slot, lifetime, async token =>
        {
            var value = await load(token);
            Store(key, value, lifetime);
            return value;
        }, ct);
    }
    private void Store<T>(string key, T value, TimeSpan lifetime)
    {
        cache.Set($"{Revision}/{key}", value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });
        cache.Set("stale/" + key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
    }

    private async Task<JsonDocument> QueryAsync(string path, CancellationToken ct)
    {
        var key = Key;
        if (!IsValidKey(key)) throw new ProviderException("Add a TMDB API key under Dashboard → Plugins → JellySteamHub to use Movies & TV.");
        var wait = budget.Reserve(DateTime.UtcNow, TimeSpan.FromSeconds(5)) ?? throw new RateLimitedException("TMDB is busy. Please try again shortly.", null);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        var bearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        var uri = new Uri(Endpoint, bearer ? path : path + (path.Contains('?') ? "&" : "?") + "api_key=" + key);
        string text;
        try { text = await http.TextAsync(uri, ct, headers: bearer ? new Dictionary<string, string> { ["Authorization"] = "Bearer " + key } : null); }
        catch (ProviderException ex) when (ex.Message.Contains("HTTP 401", StringComparison.Ordinal))
        { throw new ProviderException("TMDB rejected the API key. Check it under Dashboard → Plugins → JellySteamHub."); }
        return JsonDocument.Parse(text);
    }

    // TMDB text is plain; angle brackets are dropped so no client can mistake it for markup.
    private static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString()?.Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal) is { Length: > 0 } s ? s : null;
    private static int? Number(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
    private static string? Image(JsonElement e, string key) => ImageAt(Images, e, key);
    private static string? ImageAt(string prefix, JsonElement e, string key) => Text(e, key) is { } path && Regex.IsMatch(path, @"\A/[A-Za-z0-9_-]+\.(?:jpg|png|webp)\z", RegexOptions.None, TimeSpan.FromSeconds(1)) ? prefix + path : null;
    // Title art: prefer an English logo, then the textless one every language falls back to.
    private static string? Logo(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object
            || !images.TryGetProperty("logos", out var logos) || logos.ValueKind != JsonValueKind.Array) return null;
        var ranked = logos.EnumerateArray()
            .Select(value => (Url: ImageAt(Logos, value, "file_path"), Language: Text(value, "iso_639_1"),
                Score: value.TryGetProperty("vote_average", out var vote) && vote.TryGetDouble(out var score) ? score : 0))
            .Where(value => value.Url is not null)
            .OrderByDescending(value => value.Language is "en" ? 1 : 0).ThenByDescending(value => value.Score)
            .FirstOrDefault();
        return ranked.Url;
    }
    // Profile pictures keyed by name, so a person card has a face without Jellyfin's own metadata providers.
    private static IReadOnlyDictionary<string, string>? Photos(JsonElement root)
    {
        var credits = Credits(root);
        if (credits.ValueKind != JsonValueKind.Object) return null;
        var photos = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in new[] { "cast", "crew" })
        {
            if (!credits.TryGetProperty(group, out var people) || people.ValueKind != JsonValueKind.Array) continue;
            foreach (var person in people.EnumerateArray())
            {
                if (Text(person, "name") is not { } name) continue;
                if (Image(person, "profile_path") is not { } photo) continue;
                photos.TryAdd(name, photo);
            }
        }
        return photos.Count > 0 ? photos : null;
    }
    private static double? Vote(JsonElement e) => e.TryGetProperty("vote_average", out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var vote) ? vote : null;
    // List responses carry genre_ids; a single-title response carries genres:[{id,name}] instead.
    private static int[] GenreIds(JsonElement e)
    {
        if (e.TryGetProperty("genre_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            return ids.EnumerateArray().Select(id => id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var n) ? n : 0)
                .Where(n => n > 0).Distinct().ToArray();
        if (e.TryGetProperty("genres", out var named) && named.ValueKind == JsonValueKind.Array)
            return named.EnumerateArray().Select(genre => Number(genre, "id") ?? 0).Where(n => n > 0).Distinct().ToArray();
        return [];
    }

    // Unknown ids are dropped so a future TMDB genre never surfaces in Jellyfin as a number.
    public static IReadOnlyList<string> Genres(Film film)
    {
        var ids = film.GenreIds;
        if (ids is null || ids.Length == 0) return [];
        return ids.Select(id => GenreNames.TryGetValue(id, out var name) ? name : null).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
    }

    // TMDB scores 0-10, the scale Jellyfin stores, and 0 means "not rated yet".
    public static float? CommunityRating(Film film) => film.VoteAverage is > 0 and <= 10 ? (float)film.VoteAverage.Value : null;

    public static Film? Parse(JsonElement node, FilmKind? kind)
    {
        kind ??= Text(node, "media_type") switch { "movie" => FilmKind.Movie, "tv" => FilmKind.Tv, _ => null };
        if (kind is not { } k || Number(node, "id") is not { } id) return null;
        var title = Text(node, k == FilmKind.Movie ? "title" : "name");
        if (title is null) return null;
        var date = Text(node, k == FilmKind.Movie ? "release_date" : "first_air_date");
        int? year = date is { Length: >= 4 } && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
        var premiere = DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var released) ? (DateTime?)released : null;
        int? runtime = Number(node, "runtime") ?? (node.TryGetProperty("episode_run_time", out var runs) && runs.ValueKind == JsonValueKind.Array
            && runs.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Number } first && first.TryGetInt32(out var run) ? run : null);
        return new Film(id, k, title, year, Text(node, "overview") ?? "", Image(node, "poster_path"), runtime,
            node.TryGetProperty("adult", out var adult) && adult.ValueKind == JsonValueKind.True,
            Vote(node), GenreIds(node), premiere, Backdrop: ImageAt(Backdrops, node, "backdrop_path"));
    }
}
