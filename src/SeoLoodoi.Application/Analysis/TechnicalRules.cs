using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

public sealed class MetaDescriptionLengthRule : ISeoRule
{
    public string Code => "META_DESCRIPTION_LENGTH";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var length = c.MetaDescription?.Trim().Length ?? 0;
        return new(Code, length is > 0 and (< 70 or > 170), IssueSeverity.Low, IssueCategory.OnPage, new("metaDescriptionLength", length.ToString(), "70–170 characters"));
    }
}
public sealed class CanonicalInvalidRule : ISeoRule
{
    public string Code => "CANONICAL_INVALID";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var invalid = !string.IsNullOrWhiteSpace(c.Canonical) && (!Uri.TryCreate(c.Canonical, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"));
        return new(Code, invalid, IssueSeverity.High, IssueCategory.Indexability, new("canonical", c.Canonical, "An absolute HTTP(S) URL"));
    }
}
public sealed class CanonicalMismatchRule : ISeoRule
{
    public string Code => "CANONICAL_MISMATCH";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var mismatch = Uri.TryCreate(c.Canonical, UriKind.Absolute, out var canonical) && Uri.TryCreate(c.Url, UriKind.Absolute, out var page) && !Equivalent(page, canonical);
        return new(Code, mismatch, IssueSeverity.Medium, IssueCategory.Indexability, new("canonical", c.Canonical, c.Url));
    }
    private static bool Equivalent(Uri a, Uri b) => string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port && string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal) && string.Equals(a.Query, b.Query, StringComparison.Ordinal);
}
public sealed class NoIndexRule : ISeoRule
{
    public string Code => "NOINDEX";
    public SeoRuleResult Evaluate(PageAnalysisContext c) => new(Code, !c.IsIndexable, IssueSeverity.High, IssueCategory.Indexability, new("isIndexable", c.IsIndexable.ToString(), "true for pages intended for search"));
}
public sealed class HttpsIssueRule : ISeoRule
{
    public string Code => "HTTPS_ISSUE";
    public SeoRuleResult Evaluate(PageAnalysisContext c) => new(Code, Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) && uri.Scheme != "https", IssueSeverity.High, IssueCategory.Security, new("scheme", Uri.TryCreate(c.Url, UriKind.Absolute, out var parsed) ? parsed.Scheme : null, "https"));
}
public sealed class HeadingStructureRule : ISeoRule
{
    public string Code => "HEADING_STRUCTURE";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var levels = c.HeadingLevels ?? [];
        var jump = levels.Zip(levels.Skip(1), (a,b) => b - a).Any(delta => delta > 1);
        return new(Code, jump, IssueSeverity.Low, IssueCategory.OnPage, new("headingLevels", string.Join(",", levels), "No skipped heading levels"));
    }
}
