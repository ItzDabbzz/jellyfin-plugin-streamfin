using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

public sealed record CacheCollection(string Name, int Entries, int Capacity);

// One bounded store for every catalog result the plugin keeps in memory. A page of titles is one entry, so the
// limits are counts rather than bytes, and each collection has its own budget: a large anime listing cannot push
// films or music out, and a busy search cannot grow without end. Clearing drops everything at once, which the
// scheduled task and the settings page both use.
public sealed class CatalogCache : IDisposable
{
    // Roughly a few dozen small records per entry, except music track lookups, which are single records.
    private static readonly Dictionary<string, int> Budgets = new(StringComparer.Ordinal)
    {
        ["anime"] = 900,
        ["sources"] = 500,
        ["films"] = 900,
        ["films-details"] = 900,
        ["music"] = 1200,
        ["music-tracks"] = 2500,
        ["music-twins"] = 1200,
        ["lyrics"] = 300,
        ["radio"] = 300
    };
    public const int DefaultCapacity = 300;

    private sealed class Bucket(int capacity)
    {
        public int Capacity { get; } = capacity;
        public MemoryCache Cache { get; } = new(new MemoryCacheOptions { SizeLimit = capacity, CompactionPercentage = 0.15 });
    }

    private readonly ConcurrentDictionary<string, Bucket> buckets = new(StringComparer.Ordinal);
    private long hits, misses, stale, trimmed, generation;
    private DateTimeOffset? clearedAt;
    private bool disposed;

    // Create the known budgets up front so the settings page can show every category before anything is cached.
    public CatalogCache()
    {
        foreach (var name in Budgets.Keys) _ = For(name);
    }

    // Bumped by Clear, so a request that started before it does not put its old answer back.
    public long Generation => Interlocked.Read(ref generation);
    public DateTimeOffset? ClearedAt => clearedAt;

    private Bucket For(string name) => buckets.GetOrAdd(name, key => new Bucket(Budgets.TryGetValue(key, out var capacity) ? capacity : DefaultCapacity));

    // A raw view for callers that keep their own entry shapes. Entries still count against this collection's budget.
    public MemoryCache Collection(string name) => For(name).Cache;

    public bool TryGet<T>(string collection, string key, out T? value)
    {
        if (For(collection).Cache.TryGetValue(key, out var found) && found is T typed)
        {
            Interlocked.Increment(ref hits);
            value = typed;
            return true;
        }
        Interlocked.Increment(ref misses);
        value = default;
        return false;
    }

    public void MarkStale() => Interlocked.Increment(ref stale);

    public void Store<T>(string collection, string key, T value, TimeSpan lifetime, bool sliding = false)
    {
        var options = new MemoryCacheEntryOptions { Size = 1 };
        options.RegisterPostEvictionCallback(Trimmed);
        if (sliding) options.SlidingExpiration = lifetime;
        else options.AbsoluteExpirationRelativeToNow = lifetime;
        For(collection).Cache.Set(key, value, options);
    }

    private void Trimmed(object key, object? value, EvictionReason reason, object? state)
    {
        if (reason == EvictionReason.Capacity) Interlocked.Increment(ref trimmed);
    }

    public void Clear()
    {
        Interlocked.Increment(ref generation);
        foreach (var bucket in buckets.Values) bucket.Cache.Clear();
        Interlocked.Exchange(ref hits, 0);
        Interlocked.Exchange(ref misses, 0);
        Interlocked.Exchange(ref stale, 0);
        Interlocked.Exchange(ref trimmed, 0);
        clearedAt = DateTimeOffset.UtcNow;
    }

    public CacheStatus Status()
    {
        var collections = buckets.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new CacheCollection(pair.Key, pair.Value.Cache.Count, pair.Value.Capacity))
            .ToArray();
        return new CacheStatus(collections, collections.Sum(collection => collection.Entries), collections.Sum(collection => collection.Capacity),
            Interlocked.Read(ref hits), Interlocked.Read(ref misses), Interlocked.Read(ref stale), Interlocked.Read(ref trimmed), clearedAt);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var bucket in buckets.Values) bucket.Cache.Dispose();
        buckets.Clear();
    }
}

// Counts cover paged browse lookups; raw title, lyric and music-track lookups are shown by their entries only.
public sealed record CacheStatus(IReadOnlyList<CacheCollection> Collections, int Entries, int Capacity,
    long PageHits, long PageMisses, long PageStale, long Trimmed, DateTimeOffset? ClearedAt);
