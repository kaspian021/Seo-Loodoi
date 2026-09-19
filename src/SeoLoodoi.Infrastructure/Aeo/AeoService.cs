using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Aeo;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Aeo;

public sealed class AeoService(
    AppDbContext db,
    IProjectAccessService access,
    IAeoAnalyzer analyzer,
    IRobotsService robots) : IAeoService
{
    private const int MaxPages = 500;

    public async Task<IReadOnlyList<AiCrawlerProfileDto>> ListProfilesAsync(CancellationToken ct) =>
        await db.AiCrawlerProfiles.AsNoTracking()
            .OrderBy(x => x.Purpose).ThenBy(x => x.DisplayName)
            .Select(x => new AiCrawlerProfileDto(x.Id, x.Key, x.DisplayName, x.UserAgentToken, x.Purpose, x.Weight, x.IsEnabled, x.Notes))
            .ToListAsync(ct);

    public async Task<AiVisibilityReportDto?> LatestAsync(Guid projectId, Guid crawlId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return null;

        var snapshot = await db.AiVisibilitySnapshots.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.CrawlId == crawlId)
            .OrderByDescending(x => x.ComputedAt)
            .FirstOrDefaultAsync(ct);
        if (snapshot is null) return null;

        var report = JsonSerializer.Deserialize<AiVisibilityReportDto>(snapshot.EvidenceJson);
        return report;
    }

    public async Task<AiVisibilityReportDto?> AnalyzeAsync(Guid projectId, Guid crawlId, Guid userId, CancellationToken ct)
    {
        // A viewer may read the assessment; only an editor may spend the outbound
        // robots.txt fetch and persist a new snapshot.
        if (!await access.CanEditAsync(projectId, userId, ct)) return null;

        var project = await db.SeoProjects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId, ct);
        if (project is null) return null;
        var crawlExists = await db.Crawls.AsNoTracking().AnyAsync(x => x.Id == crawlId && x.ProjectId == projectId, ct);
        if (!crawlExists) return null;

        var profiles = await ListProfilesAsync(ct);
        var pages = await LoadPagesAsync(projectId, crawlId, ct);

        string? robotsTxt = null;
        try
        {
            var policy = await robots.GetPolicyAsync(new Uri(project.BaseUrl, UriKind.Absolute), ct);
            robotsTxt = policy.RawText;
        }
        catch (Exception)
        {
            // A failed robots fetch must not fail the whole assessment; the
            // analyzer reports the crawlability score as unknown instead.
        }

        var origin = new Uri(project.BaseUrl, UriKind.Absolute);
        var report = analyzer.Analyze(projectId, crawlId, robotsTxt, origin, profiles, pages);

        var snapshot = await db.AiVisibilitySnapshots
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.CrawlId == crawlId, ct);
        if (snapshot is null)
        {
            snapshot = new AiVisibilitySnapshot(projectId, crawlId);
            db.AiVisibilitySnapshots.Add(snapshot);
        }

        var now = DateTimeOffset.UtcNow;
        snapshot.Apply(
            report.AiCrawlabilityScore,
            report.AnswerReadinessScore,
            report.CitationReadinessScore,
            report.AiVisibilityScore,
            report.CrawlerAccess.Count(x => x.Access == "Allowed"),
            report.CrawlerAccess.Count(x => x.Access == "Blocked"),
            report.CrawlerAccess.Count(x => x.Access == "Unspecified"),
            report.Signals.PagesAnalyzed,
            JsonSerializer.Serialize(report),
            now);

        await db.SaveChangesAsync(ct);
        return report;
    }

    private async Task<IReadOnlyList<AeoPageInput>> LoadPagesAsync(Guid projectId, Guid crawlId, CancellationToken ct) =>
        await db.PageSnapshots.AsNoTracking()
            .Where(s => s.CrawlId == crawlId)
            .Join(db.CrawledUrls.AsNoTracking().Where(u => u.ProjectId == projectId && u.CrawlId == crawlId),
                s => s.CrawledUrlId, u => u.Id,
                (s, u) => new AeoPageInput(u.Url, s.TextContent, s.SchemaJson, s.HeadingsJson, s.Canonical, u.WordCount))
            .Take(MaxPages)
            .ToListAsync(ct);
}
