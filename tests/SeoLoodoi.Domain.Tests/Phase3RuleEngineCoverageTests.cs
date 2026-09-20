using SeoLoodoi.Application.Analysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeoLoodoi.Infrastructure;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

/// <summary>PHASE 3 regression coverage for the selected page rules and a healthy-page fixture.</summary>
public class Phase3RuleEngineCoverageTests
{
    private const string Url = "https://example.com/blog/seo-guide";
    private const string PerfectHeaders =
        """{"Strict-Transport-Security":"max-age=31536000","X-Content-Type-Options":"nosniff","X-Frame-Options":"DENY","Cache-Control":"public, max-age=600"}""";
    private const string PerfectSchema =
        """[{"@context":"https://schema.org","@type":"Article","headline":"راهنمای سئو","author":{"@type":"Person","name":"نویسنده"}}]""";

    // Explicit fixture set, not a count of all registered production rules.
    private static readonly IReadOnlyList<ISeoRule> AllRules =
    [
        new TitleMissingRule(), new TitleLengthRule(), new MetaDescriptionMissingRule(), new MetaDescriptionLengthRule(),
        new H1MissingRule(), new MultipleH1Rule(), new CanonicalMissingRule(), new CanonicalInvalidRule(), new CanonicalMismatchRule(),
        new LowWordCountRule(), new MissingAltRule(), new SlowResponseRule(), new HeadingStructureRule(),
        new NoIndexRule(), new HttpsIssueRule(),
        new BrokenStatusRule(), new RedirectedStatusRule(), new XRobotsNoIndexRule(), new EmptyContentTypeRule(),
        new RedirectChainLongRule(), new MixedContentAssetsRule(), new ExcessiveResourcesRule(),
        new DeepClickDepthRule(), new HreflangNoSelfRule(), new HreflangInvalidLanguageRule(),
        new SchemaSyntaxRule(), new SchemaMissingRequiredRule(), new StructuredDataNoticeRule(),
        new HstsMissingRule(), new SecurityHeadersMissingRule(), new CacheControlMissingRule(),
        new RenderBlockingResourcesRule(), new ImageDimensionsMissingRule(),
        new ThinContentRule(), new LongSentencesRule(), new KeywordStuffingRule()
    ];

    private static PageAnalysisContext Perfect() => new(
        Url: Url,
        Title: "راهنمای کامل سئو برای فروشگاه اینترنتی",
        MetaDescription: new string('ت', 110),
        H1s: ["راهنمای سئو"],
        Canonical: Url,
        WordCount: 600,
        ImageCount: 3,
        MissingAltCount: 0,
        ResponseTimeMs: 400,
        IsIndexable: true,
        HeadingLevels: [1, 2, 3],
        StatusCode: 200,
        ContentType: "text/html; charset=utf-8",
        XRobotsTag: "index, follow",
        RobotsMeta: "index, follow",
        RedirectChainJson: "[]",
        AssetsJson: "[]",
        SchemaJson: PerfectSchema,
        HreflangJson: """[{"language":"fa-IR","target":"https://example.com/blog/seo-guide"}]""",
        HeadersJson: PerfectHeaders,
        Depth: 1,
        TextContent: Sentences(6, 20));

    private static string Sentences(int count, int wordsEach)
    {
        var index = 0;
        var parts = new List<string>();
        for (var s = 0; s < count; s++)
        {
            var words = new List<string>();
            for (var w = 0; w < wordsEach; w++) words.Add($"واژه{index++}");
            parts.Add(string.Join(" ", words) + ".");
        }
        return string.Join(" ", parts);
    }

    private static string RepeatedTermText(string term, int repeats, int fillerWords)
    {
        var words = new List<string>();
        for (var i = 0; i < repeats; i++) words.Add(term);
        for (var i = 0; i < fillerWords; i++) words.Add($"متن{i}");
        return string.Join(" ", words);
    }

    private static void AssertTriggers(ISeoRule rule, PageAnalysisContext context)
    {
        var result = rule.Evaluate(context);
        Assert.True(result.Triggered, $"{rule.Code} should have fired. Evidence: {result.Evidence?.Actual}");
        Assert.Equal(rule.Code, result.Code);
    }
    private static void AssertSilent(ISeoRule rule, PageAnalysisContext context)
    {
        var result = rule.Evaluate(context);
        Assert.False(result.Triggered, $"{rule.Code} fired on a healthy page: {result.Evidence?.Actual}");
    }

    [Fact]
    public void FixtureSet_CoversEveryRegisteredRule_WithoutDuplicates()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatabaseProvider"] = "InMemory"
        }).Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(config);
        var registered = services.Where(d => d.ServiceType == typeof(ISeoRule))
            .Select(d => d.ImplementationType!.FullName).OrderBy(x => x).ToArray();
        var covered = AllRules.Select(r => r.GetType().FullName).OrderBy(x => x).ToArray();
        Assert.Equal(registered, covered);
    }

    [Fact]
    public void EveryRuleStaysSilentOnTheGoldenPage()
    {
        var page = Perfect();
        var failures = AllRules.Select(r => (Rule: r, Result: r.Evaluate(page)))
            .Where(x => x.Result.Triggered).Select(x => $"{x.Rule.Code} ({x.Result.Evidence?.Actual})").ToArray();
        Assert.Empty(failures);
    }
    [Fact]
    public void EveryRuleHasAUniqueStableCode()
    {
        var codes = AllRules.Select(r => r.Code).ToArray();
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, c => Assert.False(string.IsNullOrWhiteSpace(c)));
    }
    [Fact]
    public void RulesAreDeterministic()
    {
        var broken = Perfect() with { StatusCode = 500, Title = null, WordCount = 12 };
        foreach (var rule in AllRules)
        {
            var first = rule.Evaluate(broken);
            for (var i = 0; i < 3; i++)
            {
                var again = rule.Evaluate(broken);
                Assert.Equal(first.Triggered, again.Triggered);
                Assert.Equal(first.Severity, again.Severity);
                Assert.Equal(first.Category, again.Category);
                Assert.Equal(first.Evidence?.Actual, again.Evidence?.Actual);
            }
        }
    }
    [Fact]
    public void RulesNeverThrowOnAnEmptyPage()
    {
        var empty = new PageAnalysisContext("https://example.com/", null, null, [], null, 0, 0, 0, 0);
        foreach (var rule in AllRules)
        {
            var result = rule.Evaluate(empty);
            Assert.NotNull(result);
            Assert.Equal(rule.Code, result.Code);
        }
    }

    [Fact] public void TitleMissing_Fires_WhenTitleIsBlank() => AssertTriggers(new TitleMissingRule(), Perfect() with { Title = "   " });
    [Fact] public void TitleLength_Fires_WhenTitleIsTooShort() => AssertTriggers(new TitleLengthRule(), Perfect() with { Title = "سئو" });
    [Fact] public void TitleLength_Fires_WhenTitleIsTooLong() => AssertTriggers(new TitleLengthRule(), Perfect() with { Title = new string('ت', 120) });
    [Fact] public void TitleLength_IsSilent_AtTheBoundary() { AssertSilent(new TitleLengthRule(), Perfect() with { Title = new string('ت', 20) }); AssertSilent(new TitleLengthRule(), Perfect() with { Title = new string('ت', 65) }); }
    [Fact] public void MetaDescriptionMissing_Fires_WhenBlank() => AssertTriggers(new MetaDescriptionMissingRule(), Perfect() with { MetaDescription = null });
    [Fact] public void MetaDescriptionLength_Fires_WhenTooShort() => AssertTriggers(new MetaDescriptionLengthRule(), Perfect() with { MetaDescription = "توضیح" });
    [Fact] public void MetaDescriptionLength_Fires_WhenTooLong() => AssertTriggers(new MetaDescriptionLengthRule(), Perfect() with { MetaDescription = new string('ت', 200) });
    [Fact] public void H1Missing_Fires_WhenNoH1() => AssertTriggers(new H1MissingRule(), Perfect() with { H1s = [] });
    [Fact] public void MultipleH1_Fires_WhenMoreThanOne() => AssertTriggers(new MultipleH1Rule(), Perfect() with { H1s = ["اول", "دوم"] });
    [Fact] public void HeadingStructure_Fires_WhenALevelIsSkipped() => AssertTriggers(new HeadingStructureRule(), Perfect() with { HeadingLevels = [1, 3] });
    [Fact] public void HeadingStructure_IsSilent_WhenLevelsDescendByOne() => AssertSilent(new HeadingStructureRule(), Perfect() with { HeadingLevels = [1, 2, 3, 4] });

    [Fact] public void CanonicalMissing_Fires_WhenAbsent() => AssertTriggers(new CanonicalMissingRule(), Perfect() with { Canonical = null });
    [Fact] public void CanonicalInvalid_Fires_WhenNotAnAbsoluteHttpUrl() => AssertTriggers(new CanonicalInvalidRule(), Perfect() with { Canonical = "/relative/path" });
    [Fact] public void CanonicalInvalid_IsSilent_WhenAbsent() => AssertSilent(new CanonicalInvalidRule(), Perfect() with { Canonical = null });
    [Fact] public void CanonicalMismatch_Fires_WhenPointingElsewhere() => AssertTriggers(new CanonicalMismatchRule(), Perfect() with { Canonical = "https://example.com/other-page" });
    [Fact] public void CanonicalMismatch_IgnoresTrailingSlash() => AssertSilent(new CanonicalMismatchRule(), Perfect() with { Canonical = Url + "/" });
    [Fact] public void NoIndex_Fires_OnMetaRobotsNoindex() => AssertTriggers(new NoIndexRule(), Perfect() with { RobotsMeta = "noindex, nofollow" });
    [Fact] public void NoIndex_DoesNotFire_OnABrokenPage() => AssertSilent(new NoIndexRule(), Perfect() with { StatusCode = 404, RobotsMeta = null });
    [Fact] public void XRobotsNoIndex_Fires_OnHeaderDirective() => AssertTriggers(new XRobotsNoIndexRule(), Perfect() with { XRobotsTag = "noindex" });
    [Fact] public void XRobotsNoIndex_IsSilent_OnIndexFollow() => AssertSilent(new XRobotsNoIndexRule(), Perfect() with { XRobotsTag = "index, follow" });

    [Fact] public void BrokenStatus_Fires_On4xx() => AssertTriggers(new BrokenStatusRule(), Perfect() with { StatusCode = 404 });
    [Fact] public void BrokenStatus_EscalatesToHigh_On5xx() => Assert.Equal(IssueSeverity.High, new BrokenStatusRule().Evaluate(Perfect() with { StatusCode = 500 }).Severity);
    [Fact] public void BrokenStatus_StaysMedium_On4xx() => Assert.Equal(IssueSeverity.Medium, new BrokenStatusRule().Evaluate(Perfect() with { StatusCode = 404 }).Severity);
    [Fact] public void RedirectedStatus_Fires_On3xx() => AssertTriggers(new RedirectedStatusRule(), Perfect() with { StatusCode = 301 });
    [Fact] public void RedirectChainLong_Fires_AfterTwoHops() => AssertTriggers(new RedirectChainLongRule(), Perfect() with { RedirectChainJson = """["https://a.example/","https://b.example/","https://c.example/"]""" });
    [Fact] public void RedirectChainLong_IsSilent_AtTwoHops() => AssertSilent(new RedirectChainLongRule(), Perfect() with { RedirectChainJson = """["https://a.example/","https://b.example/"]""" });
    [Fact] public void ContentTypeMissing_Fires_WhenUndeclared() => AssertTriggers(new EmptyContentTypeRule(), Perfect() with { ContentType = null });
    [Fact] public void DeepClickDepth_Fires_BeyondThreeClicks() => AssertTriggers(new DeepClickDepthRule(), Perfect() with { Depth = 4 });
    [Fact] public void DeepClickDepth_IsSilent_AtThreeClicks() => AssertSilent(new DeepClickDepthRule(), Perfect() with { Depth = 3 });
    [Fact] public void SlowResponse_Fires_AboveThreshold() => AssertTriggers(new SlowResponseRule(), Perfect() with { ResponseTimeMs = 2000 });

    [Fact] public void LowWordCount_Fires_Below250() => AssertTriggers(new LowWordCountRule(), Perfect() with { WordCount = 120 });
    [Fact] public void ThinContent_Fires_Below150Words() => AssertTriggers(new ThinContentRule(), Perfect() with { WordCount = 80 });
    [Fact] public void ThinContent_IsSilent_OnANonIndexablePage() => AssertSilent(new ThinContentRule(), Perfect() with { WordCount = 80, IsIndexable = false });
    [Fact] public void KeywordStuffing_Fires_WhenATermIsOverUsed() => AssertTriggers(new KeywordStuffingRule(), Perfect() with { WordCount = 600, TextContent = RepeatedTermText("سئو", 8, 20) });
    [Fact] public void KeywordStuffing_IsSilent_OnNaturalProse() => AssertSilent(new KeywordStuffingRule(), Perfect() with { TextContent = Sentences(6, 20) });
    [Fact] public void LongSentences_Fire_WhenMostSentencesAreLong() => AssertTriggers(new LongSentencesRule(), Perfect() with { WordCount = 200, TextContent = Sentences(5, 30) });
    [Fact] public void LongSentences_AreSilent_WhenProseIsReadable() => AssertSilent(new LongSentencesRule(), Perfect() with { TextContent = Sentences(6, 20) });
    [Fact] public void MissingAlt_Fires_WhenImagesLackAltText() => AssertTriggers(new MissingAltRule(), Perfect() with { MissingAltCount = 2 });

    [Fact] public void SchemaSyntax_Fires_WhenTypeIsMissing() => AssertTriggers(new SchemaSyntaxRule(), Perfect() with { SchemaJson = """[{"name":"بدون نوع"}]""" });
    [Fact] public void SchemaSyntax_Fires_OnMalformedJson() => AssertTriggers(new SchemaSyntaxRule(), Perfect() with { SchemaJson = "{ این json نیست }" });
    [Fact] public void SchemaMissingRequired_Fires_ForArticleWithoutHeadline() => AssertTriggers(new SchemaMissingRequiredRule(), Perfect() with { SchemaJson = """[{"@type":"Article"}]""" });
    [Fact] public void SchemaMissingRequired_Fires_ForProductWithoutOffers() => AssertTriggers(new SchemaMissingRequiredRule(), Perfect() with { SchemaJson = """[{"@type":"Product","name":"کالا"}]""" });
    [Fact] public void StructuredDataAbsent_Fires_WhenNoJsonLd() => AssertTriggers(new StructuredDataNoticeRule(), Perfect() with { SchemaJson = "[]" });
    [Fact] public void StructuredDataAbsent_IsSilent_OnANonIndexablePage() => AssertSilent(new StructuredDataNoticeRule(), Perfect() with { SchemaJson = "[]", IsIndexable = false });

    [Fact] public void HreflangNoSelf_Fires_WhenNoAlternatePointsAtThePage() => AssertTriggers(new HreflangNoSelfRule(), Perfect() with { HreflangJson = """[{"language":"en-US","target":"https://example.com/other"}]""" });
    [Fact] public void HreflangNoSelf_IsSilent_WhenTheAlternateMatchesTheCanonical() => AssertSilent(new HreflangNoSelfRule(), Perfect() with { HreflangJson = """[{"language":"fa-IR","target":"https://example.com/blog/seo-guide"}]""" });
    [Fact] public void HreflangInvalidCode_Fires_OnAMalformedTag() => AssertTriggers(new HreflangInvalidLanguageRule(), Perfect() with { HreflangJson = """[{"language":"فارسی","target":"https://example.com/blog/seo-guide"}]""" });
    [Fact] public void HreflangInvalidCode_AcceptsXDefault() => AssertSilent(new HreflangInvalidLanguageRule(), Perfect() with { HreflangJson = """[{"language":"x-default","target":"https://example.com/blog/seo-guide"}]""" });

    [Fact] public void HttpsIssue_Fires_OnPlainHttp() => AssertTriggers(new HttpsIssueRule(), Perfect() with { Url = "http://example.com/blog/seo-guide" });
    [Fact] public void HstsMissing_Fires_WhenTheHeaderIsAbsent() => AssertTriggers(new HstsMissingRule(), Perfect() with { HeadersJson = """{"X-Content-Type-Options":"nosniff","X-Frame-Options":"DENY","Cache-Control":"max-age=60"}""" });
    [Fact] public void HstsMissing_IsSilent_OnPlainHttp() => AssertSilent(new HstsMissingRule(), Perfect() with { Url = "http://example.com/x", HeadersJson = "{}" });
    [Fact] public void SecurityHeadersMissing_Fires_WhenDefensiveHeadersAreAbsent() => AssertTriggers(new SecurityHeadersMissingRule(), Perfect() with { HeadersJson = """{"Strict-Transport-Security":"max-age=100","Cache-Control":"max-age=60"}""" });
    [Fact] public void SecurityHeadersMissing_AcceptsCspInPlaceOfFrameOptions() => AssertSilent(new SecurityHeadersMissingRule(), Perfect() with { HeadersJson = """{"X-Content-Type-Options":"nosniff","Content-Security-Policy":"default-src 'self'"}""" });
    [Fact] public void CacheControlMissing_Fires_WhenTheHeaderIsAbsent() => AssertTriggers(new CacheControlMissingRule(), Perfect() with { HeadersJson = """{"Strict-Transport-Security":"max-age=100","X-Content-Type-Options":"nosniff","X-Frame-Options":"DENY"}""" });
    [Fact] public void CacheControlMissing_IsSilent_OnANonOkResponse() => AssertSilent(new CacheControlMissingRule(), Perfect() with { StatusCode = 404, HeadersJson = "{}" });
    [Fact] public void MixedContentAssets_Fire_OnAnInsecureAssetOverHttps() => AssertTriggers(new MixedContentAssetsRule(), Perfect() with { AssetsJson = """[{"type":"Image","isMixedContent":true}]""" });
    [Fact] public void MixedContentAssets_AreSilent_WhenThePageItselfIsInsecure() => AssertSilent(new MixedContentAssetsRule(), Perfect() with { Url = "http://example.com/x", AssetsJson = """[{"type":"Image","isMixedContent":true}]""" });

    [Fact]
    public void ExcessiveResources_Fire_WhenScriptCountIsTooHigh()
    {
        var assets = string.Join(",", Enumerable.Range(0, 30).Select(_ => """{"type":"Script"}"""));
        AssertTriggers(new ExcessiveResourcesRule(), Perfect() with { AssetsJson = $"[{assets}]" });
    }
    [Fact] public void ExcessiveResources_AreSilent_OnAnEmptyAssetList() => AssertSilent(new ExcessiveResourcesRule(), Perfect() with { AssetsJson = "[]" });
    [Fact]
    public void RenderBlockingResources_Fire_ForSynchronousScripts()
    {
        var assets = string.Join(",", Enumerable.Range(0, 5).Select(_ => """{"type":"Script"}"""));
        AssertTriggers(new RenderBlockingResourcesRule(), Perfect() with { AssetsJson = $"[{assets}]" });
    }
    [Fact]
    public void RenderBlockingResources_IgnoreAsyncAndDeferredScripts()
    {
        var assets = string.Join(",", Enumerable.Range(0, 5).Select(_ => """{"type":"Script","extraAttributesJson":"{\"isAsync\":true}"}"""));
        AssertSilent(new RenderBlockingResourcesRule(), Perfect() with { AssetsJson = $"[{assets}]" });
    }
    [Fact] public void ImageDimensionsMissing_Fires_WhenWidthOrHeightIsAbsent() => AssertTriggers(new ImageDimensionsMissingRule(), Perfect() with { AssetsJson = """[{"type":"Image"}]""" });
    [Fact] public void ImageDimensionsMissing_IsSilent_WhenBothAreDeclared() => AssertSilent(new ImageDimensionsMissingRule(), Perfect() with { AssetsJson = """[{"type":"Image","extraAttributesJson":"{\"width\":\"120\",\"height\":\"80\"}"}]""" });
}
