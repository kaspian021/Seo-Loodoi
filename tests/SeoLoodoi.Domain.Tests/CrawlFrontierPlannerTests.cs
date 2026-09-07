using AwesomeAssertions;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Urls;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

public class CrawlFrontierPlannerTests
{
    [Fact]
    public async Task Accepts_same_host_deduplicates_and_rejects_external_hosts()
    {
        var store = new FakeStore(); var planner = new CrawlFrontierPlanner(new UrlNormalizer(), store);
        var count = await planner.EnqueueDiscoveredAsync(Guid.NewGuid(), Guid.NewGuid(), new("https://example.com"),
            [new("https://example.com/a#x"), new("https://EXAMPLE.com/a?utm_source=x"), new("https://evil-example.com/a"), new("mailto:test@example.com")], 1, 3, false, null, CancellationToken.None);
        count.Should().Be(1); store.Items.Should().ContainSingle(); store.Items[0].NormalizedUrl.Should().Be("https://example.com/a");
    }
    [Fact]
    public async Task Allows_real_subdomain_but_not_suffix_attack()
    {
        var store = new FakeStore(); var planner = new CrawlFrontierPlanner(new UrlNormalizer(), store);
        await planner.EnqueueDiscoveredAsync(Guid.NewGuid(), Guid.NewGuid(), new("https://example.com"), [new("https://docs.example.com/a"), new("https://example.com.evil.test/a")], 1, 3, true, null, CancellationToken.None);
        store.Items.Should().ContainSingle(x => x.Url.Contains("docs.example.com"));
    }
    [Fact]
    public async Task Honors_depth_budget()
    {
        var store = new FakeStore(); var count = await new CrawlFrontierPlanner(new UrlNormalizer(), store).EnqueueDiscoveredAsync(Guid.NewGuid(), Guid.NewGuid(), new("https://example.com"), [new("https://example.com/a")], 4, 3, false, null, CancellationToken.None);
        count.Should().Be(0); store.Items.Should().BeEmpty();
    }
    private sealed class FakeStore : ICrawlFrontierStore
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase); public List<CrawlFrontierItem> Items { get; } = [];
        public Task<bool> EnqueueAsync(CrawlFrontierItem item, CancellationToken ct) { var added = _seen.Add(item.NormalizedUrl); if (added) Items.Add(item); return Task.FromResult(added); }
        public Task<CrawlFrontierItem?> TryLeaseAsync(Guid crawlId, string workerId, DateTimeOffset now, TimeSpan duration, CancellationToken ct) => Task.FromResult<CrawlFrontierItem?>(null);
        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
