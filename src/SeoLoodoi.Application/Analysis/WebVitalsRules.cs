using System.Text.Json;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

public sealed class RenderBlockingResourcesRule(int threshold = 3) : ISeoRule
{
    public string Code => "RENDER_BLOCKING_RESOURCES";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (string.IsNullOrWhiteSpace(c.AssetsJson) || c.AssetsJson == "[]")
            return new(Code, false, IssueSeverity.Medium, IssueCategory.Performance, null);

        var blockingCount = 0;
        try
        {
            using var doc = JsonDocument.Parse(c.AssetsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var type = el.TryGetProperty("type", out var tProp) ? tProp.GetString() : (el.TryGetProperty("Type", out var tProp2) ? tProp2.GetString() : null);
                    if (type == "Script" || type == "1")
                    {
                        var extraJson = el.TryGetProperty("extraAttributesJson", out var eProp) ? eProp.GetString() : (el.TryGetProperty("ExtraAttributesJson", out var eProp2) ? eProp2.GetString() : null);
                        if (!string.IsNullOrWhiteSpace(extraJson))
                        {
                            try
                            {
                                using var extraDoc = JsonDocument.Parse(extraJson);
                                var isAsync = extraDoc.RootElement.TryGetProperty("isAsync", out var a) && a.GetBoolean();
                                var isDefer = extraDoc.RootElement.TryGetProperty("isDefer", out var d) && d.GetBoolean();
                                if (!isAsync && !isDefer) blockingCount++;
                            }
                            catch
                            {
                                blockingCount++;
                            }
                        }
                        else
                        {
                            blockingCount++;
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        var triggered = blockingCount > threshold;
        return new(Code, triggered, IssueSeverity.Medium, IssueCategory.Performance,
            triggered ? new("renderBlockingScripts", blockingCount.ToString(), $"<= {threshold} synchronous scripts (use async/defer)") : null);
    }
}

public sealed class ImageDimensionsMissingRule : ISeoRule
{
    public string Code => "IMAGE_DIMENSIONS_MISSING";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (string.IsNullOrWhiteSpace(c.AssetsJson) || c.AssetsJson == "[]")
            return new(Code, false, IssueSeverity.Low, IssueCategory.Performance, null);

        var missingCount = 0;
        try
        {
            using var doc = JsonDocument.Parse(c.AssetsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var type = el.TryGetProperty("type", out var tProp) ? tProp.GetString() : (el.TryGetProperty("Type", out var tProp2) ? tProp2.GetString() : null);
                    if (type == "Image" || type == "0")
                    {
                        var extraJson = el.TryGetProperty("extraAttributesJson", out var eProp) ? eProp.GetString() : (el.TryGetProperty("ExtraAttributesJson", out var eProp2) ? eProp2.GetString() : null);
                        if (string.IsNullOrWhiteSpace(extraJson))
                        {
                            missingCount++;
                        }
                        else
                        {
                            try
                            {
                                using var extraDoc = JsonDocument.Parse(extraJson);
                                var hasWidth = extraDoc.RootElement.TryGetProperty("width", out var w) && !string.IsNullOrWhiteSpace(w.GetString());
                                var hasHeight = extraDoc.RootElement.TryGetProperty("height", out var h) && !string.IsNullOrWhiteSpace(h.GetString());
                                if (!hasWidth || !hasHeight) missingCount++;
                            }
                            catch
                            {
                                missingCount++;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        var triggered = missingCount > 0;
        return new(Code, triggered, IssueSeverity.Low, IssueCategory.Performance,
            triggered ? new("imagesWithoutDimensions", missingCount.ToString(), "0 images missing explicit width/height (prevents Cumulative Layout Shift)") : null);
    }
}
