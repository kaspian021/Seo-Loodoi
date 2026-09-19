using System.Text.Json;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

public sealed class SchemaSyntaxRule : ISeoRule
{
    public string Code => "SCHEMA_SYNTAX_INVALID";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (string.IsNullOrWhiteSpace(c.SchemaJson) || c.SchemaJson == "[]")
            return new(Code, false, IssueSeverity.Medium, IssueCategory.StructuredData, null);

        var errors = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(c.SchemaJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        try
                        {
                            using var inner = JsonDocument.Parse(el.GetString() ?? "{}");
                            ValidateObject(inner.RootElement, errors);
                        }
                        catch
                        {
                            errors.Add("Invalid JSON syntax inside script block");
                        }
                    }
                    else if (el.ValueKind == JsonValueKind.Object)
                    {
                        ValidateObject(el, errors);
                    }
                }
            }
        }
        catch
        {
            errors.Add("Malformed Schema JSON array");
        }

        var triggered = errors.Count > 0;
        return new(Code, triggered, IssueSeverity.Medium, IssueCategory.StructuredData,
            triggered ? new("schemaSyntax", string.Join("; ", errors.Take(3)), "Valid JSON-LD object with @context and @type") : null);
    }

    private static void ValidateObject(JsonElement el, List<string> errors)
    {
        if (!el.TryGetProperty("@type", out var type) || string.IsNullOrWhiteSpace(type.GetString()))
        {
            errors.Add("Missing @type directive in JSON-LD");
        }
    }
}

public sealed class SchemaMissingRequiredRule : ISeoRule
{
    public string Code => "SCHEMA_MISSING_REQUIRED";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (string.IsNullOrWhiteSpace(c.SchemaJson) || c.SchemaJson == "[]")
            return new(Code, false, IssueSeverity.Low, IssueCategory.StructuredData, null);

        var missing = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(c.SchemaJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    JsonDocument? innerDoc = null;
                    try
                    {
                        var target = el;
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            innerDoc = JsonDocument.Parse(el.GetString() ?? "{}");
                            target = innerDoc.RootElement;
                        }

                        if (target.ValueKind == JsonValueKind.Object && target.TryGetProperty("@type", out var typeProp))
                        {
                            var type = typeProp.GetString() ?? "";
                            CheckRequiredProperties(type, target, missing);
                        }
                    }
                    finally
                    {
                        innerDoc?.Dispose();
                    }
                }
            }
        }
        catch
        {
            // Syntax issues caught by SchemaSyntaxRule
        }

        var triggered = missing.Count > 0;
        return new(Code, triggered, IssueSeverity.Low, IssueCategory.StructuredData,
            triggered ? new("missingProperties", string.Join("; ", missing.Take(3)), "Standard required Schema.org fields") : null);
    }

    private static void CheckRequiredProperties(string type, JsonElement el, List<string> missing)
    {
        if (type.Contains("Article", StringComparison.OrdinalIgnoreCase))
        {
            if (!el.TryGetProperty("headline", out _) && !el.TryGetProperty("name", out _))
                missing.Add($"{type} missing 'headline'");
            if (!el.TryGetProperty("author", out _))
                missing.Add($"{type} missing 'author'");
        }
        else if (type.Equals("Product", StringComparison.OrdinalIgnoreCase))
        {
            if (!el.TryGetProperty("name", out _))
                missing.Add("Product missing 'name'");
            if (!el.TryGetProperty("offers", out _) && !el.TryGetProperty("review", out _) && !el.TryGetProperty("aggregateRating", out _))
                missing.Add("Product missing 'offers' or 'review' or 'aggregateRating'");
        }
        else if (type.Equals("BreadcrumbList", StringComparison.OrdinalIgnoreCase))
        {
            if (!el.TryGetProperty("itemListElement", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                missing.Add("BreadcrumbList missing or empty 'itemListElement'");
        }
    }
}

public sealed class StructuredDataNoticeRule : ISeoRule
{
    public string Code => "STRUCTURED_DATA_ABSENT";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var absent = c.IsIndexable && c.StatusCode == 200 && (string.IsNullOrWhiteSpace(c.SchemaJson) || c.SchemaJson == "[]");
        return new(Code, absent, IssueSeverity.Notice, IssueCategory.StructuredData,
            absent ? new("schemaJson", "[]", "JSON-LD structured data for rich snippets eligibility") : null);
    }
}
