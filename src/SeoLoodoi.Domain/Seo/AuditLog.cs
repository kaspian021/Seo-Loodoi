using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

/// <summary>Immutable security and product activity record scoped to a tenant project.</summary>
public sealed class AuditLog : Entity
{
    private AuditLog() { }

    public AuditLog(Guid? projectId, Guid actorId, string action, string entityType, string? entityId = null, string? metadataJson = null, string? ipAddress = null)
    {
        if (actorId == Guid.Empty) throw new ArgumentException("Actor is required.", nameof(actorId));
        if (string.IsNullOrWhiteSpace(action) || action.Trim().Length > 80) throw new ArgumentException("Audit action is required.", nameof(action));
        if (string.IsNullOrWhiteSpace(entityType) || entityType.Trim().Length > 80) throw new ArgumentException("Audit entity type is required.", nameof(entityType));
        ProjectId = projectId;
        ActorId = actorId;
        Action = action.Trim().ToUpperInvariant();
        EntityType = entityType.Trim();
        EntityId = string.IsNullOrWhiteSpace(entityId) ? null : entityId.Trim()[..Math.Min(120, entityId.Trim().Length)];
        MetadataJson = string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson[..Math.Min(20_000, metadataJson.Length)];
        IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress.Trim()[..Math.Min(64, ipAddress.Trim().Length)];
    }

    public Guid? ProjectId { get; private set; }
    public Guid ActorId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public string? EntityId { get; private set; }
    public string MetadataJson { get; private set; } = "{}";
    public string? IpAddress { get; private set; }
}
