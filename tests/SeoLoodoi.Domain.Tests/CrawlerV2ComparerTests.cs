using AwesomeAssertions;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Crawling.Rendering;
using SeoLoodoi.Infrastructure.Crawling;

namespace SeoLoodoi.Domain.Tests;

/// <summary>Crawler v2 D3/D5: deterministic raw-vs-rendered comparison and SPA trigger signals, driven through the real HtmlExtractor.</summary>
public sealed class CrawlerV2ComparerTests
{
    private static readonly Uri PageUri = new("https://shop.example/product");
    private static Task<ExtractedPage> Extract(string html) => new HtmlExtractor().ExtractAsync(html, PageUri, CancellationToken.None);

    private const string StaticPage = """
        <html lang="en"><head><title>Blue Shoes</title><meta name="description" content="Buy blue shoes">
        <meta name="robots" content="index,follow"><link rel="canonical" href="https://shop.example/product">
        <link rel="alternate" hreflang="de" href="https://shop.example/de/product">
        <script type="application/ld+json">{"@context":"https://schema.org","@type":"Product","name":"Blue Shoes"}</script></head>
        <body><h1>Blue Shoes</h1><h2>Details</h2><p>Comfortable shoes.</p><a href="/cart">Cart</a><img src="/a.jpg" alt="a"></body></html>
        """;

    [Fact]
    public async Task IdenticalPages_ProduceNoDifferences()
    {
        var raw = await Extract(StaticPage); var rendered = await Extract(StaticPage);
        var diff = RawVsRenderedComparer.Compare(raw, rendered);
        diff.ChangedCount.Should().Be(0);
        diff.CriticalCount.Should().Be(0);
    }

    [Fact]
    public async Task DynamicTitleCanonicalRobotsAndLinks_AreCriticalWithEvidence()
    {
        var raw = await Extract(StaticPage);
        var rendered = await Extract(StaticPage
            .Replace("<title>Blue Shoes</title>", "<title>Loading…</title>")
            .Replace("href=\"https://shop.example/product\"", "href=\"https://shop.example/other\"")
            .Replace("index,follow", "noindex")
            .Replace("<a href=\"/cart\">Cart</a>", "<a href=\"/cart\">Cart</a><a href=\"/js-only\">JS</a>"));
        var diff = RawVsRenderedComparer.Compare(raw, rendered);
        diff.Get("title")!.Should().Match<FieldDiff>(f => f.Changed && f.Severity == DiffSeverity.Critical && f.Raw == "Blue Shoes" && f.Rendered == "Loading…");
        diff.Get("canonical")!.Rendered.Should().Be("https://shop.example/other");
        diff.Get("robots")!.Changed.Should().BeTrue();
        diff.Get("internalLinks")!.OnlyInRendered.Should().Equal("https://shop.example/js-only");
        diff.CriticalCount.Should().Be(4);
    }

    [Fact]
    public async Task JsOnlyContent_IsDetectedAsTextGrowth_AndStructuredDataAndH1Differences()
    {
        var shell = await Extract("<html><head><title>App</title></head><body><div id=\"root\"></div><script src=\"/app.js\"></script></body></html>");
        var words = string.Join(' ', Enumerable.Repeat("content", 200));
        var rendered = await Extract($"<html><head><title>App</title><script type=\"application/ld+json\">{{\"@type\":\"Article\"}}</script></head><body><div id=\"root\"><h1>Article</h1><p>{words}</p></div></body></html>");
        var diff = RawVsRenderedComparer.Compare(shell, rendered);
        diff.Get("textWordCount")!.Should().Match<FieldDiff>(f => f.Changed && f.Severity == DiffSeverity.Critical);
        diff.Get("h1")!.OnlyInRendered.Should().Equal("Article");
        diff.Get("structuredDataTypes")!.OnlyInRendered.Should().Equal("Article");
    }

    [Fact]
    public async Task Comparison_IsDeterministic_RegardlessOfElementOrderOrWhitespace()
    {
        var a = await Extract("<html><body><a href='/b'>B</a><a href='/a'>A</a><h2>X</h2><h2>Y</h2></body></html>");
        var b = await Extract("<html><body><a href='/a'>A</a>\n\n<a href='/b'>B</a><h2>Y</h2><h2>  X </h2></body></html>");
        var r1 = RawVsRenderedComparer.Compare(a, b); var r2 = RawVsRenderedComparer.Compare(a, b);
        r1.ChangedCount.Should().Be(0);
        r1.ToJson().Should().Be(r2.ToJson());
    }

    [Fact]
    public async Task JsonLdReserialization_IsNotADifference_ButInvalidJsonIsEvidence()
    {
        var a = await Extract("""<html><head><script type="application/ld+json">{"@type":"Product","name":"x"}</script></head></html>""");
        var b = await Extract("""<html><head><script type="application/ld+json">{ "name" : "x", "@type" : "Product" }</script></head></html>""");
        var broken = await Extract("""<html><head><script type="application/ld+json">{ "@type": </script></head></html>""");
        RawVsRenderedComparer.Compare(a, b).Get("structuredDataTypes")!.Changed.Should().BeFalse();
        RawVsRenderedComparer.Compare(a, broken).Get("structuredDataTypes")!.OnlyInRendered.Should().Contain("#invalid-json-ld");
    }

    [Fact]
    public async Task EvidenceLists_AreBounded()
    {
        var raw = await Extract("<html><body></body></html>");
        var links = string.Concat(Enumerable.Range(0, 500).Select(i => $"<a href='/p{i}'>p</a>"));
        var diff = RawVsRenderedComparer.Compare(raw, await Extract($"<html><body>{links}</body></html>"));
        diff.Get("internalLinks")!.OnlyInRendered!.Count.Should().Be(RawVsRenderedComparer.MaxListEvidence);
        diff.Get("internalLinks")!.Rendered.Should().Be("500");
    }

    [Fact]
    public void Triggers_EmptyReactShell_ShouldRender()
    {
        var html = "<html><head><title></title><script src='/_next/static/a.js'></script></head><body><div id=\"__next\"></div></body></html>";
        var signals = RenderTriggers.Detect(new(html, null, 0, 0, 1, 0));
        signals.Should().Contain(new[] { RenderTriggers.EmptyAppShell, RenderTriggers.FrameworkMarker, RenderTriggers.MissingTitle });
        RenderTriggers.ShouldRender(signals).Should().BeTrue();
    }

    [Fact]
    public void Triggers_ServerRenderedPage_DoesNotRender()
    {
        var signals = RenderTriggers.Detect(new(StaticPage, "Blue Shoes", 1, 400, 1, 20));
        RenderTriggers.ShouldRender(signals).Should().BeFalse();
    }

    [Theory]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0; url=/new\">")]
    [InlineData("<script>window.location.href = '/new'</script>")]
    [InlineData("<script>location.replace('/new')</script>")]
    public void Triggers_ClientRedirect_ShouldRender(string snippet)
    {
        var signals = RenderTriggers.Detect(new($"<html><head><title>t</title></head><body><h1>x</h1>{snippet}</body></html>", "t", 1, 300, 0, 3));
        signals.Should().Contain(RenderTriggers.ClientRedirect);
        RenderTriggers.ShouldRender(signals).Should().BeTrue();
    }
}
