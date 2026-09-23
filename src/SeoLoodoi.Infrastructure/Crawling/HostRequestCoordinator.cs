using System.Collections.Concurrent;

namespace SeoLoodoi.Infrastructure.Crawling;

public interface IHostRequestCoordinator
{
    ValueTask<IAsyncDisposable> AcquireAsync(Uri uri, CancellationToken ct);
    /// <summary>
    /// Feeds per-host backoff and the circuit breaker (Crawler v2, D6). Pass the HTTP status,
    /// or null for a transport failure. The default no-op keeps simple test doubles valid.
    /// </summary>
    void Report(Uri uri, int? statusCode, TimeSpan? retryAfter) { }
}

/// <summary>Thrown by <see cref="HostRequestCoordinator.AcquireAsync"/> while a host's circuit is open. The crawler treats it as a retryable fetch failure.</summary>
public sealed class HostCircuitOpenException(string message) : HttpRequestException(message);

/// <summary>
/// Per-host politeness shared by the raw fetcher and the renderer:
/// <list type="bullet">
/// <item>a concurrency cap and a minimum spacing between requests;</item>
/// <item>backoff on 429/503 that honours Retry-After, capped at <see cref="MaxBackoff"/>;</item>
/// <item>a circuit breaker that opens after N consecutive failures (5xx, 429 or transport).
/// While it is open, requests to that host fail fast instead of hammering a struggling server.</item>
/// </list>
/// </summary>
public sealed class HostRequestCoordinator : IHostRequestCoordinator, IDisposable
{
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _minimumDelay;
    private readonly int _circuitThreshold;
    private readonly TimeSpan _circuitOpenFor;

    public HostRequestCoordinator(TimeProvider clock, int maxConcurrency = 4, TimeSpan? minimumDelay = null, int circuitThreshold = 8, TimeSpan? circuitOpenFor = null)
    {
        _clock = clock; _maxConcurrency = maxConcurrency; _minimumDelay = minimumDelay ?? TimeSpan.FromMilliseconds(100);
        _circuitThreshold = Math.Max(1, circuitThreshold); _circuitOpenFor = circuitOpenFor ?? TimeSpan.FromSeconds(120);
    }

    private static string Key(Uri uri) => $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";

    public async ValueTask<IAsyncDisposable> AcquireAsync(Uri uri, CancellationToken ct)
    {
        var state = _hosts.GetOrAdd(Key(uri), _ => new HostState(_maxConcurrency));
        lock (state.Gate)
        {
            if (state.OpenUntil is { } until && until > _clock.GetUtcNow()) throw new HostCircuitOpenException($"Host circuit breaker is open until {until:O} after repeated failures.");
        }
        await state.Concurrency.WaitAsync(ct);
        try
        {
            TimeSpan wait;
            lock (state.Gate)
            {
                var now = _clock.GetUtcNow();
                wait = state.NextAllowedAt > now ? state.NextAllowedAt - now : TimeSpan.Zero;
                var slot = wait > TimeSpan.Zero ? state.NextAllowedAt : now;
                state.NextAllowedAt = slot.Add(_minimumDelay);
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _clock, ct);
            return new Lease(state.Concurrency);
        }
        catch { state.Concurrency.Release(); throw; }
    }

    public void Report(Uri uri, int? statusCode, TimeSpan? retryAfter)
    {
        var state = _hosts.GetOrAdd(Key(uri), _ => new HostState(_maxConcurrency));
        lock (state.Gate)
        {
            var now = _clock.GetUtcNow();
            var failure = statusCode is null or 429 or >= 500;
            if (!failure) { state.ConsecutiveFailures = 0; state.OpenUntil = null; return; }
            state.ConsecutiveFailures++;
            if (statusCode is 429 or 503)
            {
                var backoff = retryAfter is { } ra && ra > TimeSpan.Zero ? ra : TimeSpan.FromSeconds(Math.Pow(2, Math.Min(state.ConsecutiveFailures, 6)));
                if (backoff > MaxBackoff) backoff = MaxBackoff;
                var until = now.Add(backoff);
                if (until > state.NextAllowedAt) state.NextAllowedAt = until;
            }
            if (state.ConsecutiveFailures >= _circuitThreshold)
            {
                state.OpenUntil = now.Add(_circuitOpenFor);
                state.ConsecutiveFailures = 0;
                CrawlerMetrics.HostCircuitOpened.Add(1);
            }
        }
    }

    public void Dispose() { foreach (var state in _hosts.Values) state.Concurrency.Dispose(); }
    private sealed class HostState(int concurrency)
    {
        public readonly object Gate = new();
        public readonly SemaphoreSlim Concurrency = new(concurrency, concurrency);
        public DateTimeOffset NextAllowedAt;
        public int ConsecutiveFailures;
        public DateTimeOffset? OpenUntil;
    }
    private sealed class Lease(SemaphoreSlim semaphore) : IAsyncDisposable { private int _released; public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release(); return ValueTask.CompletedTask; } }
}
