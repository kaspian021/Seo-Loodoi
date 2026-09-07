using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

public sealed record PageAnalysisContext(string Url, string? Title, string? MetaDescription, IReadOnlyList<string> H1s, string? Canonical, int WordCount, int ImageCount, int MissingAltCount, long ResponseTimeMs, bool IsIndexable = true, IReadOnlyList<int>? HeadingLevels = null);
public sealed record RuleEvidence(string Field, string? Actual, string Expected);
public sealed record SeoRuleResult(string Code, bool Triggered, IssueSeverity Severity, IssueCategory Category, RuleEvidence? Evidence);
public interface ISeoRule { string Code { get; } SeoRuleResult Evaluate(PageAnalysisContext context); }

public sealed class TitleMissingRule : ISeoRule
{
    public string Code => "TITLE_MISSING";
    public SeoRuleResult Evaluate(PageAnalysisContext c) => new(Code, string.IsNullOrWhiteSpace(c.Title), IssueSeverity.High, IssueCategory.OnPage, new("title", c.Title, "A unique, descriptive title"));
}
public sealed class TitleLengthRule : ISeoRule
{
    public string Code => "TITLE_LENGTH";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var length = c.Title?.Trim().Length ?? 0;
        return new(Code, length is > 0 and (< 20 or > 65), IssueSeverity.Medium, IssueCategory.OnPage, new("titleLength", length.ToString(), "20–65 characters"));
    }
}
public sealed class MetaDescriptionMissingRule : ISeoRule
{
    public string Code => "META_DESCRIPTION_MISSING";
    public SeoRuleResult Evaluate(PageAnalysisContext c) => new(Code, string.IsNullOrWhiteSpace(c.MetaDescription), IssueSeverity.Medium, IssueCategory.OnPage, new("metaDescription", c.MetaDescription, "A useful page description"));
}
public sealed class H1MissingRule : ISeoRule
{
    public string Code => "H1_MISSING";
    public SeoRuleResult Evaluate(PageAnalysisContext c) => new(Code, c.H1s.Count == 0, IssueSeverity.High, IssueCategory.OnPage, new("h1Count", c.H1s.Count.ToString(), "Exactly one primary H1"));
}
public sealed class MultipleH1Rule : ISeoRule
{
    public string Code => "MULTIPLE_H1";
    public SeoRuleResult Evaluate(PageAnalysisContext c) => new(Code, c.H1s.Count > 1, IssueSeverity.Medium, IssueCategory.OnPage, new("h1Count", c.H1s.Count.ToString(), "Exactly one primary H1"));
}
public sealed class CanonicalMissingRule : ISeoRule
{
    public string Code => "CANONICAL_MISSING";
    public SeoRuleResult Evaluate(PageAnalysisContext c) => new(Code, string.IsNullOrWhiteSpace(c.Canonical), IssueSeverity.Medium, IssueCategory.Indexability, new("canonical", c.Canonical, "A valid canonical URL"));
}
public sealed class LowWordCountRule(int threshold = 250) : ISeoRule
{
    public string Code => "LOW_WORD_COUNT";
    public SeoRuleResult Evaluate(PageAnalysisContext c) => new(Code, c.WordCount < threshold, IssueSeverity.Low, IssueCategory.Content, new("wordCount", c.WordCount.ToString(), $">= {threshold} (configurable by page type)"));
}
public sealed class MissingAltRule : ISeoRule
{
    public string Code => "ALT_MISSING";
    public SeoRuleResult Evaluate(PageAnalysisContext c) => new(Code, c.MissingAltCount > 0, IssueSeverity.Low, IssueCategory.OnPage, new("missingAltCount", c.MissingAltCount.ToString(), "0 informative images without alt text"));
}
public sealed class SlowResponseRule(long thresholdMs = 1500) : ISeoRule
{
    public string Code => "SLOW_RESPONSE";
    public SeoRuleResult Evaluate(PageAnalysisContext c) => new(Code, c.ResponseTimeMs > thresholdMs, IssueSeverity.Medium, IssueCategory.Performance, new("responseTimeMs", c.ResponseTimeMs.ToString(), $"<= {thresholdMs} ms"));
}
