using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

public enum SubscriptionStatus
{
    Active,
    Trialing,
    PastDue,
    Canceled,
    Expired
}

/// <summary>
/// Authoritative, tenant-scoped entitlement record synchronized from the central
/// Loodoi Billing authority. SEO Loodoi never processes payments directly.
/// </summary>
public sealed class TenantEntitlement : Entity
{
    private TenantEntitlement() { }

    public TenantEntitlement(
        Guid userId,
        string? loodoiAccountId,
        string plan = "Starter",
        SubscriptionStatus status = SubscriptionStatus.Active,
        DateTimeOffset? periodStart = null,
        DateTimeOffset? periodEnd = null,
        int maxProjects = 3,
        int maxPagesPerMonth = 1000,
        int maxKeywords = 25,
        int maxCompetitors = 3,
        int maxTeamMembers = 3,
        int maxAiCreditsPerMonth = 25,
        int aiCreditsUsed = 0,
        int retentionDays = 30,
        string? featuresJson = null)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User ID is required.", nameof(userId));
        UserId = userId;
        LoodoiAccountId = string.IsNullOrWhiteSpace(loodoiAccountId) ? null : ValidateAccountId(loodoiAccountId);
        Plan = string.IsNullOrWhiteSpace(plan) ? "Starter" : plan.Trim();
        Status = status;
        PeriodStart = periodStart ?? DateTimeOffset.UtcNow;
        PeriodEnd = periodEnd ?? PeriodStart.AddDays(30);
        MaxProjects = Math.Max(1, maxProjects);
        MaxPagesPerMonth = Math.Max(10, maxPagesPerMonth);
        MaxKeywords = Math.Max(1, maxKeywords);
        MaxCompetitors = Math.Max(0, maxCompetitors);
        MaxTeamMembers = Math.Max(1, maxTeamMembers);
        MaxAiCreditsPerMonth = Math.Max(0, maxAiCreditsPerMonth);
        AiCreditsUsed = Math.Max(0, aiCreditsUsed);
        RetentionDays = Math.Max(1, retentionDays);
        FeaturesJson = string.IsNullOrWhiteSpace(featuresJson) ? "[]" : featuresJson;
        LastSyncedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }
    /// <summary>
    /// Stable, immutable identifier of the central Loodoi account that owns this
    /// tenant. Null until the Loodoi identity provider has resolved the account
    /// (at the latest when checkout starts). Never derived from e-mail, never
    /// invented by a billing webhook, never changed once set.
    /// </summary>
    public string? LoodoiAccountId { get; private set; }
    public string Plan { get; private set; } = "Starter";
    public SubscriptionStatus Status { get; private set; } = SubscriptionStatus.Active;
    public DateTimeOffset PeriodStart { get; private set; }
    public DateTimeOffset PeriodEnd { get; private set; }
    public int MaxProjects { get; private set; } = 3;
    public int MaxPagesPerMonth { get; private set; } = 1000;
    public int MaxKeywords { get; private set; } = 25;
    public int MaxCompetitors { get; private set; } = 3;
    public int MaxTeamMembers { get; private set; } = 3;
    public int MaxAiCreditsPerMonth { get; private set; } = 25;
    public int AiCreditsUsed { get; private set; }
    public int RetentionDays { get; private set; } = 30;
    public string FeaturesJson { get; private set; } = "[]";
    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>
    /// Limits that may actually be consumed right now. A canceled, expired or
    /// past-due subscription falls back to the Free allowance so premium quota
    /// can never be spent without an active subscription. Existing data is
    /// never deleted by a downgrade; only new consumption is blocked.
    /// </summary>
    public EffectiveLimits Effective(DateTimeOffset now) => IsActive(now)
        ? new(MaxProjects, MaxPagesPerMonth, MaxKeywords, MaxCompetitors, MaxTeamMembers, MaxAiCreditsPerMonth)
        : EffectiveLimits.InactiveFallback;

    public bool IsActive(DateTimeOffset now) =>
        (Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing) &&
        (PeriodEnd >= now || PeriodEnd == DateTimeOffset.MinValue);

    public void UpdateSubscription(
        string plan,
        SubscriptionStatus status,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        int maxProjects,
        int maxPagesPerMonth,
        int maxKeywords,
        int maxCompetitors,
        int maxTeamMembers,
        int maxAiCreditsPerMonth,
        int retentionDays,
        string? featuresJson,
        DateTimeOffset now)
    {
        Plan = string.IsNullOrWhiteSpace(plan) ? Plan : plan.Trim();
        Status = status;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        MaxProjects = Math.Max(1, maxProjects);
        MaxPagesPerMonth = Math.Max(10, maxPagesPerMonth);
        MaxKeywords = Math.Max(1, maxKeywords);
        MaxCompetitors = Math.Max(0, maxCompetitors);
        MaxTeamMembers = Math.Max(1, maxTeamMembers);
        MaxAiCreditsPerMonth = Math.Max(0, maxAiCreditsPerMonth);
        RetentionDays = Math.Max(1, retentionDays);
        if (featuresJson is not null) FeaturesJson = featuresJson;
        LastSyncedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Binds the tenant to its stable central Loodoi account id. Idempotent for
    /// the same id; rebinding to a different id is refused (identity is immutable).
    /// </summary>
    public void LinkLoodoiAccount(string loodoiAccountId, DateTimeOffset now)
    {
        var normalized = ValidateAccountId(loodoiAccountId);
        if (LoodoiAccountId is not null)
        {
            if (!string.Equals(LoodoiAccountId, normalized, StringComparison.Ordinal))
                throw new LoodoiIdentityConflictException("Tenant is already bound to a different Loodoi account.");
            return;
        }
        LoodoiAccountId = normalized;
        UpdatedAt = now;
    }

    public static string ValidateAccountId(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 3 or > 128 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ':' or '.'))
            throw new ArgumentException("Loodoi account ID has an invalid format.", nameof(value));
        return trimmed;
    }

    public bool TryConsumeAiCredits(int amount, DateTimeOffset now)
    {
        if (amount <= 0) return true;
        if (AiCreditsUsed + amount > MaxAiCreditsPerMonth) return false;
        AiCreditsUsed += amount;
        UpdatedAt = now;
        return true;
    }

    public void ResetPeriodCredits(DateTimeOffset newPeriodStart, DateTimeOffset newPeriodEnd, DateTimeOffset now)
    {
        PeriodStart = newPeriodStart;
        PeriodEnd = newPeriodEnd;
        AiCreditsUsed = 0;
        UpdatedAt = now;
    }
}

/// <summary>
/// Server-side record of a checkout redirect. The signed return token only
/// proves that *this* server started *this* checkout for *this* user; it never
/// proves payment. Each session is single-use (anti-replay) and short-lived.
/// </summary>
public sealed class BillingCheckoutSession : Entity
{
    private BillingCheckoutSession() { }

    public BillingCheckoutSession(Guid userId, string nonce, string plan, DateTimeOffset expiresAt)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User ID is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(nonce)) throw new ArgumentException("Nonce is required.", nameof(nonce));
        UserId = userId;
        Nonce = nonce;
        Plan = plan;
        ExpiresAt = expiresAt;
    }

    public Guid UserId { get; private set; }
    public string Nonce { get; private set; } = string.Empty;
    public string Plan { get; private set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }

    public bool TryConsume(DateTimeOffset now)
    {
        if (ConsumedAt is not null || now > ExpiresAt) return false;
        ConsumedAt = now;
        UpdatedAt = now;
        return true;
    }
}

/// <summary>Idempotency ledger for billing webhooks: an EventId is applied at most once.</summary>
public sealed class ProcessedBillingEvent : Entity
{
    private ProcessedBillingEvent() { }

    public ProcessedBillingEvent(string eventId, string eventType, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("Event ID is required.", nameof(eventId));
        EventId = eventId.Trim();
        EventType = (eventType ?? string.Empty).Trim();
        UserId = userId;
    }

    public string EventId { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public Guid UserId { get; private set; }
}

/// <summary>Raised when a tenant would be re-bound to a different Loodoi account.</summary>
public sealed class LoodoiIdentityConflictException(string message) : InvalidOperationException(message) { }

public sealed record EffectiveLimits(int MaxProjects, int MaxPagesPerMonth, int MaxKeywords, int MaxCompetitors, int MaxTeamMembers, int MaxAiCreditsPerMonth)
{
    /// <summary>Free-plan allowance applied while a subscription is not active (AI credits: none).</summary>
    public static readonly EffectiveLimits InactiveFallback = new(1, 100, 10, 1, 1, 0);
}
