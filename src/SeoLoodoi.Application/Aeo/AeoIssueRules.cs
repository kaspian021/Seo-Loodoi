using System.Text.Json;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Aeo;

public sealed record AeoIssueFinding(string Code, IssueCategory Category, string Title, string Description, string EvidenceJson);

/// <summary>
/// Site-level advisory rules, separate from the per-page SEO score. Unknown
/// evidence is not a failed rule. These findings do not predict AI citations.
/// </summary>
public static class AeoIssueRules
{
    public const string Blocked = "AEO_CRAWLERS_BLOCKED";
    public const string AnswerStructure = "AEO_ANSWER_STRUCTURE_MISSING";
    public static readonly string[] Codes = [Blocked, AnswerStructure];

    public static bool WasEvaluated(string code, AiVisibilityReportDto report) => code switch
    {
        Blocked => report.RobotsAvailable,
        AnswerStructure => report.Signals.PagesAnalyzed > 0 && report.Signals.PagesWithEvaluableAnswerStructure == report.Signals.PagesAnalyzed,
        _ => false
    };

    public static IReadOnlyList<AeoIssueFinding> Evaluate(AiVisibilityReportDto report, DateTimeOffset observedAt)
    {
        var findings = new List<AeoIssueFinding>();
        var blocked = report.CrawlerAccess.Where(x => x.Access == "Blocked").Select(x => x.UserAgentToken).ToArray();
        if (report.RobotsAvailable && blocked.Length > 0)
            findings.Add(new(Blocked, IssueCategory.Indexability,
                "دسترسی برخی خزنده‌های هوش مصنوعی مسدود است",
                "robots.txt دسترسی این خزنده‌ها به ریشه سایت را مسدود می‌کند. ممکن است این سیاست عمدی باشد؛ پیش از تغییر، هدف خزنده و سیاست سایت را بررسی کنید. این یافته به معنی نبود استناد یا رتبه نیست.",
                JsonSerializer.Serialize(new { source = "AEO", report.ProjectId, report.CrawlId, observedAt, blockedUserAgents = blocked, scope = "root" })));
        if (WasEvaluated(AnswerStructure, report) && report.Signals.PagesWithQuestionHeadings == 0 && report.Signals.PagesWithFaqSchema == 0)
            findings.Add(new(AnswerStructure, IssueCategory.Content,
                "ساختار پرسش و پاسخ در نمونه تحلیل‌شده مشاهده نشد",
                "در صفحات متنی تحلیل‌شده، عنوان پرسشی یا ساختار FAQ/Q&A مشاهده نشد. فقط در صورت تناسب با محتوا، پاسخ روشن به پرسش کاربران اضافه کنید؛ این توصیه تضمین نمایش در پاسخ موتورهای هوش مصنوعی نیست.",
                JsonSerializer.Serialize(new { source = "AEO", report.ProjectId, report.CrawlId, observedAt, report.Signals.PagesAnalyzed, report.Signals.PagesWithQuestionHeadings, report.Signals.PagesWithFaqSchema, report.Signals.PagesWithEvaluableAnswerStructure, scope = "sample (up to 500 pages)" })));
        return findings;
    }
}
