using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Crawling.Rendering;

namespace SeoLoodoi.Infrastructure.Crawling.Rendering;

/// <summary>
/// Admission control for the renderer (D2 and D9 fair capacity):
/// <list type="bullet">
/// <item>a global concurrency cap;</item>
/// <item>a per-project concurrency cap;</item>
/// <item>a bounded wait queue that rejects instead of piling up (backpressure);</item>
/// <item>a circuit breaker that stops rendering after repeated infrastructure failures (timeouts or crashes).</item>
/// </list>
/// Pure in-process logic, so it is unit-testable without a browser.
/// </summary>
public sealed class RenderGate
{
    private readonly RenderingOptions _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _global;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _projects = new();
    private readonly object _gate = new();
    private int _outstanding;
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;

    public RenderGate(IOptions<RenderingOptions> options, TimeProvider clock)
    {
        _options = options.Value; _clock = clock;
        _global = new SemaphoreSlim(_options.MaxConcurrentRenders, _options.MaxConcurrentRenders);
    }

    /// <summary>Renders holding or waiting for a slot.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);
    public bool IsCircuitOpen { get { lock (_gate) return _openUntil is { } until && until > _clock.GetUtcNow(); } }

    /// <summary>Waits for a slot. Throws <see cref="RenderCapacityException"/> when the queue is full or the circuit is open.</summary>
    public async Task<IAsyncDisposable> EnterAsync(Guid projectId, CancellationToken ct)
    {
        if (IsCircuitOpen) { CrawlerMetrics.RenderRejected.Add(1, new KeyValuePair<string, object?>("reason", "circuit_open")); throw new RenderCapacityException("Renderer circuit is open after repeated failures."); }
        if (Interlocked.Increment(ref _outstanding) > _options.MaxQueuedRenders + _options.MaxConcurrentRenders)
        {
            Interlocked.Decrement(ref _outstanding);
            CrawlerMetrics.RenderRejected.Add(1, new KeyValuePair<string, object?>("reason", "queue_full"));
            throw new RenderCapacityException("Render queue is full.");
        }
        var project = _projects.GetOrAdd(projectId, _ => new SemaphoreSlim(_options.MaxConcurrentRendersPerProject, _options.MaxConcurrentRendersPerProject));
        var projectHeld = false; var globalHeld = false;
        try
        {
            await project.WaitAsync(ct); projectHeld = true;
            await _global.WaitAsync(ct); globalHeld = true;
            return new Slot(this, project);
        }
        catch
        {
            if (globalHeld) _global.Release();
            if (projectHeld) project.Release();
            Interlocked.Decrement(ref _outstanding);
            throw;
        }
    }

    /// <summary>Feeds the circuit breaker. Only infrastructure failures (timeout, crash) count; page-level problems do not.</summary>
    public void Report(RenderFailureKind failure)
    {
        lock (_gate)
        {
            if (failure is RenderFailureKind.Timeout or RenderFailureKind.Crash)
            {
                if (++_consecutiveFailures >= _options.CircuitFailureThreshold)
                {
                    _openUntil = _clock.GetUtcNow().AddSeconds(_options.CircuitOpenSeconds);
                    _consecutiveFailures = 0;
                    CrawlerMetrics.RenderCircuitOpened.Add(1);
                }
            }
            else _consecutiveFailures = 0;
        }
    }

    private sealed class Slot(RenderGate owner, SemaphoreSlim project) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) { owner._global.Release(); project.Release(); Interlocked.Decrement(ref owner._outstanding); }
            return ValueTask.CompletedTask;
        }
    }
}
