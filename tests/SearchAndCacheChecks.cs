using Jellyfin.Data.Enums;
using StreamFin;

internal static class SearchAndCacheChecks
{
    public static void Run()
    {
        void Check(bool valid, string name)
        {
            if (!valid) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }

        Check(StreamableSearch.IsEnabled(new Configuration { SearchResultsEnabled = false }) == false
            && StreamableSearch.IsEnabled(new Configuration()) && StreamableSearch.IsEnabled(null),
            "the search toggle turns online results off without touching the channels");
        Check(CatalogBrowser.Kinds(false, [], [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.TvChannel]).Length == 0,
            "a Videos row that excludes movies gets no online titles");
        Check(CatalogBrowser.Kinds(false, [BaseItemKind.LiveTvProgram], []).Length == 0,
            "a Programs row never gets movies or shows");
        Check(CatalogBrowser.Kinds(false, [], []).SequenceEqual([BaseItemKind.Movie, BaseItemKind.Series]),
            "a general search defaults to movies and shows");
        Check(CatalogBrowser.Kinds(true, [BaseItemKind.Series], []).SequenceEqual([BaseItemKind.Series]),
            "an anime catalog only returns series");
        Check(StreamableSearch.Mark(null).SequenceEqual([StreamableSearch.Tag, StreamableSearch.PluginTag]),
            "a found title gets both the streamable and plugin tags");
        Check(StreamableSearch.Mark([SearchHistory.HiddenTag, "Drama", "StreamFin", "Streamable", "Drama"])
                .SequenceEqual([StreamableSearch.Tag, StreamableSearch.PluginTag, "Drama"]),
            "search tags drop the clear marker, duplicates and stale copies while keeping other tags");

        using var store = new CatalogCache();
        store.Store("anime", "popular/1", "page", TimeSpan.FromMinutes(30));
        Check(store.TryGet<string>("anime", "popular/1", out var page) && page == "page", "a stored catalog page is served from memory");
        Check(!store.TryGet<string>("anime", "popular/2", out _), "an unrelated key is a miss");
        var status = store.Status();
        Check(status.Collections.Single(collection => collection.Name == "anime") is { Entries: 1, Capacity: 900 },
            "each category reports its own entries and budget");
        Check(status is { Entries: 1, PageHits: 1, PageMisses: 1 }, "cache totals count page hits and misses");
        Check(store.Status().Collections.All(collection => collection.Entries <= collection.Capacity),
            "no category reports more entries than its budget");

        var generation = store.Generation;
        store.Clear();
        Check(!store.TryGet<string>("anime", "popular/1", out _) && store.Generation > generation && store.ClearedAt is not null,
            "clearing empties every collection, bumps the generation and records when it happened");
        Check(store.Status() is { Entries: 0, PageHits: 0, PageMisses: 1 } && store.Status().Collections.Single(c => c.Name == "anime").Entries == 0,
            "clearing resets the counters and leaves the category budgets in place");

        // A request that was already out when Clear landed must not put its old answer back.
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browse = new BrowseCache<string>(store, "anime", value => value.Length > 0);
        var pending = browse.GetAsync("late", TimeSpan.FromMinutes(30), _ => gate.Task, default);
        store.Clear();
        gate.SetResult("old");
        Check(pending.Result == "old", "a request that started before a clear still answers its own viewer");
        var reloads = 0;
        var fresh = browse.GetAsync("late", TimeSpan.FromMinutes(30), _ => { reloads++; return Task.FromResult("new"); }, default);
        Check(fresh.Result == "new" && reloads == 1, "a cleared page is fetched again instead of keeping the pre-clear answer");
    }
}
