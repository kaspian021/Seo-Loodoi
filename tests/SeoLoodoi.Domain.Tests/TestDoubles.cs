using SeoLoodoi.Application.Projects;

namespace SeoLoodoi.Domain.Tests;

/// <summary>Minimal service doubles shared by provider-level service tests.</summary>
internal sealed class AccessStub(bool canEdit = true) : IProjectAccessService
{
    public Task<ProjectAccess?> GetAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult<ProjectAccess?>(null);
    public Task<bool> CanViewAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> CanEditAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult(canEdit);
    public Task<bool> CanManageAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult(canEdit);
}

internal sealed class QuotaStub : IQuotaService
{
    public Task<QuotaStatus> GetAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
    public Task EnsureCanCreateProjectAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    public Task EnsureCanStartCrawlAsync(Guid projectId, Guid userId, int requestedPages, CancellationToken ct) => Task.CompletedTask;
    public Task EnsureCanAddKeywordAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.CompletedTask;
    public Task EnsureCanAddCompetitorAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class AuditStub : IAuditLogService
{
    public Task RecordAsync(Guid? projectId, Guid actorId, string action, string entityType, string? entityId, string? metadataJson, string? ipAddress, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditLogDto>>([]);
}
