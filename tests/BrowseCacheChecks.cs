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
    }
}
