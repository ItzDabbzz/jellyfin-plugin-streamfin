namespace StreamFin;

public sealed record SourceMatches(string Id, string Name, IReadOnlyList<SourceTitle> Titles, string? Error = null);
public sealed record AnimePlayback(string SourceKey, IReadOnlyList<ResolvedVideo> Videos);

public sealed class SourceRegistry(IEnumerable<IAnimeSource> providers)
{
    public static readonly string[] Order = ["zoko", "animegg", "animeheaven"];
    private readonly IAnimeSource[] ordered = providers.OrderBy(p => Array.IndexOf(Order, p.Id) is var i and >= 0 ? i : Order.Length).ToArray();
    private readonly IReadOnlyDictionary<string, IAnimeSource> sources = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
    public static bool IsEnabled(string id) => IsEnabled(id, Plugin.Instance?.Configuration);
    public static bool IsEnabled(string id, Configuration? config) => config is { Enabled: true } && id switch
    {
        "zoko" => config.ZokoEnabled,
        "animegg" => config.AnimeGgEnabled,
        "animeheaven" => config.AnimeHeavenEnabled,
        _ => false
    };
    public static bool AnyEnabled => Order.Any(IsEnabled);
    public static string CacheKey => string.Join('-', Order.Select(id => IsEnabled(id)));

    // Keep legacy AnimeGG keys byte-for-byte: changing them would orphan saved episode progress.
    public static string Key(string provider, string id) => provider == "animegg" ? id : provider + ":" + id;
    public static (string Provider, string Id) Split(string key)
    {
        var separator = key.IndexOf(':');
        return separator < 0 ? ("animegg", key) : (key[..separator], key[(separator + 1)..]);
    }
    public IAnimeSource Get(string key, out string id)
    {
        var parsed = Split(key);
        id = parsed.Id;
        if (!IsEnabled(parsed.Provider) || !sources.TryGetValue(parsed.Provider, out var source))
            throw new ProviderException("This provider is disabled or unavailable. Choose another provider under the title.");
        return source;
    }
    public Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string key, Anime anime, CancellationToken ct)
        => Get(key, out var id).EpisodesAsync(id, anime, ct);
    public Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string key, CancellationToken ct)
        => Get(key, out var id).ResolveAsync(id, ct);
    public string Name(string key) => Get(key, out _).Name;

    // Keep the selected episode's identity and watch state even when its provider cannot play it.
    // An alternate must have one exact title/year match and the same episode number.
    public async Task<AnimePlayback> ResolveWithFallbackAsync(Anime anime, decimal? number, string preferredKey,
        CancellationToken ct, Configuration? configuration = null)
    {
        var config = configuration ?? Plugin.Instance?.Configuration;
        var preferred = Split(preferredKey);
        if (!IsEnabled(preferred.Provider, config) || !sources.ContainsKey(preferred.Provider))
            throw new ProviderException("This provider is disabled or unavailable. Choose another provider under the title.");
        ct.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        var failures = new List<string>();
        foreach (var source in ordered.Where(p => IsEnabled(p.Id, config))
                     .OrderBy(p => p.Id == preferred.Provider ? 0 : 1))
        {
            if (source.Id != preferred.Provider && (number is null || config?.AnimeProviderFallback == false)) continue;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            attempt.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var episodeId = preferred.Id;
                if (source.Id != preferred.Provider)
                {
                    var match = await MatchAsync(source, anime, attempt.Token);
                    var names = anime.Aliases.Prepend(anime.Title).Select(AnimeGgSource.Normalize).ToHashSet();
                    var exact = match.Titles.Where(t => names.Contains(AnimeGgSource.Normalize(t.Title))
                        && (!t.Year.HasValue || t.Year == anime.Year)).ToArray();
                    if (exact.Length != 1) continue;
                    var episodes = await source.EpisodesAsync(exact[0].Id, anime, attempt.Token);
                    var matching = episodes.Where(e => e.Number == number).ToArray();
                    if (matching.Length != 1) continue;
                    episodeId = matching[0].Id;
                }
                var videos = await source.ResolveAsync(episodeId, attempt.Token);
                if (videos.Count == 0) throw new ProviderException("No playable video was returned.");
                return new(Key(source.Id, episodeId), videos);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is ProviderException or HttpRequestException
                or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or OperationCanceledException)
            {
                failures.Add(source.Name + ": " + (ex is ProviderException ? ex.Message : "temporarily unavailable"));
                if (budget.IsCancellationRequested) break;
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new ProviderException("No enabled provider could play this episode. " + string.Join(" ", failures));
    }

    public async Task<IReadOnlyList<SourceMatches>> SearchAsync(Anime anime, CancellationToken ct)
        => await Task.WhenAll(ordered.Where(p => IsEnabled(p.Id)).Select(p => MatchAsync(p, anime, ct)));

    public static async Task<SourceMatches> MatchAsync(IAnimeSource source, Anime anime, CancellationToken ct)
    {
        if (source is IDirectAnimeSource direct)
            return direct.Direct(anime) is { } title ? new(source.Id, source.Name, [title])
                : new(source.Id, source.Name, [], "No MyAnimeList entry for this title.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var candidates = new List<SourceTitle>();
            foreach (var title in anime.Aliases.Prepend(anime.Title).Distinct().Take(3))
            {
                var response = await source.SearchAsync(title, timeout.Token);
                candidates.AddRange(response);
                if (candidates.Any(c => AnimeGgSource.Normalize(c.Title) == AnimeGgSource.Normalize(title))) break;
            }
            var names = anime.Aliases.Prepend(anime.Title).Select(AnimeGgSource.Normalize).ToHashSet();
            // Some sites put unrelated promoted titles on empty/error search pages.
            // Never present one of those as a match for the requested catalog title.
            var unique = candidates.DistinctBy(c => c.Id).Where(c => names.Any(name =>
            {
                var candidate = AnimeGgSource.Normalize(c.Title);
                return candidate == name || (Math.Min(name.Length, candidate.Length) >= 4
                    && (candidate.Contains(name, StringComparison.Ordinal) || name.Contains(candidate, StringComparison.Ordinal)));
            })).ToArray();
            var exact = unique.Where(c => names.Contains(AnimeGgSource.Normalize(c.Title)) && (!c.Year.HasValue || c.Year == anime.Year)).ToArray();
            return new(source.Id, source.Name, exact.Length == 1 ? exact : unique);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or OperationCanceledException)
        {
            return new(source.Id, source.Name, [], "Temporarily unavailable. Try the other provider or refresh the catalog cache.");
        }
    }
}
