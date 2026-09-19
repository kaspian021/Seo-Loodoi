using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

public sealed class DeepClickDepthRule(int maxDepth = 3) : ISeoRule
{
    public string Code => "DEEP_CLICK_DEPTH";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var triggered = c.Depth > maxDepth;
        return new(Code, triggered, IssueSeverity.Medium, IssueCategory.Technical,
            triggered ? new("clickDepth", c.Depth.ToString(), $"<= {maxDepth} clicks from homepage") : null);
    }
}
