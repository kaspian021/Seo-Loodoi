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

public sealed class RedirectChainLongRule : ISeoRule
{
    public string Code => "REDIRECT_CHAIN_LONG";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var hops = CountHops(c.RedirectChainJson);
        var triggered = hops > 2;
        return new(Code, triggered, IssueSeverity.Medium, IssueCategory.Technical, new("redirectHops", hops.ToString(), "<= 2 redirect hops"));
    }

    private static int CountHops(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}

public sealed class MixedContentAssetsRule : ISeoRule
{
    public string Code => "MIXED_CONTENT_ASSETS";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var isHttps = Uri.TryCreate(c.Url, UriKind.Absolute, out var pageUri) && pageUri.Scheme == "https";
        if (!isHttps || string.IsNullOrWhiteSpace(c.AssetsJson) || c.AssetsJson == "[]")
            return new(Code, false, IssueSeverity.High, IssueCategory.Security, null);

        var count = 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(c.AssetsJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("isMixedContent", out var mixedProp) && mixedProp.GetBoolean())
                        count++;
                    else if (el.TryGetProperty("IsMixedContent", out var mixedProp2) && mixedProp2.GetBoolean())
                        count++;
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return new(Code, count > 0, IssueSeverity.High, IssueCategory.Security, new("insecureAssetsCount", count.ToString(), "0 insecure HTTP assets on HTTPS page"));
    }
}

public sealed class ExcessiveResourcesRule(int scriptLimit = 25, int totalLimit = 75) : ISeoRule
{
    public string Code => "EXCESSIVE_RESOURCES";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (string.IsNullOrWhiteSpace(c.AssetsJson) || c.AssetsJson == "[]")
            return new(Code, false, IssueSeverity.Low, IssueCategory.Performance, null);

        var total = 0;
        var scripts = 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(c.AssetsJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                total = doc.RootElement.GetArrayLength();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var type = el.TryGetProperty("type", out var tProp) ? tProp.GetString() : (el.TryGetProperty("Type", out var tProp2) ? tProp2.GetString() : null);
                    if (type == "Script" || type == "1") scripts++;
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        var triggered = scripts > scriptLimit || total > totalLimit;
        return new(Code, triggered, IssueSeverity.Low, IssueCategory.Performance, new("totalAssets", $"total={total}, scripts={scripts}", $"total <= {totalLimit}, scripts <= {scriptLimit}"));
    }
}
