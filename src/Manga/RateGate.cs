namespace StreamFin.Manga;

// Spaces out calls to one upstream API and honours "slow down" answers, so a large library cannot get the plugin blocked.
public sealed class RateGate(TimeSpan spacing)
{
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly object _lock = new();
    private DateTimeOffset _nextCall = DateTimeOffset.MinValue;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    // True while the service has asked us to stop; callers skip the request and try again later.
    public bool IsPaused { get { lock (_lock) return DateTimeOffset.UtcNow < _pausedUntil; } }

    public async Task WaitAsync(CancellationToken ct)
    {
        await _turn.WaitAsync(ct);
        try
        {
            TimeSpan wait;
            lock (_lock) wait = _nextCall - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            lock (_lock) _nextCall = DateTimeOffset.UtcNow + spacing;
        }
        finally { _turn.Release(); }
    }

    // Called on HTTP 429 (or the service's own rate-limit code). Nothing is sent until the pause ends.
    public void Pause(TimeSpan duration)
    {
        lock (_lock)
        {
            var until = DateTimeOffset.UtcNow + duration;
            if (until > _pausedUntil) _pausedUntil = until;
        }
    }
}
