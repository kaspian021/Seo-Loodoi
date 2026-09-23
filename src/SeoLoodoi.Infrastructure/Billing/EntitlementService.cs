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
        if (!PlanCatalog.IsValidPlan(request.TargetPlan)) throw new ArgumentException("Unknown plan.");
        var ownerId = await FindBillingOwnerAsync(userId, ct);
        var plan = PlanCatalog.GetPlan(request.TargetPlan);
        var returnUrl = ResolveReturnUrl(request.ReturnUrl);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(Math.Clamp(_options.CheckoutSessionMinutes, 5, 120));
        var timestamp = now.ToUnixTimeSeconds();

        // The nonce is persisted server-side so the signed state is single-use.
        db.BillingCheckoutSessions.Add(new BillingCheckoutSession(ownerId, nonce, plan.PlanId, expiresAt));
        await db.SaveChangesAsync(ct);

        var stateData = $"{ownerId:N}|{plan.PlanId}|{nonce}|{timestamp}";
        var signature = WebhookSignature.Sign(timestamp, stateData, _options.SecretKey);
        var signedToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{stateData}|{signature}"));
        var entitlement = await GetOrCreateEntitlementAsync(ownerId, ct);

        var checkoutUrl = _options.EnableDevMock
            // DEVELOPMENT ADAPTER: no payment page exists; bounce straight back.
            ? AppendQuery(returnUrl, "session", signedToken)
            : $"{_options.CheckoutEndpoint}?token={Uri.EscapeDataString(signedToken)}&plan={Uri.EscapeDataString(plan.PlanId)}&account={Uri.EscapeDataString(entitlement.LoodoiAccountId)}&returnUrl={Uri.EscapeDataString(returnUrl)}";

        await audit.RecordAsync(null, userId, "CHECKOUT_INITIATED", "TenantEntitlement", ownerId.ToString(), JsonSerializer.Serialize(new { targetPlan = plan.PlanId, expiresAt, devMock = _options.EnableDevMock }), null, ct);
        return new CheckoutSessionResponse(checkoutUrl, signedToken, expiresAt);
    }

    /// <summary>
    /// Handles the browser returning from Loodoi checkout. The signed state proves
    /// only that this server started the checkout for this user; it is NOT proof of
    /// payment. In production the plan changes exclusively via the signed billing
    /// webhook and this call just validates/consumes the state and returns the
    /// refreshed entitlement. Only the explicitly-enabled development adapter
    /// applies the plan here.
    /// </summary>
    public async Task<TenantEntitlementDto> ProcessCheckoutReturnAsync(Guid userId, CheckoutReturnRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SignedToken)) throw new ArgumentException("A valid signed checkout token is required.");
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.SignedToken.Trim())); }
        catch (FormatException) { throw new ArgumentException("Invalid checkout token format."); }

        var parts = decoded.Split('|');
        if (parts.Length != 5) throw new ArgumentException("Malformed checkout token structure.");
        var (tokenUserIdStr, planId, nonce, timestampStr, signature) = (parts[0], parts[1], parts[2], parts[3], parts[4]);
        if (!Guid.TryParse(tokenUserIdStr, out var tokenUserId) || !long.TryParse(timestampStr, out var timestamp))
            throw new ArgumentException("Corrupted token values.");

        var payloadToVerify = $"{tokenUserId:N}|{planId}|{nonce}|{timestamp}";
        if (!WebhookSignature.Verify(timestamp, payloadToVerify, _options.SecretKey, signature))
            throw new InvalidOperationException("Checkout session token signature verification failed.");

        var ownerId = await FindBillingOwnerAsync(userId, ct);
        if (ownerId != tokenUserId) throw new InvalidOperationException("Token does not match the active authenticated user.");

        var now = DateTimeOffset.UtcNow;
        var session = await db.BillingCheckoutSessions.SingleOrDefaultAsync(x => x.Nonce == nonce, ct);
        if (session is null || session.UserId != ownerId || !string.Equals(session.Plan, planId, StringComparison.Ordinal))
            throw new InvalidOperationException("Unknown checkout session.");
        if (!session.TryConsume(now))
            throw new InvalidOperationException("Checkout session has expired or was already used. Please initiate upgrade again.");
        await db.SaveChangesAsync(ct);

        if (!_options.EnableDevMock)
        {
            await audit.RecordAsync(null, userId, "CHECKOUT_RETURNED", "TenantEntitlement", ownerId.ToString(), JsonSerializer.Serialize(new { plan = planId }), null, ct);
            return await GetEntitlementsAsync(userId, ct);
        }

        // ---- DEVELOPMENT ADAPTER ONLY (EnableDevMock=true; refused outside Development) ----
        var plan = PlanCatalog.GetPlan(planId);
        var entitlement = await GetOrCreateEntitlementAsync(ownerId, ct);
        var periodEnd = now.AddMonths(1);
        entitlement.UpdateSubscription(plan.PlanId, SubscriptionStatus.Active, now, periodEnd, plan.MaxProjects, plan.MaxPagesPerMonth, plan.MaxKeywords,
            plan.MaxCompetitors, plan.MaxTeamMembers, plan.MaxAiCreditsPerMonth, plan.RetentionDays, JsonSerializer.Serialize(plan.Features), now);
        entitlement.ResetPeriodCredits(now, periodEnd, now);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(null, userId, "PLAN_UPGRADED_DEV_MOCK", "TenantEntitlement", entitlement.Id.ToString(), JsonSerializer.Serialize(new { plan = plan.PlanId, periodEnd }), null, ct);
        logger.LogWarning("DEV billing adapter applied plan {Plan} to tenant {UserId} without payment", plan.PlanId, ownerId);
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
        if (ageSeconds > Math.Clamp(_options.WebhookToleranceSeconds, 30, 900))
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
            logger.LogWarning(ex, "Failed to parse billing webhook payload");
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.EventId) || payload.EventId.Length > 128)
        {
            logger.LogWarning("Billing webhook rejected: missing event id");
            return false;
        }
        // Idempotency / replay protection: an already-applied event is acknowledged, never re-applied.
        if (await db.ProcessedBillingEvents.AnyAsync(x => x.EventId == payload.EventId, ct))
        {
            logger.LogInformation("Billing webhook {EventId} already processed; ignoring replay", payload.EventId);
            return true;
        }
        if (!Enum.TryParse<SubscriptionStatus>(payload.Status, true, out var status) || !Enum.IsDefined(status))
        {
            logger.LogWarning("Billing webhook {EventId} rejected: unknown status", payload.EventId);
            return false;
        }
        if (!PlanCatalog.IsValidPlan(payload.Plan))
        {
            logger.LogWarning("Billing webhook {EventId} rejected: unknown plan", payload.EventId);
            return false;
        }

        // Resolve by stable Loodoi account id first; a UserId in the payload must agree with it.
        TenantEntitlement? entitlement = null;
        if (!string.IsNullOrWhiteSpace(payload.LoodoiAccountId))
            entitlement = await db.TenantEntitlements.SingleOrDefaultAsync(x => x.LoodoiAccountId == payload.LoodoiAccountId, ct);
        if (entitlement is not null && payload.UserId != Guid.Empty && entitlement.UserId != payload.UserId)
        {
            logger.LogWarning("Billing webhook {EventId} rejected: account/user mismatch", payload.EventId);
            return false;
        }
        if (entitlement is null && payload.UserId != Guid.Empty)
        {
            entitlement = await db.TenantEntitlements.SingleOrDefaultAsync(x => x.UserId == payload.UserId, ct);
            if (entitlement is not null && !string.IsNullOrWhiteSpace(payload.LoodoiAccountId) && entitlement.LoodoiAccountId != payload.LoodoiAccountId)
                entitlement.LinkLoodoiAccount(payload.LoodoiAccountId);
        }
        if (entitlement is null)
        {
            if (payload.UserId == Guid.Empty || string.IsNullOrWhiteSpace(payload.LoodoiAccountId))
            {
                logger.LogWarning("Billing webhook {EventId} could not resolve a tenant", payload.EventId);
                return false;
            }
            entitlement = new TenantEntitlement(payload.UserId, payload.LoodoiAccountId);
            db.TenantEntitlements.Add(entitlement);
        }

        var planDef = PlanCatalog.GetPlan(payload.Plan);
        var now = DateTimeOffset.UtcNow;
        var newPeriod = payload.PeriodStart != entitlement.PeriodStart;
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
            featuresJson: JsonSerializer.Serialize(payload.Features ?? planDef.Features.ToArray()),
            now: now);
        if (newPeriod) entitlement.ResetPeriodCredits(payload.PeriodStart, payload.PeriodEnd, now);
        db.ProcessedBillingEvents.Add(new ProcessedBillingEvent(payload.EventId, payload.EventType, entitlement.UserId));

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (db.Database.IsRelational())
        {
            // Unique EventId lost a race with a concurrent delivery of the same event.
            logger.LogInformation("Billing webhook {EventId} concurrently processed; ignoring", payload.EventId);
            return true;
        }
        await audit.RecordAsync(null, entitlement.UserId, "BILLING_WEBHOOK_PROCESSED", "TenantEntitlement", entitlement.Id.ToString(), JsonSerializer.Serialize(new { payload.EventId, payload.EventType, payload.Plan, payload.Status }), null, ct);
        logger.LogInformation("Billing webhook {EventId} processed for {UserId}", payload.EventId, entitlement.UserId);
        return true;
    }

    public async Task<bool> ConsumeAiCreditsAsync(Guid userId, int amount, CancellationToken ct)
    {
        if (amount <= 0) return true;
        var ownerId = await FindBillingOwnerAsync(userId, ct);
        var entitlement = await GetOrCreateEntitlementAsync(ownerId, ct);
        var now = DateTimeOffset.UtcNow;
        if (!entitlement.IsActive(now)) return false;

        if (db.Database.IsRelational())
        {
            // Atomic conditional increment: concurrent requests cannot overspend.
            var updated = await db.TenantEntitlements
                .Where(x => x.Id == entitlement.Id && x.AiCreditsUsed + amount <= x.MaxAiCreditsPerMonth)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AiCreditsUsed, x => x.AiCreditsUsed + amount).SetProperty(x => x.UpdatedAt, now), ct);
            return updated == 1;
        }
        var success = entitlement.TryConsumeAiCredits(amount, now);
        if (success) await db.SaveChangesAsync(ct);
        return success;
    }

    private string ResolveReturnUrl(string? requested)
    {
        const string fallback = "/";
        if (string.IsNullOrWhiteSpace(requested)) return fallback;
        var value = requested.Trim();
        // Same-site relative path (reject protocol-relative "//host" and "/\host").
        if (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal) && !value.StartsWith("/\\", StringComparison.Ordinal)) return value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            var origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            if (_options.AllowedReturnOrigins.Any(o => string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase))) return value;
        }
        throw new ArgumentException("Return URL is not an allowed SEO Loodoi address.");
    }

    private static string AppendQuery(string url, string key, string value)
    {
        var fragmentIndex = url.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
        var baseUrl = fragmentIndex >= 0 ? url[..fragmentIndex] : url;
        var separator = baseUrl.Contains('?') ? '&' : '?';
        return $"{baseUrl}{separator}{key}={Uri.EscapeDataString(value)}{fragment}";
    }

    private async Task<TenantEntitlement> GetOrCreateEntitlementAsync(Guid ownerId, CancellationToken ct)
    {
        var existing = await db.TenantEntitlements.SingleOrDefaultAsync(x => x.UserId == ownerId, ct);
        if (existing is not null) return existing;

        // A tenant that has never received a billing event gets the configured
        // default (Free) plan. Paid plans come only from the billing authority.
        var starter = PlanCatalog.GetPlan(PlanCatalog.IsValidPlan(_options.DefaultPlan) ? _options.DefaultPlan : PlanCatalog.Free);
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
