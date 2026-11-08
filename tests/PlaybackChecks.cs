using StreamFin;

internal static class PlaybackChecks
{
    public static async Task RunAsync()
    {
        await NavigationAsync();
        var anime = new Anime(1, "Example", [], 2026, "", null, 24, false, [], 10, 12);
        var config = new Configuration { Enabled = true };
        var calls = new List<string>();
        var unavailable = new TestSource("zoko", calls, fail: true);
        var working = new TestSource("animegg", calls);
        var registry = new SourceRegistry([unavailable, working], new CatalogCache());
        var result = await registry.ResolveWithFallbackAsync(anime, 2, "zoko:10-2", default, config);
        Check(result.SourceKey == "episode-2" && result.Videos.Count == 1
            && calls.SequenceEqual(["zoko:resolve", "animegg:search", "animegg:episodes", "animegg:resolve"]),
            "missing subtitles fall back to the same episode on another enabled provider");

        calls.Clear();
        await registry.ResolveWithFallbackAsync(anime, 2, "zoko:10-2", default, config);
        Check(calls.SequenceEqual(["zoko:resolve", "animegg:resolve"]),
            "repeat playback reuses provider matches and episodes but resolves fresh playback URLs");

        calls.Clear();
        result = await registry.ResolveWithFallbackAsync(anime, 2, "episode-2", default, config);
        Check(result.SourceKey == "episode-2" && calls.SequenceEqual(["animegg:resolve"]), "working selected provider is used without searching others");

        calls.Clear();
        await RejectAsync(() => registry.ResolveWithFallbackAsync(anime, 2, "zoko:10-2", default,
            new Configuration { Enabled = true, AnimeGgEnabled = false }), "disabled providers are never used for fallback");
        Check(calls.SequenceEqual(["zoko:resolve"]), "disabled provider receives no requests");

        calls.Clear();
        var ambiguous = new SourceRegistry([unavailable, new TestSource("animegg", calls, ambiguous: true)], new CatalogCache());
        await RejectAsync(() => ambiguous.ResolveWithFallbackAsync(anime, 2, "zoko:10-2", default, config), "ambiguous title matches are not guessed");
        Check(!calls.Contains("animegg:resolve"), "ambiguous matches never start playback");

        calls.Clear();
        await RejectAsync(() => registry.ResolveWithFallbackAsync(anime, null, "zoko:10-2", default, config), "unknown episode numbers never become a different episode");
        Check(calls.SequenceEqual(["zoko:resolve"]), "unknown episode skips fallback searches");

        calls.Clear();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await registry.ResolveWithFallbackAsync(anime, 2, "zoko:10-2", cancelled.Token, config); throw new Exception("Cancellation was swallowed"); }
        catch (OperationCanceledException) { Check(calls.Count == 0, "cancelled playback does not call providers"); }

        calls.Clear();
        var wrongEpisode = new SourceRegistry([unavailable, new TestSource("animegg", calls, number: 3)], new CatalogCache());
        await RejectAsync(() => wrongEpisode.ResolveWithFallbackAsync(anime, 2, "zoko:10-2", default, config), "fallback requires the exact episode number");
        Check(!calls.Contains("animegg:resolve"), "wrong episode is never resolved");
    }

    private static async Task NavigationAsync()
    {
        Check(ChannelNavigation.IsNavigationRequest("/Users/user/Items/series")
            && ChannelNavigation.IsNavigationRequest("/Shows/series/Episodes")
            && !ChannelNavigation.IsNavigationRequest("/Users/user/Items/series/Images/Primary"),
            "legacy item-detail requests hydrate channel children before main-card Play");
        var series = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), ExternalId = "title/popular/1" };
        var first = new MediaBrowser.Controller.Entities.TV.Season { Id = Guid.NewGuid(), ExternalId = "source/popular/1/zoko:10" };
        var second = new MediaBrowser.Controller.Entities.TV.Season { Id = Guid.NewGuid(), ExternalId = "source/popular/1/show" };
        var episode = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), ExternalId = "episode/popular/1/episode-2" };
        var alternate = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), ParentId = first.Id, IndexNumber = 1, ParentIndexNumber = 1 };
        episode.ParentId = second.Id;
        episode.IndexNumber = 1;
        episode.ParentIndexNumber = 1;
        Check(ChannelNavigation.SourceEpisodes([alternate, episode], second.Id).Select(e => e.Id).SequenceEqual([episode.Id]),
            "provider playback uses physical source membership even when both are season one");
        var longSeries = Enumerable.Range(1, 1200).Reverse().Select(number => new MediaBrowser.Controller.Entities.TV.Episode
            { Id = Guid.NewGuid(), ParentId = second.Id, IndexNumber = number }).Append(alternate);
        var sourceQueue = ChannelNavigation.SourceEpisodes(longSeries, second.Id);
        Check(sourceQueue.Count == 1200 && sourceQueue.Select(e => e.IndexNumber).SequenceEqual(Enumerable.Range(1, 1200).Select(number => (int?)number)),
            "long source queues retain every episode in episode order without provider duplicates");
        var loaded = new List<Guid>();
        Task<IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem>> Load(MediaBrowser.Controller.Entities.Folder folder)
        {
            loaded.Add(folder.Id);
            return Task.FromResult<IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem>>(folder == series ? [first, second] : folder == second ? [episode] : []);
        }
        var chosen = await ChannelNavigation.PopulateAsync(series, Load, _ => Task.FromResult(true), descendants: true, singleSource: true, default);
        Check(chosen == second.Id && loaded.SequenceEqual([series.Id, first.Id, second.Id]), "main-card Play discovers episodes and skips empty anime sources");
        loaded.Clear();
        await ChannelNavigation.PopulateAsync(series, Load, _ => Task.FromResult(true), descendants: false, singleSource: true, default);
        Check(loaded.SequenceEqual([series.Id]), "opening a series does not fetch all episode lists");
        loaded.Clear();
        await ChannelNavigation.PopulateAsync(first, Load, _ => Task.FromResult(true), descendants: true, singleSource: true, default);
        Check(loaded.SequenceEqual([first.Id]), "explicit source selection does not load a different source");
        loaded.Clear();
        await ChannelNavigation.PopulateAsync(series, Load, item => Task.FromResult(item != first), descendants: true, singleSource: true, default);
        Check(loaded.SequenceEqual([series.Id, second.Id]), "hidden source folders are not populated");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    private static async Task RejectAsync(Func<Task<AnimePlayback>> action, string message)
    {
        try { await action(); }
        catch (ProviderException) { Check(true, message); return; }
        throw new Exception("FAIL: " + message);
    }

    private sealed class TestSource(string id, List<string> calls, bool fail = false, bool ambiguous = false, decimal number = 2) : IAnimeSource
    {
        public string Id => id;
        public string Name => id;
        public Task<IReadOnlyList<SourceTitle>> SearchAsync(string title, CancellationToken ct)
        {
            calls.Add(id + ":search");
            return Task.FromResult<IReadOnlyList<SourceTitle>>(ambiguous
                ? [new("one", "Example", 2026), new("two", "Example", 2026)] : [new("one", "Example", 2026)]);
        }
        public Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string seriesId, Anime anime, CancellationToken ct)
        {
            calls.Add(id + ":episodes");
            return Task.FromResult<IReadOnlyList<SourceEpisode>>([new("episode-2", number, "Episode")]);
        }
        public Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string episodeId, CancellationToken ct)
        {
            calls.Add(id + ":resolve");
            if (fail) throw new ProviderException("No English subtitles");
            return Task.FromResult<IReadOnlyList<ResolvedVideo>>([new(new("https://example.org/video.mp4"), "1080p", new Dictionary<string, string>(), true)]);
        }
    }
}
