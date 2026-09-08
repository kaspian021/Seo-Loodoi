namespace SeoLoodoi.Application.Projects;

public sealed record AuditLogDto(Guid Id, Guid? ProjectId, Guid ActorId, string Action, string EntityType, string? EntityId, string MetadataJson, string? IpAddress, DateTimeOffset CreatedAt);

public interface IAuditLogService
{
    Task RecordAsync(Guid? projectId, Guid actorId, string action, string entityType, string? entityId, string? metadataJson, string? ipAddress, CancellationToken ct);
    Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct);
}
