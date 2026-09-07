using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Infrastructure.Crawling;

namespace SeoLoodoi.Domain.Tests;

public class SitemapDiscoveryTests
{
    [Fact]
    public async Task Traverses_nested_index_and_deduplicates_urls()
    {
        var index = Xml("sitemapindex", "sitemap", "https://example.com/a.xml", "https://example.com/b.xml.gz");
        var a = Xml("urlset", "url", "https://example.com/one", "https://example.com/shared");
        var b = Gzip(Xml("urlset", "url", "https://example.com/shared", "https://example.com/two"));
        var fetcher = new MapFetcher(new() { ["https://example.com/index.xml"] = (index, "application/xml"), ["https://example.com/a.xml"] = (a, "application/xml"), ["https://example.com/b.xml.gz"] = (b, "application/gzip") });
        var result = await new SitemapDiscoveryService(fetcher, new SitemapParser()).DiscoverAsync([new("https://example.com/index.xml")], 10, 10, CancellationToken.None);
        result.Urls.Select(x => x.Location.AbsoluteUri).Should().BeEquivalentTo("https://example.com/one", "https://example.com/shared", "https://example.com/two");
        result.ProcessedSitemaps.Should().HaveCount(3); result.Truncated.Should().BeFalse(); result.Errors.Should().BeEmpty();
    }
    [Fact]
    public async Task Applies_document_limit_to_hostile_nested_indexes()
    {
        var map = Enumerable.Range(0, 5).ToDictionary(i => $"https://example.com/{i}.xml", i => (Xml("sitemapindex", "sitemap", $"https://example.com/{i+1}.xml"), "application/xml"));
        var result = await new SitemapDiscoveryService(new MapFetcher(map), new SitemapParser()).DiscoverAsync([new("https://example.com/0.xml")], 2, 100, CancellationToken.None);
        result.ProcessedSitemaps.Should().HaveCount(2); result.Truncated.Should().BeTrue();
    }
    private static byte[] Xml(string root, string item, params string[] urls) => Encoding.UTF8.GetBytes($"<{root} xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">{string.Concat(urls.Select(x => $"<{item}><loc>{x}</loc></{item}>"))}</{root}>");
    private static byte[] Gzip(byte[] value) { using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(value); return output.ToArray(); }
    private sealed class MapFetcher(Dictionary<string,(byte[] Content,string Type)> map) : IPageFetcher
    {
        public Task<FetchResult> FetchAsync(Uri uri, int max, CancellationToken ct) { var x = map[uri.AbsoluteUri]; return Task.FromResult(new FetchResult(uri, uri, 200, x.Type, new Dictionary<string,string[]>(), x.Content, TimeSpan.Zero, [])); }
    }
}
