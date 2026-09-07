using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

public sealed record ScoreBreakdown(decimal Overall, IReadOnlyDictionary<IssueCategory, decimal> Categories, string Version);
public interface IScoringEngine { ScoreBreakdown Calculate(IReadOnlyCollection<SeoRuleResult> results); }

public sealed class ScoringEngine : IScoringEngine
{
    public const string Version = "1.1.0";
    private static readonly IReadOnlyDictionary<IssueCategory, decimal> Weights = new Dictionary<IssueCategory, decimal>
    {
        [IssueCategory.Technical] = .20m, [IssueCategory.Indexability] = .15m, [IssueCategory.OnPage] = .15m,
        [IssueCategory.Content] = .15m, [IssueCategory.InternalLinks] = .10m, [IssueCategory.StructuredData] = .05m,
        [IssueCategory.Performance] = .10m, [IssueCategory.International] = .05m, [IssueCategory.Security] = .05m
    };
    private static decimal MaximumPenalty(IssueSeverity severity) => severity switch { IssueSeverity.Critical => 40, IssueSeverity.High => 25, IssueSeverity.Medium => 15, IssueSeverity.Low => 7, _ => 2 };

    public ScoreBreakdown Calculate(IReadOnlyCollection<SeoRuleResult> results)
    {
        var categories = new Dictionary<IssueCategory, decimal>();
        foreach (var category in Enum.GetValues<IssueCategory>())
        {
            var penalty = results.Where(x => x.Category == category).GroupBy(x => x.Code).Sum(group =>
            {
                var affected = group.Count(x => x.Triggered);
                if (affected == 0) return 0m;
                var severity = group.Where(x => x.Triggered).Max(x => x.Severity);
                var affectedRatio = (decimal)affected / group.Count();
                return MaximumPenalty(severity) * affectedRatio;
            });
            categories[category] = decimal.Round(Math.Max(0m, 100m - penalty), 1, MidpointRounding.AwayFromZero);
        }
        var overall = decimal.Round(Weights.Sum(w => categories[w.Key] * w.Value), 1, MidpointRounding.AwayFromZero);
        return new(overall, categories, Version);
    }
}
