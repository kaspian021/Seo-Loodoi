using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

public sealed class BrokenStatusRule : ISeoRule
{
    public string Code => "BROKEN_STATUS";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var triggered = c.StatusCode >= 400;
        return new(Code, triggered, c.StatusCode >= 500 ? IssueSeverity.High : IssueSeverity.Medium, IssueCategory.Technical, new("statusCode", c.StatusCode.ToString(), "HTTP status below 400"));
    }
}

public sealed class RedirectedStatusRule : ISeoRule
{
    public string Code => "REDIRECTED_PAGE";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var triggered = c.StatusCode is >= 300 and < 400;
        return new(Code, triggered, IssueSeverity.Notice, IssueCategory.Technical, new("statusCode", c.StatusCode.ToString(), "200 for a crawlable content URL"));
    }
}

public sealed class XRobotsNoIndexRule : ISeoRule
{
    public string Code => "X_ROBOTS_NOINDEX";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var triggered = c.XRobotsTag?.Contains("noindex", StringComparison.OrdinalIgnoreCase) == true;
        return new(Code, triggered, IssueSeverity.High, IssueCategory.Indexability, new("xRobotsTag", c.XRobotsTag, "No noindex directive on an indexable page"));
    }
}

public sealed class EmptyContentTypeRule : ISeoRule
{
    public string Code => "CONTENT_TYPE_MISSING";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var triggered = string.IsNullOrWhiteSpace(c.ContentType);
        return new(Code, triggered, IssueSeverity.Low, IssueCategory.Technical, new("contentType", c.ContentType, "A declared response content type"));
    }
}
