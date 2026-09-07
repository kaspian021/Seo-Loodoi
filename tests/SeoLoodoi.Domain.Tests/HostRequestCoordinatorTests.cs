using AwesomeAssertions;
using SeoLoodoi.Infrastructure.Crawling;

namespace SeoLoodoi.Domain.Tests;

public class HostRequestCoordinatorTests
{
    [Fact]
    public async Task Enforces_concurrency_per_host_without_blocking_other_hosts()
    {
        using var coordinator = new HostRequestCoordinator(TimeProvider.System, 2, TimeSpan.Zero);
        var a = await coordinator.AcquireAsync(new Uri("https://example.com/a"), CancellationToken.None);
        var b = await coordinator.AcquireAsync(new Uri("https://example.com/b"), CancellationToken.None);
        var blocked = coordinator.AcquireAsync(new Uri("https://example.com/c"), CancellationToken.None).AsTask();
        await Task.Delay(30); blocked.IsCompleted.Should().BeFalse();
        var other = coordinator.AcquireAsync(new Uri("https://other.example/c"), CancellationToken.None).AsTask();
        other.IsCompletedSuccessfully.Should().BeTrue();
        await a.DisposeAsync(); var c = await blocked.WaitAsync(TimeSpan.FromSeconds(1));
        await b.DisposeAsync(); await c.DisposeAsync(); await (await other).DisposeAsync();
    }
}
