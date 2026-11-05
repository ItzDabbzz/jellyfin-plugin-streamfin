using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

// Metadata only: never retain signed playback URLs. A caller leaving the page must
// not cancel the shared request for everyone else opening the same title.
public sealed class BrowseCache<T>(Func<T, bool> usable, Func<DateTimeOffset>? clock = null) : IDisposable
{
    private sealed record Entry(T Value, DateTimeOffset FreshUntil, DateTimeOffset ExpiresAt);
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> pending = new();
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public async Task<T> GetAsync(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (cache.TryGetValue<Entry>(key, out var saved))
        {
            if (saved!.FreshUntil <= Now) _ = RefreshAsync(key, lifetime, load, saved);
            return saved.Value;
        }
        return await RefreshAsync(key, lifetime, load, null).WaitAsync(ct);
    }

    private Task<T> RefreshAsync(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, Entry? saved) =>
        pending.GetOrAdd(key, _ => new Lazy<Task<T>>(() => LoadAsync(key, lifetime, load, saved))).Value;

    private async Task<T> LoadAsync(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, Entry? saved)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await load(timeout.Token);
            if (usable(result)) Store(key, new Entry(result, Now + lifetime, DateTimeOffset.UtcNow.AddHours(24)));
            else if (saved is not null) return Keep(key, saved);
            return result;
        }
        catch (Exception) when (saved is not null)
        {
            // Background refresh failures must be observed, and must not hammer a
            // failing provider or extend the last successful result's 24-hour life.
            return Keep(key, saved);
        }
        finally { pending.TryRemove(key, out _); }
    }

    private T Keep(string key, Entry saved)
    {
        Store(key, saved with { FreshUntil = Now.AddSeconds(30) });
        return saved.Value;
    }
    private void Store(string key, Entry value) => cache.Set(key, value, new MemoryCacheEntryOptions
        { Size = 1, AbsoluteExpiration = value.ExpiresAt });

    public void Dispose() => cache.Dispose();
}
