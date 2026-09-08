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
    public void Blank_line_separates_an_empty_wildcard_group()
    {
        var doc = _parser.Parse("User-agent: *\n\nUser-agent: SEO-LoodoiBot\nDisallow: /private", new Uri("https://example.com"));
        doc.IsAllowed("SEO-LoodoiBot", new Uri("https://example.com/public")).Should().BeTrue();
        doc.IsAllowed("SEO-LoodoiBot", new Uri("https://example.com/private")).Should().BeFalse();
    }
    [Fact]
    public void Matching_group_exposes_crawl_delay()
    {
        var doc = _parser.Parse("User-agent: *\nCrawl-delay: 2.5", new Uri("https://example.com"));
        doc.GetCrawlDelay("SEO-LoodoiBot").Should().Be(TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public void Empty_disallow_allows_crawling()
    {
        var doc = _parser.Parse("User-agent: *\nDisallow:", new Uri("https://example.com"));
        doc.IsAllowed("SEO-LoodoiBot", new Uri("https://example.com/anything")).Should().BeTrue();
    }
}
