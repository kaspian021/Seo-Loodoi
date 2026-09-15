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

    [Fact]
    public void Handles_wildcard_patterns_and_clean_param()
    {
        var doc = _parser.Parse("""
            User-agent: *
            Disallow: /catalog/*/print$
            Clean-param: utm_source /shop/
            """, new Uri("https://example.com"));
        doc.IsAllowed("SEO-LoodoiBot", new Uri("https://example.com/catalog/shoes/print")).Should().BeFalse();
        doc.IsAllowed("SEO-LoodoiBot", new Uri("https://example.com/catalog/shoes/print-preview")).Should().BeTrue();
        doc.CleanParams.Should().NotBeNull();
        doc.CleanParams!.Should().ContainSingle();
        doc.CleanParams[0].Parameter.Should().Be("utm_source");
        doc.CleanParams[0].Path.Should().Be("/shop/");
    }

    [Fact]
    public void Handles_utf8_and_percent_encoded_paths()
    {
        var doc = _parser.Parse("""
            User-agent: *
            Disallow: /درباره-ما/
            """, new Uri("https://example.com"));
        doc.IsAllowed("SEO-LoodoiBot", new Uri("https://example.com/%D8%AF%D8%B1%D8%A8%D8%A7%D8%B1%D9%87-%D9%85%D8%A7/")).Should().BeFalse();
        doc.IsAllowed("SEO-LoodoiBot", new Uri("https://example.com/درباره-ما/")).Should().BeFalse();
        doc.IsAllowed("SEO-LoodoiBot", new Uri("https://example.com/تماس-با-ما/")).Should().BeTrue();
    }
}
