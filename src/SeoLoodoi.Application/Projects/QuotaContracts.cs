namespace SeoLoodoi.Application.Projects;

public sealed record QuotaStatus(string Plan, int MaxProjects, int ProjectsUsed, int PagesPerMonth, int PagesUsed, int MaxKeywords, int KeywordsUsed, int MaxCompetitors, int CompetitorsUsed, DateOnly PeriodStart);

public interface IQuotaService
{
    Task<QuotaStatus> GetAsync(Guid userId, CancellationToken ct);
    Task EnsureCanCreateProjectAsync(Guid userId, CancellationToken ct);
    Task EnsureCanStartCrawlAsync(Guid projectId, Guid userId, int requestedPages, CancellationToken ct);
    Task EnsureCanAddKeywordAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task EnsureCanAddCompetitorAsync(Guid projectId, Guid userId, CancellationToken ct);
}

public sealed class QuotaExceededException(string message) : InvalidOperationException(message) { }
