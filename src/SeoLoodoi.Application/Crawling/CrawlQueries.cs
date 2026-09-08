namespace SeoLoodoi.Application.Crawling;

public sealed record CrawlSummary(Guid Id, string Status, int PagesDiscovered, int PagesCrawled, int Errors, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, DateTimeOffset? HeartbeatAt, string? AnalysisStatus = null, string? ErrorMessage = null);
public interface ICrawlQueryService { Task<IReadOnlyList<CrawlSummary>> ListAsync(Guid projectId, Guid ownerId, CancellationToken ct); }
