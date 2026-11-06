using StreamFin;

internal static class BrowseCacheChecks
{
    public static async Task RunAsync()
    {
        void Check(bool valid, string name)
        {
            if (!valid) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        var now = DateTimeOffset.UtcNow;
        using var cache = new BrowseCache<string>(value => value.Length > 0, () => now);
        var lifetime = TimeSpan.FromMinutes(30);
        var calls = 0;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> Load(CancellationToken _) { calls++; return completion.Task; }
        using var cancelled = new CancellationTokenSource();
        var first = cache.GetAsync("title", lifetime, Load, cancelled.Token);
        var second = cache.GetAsync("title", lifetime, Load, default);
        cancelled.Cancel();
        try { await first; throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        completion.SetResult("episodes");
        Check(await second == "episodes" && calls == 1, "duplicate browse requests share work and one cancelled viewer cannot cancel another");
        Check(await cache.GetAsync("title", lifetime, Load, default) == "episodes" && calls == 1,
            "repeat browsing reuses successful metadata");

        now += TimeSpan.FromHours(1);
        var refresh = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> Refresh(CancellationToken _) { calls++; return refresh.Task; }
        var stale = cache.GetAsync("title", lifetime, Refresh, default);
        Check(stale.IsCompletedSuccessfully && await stale == "episodes", "stale metadata returns immediately while refresh runs");
        Check(await cache.GetAsync("title", lifetime, Refresh, default) == "episodes" && calls == 2,
            "stale viewers share one background refresh");
        refresh.SetException(new HttpRequestException("offline"));
        // Wait for the refresh continuation, with a bounded yield loop rather than a network call.
        for (var attempt = 0; attempt < 100; attempt++) await Task.Delay(1);
        var retries = 0;
        Check(await cache.GetAsync("title", lifetime, _ => { retries++; return Task.FromResult(""); }, default) == "episodes" && retries == 0,
            "failed refresh keeps good metadata and backs off");
        var emptyCalls = 0;
        Task<string> Empty(CancellationToken _) { emptyCalls++; return Task.FromResult(""); }
        await cache.GetAsync("empty", lifetime, Empty, default);
        await cache.GetAsync("empty", lifetime, Empty, default);
        Check(emptyCalls == 2, "empty provider responses do not poison future browsing");
        Check(await cache.GetAsync("revision2/title", lifetime, _ => Task.FromResult("new episodes"), default) == "new episodes",
            "cache revision keys bypass previous results");
        var failures = 0;
        Task<string> Failing(CancellationToken _) { failures++; throw new HttpRequestException("offline"); }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try { await cache.GetAsync("cold-failure", lifetime, Failing, default); throw new Exception("Provider failure ignored"); }
            catch (HttpRequestException) { }
        }
        Check(failures == 2, "cold request failures release shared requests and allow a retry");
        using var pages = new BrowseCache<FilmPage>(page => page.Items.Count > 0);
        var movie = new Film(1, FilmKind.Movie, "Example", 2026, "", null, 90, false);
        var pageCalls = 0;
        Task<FilmPage> Page(CancellationToken _) { pageCalls++; return Task.FromResult(new FilmPage([movie], true, 100)); }
        await pages.GetAsync("movies/1", lifetime, Page, default);
        var reused = await pages.GetAsync("movies/1", lifetime, Page, default);
        await pages.GetAsync("movies/2", lifetime, Page, default);
        Check(pageCalls == 2 && reused.TotalCount == 100 && reused.HasNext,
            "cached catalog pages retain pagination metadata and separate page identities");
    }
}
