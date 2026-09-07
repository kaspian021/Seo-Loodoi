using System.Collections.Concurrent;

namespace SeoLoodoi.Infrastructure.Crawling;

public interface IHostRequestCoordinator
{
    ValueTask<IAsyncDisposable> AcquireAsync(Uri uri, CancellationToken ct);
}

public sealed class HostRequestCoordinator : IHostRequestCoordinator, IDisposable
{
    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _minimumDelay;

    public HostRequestCoordinator(TimeProvider clock, int maxConcurrency = 4, TimeSpan? minimumDelay = null)
    {
        _clock = clock; _maxConcurrency = maxConcurrency; _minimumDelay = minimumDelay ?? TimeSpan.FromMilliseconds(100);
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(Uri uri, CancellationToken ct)
    {
        var key = $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";
        var state = _hosts.GetOrAdd(key, _ => new HostState(_maxConcurrency));
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

    public void Dispose() { foreach (var state in _hosts.Values) state.Concurrency.Dispose(); }
    private sealed class HostState(int concurrency) { public readonly object Gate = new(); public readonly SemaphoreSlim Concurrency = new(concurrency, concurrency); public DateTimeOffset NextAllowedAt; }
    private sealed class Lease(SemaphoreSlim semaphore) : IAsyncDisposable { private int _released; public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release(); return ValueTask.CompletedTask; } }
}
