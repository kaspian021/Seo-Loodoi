using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Infrastructure.Billing;

public sealed class EntitlementService(
    AppDbContext db,
    IOptions<LoodoiBillingOptions> options,
    IAuditLogService audit,
    ILogger<EntitlementService> logger) : IEntitlementService
{
    private readonly LoodoiBillingOptions _options = options.Value;

    public async Task<TenantEntitlementDto> GetEntitlementsAsync(Guid userId, CancellationToken ct)
    {
        var ownerId = await FindBillingOwnerAsync(userId, ct);
        var entitlement = await GetOrCreateEntitlementAsync(ownerId, ct);
        var now = DateTimeOffset.UtcNow;
        var periodStartUtc = new DateTimeOffset(new DateOnly(now.Year, now.Month, 1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        var projectsUsed = await db.SeoProjects.CountAsync(x => x.OwnerId == ownerId && x.Status != ProjectStatus.Archived, ct);
        var pagesUsed = await (from url in db.CrawledUrls
                               join project in db.SeoProjects on url.ProjectId equals project.Id
                               join crawl in db.Crawls on url.CrawlId equals crawl.Id
                               where url.ProjectId != Guid.Empty && project.OwnerId == ownerId && crawl.StartedAt >= periodStartUtc
                               select url).CountAsync(ct);
        var keywordsUsed = await (from k in db.Keywords
                                  join project in db.SeoProjects on k.ProjectId equals project.Id
                                  where project.OwnerId == ownerId
                                  select k).CountAsync(ct);
        var competitorsUsed = await (from c in db.Competitors
                                     join project in db.SeoProjects on c.ProjectId equals project.Id
                                     where project.OwnerId == ownerId
                                     select c).CountAsync(ct);
        var teamMembersUsed = await (from m in db.ProjectMembers
                                     join project in db.SeoProjects on m.ProjectId equals project.Id
                                     where project.OwnerId == ownerId
                                     select m.UserId).Distinct().CountAsync(ct);

        var features = DeserializeFeatures(entitlement.FeaturesJson);
        return new TenantEntitlementDto(
            UserId: ownerId,
            LoodoiAccountId: entitlement.LoodoiAccountId,
            Plan: entitlement.Plan,
            Status: entitlement.Status.ToString(),
            PeriodStart: entitlement.PeriodStart,
            PeriodEnd: entitlement.PeriodEnd,
            MaxProjects: entitlement.MaxProjects,
            ProjectsUsed: projectsUsed,
            MaxPagesPerMonth: entitlement.MaxPagesPerMonth,
            PagesUsed: pagesUsed,
            MaxKeywords: entitlement.MaxKeywords,
            KeywordsUsed: keywordsUsed,
            MaxCompetitors: entitlement.MaxCompetitors,
            CompetitorsUsed: competitorsUsed,
            MaxTeamMembers: entitlement.MaxTeamMembers,
            TeamMembersUsed: teamMembersUsed,
            MaxAiCreditsPerMonth: entitlement.MaxAiCreditsPerMonth,
            AiCreditsUsed: entitlement.AiCreditsUsed,
            RetentionDays: entitlement.RetentionDays,
            Features: features,
            IsActive: entitlement.IsActive(now));
    }

    public Task<IReadOnlyList<PlanDefinitionDto>> GetAvailablePlansAsync(CancellationToken ct) =>
        Task.FromResult(PlanCatalog.GetPlans());

    public async Task<CheckoutSessionResponse> CreateCheckoutSessionAsync(Guid userId, CheckoutSessionRequest request, CancellationToken ct)
    {
        var ownerId = await FindBillingOwnerAsync(userId, ct);
        var plan = PlanCatalog.GetPlan(request.TargetPlan);
        var nonce = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(30);
        var timestamp = now.ToUnixTimeSeconds();

        // Create signed payload with HMAC-SHA256
        var stateData = $"{ownerId:N}|{plan.PlanId}|{nonce}|{timestamp}";
        var signature = WebhookSignature.Sign(timestamp, stateData, _options.SecretKey);
        var signedToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{stateData}|{signature}"));

        var returnUrl = string.IsNullOrWhiteSpace(request.ReturnUrl) ? "/billing" : request.ReturnUrl.Trim();
        var checkoutUrl = _options.EnableDevMock
            ? $"{returnUrl}?session={Uri.EscapeDataString(signedToken)}&plan={plan.PlanId}"
            : $"{_options.CheckoutEndpoint}?token={Uri.EscapeDataString(signedToken)}&returnUrl={Uri.EscapeDataString(returnUrl)}";

        await audit.RecordAsync(null, userId, "CHECKOUT_INITIATED", "TenantEntitlement", ownerId.ToString(), JsonSerializer.Serialize(new { targetPlan = plan.PlanId, expiresAt }), null, ct);
        return new CheckoutSessionResponse(checkoutUrl, signedToken, expiresAt);
    }

    public async Task<TenantEntitlementDto> ProcessCheckoutReturnAsync(Guid userId, CheckoutReturnRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SignedToken)) throw new ArgumentException("A valid signed checkout token is required.");
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.SignedToken.Trim())); }
        catch (FormatException) { throw new ArgumentException("Invalid checkout token format."); }

        var parts = decoded.Split('|');
        if (parts.Length != 5) throw new ArgumentException("Malformed checkout token structure.");
        var tokenUserIdStr = parts[0];
        var planId = parts[1];
        var nonce = parts[2];
        var timestampStr = parts[3];
        var signature = parts[4];

        if (!Guid.TryParse(tokenUserIdStr, out var tokenUserId) || !long.TryParse(timestampStr, out var timestamp))
            throw new ArgumentException("Corrupted token values.");

        var ageSeconds = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp);
        if (ageSeconds > 1800) throw new InvalidOperationException("Checkout session token has expired. Please initiate upgrade again.");

        var payloadToVerify = $"{tokenUserId:N}|{planId}|{nonce}|{timestamp}";
        if (!WebhookSignature.Verify(timestamp, payloadToVerify, _options.SecretKey, signature))
            throw new InvalidOperationException("Checkout session token signature verification failed.");

        var ownerId = await FindBillingOwnerAsync(userId, ct);
        if (ownerId != tokenUserId) throw new InvalidOperationException("Token does not match the active authenticated user.");

        var plan = PlanCatalog.GetPlan(planId);
        var entitlement = await GetOrCreateEntitlementAsync(ownerId, ct);
        var now = DateTimeOffset.UtcNow;
        var periodEnd = now.AddMonths(1);

        entitlement.UpdateSubscription(
            plan: plan.PlanId,
            status: SubscriptionStatus.Active,
            periodStart: now,
            periodEnd: periodEnd,
            maxProjects: plan.MaxProjects,
            maxPagesPerMonth: plan.MaxPagesPerMonth,
            maxKeywords: plan.MaxKeywords,
            maxCompetitors: plan.MaxCompetitors,
            maxTeamMembers: plan.MaxTeamMembers,
            maxAiCreditsPerMonth: plan.MaxAiCreditsPerMonth,
            retentionDays: plan.RetentionDays,
            featuresJson: JsonSerializer.Serialize(plan.Features),
            now: now);
        entitlement.ResetPeriodCredits(now, periodEnd, now);

        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(null, userId, "PLAN_UPGRADED", "TenantEntitlement", entitlement.Id.ToString(), JsonSerializer.Serialize(new { plan = plan.PlanId, periodEnd }), null, ct);
        logger.LogInformation("Tenant {UserId} successfully upgraded to {Plan}", ownerId, plan.PlanId);

        return await GetEntitlementsAsync(userId, ct);
    }

    public async Task<bool> ProcessWebhookAsync(string payloadJson, string? signatureHeader, string? timestampHeader, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(timestampHeader))
        {
            logger.LogWarning("Billing webhook rejected: missing signature or timestamp");
            return false;
        }

        if (!long.TryParse(timestampHeader, out var timestamp)) return false;
        var ageSeconds = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp);
        if (ageSeconds > 600)
        {
            logger.LogWarning("Billing webhook rejected: timestamp drift too large ({AgeSeconds}s)", ageSeconds);
            return false;
        }

        if (!WebhookSignature.Verify(timestamp, payloadJson, _options.WebhookSecret, signatureHeader))
        {
            logger.LogWarning("Billing webhook signature mismatch");
            return false;
        }

        BillingWebhookPayload payload;
        try { payload = JsonSerializer.Deserialize<BillingWebhookPayload>(payloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException(); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse billing webhook payload");
            return false;
        }

        TenantEntitlement? entitlement = null;
        if (payload.UserId != Guid.Empty)
            entitlement = await db.TenantEntitlements.SingleOrDefaultAsync(x => x.UserId == payload.UserId, ct);
        if (entitlement is null && !string.IsNullOrWhiteSpace(payload.LoodoiAccountId))
            entitlement = await db.TenantEntitlements.SingleOrDefaultAsync(x => x.LoodoiAccountId == payload.LoodoiAccountId, ct);

        if (entitlement is null)
        {
            if (payload.UserId == Guid.Empty)
            {
                logger.LogWarning("Billing webhook could not resolve user for account {LoodoiAccountId}", payload.LoodoiAccountId);
                return false;
            }
            entitlement = new TenantEntitlement(payload.UserId, payload.LoodoiAccountId);
            db.TenantEntitlements.Add(entitlement);
        }

        if (!Enum.TryParse<SubscriptionStatus>(payload.Status, true, out var status))
            status = SubscriptionStatus.Active;

        var planDef = PlanCatalog.GetPlan(payload.Plan);
        var now = DateTimeOffset.UtcNow;
        entitlement.UpdateSubscription(
            plan: planDef.PlanId,
            status: status,
            periodStart: payload.PeriodStart,
            periodEnd: payload.PeriodEnd,
            maxProjects: payload.MaxProjects ?? planDef.MaxProjects,
            maxPagesPerMonth: payload.MaxPagesPerMonth ?? planDef.MaxPagesPerMonth,
            maxKeywords: payload.MaxKeywords ?? planDef.MaxKeywords,
            maxCompetitors: payload.MaxCompetitors ?? planDef.MaxCompetitors,
            maxTeamMembers: payload.MaxTeamMembers ?? planDef.MaxTeamMembers,
            maxAiCreditsPerMonth: payload.MaxAiCreditsPerMonth ?? planDef.MaxAiCreditsPerMonth,
            retentionDays: payload.RetentionDays ?? planDef.RetentionDays,
            featuresJson: payload.Features is not null ? JsonSerializer.Serialize(payload.Features) : null,
            now: now);

        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(null, entitlement.UserId, "BILLING_WEBHOOK_PROCESSED", "TenantEntitlement", entitlement.Id.ToString(), JsonSerializer.Serialize(new { payload.EventId, payload.EventType, payload.Plan, payload.Status }), null, ct);
        logger.LogInformation("Billing webhook {EventId} processed successfully for {UserId}", payload.EventId, entitlement.UserId);
        return true;
    }

    public async Task<bool> ConsumeAiCreditsAsync(Guid userId, int amount, CancellationToken ct)
    {
        var ownerId = await FindBillingOwnerAsync(userId, ct);
        var entitlement = await GetOrCreateEntitlementAsync(ownerId, ct);
        var success = entitlement.TryConsumeAiCredits(amount, DateTimeOffset.UtcNow);
        if (success) await db.SaveChangesAsync(ct);
        return success;
    }

    private async Task<TenantEntitlement> GetOrCreateEntitlementAsync(Guid ownerId, CancellationToken ct)
    {
        var existing = await db.TenantEntitlements.SingleOrDefaultAsync(x => x.UserId == ownerId, ct);
        if (existing is not null) return existing;

        var starter = PlanCatalog.GetPlan(PlanCatalog.Starter);
        var now = DateTimeOffset.UtcNow;
        var created = new TenantEntitlement(
            userId: ownerId,
            loodoiAccountId: $"loodoi_acc_{ownerId:N}",
            plan: starter.PlanId,
            status: SubscriptionStatus.Active,
            periodStart: now,
            periodEnd: now.AddMonths(1),
            maxProjects: starter.MaxProjects,
            maxPagesPerMonth: starter.MaxPagesPerMonth,
            maxKeywords: starter.MaxKeywords,
            maxCompetitors: starter.MaxCompetitors,
            maxTeamMembers: starter.MaxTeamMembers,
            maxAiCreditsPerMonth: starter.MaxAiCreditsPerMonth,
            aiCreditsUsed: 0,
            retentionDays: starter.RetentionDays,
            featuresJson: JsonSerializer.Serialize(starter.Features));

        db.TenantEntitlements.Add(created);
        await db.SaveChangesAsync(ct);
        return created;
    }

    private async Task<Guid> FindBillingOwnerAsync(Guid userId, CancellationToken ct)
    {
        var owned = await db.SeoProjects.Where(x => x.OwnerId == userId).Select(x => (Guid?)x.OwnerId).FirstOrDefaultAsync(ct);
        if (owned is not null) return owned.Value;
        return await db.ProjectMembers.Where(x => x.UserId == userId).Join(db.SeoProjects, member => member.ProjectId, project => project.Id, (_, project) => (Guid?)project.OwnerId).FirstOrDefaultAsync(ct) ?? userId;
    }

    private static IReadOnlyList<string> DeserializeFeatures(string? json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }
}
