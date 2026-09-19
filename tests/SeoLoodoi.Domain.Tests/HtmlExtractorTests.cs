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

    [Fact]
    public async Task Extracts_rich_assets_and_detects_mixed_content()
    {
        const string html = """
            <html lang="en">
            <head>
                <link rel="stylesheet" href="http://insecure.example/style.css">
                <link rel="preload" as="font" href="/fonts/vazir.woff2">
                <script src="/js/app.js" async defer></script>
            </head>
            <body>
                <img src="/img/logo.png" alt="Company Logo" loading="lazy" width="200" height="50">
                <img src="http://insecure.example/ad.png">
                <iframe src="https://player.example/embed/123" title="Video Player"></iframe>
            </body>
            </html>
            """;
        var page = await new HtmlExtractor().ExtractAsync(html, new Uri("https://example.com/"), CancellationToken.None);
        page.Assets.Should().NotBeNull();
        page.Assets!.Count.Should().Be(6);

        var stylesheet = page.Assets.Single(x => x.Type == SeoLoodoi.Application.Crawling.AssetType.Stylesheet);
        stylesheet.Url.Should().Be("http://insecure.example/style.css");
        stylesheet.IsMixedContent.Should().BeTrue();
        stylesheet.IsExternal.Should().BeTrue();

        var script = page.Assets.Single(x => x.Type == SeoLoodoi.Application.Crawling.AssetType.Script);
        script.Url.Should().Be("https://example.com/js/app.js");
        script.IsMixedContent.Should().BeFalse();

        var font = page.Assets.Single(x => x.Type == SeoLoodoi.Application.Crawling.AssetType.Font);
        font.Url.Should().Be("https://example.com/fonts/vazir.woff2");

        var logoImg = page.Assets.Single(x => x.Type == SeoLoodoi.Application.Crawling.AssetType.Image && x.AltText == "Company Logo");
        logoImg.Url.Should().Be("https://example.com/img/logo.png");
        logoImg.IsMixedContent.Should().BeFalse();

        var insecureImg = page.Assets.Single(x => x.Type == SeoLoodoi.Application.Crawling.AssetType.Image && x.AltText == null);
        insecureImg.Url.Should().Be("http://insecure.example/ad.png");
        insecureImg.IsMixedContent.Should().BeTrue();

        var iframe = page.Assets.Single(x => x.Type == SeoLoodoi.Application.Crawling.AssetType.Iframe);
        iframe.Url.Should().Be("https://player.example/embed/123");
        iframe.AltText.Should().Be("Video Player");
    }
}
