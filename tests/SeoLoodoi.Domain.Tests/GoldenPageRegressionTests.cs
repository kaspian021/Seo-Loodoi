using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Content;
using SeoLoodoi.Application.Links;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

/// <summary>Golden regression fixtures for page rules, content, link graphs and Persian text.</summary>
public class GoldenPageRegressionTests
{
    private static readonly IContentSimilarityEngine Similarity = new ContentSimilarityEngine();
    private static readonly IInternalLinkGraph LinkGraph = new InternalLinkGraph();

    private static PageAnalysisContext Page(
        string url = "https://example.com/page",
        string? title = "عنوان استاندارد و توصیفی برای این صفحه",
        string? metaDescription = null,
        string? canonical = "https://example.com/page",
        int statusCode = 200,
        int wordCount = 400,
        string? robotsMeta = "index, follow",
        string? xRobotsTag = "index, follow",
        string? redirectChainJson = "[]",
        string? schemaJson = null,
        string? hreflangJson = null,
        string? assetsJson = "[]",
        string? headersJson = "{}") =>
        new(url, title, metaDescription ?? new string('ت', 110), ["سرصفحه"], canonical, wordCount, 2, 0, 300,
            IsIndexable: true, HeadingLevels: [1, 2, 2], StatusCode: statusCode, ContentType: "text/html; charset=utf-8",
            XRobotsTag: xRobotsTag, RobotsMeta: robotsMeta, RedirectChainJson: redirectChainJson, AssetsJson: assetsJson,
            SchemaJson: schemaJson, HreflangJson: hreflangJson, HeadersJson: headersJson, Depth: 1,
            TextContent: "سئو مجموعه‌ای از روش‌ها برای بهبود دید وب‌سایت در نتایج جستجو است.");

    private static bool Fires(ISeoRule rule, PageAnalysisContext page) => rule.Evaluate(page).Triggered;

    [Fact]
    public void PerfectPage_ProducesNoHighOrCriticalFindings()
    {
        var page = Page(
            schemaJson: """[{"@context":"https://schema.org","@type":"Article","headline":"ت","author":{"@type":"Person","name":"ن"}}]""",
            hreflangJson: """[{"language":"fa-IR","target":"https://example.com/page"}]""",
            headersJson: """{"Strict-Transport-Security":"max-age=31536000","X-Content-Type-Options":"nosniff","X-Frame-Options":"DENY","Cache-Control":"max-age=600"}""");
        var severe = new ISeoRule[]
        {
            new TitleMissingRule(), new H1MissingRule(), new MultipleH1Rule(), new CanonicalInvalidRule(),
            new NoIndexRule(), new XRobotsNoIndexRule(), new BrokenStatusRule(), new HttpsIssueRule(),
            new HreflangNoSelfRule(), new HreflangInvalidLanguageRule(), new SchemaSyntaxRule(),
            new KeywordStuffingRule(), new MixedContentAssetsRule()
        }.Where(r => Fires(r, page)).Select(r => r.Code).ToArray();
        Assert.Empty(severe);
    }

    [Fact]
    public void MissingTitle_IsDetected() => Assert.True(Fires(new TitleMissingRule(), Page(title: null)));

    [Fact]
    public void MissingTitle_IsReportedAsHighSeverity()
    {
        var result = new TitleMissingRule().Evaluate(Page(title: null));
        Assert.Equal(IssueSeverity.High, result.Severity);
        Assert.Equal(IssueCategory.OnPage, result.Category);
        Assert.Equal("title", result.Evidence!.Field);
    }

    [Fact]
    public void DuplicateTitle_IsRecognisedAsIdenticalContent()
    {
        const string title = "راهنمای خرید لپ‌تاپ";
        Assert.Equal(1.0m, Similarity.Similarity(title, title));
        Assert.Equal(Similarity.ExactHash(Similarity.Normalize(title)), Similarity.ExactHash(Similarity.Normalize(title)));
    }

    [Fact]
    public void DuplicateTitle_IsNotYetDetectedByARule()
    {
        // This selected list is only a gap reminder, not proof about the full rule registry.
        var codes = new ISeoRule[]
        {
            new TitleMissingRule(), new TitleLengthRule(), new CanonicalMismatchRule(), new CanonicalMissingRule()
        }.Select(r => r.Code).ToArray();
        Assert.DoesNotContain("DUPLICATE_TITLE", codes);
    }

    [Fact]
    public void MissingCanonical_IsDetected() => Assert.True(Fires(new CanonicalMissingRule(), Page(canonical: null)));
    [Fact]
    public void MissingCanonical_IsMediumSeverityNotCritical() =>
        Assert.Equal(IssueSeverity.Medium, new CanonicalMissingRule().Evaluate(Page(canonical: null)).Severity);
    [Fact]
    public void Noindex_IsDetectedFromMetaRobots() => Assert.True(Fires(new NoIndexRule(), Page(robotsMeta: "noindex, nofollow")));
    [Fact]
    public void Noindex_IsDetectedFromTheHttpHeader() => Assert.True(Fires(new XRobotsNoIndexRule(), Page(xRobotsTag: "noindex")));

    [Fact]
    public void Noindex_DoesNotDoubleReport_ForTheHeaderAndMetaVariants()
    {
        // Distinct source-specific rule codes; this is not a persistence deduplication test.
        var page = Page(robotsMeta: "noindex", xRobotsTag: "noindex");
        Assert.Equal(IssueCategory.Indexability, new NoIndexRule().Evaluate(page).Category);
        Assert.Equal(IssueCategory.Indexability, new XRobotsNoIndexRule().Evaluate(page).Category);
        Assert.NotEqual(new NoIndexRule().Code, new XRobotsNoIndexRule().Code);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(500)]
    [InlineData(503)]
    public void BrokenLinks_AreDetected(int statusCode) => Assert.True(Fires(new BrokenStatusRule(), Page(statusCode: statusCode)));
    [Fact]
    public void BrokenLinks_AreNotReportedAsNoindex() => Assert.False(Fires(new NoIndexRule(), Page(statusCode: 404, robotsMeta: null)));

    [Fact]
    public void RedirectChain_IsDetected_WhenLongerThanTwoHops()
    {
        var chain = """["https://example.com/a","https://example.com/b","https://example.com/c","https://example.com/d"]""";
        Assert.True(Fires(new RedirectChainLongRule(), Page(redirectChainJson: chain)));
    }
    [Fact]
    public void RedirectChain_IsTolerated_AtTwoHops()
    {
        var chain = """["https://example.com/a","https://example.com/b"]""";
        Assert.False(Fires(new RedirectChainLongRule(), Page(redirectChainJson: chain)));
    }
    [Fact]
    public void RedirectChain_SurvivesMalformedJson() =>
        Assert.False(Fires(new RedirectChainLongRule(), Page(redirectChainJson: "not json")));

    [Fact]
    public void OrphanPage_IsDetectedByTheLinkGraph()
    {
        var home = Guid.NewGuid();
        var linked = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var pages = new[]
        {
            new GraphPage(home, "https://example.com/", IsRoot: true),
            new GraphPage(linked, "https://example.com/linked"),
            new GraphPage(orphan, "https://example.com/orphan", IsInSitemap: true)
        };
        var links = new[] { new GraphLink(home, linked, "صفحه مرتبط") };
        var summary = LinkGraph.AnalyzeGraph(pages, links);
        Assert.Equal(1, summary.OrphanPageCount);
        Assert.True(summary.Pages.Single(p => p.PageId == orphan).IsOrphan);
        Assert.False(summary.Pages.Single(p => p.PageId == linked).IsOrphan);
    }
    [Fact]
    public void OrphanPage_ExcludesTheHomepage()
    {
        var home = Guid.NewGuid();
        var summary = LinkGraph.AnalyzeGraph([new GraphPage(home, "https://example.com/", IsRoot: true)], []);
        Assert.Equal(0, summary.OrphanPageCount);
    }

    [Fact]
    public void DuplicateContent_IsClusteredAsExact()
    {
        const string text = "این یک متن تکراری برای بررسی تشخیص محتوای مشابه است.";
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var clusters = Similarity.Cluster([new ContentDocument(a, text), new ContentDocument(b, text)]);
        var cluster = Assert.Single(clusters);
        Assert.True(cluster.Exact);
        Assert.Equal(2, cluster.DocumentIds.Count);
    }
    [Fact]
    public void DuplicateContent_IgnoresWhitespaceAndCasing()
    {
        var left = "  محتوای   یکسان  برای آزمون ";
        var right = "محتوای یکسان برای آزمون";
        Assert.Equal(Similarity.ExactHash(Similarity.Normalize(left)), Similarity.ExactHash(Similarity.Normalize(right)));
    }
    [Fact]
    public void DuplicateContent_DoesNotClusterUnrelatedPages()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var clusters = Similarity.Cluster(
        [
            new ContentDocument(a, "گزارش سالانه فروش شرکت در بازار داخلی"),
            new ContentDocument(b, "آموزش پخت غذاهای دریایی در جنوب کشور")
        ]);
        Assert.Empty(clusters);
    }

    [Fact]
    public void SchemaErrors_AreDetected_WhenTypeIsMissing()
    {
        var result = new SchemaSyntaxRule().Evaluate(Page(schemaJson: """[{"name":"بدون نوع"}]"""));
        Assert.True(result.Triggered);
        Assert.Equal(IssueCategory.StructuredData, result.Category);
    }
    [Fact]
    public void SchemaErrors_AreDetected_WhenRequiredFieldsAreMissing() =>
        Assert.True(Fires(new SchemaMissingRequiredRule(), Page(schemaJson: """[{"@type":"Article"}]""")));
    [Fact]
    public void SchemaErrors_AreNotReported_WhenSchemaIsAbsent()
    {
        Assert.False(Fires(new SchemaSyntaxRule(), Page(schemaJson: null)));
        Assert.False(Fires(new SchemaMissingRequiredRule(), Page(schemaJson: null)));
        Assert.True(Fires(new StructuredDataNoticeRule(), Page(schemaJson: null)));
    }

    [Fact]
    public void HreflangErrors_AreDetected_WhenSelfReferenceIsMissing()
    {
        var page = Page(hreflangJson: """[{"language":"en-US","target":"https://example.com/en"}]""");
        Assert.True(Fires(new HreflangNoSelfRule(), page));
    }
    [Fact]
    public void HreflangErrors_AreDetected_WhenTheLanguageCodeIsInvalid() =>
        Assert.True(Fires(new HreflangInvalidLanguageRule(), Page(hreflangJson: """[{"language":"فارسی","target":"https://example.com/page"}]""")));
    [Fact]
    public void HreflangErrors_AreNotReported_ForAValidSelfReferencingSet() =>
        Assert.False(Fires(new HreflangInvalidLanguageRule(), Page(hreflangJson: """[{"language":"fa-IR","target":"https://example.com/page"},{"language":"x-default","target":"https://example.com/page"}]""")));

    [Fact]
    public void PersianContent_IsAnalysedWithoutDroppingWords()
    {
        var engine = new ContentQualityEngine();
        var text = string.Join(" ", Enumerable.Range(0, 40).Select(i => $"واژه‌پارسی{i}"));
        var analysis = engine.Analyze(text, "https://example.com/fa");
        Assert.Equal(40, analysis.Readability.WordCount);
        // Forty observed words are correctly counted but below the 150-word threshold.
        Assert.True(analysis.IsThinContent);
    }
    [Fact]
    public void PersianContent_ReadabilityGradeIsReported()
    {
        var engine = new ContentQualityEngine();
        var shortSentences = string.Join(" ", Enumerable.Range(0, 8).Select(i => $"این جمله کوتاه شماره {i} است."));
        var analysis = engine.Analyze(shortSentences, "https://example.com/fa");
        Assert.False(string.IsNullOrWhiteSpace(analysis.Readability.ReadabilityGrade));
        Assert.True(analysis.Readability.SentenceCount >= 8);
    }
    [Fact]
    public void PersianContent_HandlesRightToLeftMarksAndYeForms()
    {
        var engine = new ContentQualityEngine();
        var analysis = engine.Analyze("می‌خواهم کتاب‌های جدید را بخوانم", "https://example.com/fa");
        Assert.Equal(5, analysis.Readability.WordCount);
    }
    [Fact]
    public void EmptyPersianContent_IsReportedAsThinRatherThanPerfect()
    {
        var analysis = new ContentQualityEngine().Analyze("", "https://example.com/fa");
        Assert.True(analysis.IsThinContent);
        Assert.Equal(0, analysis.Readability.WordCount);
    }
}
