namespace SeoLoodoi.Application.Monitoring;

public sealed record AlertRuleDto(Guid Id, string Type, decimal Threshold, string Channel, string? Destination, bool IsEnabled, DateTimeOffset CreatedAt);
public sealed record CreateAlertRuleRequest(string Type = "SCORE_DROP", decimal Threshold = 5, string Channel = "dashboard", string? Destination = null);
public sealed record UpdateAlertRuleRequest(bool IsEnabled, decimal Threshold, string Channel, string? Destination = null);
public sealed record AlertEventDto(Guid Id, Guid AlertRuleId, string EventType, string PayloadJson, DateTimeOffset DetectedAt, bool IsRead);
public interface IEmailDelivery
{
    Task SendAsync(string email, string subject, string htmlMessage, CancellationToken ct);
}
public interface IAlertService
{
    Task<IReadOnlyList<AlertRuleDto>> ListRulesAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<AlertRuleDto?> CreateRuleAsync(Guid projectId, Guid userId, CreateAlertRuleRequest request, CancellationToken ct);
    Task<bool> UpdateRuleAsync(Guid projectId, Guid ruleId, Guid userId, UpdateAlertRuleRequest request, CancellationToken ct);
    Task<IReadOnlyList<AlertEventDto>> ListEventsAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<int> CheckAsync(Guid projectId, Guid userId, CancellationToken ct);
}
