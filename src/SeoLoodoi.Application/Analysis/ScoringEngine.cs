using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

public sealed record ScoreBreakdown(decimal? Overall, IReadOnlyDictionary<IssueCategory, decimal?> Categories, string Version, bool IsPartial, IReadOnlyList<IssueCategory> CoveredCategories);
public interface IScoringEngine { ScoreBreakdown Calculate(IReadOnlyCollection<SeoRuleResult> results); }

public sealed class ScoringEngine : IScoringEngine
{
    public const string Version = "2.0.0";
    private static readonly IReadOnlyDictionary<IssueCategory, decimal> Weights = new Dictionary<IssueCategory, decimal>
    {
        [IssueCategory.Technical] = .20m, [IssueCategory.Indexability] = .15m, [IssueCategory.OnPage] = .15m,
        [IssueCategory.Content] = .15m, [IssueCategory.InternalLinks] = .10m, [IssueCategory.StructuredData] = .05m,
        [IssueCategory.Performance] = .10m, [IssueCategory.International] = .05m, [IssueCategory.Security] = .05m
    };
    private static decimal MaximumPenalty(IssueSeverity severity) => severity switch { IssueSeverity.Critical => 40, IssueSeverity.High => 25, IssueSeverity.Medium => 15, IssueSeverity.Low => 7, _ => 2 };

    public ScoreBreakdown Calculate(IReadOnlyCollection<SeoRuleResult> results)
    {
        var categories = new Dictionary<IssueCategory, decimal?>();
        var covered = new List<IssueCategory>();
        foreach (var category in Enum.GetValues<IssueCategory>())
        {
            var group = results.Where(x => x.Category == category).ToArray();
            // A category without any evaluated rule has no evidence. Persisting
            // 100 for it would fabricate a perfect score, so it stays null.
            if (group.Length == 0) { categories[category] = null; continue; }
            covered.Add(category);
            var penalty = group.GroupBy(x => x.Code).Sum(rule =>
            {
                var affected = rule.Count(x => x.Triggered);
                if (affected == 0) return 0m;
                var severity = rule.Where(x => x.Triggered).Max(x => x.Severity);
                var affectedRatio = (decimal)affected / rule.Count();
                return MaximumPenalty(severity) * affectedRatio;
            });
            categories[category] = decimal.Round(Math.Max(0m, 100m - penalty), 1, MidpointRounding.AwayFromZero);
        }
        // The overall score is a weighted mean over covered categories only, so
        // unevaluated areas can neither inflate nor deflate the result.
        decimal? overall = null;
        if (covered.Count > 0)
        {
            var weightSum = covered.Sum(c => Weights[c]);
            overall = decimal.Round(covered.Sum(c => categories[c]!.Value * Weights[c]) / weightSum, 1, MidpointRounding.AwayFromZero);
        }
        var isPartial = covered.Count != Weights.Count;
        return new(overall, categories, Version, isPartial, covered);
    }
}
