using AwesomeAssertions;
using SeoLoodoi.Application.Crawling;

namespace SeoLoodoi.Domain.Tests;

public class RobotsParserTests
{
    private readonly RobotsParser _parser = new();
    [Fact]
    public void Most_specific_rule_wins_and_allow_wins_equal_length()
    {
        var doc = _parser.Parse("""
            User-agent: *
            Disallow: /private/
            Allow: /private/public/
            Sitemap: https://example.com/sitemap.xml
            """, new Uri("https://example.com"));
        doc.IsAllowed("SEO-LoodoiBot", new Uri("https://example.com/private/secret")).Should().BeFalse();
        doc.IsAllowed("SEO-LoodoiBot", new Uri("https://example.com/private/public/page")).Should().BeTrue();
        doc.Sitemaps.Should().ContainSingle().Which.AbsoluteUri.Should().Be("https://example.com/sitemap.xml");
    }
    [Fact]
    public void Specific_agent_group_overrides_wildcard_group()
    {
        var doc = _parser.Parse("User-agent: *\nDisallow: /\n\nUser-agent: SEO-LoodoiBot\nAllow: /", new Uri("https://example.com"));
        doc.IsAllowed("SEO-LoodoiBot/0.1", new Uri("https://example.com/page")).Should().BeTrue();
    }
    [Fact]
    public void Empty_disallow_allows_crawling()
    {
        var doc = _parser.Parse("User-agent: *\nDisallow:", new Uri("https://example.com"));
        doc.IsAllowed("SEO-LoodoiBot", new Uri("https://example.com/anything")).Should().BeTrue();
    }
}
