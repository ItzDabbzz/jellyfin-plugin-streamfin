using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StreamFin.Manga;

// A single in-process writer also makes queue claims atomic. SQLite provides crash durability.
public sealed class Store
{
    private readonly object _gate = new();
    private readonly string _connectionString;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Store() : this(Path.Combine(MangaHost.DataFolder, "jellymanga.db")) { }
    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS records (kind TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(kind,id)); PRAGMA user_version=1;";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connectionString); db.Open(); return db; }
    public T? Get<T>(string kind, string id)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM records WHERE kind=$kind AND id=$id";
            cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is string s ? JsonSerializer.Deserialize<T>(s, Json) : default;
        }
    }
    public List<T> All<T>(string kind)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM records WHERE kind=$kind ORDER BY id"; cmd.Parameters.AddWithValue("$kind", kind);
            using var reader = cmd.ExecuteReader(); var values = new List<T>();
            while (reader.Read()) values.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
            return values;
        }
    }
    public void Put<T>(string kind, string id, T value)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO records(kind,id,json) VALUES($kind,$id,$json) ON CONFLICT(kind,id) DO UPDATE SET json=excluded.json";
            cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Json)); cmd.ExecuteNonQuery();
        }
    }
    public void Delete(string kind, string id)
    {
        lock (_gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM records WHERE kind=$kind AND id=$id"; cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    }
    public DownloadJob Enqueue(Chapter chapter, string sourceId, string user)
    {
        lock (_gate)
        {
            var current = Get<DownloadJob>("job", chapter.Id);
            if (current != null) return current;
            var job = new DownloadJob(chapter with { Title = MangaNames.CleanChapterTitle(chapter.Title) }, sourceId, user); Put("job", chapter.Id, job); return job;
        }
    }
    public void EnqueueChapters(IEnumerable<Chapter> chapters, string sourceId, string user)
    {
        // The scheduler must see the whole chapter list before claiming its first job.
        lock (_gate)
        {
            // One upload per chapter number. MangaDex lists the same chapter once per scanlation group, so a request
            // would otherwise queue three copies. A number that is already queued, downloading or downloaded is skipped.
            var taken = All<DownloadJob>("job").Select(job => (job.Chapter.SeriesId, Key: VolumeArt.Key(job.Chapter.Number))).ToHashSet();
            var picks = chapters
                .GroupBy(chapter => (chapter.SeriesId, Key: VolumeArt.Key(chapter.Number)))
                .Where(group => !taken.Contains(group.Key))
                .Select(group => group
                    .OrderBy(chapter => chapter.SourceDate ?? DateTimeOffset.MaxValue)
                    .ThenBy(chapter => chapter.Id, StringComparer.Ordinal)
                    .First())
                .OrderBy(chapter => chapter.Number)
                .ToList();
            foreach (var chapter in picks)
                Enqueue(chapter, sourceId, user);
        }
    }

    public DownloadJob? Claim(IReadOnlyDictionary<string, int> activeCounts, IReadOnlyDictionary<string, int> sourceLimits, int globalLimit)
    {
        if (activeCounts.Values.Sum() >= globalLimit) return null;
        var busy = activeCounts.Where(s => !sourceLimits.TryGetValue(s.Key, out var limit) || s.Value >= limit).Select(s => s.Key).ToHashSet();
        return Claim(busy, sourceLimits.Keys.ToHashSet());
    }
    public DownloadJob? Claim(IReadOnlySet<string> busySources, IReadOnlySet<string> enabled)
    {
        lock (_gate)
        {
            var removals = All<string>("removing-series").ToHashSet();
            var now = DateTimeOffset.UtcNow;
            var job = All<DownloadJob>("job")
                .Where(j => !removals.Contains(j.Chapter.SeriesId) && j.Status == "Queued"
                    && !busySources.Contains(j.SourceId) && enabled.Contains(j.SourceId)
                    && (j.RetryAt == null || j.RetryAt <= now))
                .OrderBy(j => j.Chapter.Number)
                .ThenBy(j => j.Chapter.SeriesId, StringComparer.Ordinal)
                .ThenBy(j => j.Chapter.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (job == null) return null;
            job = job with { Status = "Downloading", Attempts = job.Attempts + 1, Error = null }; Put("job", job.Chapter.Id, job); return job;
        }
    }
    public bool Finish(DownloadJob job)
    {
        lock (_gate)
        {
            var current = Get<DownloadJob>("job", job.Chapter.Id);
            if (current?.Status != "Downloading") return false;
            Put("job", job.Chapter.Id, job); return true;
        }
    }
    public void Recover()
    {
        lock (_gate) foreach (var job in All<DownloadJob>("job").Where(j => j.Status == "Downloading")) Put("job", job.Chapter.Id, job with { Status = "Queued", Error = "Recovered after restart." });
    }
    public void Event(string kind, string message)
    {
        lock (_gate)
        {
            var entry = new DiagnosticEvent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, kind, message);
            Put("event", entry.Id, entry);
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM records WHERE kind='event' AND id NOT IN (SELECT id FROM records WHERE kind='event' ORDER BY id DESC LIMIT 500)"; cmd.ExecuteNonQuery();
        }
    }
    public void UpdateSubscription(string id, Func<Subscription, Subscription> update)
    {
        lock (_gate) { var subscription = Get<Subscription>("series", id); if (subscription != null) Put("series", id, update(subscription)); }
    }
    public bool IsRemovingSeries(string id) => Get<string>("removing-series", id) != null;

    public void ForgetSeries(string id)
    {
        lock (_gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                DELETE FROM records
                WHERE (kind IN ('series', 'removing-series') AND id = $id)
                   OR (kind = 'job' AND json_extract(json, '$.chapter.seriesId') = $id)
                   OR ((kind = 'request' OR kind LIKE 'progress:%') AND json_extract(json, '$.seriesId') = $id)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void Action(string id, string action)
    {
        lock (_gate)
        {
            var job = Get<DownloadJob>("job", id) ?? throw new KeyNotFoundException("Download not found.");
            if (IsRemovingSeries(job.Chapter.SeriesId)) throw new ArgumentException("This manga is being removed.");
            if (action == "redownload")
            {
                if (job.Status != "Complete") throw new ArgumentException("Only a downloaded chapter can be redownloaded.");
                Put("job", id, job with { Status = "Queued", Attempts = 0, RetryAt = null, Error = null });
                return;
            }
            if (job.Status == "Complete") throw new ArgumentException("Completed downloads cannot be modified here.");
            var status = action switch { "retry" or "resume" => "Queued", "pause" => "Paused", "cancel" => "Cancelled", _ => throw new ArgumentException("Unknown download action.") };
            if (status == "Queued" && job.Status == "Downloading") throw new ArgumentException("Pause or cancel the active download first.");
            Put("job", id, job with { Status = status, Attempts = status == "Queued" ? 0 : job.Attempts, RetryAt = null, Error = null });
        }
    }
}
