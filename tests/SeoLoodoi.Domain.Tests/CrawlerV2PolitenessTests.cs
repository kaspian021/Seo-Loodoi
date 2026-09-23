using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SeoLoodoi.Application.Crawling.Rendering;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Crawling.Rendering;

namespace SeoLoodoi.Domain.Tests;

/// <summary>Crawler v2 D1/D2/D6: settings validation, render admission control, host backoff and circuit breaking.</summary>
public sealed class CrawlerV2PolitenessTests
{
    [Fact]
    public void LegacySettings_KeepPreV2Behaviour()
    {
        var s = new CrawlSettings().Validate();
        s.EffectiveRenderMode.Should().Be("html");
        s.EffectiveDiscoveryMode.Should().Be("hybrid");
        s.FollowsLinks.Should().BeTrue(); s.UsesSitemaps.Should().BeTrue();
    }

    [Theory]
    [InlineData("js", "spider", "mobile")]
    [InlineData("AUTO", "Sitemap", "Desktop")]
    public void ValidModes_AreNormalized(string render, string discovery, string viewport)
    {
        var s = new CrawlSettings(RenderMode: render, DiscoveryMode: discovery, Viewport: viewport).Validate();
        s.RenderMode.Should().Be(render.ToLowerInvariant()); s.DiscoveryMode.Should().Be(discovery.ToLowerInvariant()); s.Viewport.Should().Be(viewport.ToLowerInvariant());
    }

    [Theory]
    [InlineData("chrome", null, null, null)]
    [InlineData(null, "everything", null, null)]
    [InlineData(null, null, "tablet", null)]
    [InlineData(null, null, null, -1)]
    public void InvalidModes_AreRejected(string? render, string? discovery, string? viewport, int? maxRenders)
    {
        var act = () => new CrawlSettings(RenderMode: render, DiscoveryMode: discovery, Viewport: viewport, MaxRendersPerCrawl: maxRenders).Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UrlListMode_RequiresValidAbsoluteHttpUrls()
    {
        new CrawlSettings(DiscoveryMode: "list", UrlList: "https://a.example/1\nhttps://a.example/2\r\n").Validate().ParseUrlList().Should().HaveCount(2);
        ((Action)(() => new CrawlSettings(DiscoveryMode: "list").Validate())).Should().Throw<ArgumentException>();
        ((Action)(() => new CrawlSettings(DiscoveryMode: "list", UrlList: "file:///etc/passwd").Validate())).Should().Throw<ArgumentException>();
        ((Action)(() => new CrawlSettings(DiscoveryMode: "list", UrlList: "https://user:pw@a.example/").Validate())).Should().Throw<ArgumentException>();
        var tooMany = string.Join('\n', Enumerable.Range(0, CrawlSettings.MaxUrlListEntries + 1).Select(i => $"https://a.example/{i}"));
        ((Action)(() => new CrawlSettings(DiscoveryMode: "list", UrlList: tooMany).Validate())).Should().Throw<ArgumentException>();
    }

    private static RenderGate Gate(RenderingOptions o, TimeProvider? clock = null) => new(Options.Create(o), clock ?? TimeProvider.System);

    [Fact]
    public async Task RenderGate_EnforcesGlobalConcurrency_AndRejectsWhenQueueFull()
    {
        var gate = Gate(new RenderingOptions { MaxConcurrentRenders = 2, MaxConcurrentRendersPerProject = 2, MaxQueuedRenders = 1 });
        var p = Guid.NewGuid();
        await using var a = await gate.EnterAsync(p, default);
        await using var b = await gate.EnterAsync(p, default);
        var queued = gate.EnterAsync(p, default);           // waits (queue slot 1)
        queued.IsCompleted.Should().BeFalse();
        var reject = () => gate.EnterAsync(p, default);      // queue full → backpressure
        await reject.Should().ThrowAsync<RenderCapacityException>();
        await a.DisposeAsync();
        await using var c = await queued.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RenderGate_PerProjectCap_LeavesCapacityForOtherTenants()
    {
        var gate = Gate(new RenderingOptions { MaxConcurrentRenders = 2, MaxConcurrentRendersPerProject = 1, MaxQueuedRenders = 4 });
        var noisy = Guid.NewGuid(); var other = Guid.NewGuid();
        await using var first = await gate.EnterAsync(noisy, default);
        var secondNoisy = gate.EnterAsync(noisy, default);
        secondNoisy.IsCompleted.Should().BeFalse("one project may hold only its own slot");
        await using var otherSlot = await gate.EnterAsync(other, default).WaitAsync(TimeSpan.FromSeconds(5));
        await first.DisposeAsync();
        await using var s = await secondNoisy.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RenderGate_CircuitOpensAfterRepeatedCrashes_AndClosesAfterCooldown()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = Gate(new RenderingOptions { CircuitFailureThreshold = 3, CircuitOpenSeconds = 30 }, clock);
        gate.Report(RenderFailureKind.Crash); gate.Report(RenderFailureKind.Timeout);
        gate.Report(RenderFailureKind.NavigationFailed);     // page-level failure resets the streak
        gate.IsCircuitOpen.Should().BeFalse();
        gate.Report(RenderFailureKind.Crash); gate.Report(RenderFailureKind.Crash); gate.Report(RenderFailureKind.Timeout);
        gate.IsCircuitOpen.Should().BeTrue();
        await ((Func<Task>)(() => gate.EnterAsync(Guid.NewGuid(), default))).Should().ThrowAsync<RenderCapacityException>();
        clock.Advance(TimeSpan.FromSeconds(31));
        gate.IsCircuitOpen.Should().BeFalse();
    }

    [Fact]
    public async Task HostCoordinator_BacksOffOn429_HonouringRetryAfter()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new HostRequestCoordinator(clock, 4, TimeSpan.Zero);
        var uri = new Uri("https://slow.example/a");
        coordinator.Report(uri, 429, TimeSpan.FromSeconds(10));
        var acquire = coordinator.AcquireAsync(uri, default).AsTask();
        await Task.Delay(50);
        acquire.IsCompleted.Should().BeFalse("Retry-After must delay the next request to the host");
        clock.Advance(TimeSpan.FromSeconds(10));
        await using var lease = await acquire.WaitAsync(TimeSpan.FromSeconds(5));
        await using var other = await coordinator.AcquireAsync(new Uri("https://fast.example/"), default);  // other hosts unaffected
    }

    [Fact]
    public async Task HostCoordinator_RetryAfter_IsCapped()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new HostRequestCoordinator(clock, 4, TimeSpan.Zero);
        var uri = new Uri("https://hostile.example/");
        coordinator.Report(uri, 503, TimeSpan.FromDays(3));
        var acquire = coordinator.AcquireAsync(uri, default).AsTask();
        clock.Advance(HostRequestCoordinator.MaxBackoff);
        await using var lease = await acquire.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HostCoordinator_CircuitOpensAfterConsecutiveFailures_AndSuccessResets()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new HostRequestCoordinator(clock, 4, TimeSpan.Zero, circuitThreshold: 3, circuitOpenFor: TimeSpan.FromSeconds(60));
        var uri = new Uri("https://down.example/");
        coordinator.Report(uri, 500, null); coordinator.Report(uri, null, null);
        coordinator.Report(uri, 200, null);                  // success resets the streak
        await using (await coordinator.AcquireAsync(uri, default)) { }
        coordinator.Report(uri, 500, null); coordinator.Report(uri, 502, null); coordinator.Report(uri, null, null);
        await ((Func<Task>)(async () => await coordinator.AcquireAsync(uri, default))).Should().ThrowAsync<HostCircuitOpenException>();
        clock.Advance(TimeSpan.FromSeconds(61));
        await using (await coordinator.AcquireAsync(uri, default)) { }
    }

    [Fact]
    public void RenderQuotaPolicy_InactiveSubscriptionsGetNoRenders()
    {
        RenderQuotaPolicy.MonthlyRenders("Pro", true).Should().BeGreaterThan(RenderQuotaPolicy.MonthlyRenders("Starter", true));
        RenderQuotaPolicy.MonthlyRenders("Enterprise", false).Should().Be(0);
        RenderQuotaPolicy.MonthlyRenders("Unknown", true).Should().Be(0);
    }

    [Fact]
    public void ViewportProfiles_AreFixedAndDistinct()
    {
        ViewportProfile.For("mobile").IsMobile.Should().BeTrue();
        ViewportProfile.For("anything-else").Should().Be(ViewportProfile.Desktop);
    }
}
