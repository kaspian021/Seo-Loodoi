using SeoLoodoi.Application.Content;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

public sealed class KeywordStuffingRule(IContentQualityEngine? qualityEngine = null) : ISeoRule
{
    private readonly IContentQualityEngine _engine = qualityEngine ?? new ContentQualityEngine();
    public string Code => "KEYWORD_STUFFING";

    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (string.IsNullOrWhiteSpace(c.TextContent) || c.WordCount < 100)
            return new(Code, false, IssueSeverity.High, IssueCategory.Content, null);

        var analysis = _engine.Analyze(c.TextContent, c.Url);
        var stuffing = analysis.TopKeywords.FirstOrDefault(k => k.IsStuffing);
        var triggered = stuffing is not null;

        return new(Code, triggered, IssueSeverity.High, IssueCategory.Content,
            triggered ? new("keywordDensity", $"'{stuffing!.Term}' at {stuffing.DensityPercentage}% ({stuffing.Count} times)", "< 3.5% keyword density") : null);
    }
}

public sealed class LongSentencesRule(IContentQualityEngine? qualityEngine = null) : ISeoRule
{
    private readonly IContentQualityEngine _engine = qualityEngine ?? new ContentQualityEngine();
    public string Code => "CONTENT_LONG_SENTENCES";

    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (string.IsNullOrWhiteSpace(c.TextContent) || c.WordCount < 100)
            return new(Code, false, IssueSeverity.Low, IssueCategory.Content, null);

        var analysis = _engine.Analyze(c.TextContent, c.Url);
        var triggered = analysis.Readability.SentenceCount >= 4 && analysis.Readability.LongSentencePercentage > 30m;

        return new(Code, triggered, IssueSeverity.Low, IssueCategory.Content,
            triggered ? new("longSentencePercentage", $"{analysis.Readability.LongSentencePercentage}% ({analysis.Readability.LongSentenceCount}/{analysis.Readability.SentenceCount} sentences > 25 words)", "<= 30% long sentences for high readability") : null);
    }
}

public sealed class ThinContentRule : ISeoRule
{
    public string Code => "THIN_CONTENT";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var triggered = c.IsIndexable && c.StatusCode == 200 && c.WordCount > 0 && c.WordCount < 150;
        return new(Code, triggered, IssueSeverity.Medium, IssueCategory.Content,
            triggered ? new("wordCount", c.WordCount.ToString(), ">= 150 meaningful words for indexable content pages") : null);
    }
}
