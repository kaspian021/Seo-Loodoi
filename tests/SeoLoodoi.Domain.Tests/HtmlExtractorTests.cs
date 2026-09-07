using AwesomeAssertions;
using SeoLoodoi.Infrastructure.Crawling;

namespace SeoLoodoi.Domain.Tests;

public class HtmlExtractorTests
{
    [Fact]
    public async Task Extracts_core_seo_evidence_without_executing_scripts()
    {
        const string html = """<html lang="fa"><head><title> صفحه نمونه </title><meta name="description" content="توضیح صفحه"><link rel="canonical" href="/canonical"><meta property="og:title" content="OG"><script type="application/ld+json">{"@type":"Article"}</script></head><body><h1>عنوان اصلی</h1><a href="/about">درباره ما</a><img src="a.jpg"><script>window.evil=true</script><p>این یک متن فارسی آزمایشی است</p></body></html>""";
        var page = await new HtmlExtractor().ExtractAsync(html, new Uri("https://example.com/page"), CancellationToken.None);
        page.Title.Should().Be("صفحه نمونه");
        page.Canonical.Should().Be("https://example.com/canonical");
        page.Language.Should().Be("fa");
        page.Headings.Should().ContainSingle(x => x.Level == 1 && x.Text == "عنوان اصلی");
        page.Links.Should().ContainSingle(x => x.IsInternal);
        page.ImageCount.Should().Be(1);
        page.MissingAltCount.Should().Be(1);
        page.JsonLd.Should().ContainSingle();
        page.OpenGraph.Should().ContainKey("og:title");
    }
}
