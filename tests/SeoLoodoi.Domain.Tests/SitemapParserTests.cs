using System.Text;
using AwesomeAssertions;
using SeoLoodoi.Application.Crawling;

namespace SeoLoodoi.Domain.Tests;

public class SitemapParserTests
{
    [Fact]
    public void Parses_namespaced_urlset()
    {
        const string xml = """<?xml version="1.0"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://example.com/</loc><lastmod>2026-01-02</lastmod></url><url><loc>not-a-url</loc></url></urlset>""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = new SitemapParser().Parse(stream, new Uri("https://example.com/sitemap.xml"));
        result.Kind.Should().Be(SitemapKind.UrlSet);
        result.Entries.Should().ContainSingle();
        result.Entries[0].LastModified.Should().NotBeNull();
    }
    [Fact]
    public void Rejects_dtd_to_prevent_xxe()
    {
        const string xml = "<!DOCTYPE foo [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><urlset><url><loc>&xxe;</loc></url></urlset>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var act = () => new SitemapParser().Parse(stream, new Uri("https://example.com/sitemap.xml"));
        act.Should().Throw<Exception>();
    }
}
