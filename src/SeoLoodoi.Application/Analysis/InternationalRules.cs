using System.Text.Json;
using System.Text.RegularExpressions;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

public sealed class HreflangNoSelfRule : ISeoRule
{
    public string Code => "HREFLANG_NO_SELF_REFERENCE";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (string.IsNullOrWhiteSpace(c.HreflangJson) || c.HreflangJson == "[]")
            return new(Code, false, IssueSeverity.Medium, IssueCategory.International, null);

        var hasSelf = false;
        var count = 0;
        try
        {
            using var doc = JsonDocument.Parse(c.HreflangJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    count++;
                    var target = el.TryGetProperty("target", out var tProp) ? tProp.GetString() : (el.TryGetProperty("Target", out var tProp2) ? tProp2.GetString() : null);
                    if (target is not null && (Equivalent(target, c.Url) || (c.Canonical is not null && Equivalent(target, c.Canonical))))
                    {
                        hasSelf = true;
                        break;
                    }
                }
            }
        }
        catch
        {
            return new(Code, false, IssueSeverity.Medium, IssueCategory.International, null);
        }

        var triggered = count > 0 && !hasSelf;
        return new(Code, triggered, IssueSeverity.Medium, IssueCategory.International,
            triggered ? new("hreflangSelf", "Missing self-referencing alternate", c.Canonical ?? c.Url) : null);
    }

    private static bool Equivalent(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var uriA) &&
        Uri.TryCreate(b, UriKind.Absolute, out var uriB) &&
        string.Equals(uriA.IdnHost, uriB.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uriA.AbsolutePath.TrimEnd('/'), uriB.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

public sealed class HreflangInvalidLanguageRule : ISeoRule
{
    public string Code => "HREFLANG_INVALID_CODE";
    private static readonly Regex ValidTagRegex = new(@"^(?:[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*|x-default)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (string.IsNullOrWhiteSpace(c.HreflangJson) || c.HreflangJson == "[]")
            return new(Code, false, IssueSeverity.Medium, IssueCategory.International, null);

        var invalidTags = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(c.HreflangJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var lang = el.TryGetProperty("language", out var lProp) ? lProp.GetString() : (el.TryGetProperty("Language", out var lProp2) ? lProp2.GetString() : null);
                    if (string.IsNullOrWhiteSpace(lang) || !ValidTagRegex.IsMatch(lang.Trim()))
                    {
                        invalidTags.Add(lang ?? "null");
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        var triggered = invalidTags.Count > 0;
        return new(Code, triggered, IssueSeverity.Medium, IssueCategory.International,
            triggered ? new("invalidHreflangTags", string.Join(", ", invalidTags), "Valid ISO 639-1 language code or BCP 47 / x-default") : null);
    }
}
