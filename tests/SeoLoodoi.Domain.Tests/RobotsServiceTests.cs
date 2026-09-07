using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Infrastructure.Crawling;

namespace SeoLoodoi.Domain.Tests;

public class RobotsServiceTests
{
    [Fact]
    public async Task Caches_successful_policy_per_origin()
    {
        var fetcher = new StubFetcher(200, "User-agent: *\nDisallow: /admin");
        var service = new RobotsService(fetcher, new RobotsParser(), new MemoryCache(new MemoryCacheOptions()));
        var first = await service.GetPolicyAsync(new Uri("https://example.com/page"), CancellationToken.None);
        var second = await service.GetPolicyAsync(new Uri("https://example.com/other"), CancellationToken.None);
        fetcher.Calls.Should().Be(1);
        first.CanCrawl("SEO-LoodoiBot", new Uri("https://example.com/admin/x")).Should().BeFalse();
        second.Should().BeSameAs(first);
    }
    [Theory]
    [InlineData(403, false)]
    [InlineData(404, true)]
    [InlineData(503, false)]
    public async Task Applies_conservative_http_status_policy(int status, bool expected)
    {
        var service = new RobotsService(new StubFetcher(status, ""), new RobotsParser(), new MemoryCache(new MemoryCacheOptions()));
        var policy = await service.GetPolicyAsync(new Uri("https://example.com"), CancellationToken.None);
        policy.CanCrawl("SEO-LoodoiBot", new Uri("https://example.com/page")).Should().Be(expected);
    }
    private sealed class StubFetcher(int status, string body) : IPageFetcher
    {
        public int Calls { get; private set; }
        public Task<FetchResult> FetchAsync(Uri uri, int max, CancellationToken ct) { Calls++; return Task.FromResult(new FetchResult(uri, uri, status, "text/plain", new Dictionary<string,string[]>(), Encoding.UTF8.GetBytes(body), TimeSpan.Zero, [])); }
    }
}
