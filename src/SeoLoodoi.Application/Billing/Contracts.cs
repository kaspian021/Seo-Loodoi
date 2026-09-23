namespace SeoLoodoi.Application.Billing;

public sealed record PlanDefinitionDto(
    string PlanId,
    string Name,
    string Description,
    long PriceTomansPerMonth,
    int MaxProjects,
    int MaxPagesPerMonth,
    int MaxKeywords,
    int MaxCompetitors,
    int MaxTeamMembers,
    int MaxAiCreditsPerMonth,
    int RetentionDays,
    IReadOnlyList<string> Features);

public sealed record TenantEntitlementDto(
    Guid UserId,
    string? LoodoiAccountId,
    string Plan,
    string Status,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    int MaxProjects,
    int ProjectsUsed,
    int MaxPagesPerMonth,
    int PagesUsed,
    int MaxKeywords,
    int KeywordsUsed,
    int MaxCompetitors,
    int CompetitorsUsed,
    int MaxTeamMembers,
    int TeamMembersUsed,
    int MaxAiCreditsPerMonth,
    int AiCreditsUsed,
    int RetentionDays,
    IReadOnlyList<string> Features,
    bool IsActive);

public sealed record CheckoutSessionRequest(string TargetPlan, string? ReturnUrl = null);

public sealed record CheckoutSessionResponse(string CheckoutUrl, string SessionToken, DateTimeOffset ExpiresAt);

public sealed record CheckoutReturnRequest(string SignedToken);

public sealed record BillingWebhookPayload(
    string EventId,
    string EventType,
    string LoodoiAccountId,
    Guid UserId,
    string Plan,
    string Status,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    int? MaxProjects = null,
    int? MaxPagesPerMonth = null,
    int? MaxKeywords = null,
    int? MaxCompetitors = null,
    int? MaxTeamMembers = null,
    int? MaxAiCreditsPerMonth = null,
    int? RetentionDays = null,
    string[]? Features = null);

public interface IEntitlementService
{
    Task<TenantEntitlementDto> GetEntitlementsAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<PlanDefinitionDto>> GetAvailablePlansAsync(CancellationToken ct);
    Task<CheckoutSessionResponse> CreateCheckoutSessionAsync(Guid userId, CheckoutSessionRequest request, CancellationToken ct);
    Task<TenantEntitlementDto> ProcessCheckoutReturnAsync(Guid userId, CheckoutReturnRequest request, CancellationToken ct);
    Task<bool> ProcessWebhookAsync(string payloadJson, string? signatureHeader, string? timestampHeader, CancellationToken ct);
    Task<bool> ConsumeAiCreditsAsync(Guid userId, int amount, CancellationToken ct);
}
