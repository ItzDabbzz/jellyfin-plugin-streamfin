using System.Collections.Concurrent;

namespace StreamFin;

// Metadata only: never retain signed playback URLs. A caller leaving the page must
// not cancel the shared request for everyone else opening the same title.
public sealed class BrowseCache<T>(CatalogCache store, string collection, Func<T, bool> usable, Func<DateTimeOffset>? clock = null)
{
    private sealed record Entry(T Value, DateTimeOffset FreshUntil, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> pending = new();
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public async Task<T> GetAsync(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (store.TryGet(collection, key, out Entry? saved) && saved is not null)
        {
            if (saved.FreshUntil <= Now)
            {
                store.MarkStale();
                _ = RefreshAsync(key, lifetime, load, saved);
            }
            return saved.Value;
        }
        return await RefreshAsync(key, lifetime, load, null).WaitAsync(ct);
    }

    private Task<T> RefreshAsync(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, Entry? saved) =>
        pending.GetOrAdd(key, _ => new Lazy<Task<T>>(() => LoadAsync(key, lifetime, load, saved))).Value;

    private async Task<T> LoadAsync(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, Entry? saved)
    {
        // A clear that lands while this request is out wins: the old answer is returned to this viewer only.
        var generation = store.Generation;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await load(timeout.Token);
            if (usable(result))
            {
                if (generation == store.Generation) Store(key, new Entry(result, Now + lifetime, DateTimeOffset.UtcNow.AddHours(24)));
            }
            else if (saved is not null) return Keep(key, saved, generation);
            return result;
        }
        catch (Exception) when (saved is not null)
        {
            // Background refresh failures must be observed, and must not hammer a
            // failing provider or extend the last successful result's 24-hour life.
            return Keep(key, saved, generation);
        }
        finally { pending.TryRemove(key, out _); }
    }

    private T Keep(string key, Entry saved, long generation)
    {
        if (generation == store.Generation) Store(key, saved with { FreshUntil = Now.AddSeconds(30) });
        return saved.Value;
    }

    private void Store(string key, Entry value) =>
        store.Store(collection, key, value, value.ExpiresAt - DateTimeOffset.UtcNow is var left && left > TimeSpan.FromMinutes(1) ? left : TimeSpan.FromMinutes(1));
}
