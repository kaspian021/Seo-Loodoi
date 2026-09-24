using AwesomeAssertions;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// P1 Batch 2 (Indexability + HTTP + URL metadata): trigger / silent / boundary /
/// multilingual coverage for every new deterministic page rule.
/// </summary>
public class TechnicalSeoBatch2RulesTests
{
    private static PageAnalysisContext Perfect() => new(
        Url: "https://example.com/blog/seo-guide", Title: "راهنمای جامع سئو", MetaDescription: "توضیح کامل صفحه",
        H1s: ["عنوان اصلی"], Canonical: "https://example.com/blog/seo-guide", WordCount: 500, ImageCount: 1,
        MissingAltCount: 0, ResponseTimeMs: 120, IsIndexable: true, HeadingLevels: [1, 2], StatusCode: 200,
        ContentType: "text/html", XRobotsTag: null, RobotsMeta: null, RedirectChainJson: "[]", AssetsJson: "[]");

    private static void AssertTriggers(ISeoRule rule, PageAnalysisContext context)
    {
        var result = rule.Evaluate(context);
        result.Triggered.Should().BeTrue($"{rule.Code} must fire here");
        result.Evidence.Should().NotBeNull("every triggered issue must carry machine-readable evidence");
    }

    private static void AssertSilent(ISeoRule rule, PageAnalysisContext context) =>
        rule.Evaluate(context).Triggered.Should().BeFalse($"{rule.Code} must stay silent here");

    // ---- Robots directives ----

    [Fact] public void MetaNoFollow_Fires_OnNoFollowDirective() => AssertTriggers(new MetaRobotsNoFollowRule(), Perfect() with { RobotsMeta = "noindex, nofollow" });
    [Fact] public void MetaNoFollow_IsSilent_OnPlainNoIndex() => AssertSilent(new MetaRobotsNoFollowRule(), Perfect() with { RobotsMeta = "noindex" });
    [Fact] public void MetaNoFollow_IsSilent_WithoutMeta() => AssertSilent(new MetaRobotsNoFollowRule(), Perfect());

    [Fact] public void MetaNoArchive_Fires_OnDirective() => AssertTriggers(new MetaRobotsNoArchiveRule(), Perfect() with { RobotsMeta = "noarchive, nosnippet" });
    [Fact] public void MetaNoArchive_IsSilent_OnLookalikeToken() => AssertSilent(new MetaRobotsNoArchiveRule(), Perfect() with { RobotsMeta = "noarchivex" });

    [Fact] public void MetaNoSnippet_Fires_OnDirective() => AssertTriggers(new MetaRobotsNoSnippetRule(), Perfect() with { RobotsMeta = "nosnippet" });
    [Fact] public void MetaNoSnippet_IsCaseInsensitive() => AssertTriggers(new MetaRobotsNoSnippetRule(), Perfect() with { RobotsMeta = "NoSnippet" });

    [Fact] public void MetaNoImageIndex_Fires_OnDirective() => AssertTriggers(new MetaRobotsNoImageIndexRule(), Perfect() with { RobotsMeta = "noimageindex" });
    [Fact] public void MetaNoImageIndex_IsSilent_OnNoIndexOnly() => AssertSilent(new MetaRobotsNoImageIndexRule(), Perfect() with { RobotsMeta = "noindex" });

    [Fact] public void XRobotsNoFollow_Fires_OnHeaderDirective() => AssertTriggers(new XRobotsNoFollowRule(), Perfect() with { XRobotsTag = "nofollow, noindex" });
    [Fact] public void XRobotsNoFollow_IsSilent_OnMetaOnly() => AssertSilent(new XRobotsNoFollowRule(), Perfect() with { RobotsMeta = "nofollow" });

    [Fact] public void IndexabilityContradiction_Fires_WhenSourcesDisagree() =>
        AssertTriggers(new IndexabilityContradictionRule(), Perfect() with { RobotsMeta = "noindex", XRobotsTag = "index" });
    [Fact] public void IndexabilityContradiction_IsSilent_WhenSourcesAgree() =>
        AssertSilent(new IndexabilityContradictionRule(), Perfect() with { RobotsMeta = "noindex", XRobotsTag = "noindex" });
    [Fact] public void IndexabilityContradiction_IsSilent_WithOneSourceOnly() =>
        AssertSilent(new IndexabilityContradictionRule(), Perfect() with { RobotsMeta = "noindex", XRobotsTag = null });
    [Fact] public void IndexabilityContradiction_Fires_OnSameSourceConflict() =>
        AssertTriggers(new IndexabilityContradictionRule(), Perfect() with { RobotsMeta = "index, noindex" });

    // ---- Canonical ----

    [Fact] public void CanonicalExternal_Fires_OnCrossHostCanonical() =>
        AssertTriggers(new CanonicalExternalRule(), Perfect() with { Canonical = "https://other.example/page" });
    [Fact] public void CanonicalExternal_IsSilent_OnSelfCanonical() => AssertSilent(new CanonicalExternalRule(), Perfect());
    [Fact] public void CanonicalExternal_IsSilent_OnWwwVariantOfSameHost() =>
        AssertSilent(new CanonicalExternalRule(), Perfect() with { Canonical = "https://www.example.com/blog/seo-guide" });
    [Fact] public void CanonicalExternal_IsSilent_OnUnparseableCanonical() =>
        AssertSilent(new CanonicalExternalRule(), Perfect() with { Canonical = "::not a url::" });

    // ---- HTTP ----

    [Fact] public void Soft404_Fires_OnNotFoundTitle() =>
        AssertTriggers(new Soft404Rule(), Perfect() with { Title = "404 - پیدا نشد", WordCount = 4 });
    [Fact] public void Soft404_Fires_OnEnglishMarker() =>
        AssertTriggers(new Soft404Rule(), Perfect() with { Title = "Page not found", WordCount = 3 });
    [Fact] public void Soft404_IsSilent_OnRealArticleMentioning404() =>
        AssertSilent(new Soft404Rule(), Perfect() with { Title = "How to fix 404 errors" });
    [Fact] public void Soft404_Fires_OnEmptyBodyWith200() =>
        AssertTriggers(new Soft404Rule(), Perfect() with { WordCount = 2 });
    [Fact] public void Soft404_IsSilent_OnRealContent() => AssertSilent(new Soft404Rule(), Perfect());
    [Fact] public void Soft404_IsSilent_On404Status() =>
        AssertSilent(new Soft404Rule(), Perfect() with { StatusCode = 404, Title = "پیدا نشد" });

    [Fact] public void RedirectLoop_Fires_OnRepeatedHop() =>
        AssertTriggers(new RedirectLoopRule(), Perfect() with { RedirectChainJson = """["https://a.example/1","https://a.example/2","https://a.example/1"]""" });
    [Fact] public void RedirectLoop_IsSilent_OnAcyclicChain() =>
        AssertSilent(new RedirectLoopRule(), Perfect() with { RedirectChainJson = """["https://a.example/1","https://a.example/2"]""" });
    [Fact] public void RedirectLoop_SurvivesMalformedJson() =>
        AssertSilent(new RedirectLoopRule(), Perfect() with { RedirectChainJson = "not json" });

    [Fact] public void ContentTypeUnsupported_Fires_OnBinaryType() =>
        AssertTriggers(new ContentTypeUnsupportedRule(), Perfect() with { ContentType = "video/mp4" });
    [Fact] public void ContentTypeUnsupported_IsSilent_OnHtmlAndPdf() { AssertSilent(new ContentTypeUnsupportedRule(), Perfect()); AssertSilent(new ContentTypeUnsupportedRule(), Perfect() with { ContentType = "application/pdf" }); }
    [Fact] public void ContentTypeUnsupported_IsSilent_OnMissingType() =>
        AssertSilent(new ContentTypeUnsupportedRule(), Perfect() with { ContentType = null });

    // ---- URL hygiene ----

    [Fact] public void UrlTooLong_Fires_OnLongUrl()
    {
        AssertTriggers(new UrlTooLongRule(), Perfect() with { Url = "https://example.com/" + new string('a', 150) });
    }
    [Fact] public void UrlTooLong_IsSilent_AtBoundary_AndHonoursConfiguredThreshold()
    {
        var url = "https://example.com/" + new string('a', 20);
        AssertSilent(new UrlTooLongRule(1000), Perfect() with { Url = url });
        AssertTriggers(new UrlTooLongRule(10), Perfect() with { Url = url });
    }

    [Fact] public void UrlTooDeep_Fires_OnDeepPath() =>
        AssertTriggers(new UrlTooDeepRule(), Perfect() with { Url = "https://example.com/a/b/c/d/e/f/g/h" });
    [Fact] public void UrlTooDeep_IsSilent_AtBoundary() =>
        AssertSilent(new UrlTooDeepRule(), Perfect() with { Url = "https://example.com/a/b/c/d/e/f" });

    [Fact] public void UrlUpperCase_Fires_OnMixedCasePath() =>
        AssertTriggers(new UrlUpperCaseRule(), Perfect() with { Url = "https://example.com/Blog/Post" });
    [Fact] public void UrlUpperCase_IsSilent_OnUppercaseHostOnly() =>
        AssertSilent(new UrlUpperCaseRule(), Perfect() with { Url = "https://EXAMPLE.com/blog/post" });

    [Fact] public void UrlEncodingAnomaly_Fires_OnEncodedLetter() =>
        AssertTriggers(new UrlEncodingAnomalyRule(), Perfect() with { Url = "https://example.com/%41%42" });
    [Fact] public void UrlEncodingAnomaly_Fires_OnDoubleEncoding() =>
        AssertTriggers(new UrlEncodingAnomalyRule(), Perfect() with { Url = "https://example.com/a%2520b" });
    [Fact] public void UrlEncodingAnomaly_IsSilent_OnLegitimateUtf8Encoding() =>
        AssertSilent(new UrlEncodingAnomalyRule(), Perfect() with { Url = "https://example.com/%D9%85%D8%AD%D8%AA%D9%88%D8%A7" });

    [Fact] public void UrlFragment_Fires_OnFragment() =>
        AssertTriggers(new UrlFragmentRule(), Perfect() with { Url = "https://example.com/page#section" });
    [Fact] public void UrlFragment_IsSilent_WithoutFragment() => AssertSilent(new UrlFragmentRule(), Perfect());

    [Fact] public void UrlTrackingParameter_Fires_OnUtmAndGclid()
    {
        AssertTriggers(new UrlTrackingParameterRule(), Perfect() with { Url = "https://example.com/page?utm_source=x&page=2" });
        AssertTriggers(new UrlTrackingParameterRule(), Perfect() with { Url = "https://example.com/page?gclid=abc" });
    }
    [Fact] public void UrlTrackingParameter_IsSilent_OnNormalParameters() =>
        AssertSilent(new UrlTrackingParameterRule(), Perfect() with { Url = "https://example.com/page?page=2&q=%D8%B3%D8%A6%D9%88" });

    [Fact] public void UrlParameterDuplicate_Fires_OnRepeatedKey() =>
        AssertTriggers(new UrlParameterDuplicateRule(), Perfect() with { Url = "https://example.com/page?a=1&a=2" });
    [Fact] public void UrlParameterDuplicate_IsSilent_OnDistinctKeys() =>
        AssertSilent(new UrlParameterDuplicateRule(), Perfect() with { Url = "https://example.com/page?a=1&b=2" });
}
