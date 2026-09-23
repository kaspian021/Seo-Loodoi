using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Billing;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Domain.Tests;

public sealed class TenantEntitlementTests
{
    private sealed class NoOpAuditService : IAuditLogService
    {
        public Task RecordAsync(Guid? projectId, Guid actorId, string action, string entityType, string? entityId, string? metadataJson, string? ipAddress, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditLogDto>>([]);
    }

    private static AppDbContext CreateInMemoryDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    [Fact]
    public void TenantEntitlement_InitializesWithSafeDefaults_AndCalculatesIsActive()
    {
        var userId = Guid.NewGuid();
        var entitlement = new TenantEntitlement(userId, "acc_123", "Starter", SubscriptionStatus.Active);

        entitlement.UserId.Should().Be(userId);
        entitlement.LoodoiAccountId.Should().Be("acc_123");
        entitlement.Plan.Should().Be("Starter");
        entitlement.Status.Should().Be(SubscriptionStatus.Active);
        entitlement.MaxProjects.Should().Be(3);
        entitlement.MaxPagesPerMonth.Should().Be(1000);
        entitlement.MaxKeywords.Should().Be(25);
        entitlement.MaxCompetitors.Should().Be(3);
        entitlement.MaxTeamMembers.Should().Be(3);
        entitlement.MaxAiCreditsPerMonth.Should().Be(25);
        entitlement.AiCreditsUsed.Should().Be(0);

        entitlement.IsActive(DateTimeOffset.UtcNow).Should().BeTrue();
        entitlement.IsActive(DateTimeOffset.UtcNow.AddDays(60)).Should().BeFalse("expired period is not active");
    }

    [Fact]
    public void TryConsumeAiCredits_EnforcesMonthlyAllowance()
    {
        var entitlement = new TenantEntitlement(Guid.NewGuid(), "acc_test", "Starter", maxAiCreditsPerMonth: 2);
        var now = DateTimeOffset.UtcNow;

        entitlement.TryConsumeAiCredits(1, now).Should().BeTrue();
        entitlement.AiCreditsUsed.Should().Be(1);

        entitlement.TryConsumeAiCredits(1, now).Should().BeTrue();
        entitlement.AiCreditsUsed.Should().Be(2);

        entitlement.TryConsumeAiCredits(1, now).Should().BeFalse("consuming beyond allowance must be rejected");
        entitlement.AiCreditsUsed.Should().Be(2);

        entitlement.ResetPeriodCredits(now, now.AddMonths(1), now);
        entitlement.AiCreditsUsed.Should().Be(0, "resetting period restores credits");
        entitlement.TryConsumeAiCredits(1, now).Should().BeTrue();
    }

    [Fact]
    public void PlanCatalog_ProvidesFourComprehensiveTiers()
    {
        var plans = PlanCatalog.GetPlans();
        plans.Should().HaveCount(4);

        var free = PlanCatalog.GetPlan(PlanCatalog.Free);
        free.MaxProjects.Should().Be(1);
        free.MaxPagesPerMonth.Should().Be(100);

        var starter = PlanCatalog.GetPlan(PlanCatalog.Starter);
        starter.MaxProjects.Should().Be(3);
        starter.MaxPagesPerMonth.Should().Be(1000);

        var pro = PlanCatalog.GetPlan(PlanCatalog.Pro);
        pro.MaxProjects.Should().Be(10);
        pro.MaxPagesPerMonth.Should().Be(10000);

        var enterprise = PlanCatalog.GetPlan(PlanCatalog.Enterprise);
        enterprise.MaxProjects.Should().Be(50);
        enterprise.MaxPagesPerMonth.Should().Be(100000);
    }

    [Fact]
    public async Task CheckoutSession_GeneratesSignedState_AndProcessCheckoutReturnUpgradesEntitlement()
    {
        await using var db = CreateInMemoryDb();
        var options = Options.Create(new LoodoiBillingOptions
        {
            SecretKey = "test_signing_key_secret_for_hmac_32_chars!",
            EnableDevMock = true
        });
        var service = new EntitlementService(db, options, new NoOpAuditService(), NullLogger<EntitlementService>.Instance, new DevelopmentLoodoiIdentityProvider());
        var userId = Guid.NewGuid();

        // 1. A tenant with no billing history starts on the Free plan (paid plans come only from billing)
        var initial = await service.GetEntitlementsAsync(userId, CancellationToken.None);
        initial.Plan.Should().Be("Free");

        // 2. Create checkout session for Pro
        var session = await service.CreateCheckoutSessionAsync(userId, new CheckoutSessionRequest("Pro"), CancellationToken.None);
        session.SessionToken.Should().NotBeNullOrWhiteSpace();

        // 3. Process return with the signed token
        var upgraded = await service.ProcessCheckoutReturnAsync(userId, new CheckoutReturnRequest(session.SessionToken), CancellationToken.None);
        upgraded.Plan.Should().Be("Pro");
        upgraded.MaxProjects.Should().Be(10);
        upgraded.MaxPagesPerMonth.Should().Be(10000);
        upgraded.MaxAiCreditsPerMonth.Should().Be(100);
        upgraded.IsActive.Should().BeTrue();

        // 4. Verify tampering with signed token is rejected
        var tamperedToken = session.SessionToken[..^4] + "aaaa";
        var tamperAction = () => service.ProcessCheckoutReturnAsync(userId, new CheckoutReturnRequest(tamperedToken), CancellationToken.None);
        await tamperAction.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task ProcessWebhookAsync_VerifiesSignatureAndAppliesPlanUpdate()
    {
        await using var db = CreateInMemoryDb();
        var webhookSecret = "test_webhook_secret_key_32_characters_long!";
        var options = Options.Create(new LoodoiBillingOptions
        {
            WebhookSecret = webhookSecret
        });
        var service = new EntitlementService(db, options, new NoOpAuditService(), NullLogger<EntitlementService>.Instance);
        var userId = Guid.NewGuid();
        // A3: the account must already be bound (at checkout) before a webhook can apply to it.
        db.TenantEntitlements.Add(new TenantEntitlement(userId, "acc_premium", "Free"));
        await db.SaveChangesAsync();

        var payload = new BillingWebhookPayload(
            EventId: "evt_1001",
            EventType: "subscription.updated",
            LoodoiAccountId: "acc_premium",
            UserId: userId,
            Plan: "Enterprise",
            Status: "Active",
            PeriodStart: DateTimeOffset.UtcNow,
            PeriodEnd: DateTimeOffset.UtcNow.AddMonths(1));

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = WebhookSignature.Sign(timestamp, json, webhookSecret);

        // 1. Process valid webhook
        var result = await service.ProcessWebhookAsync(json, signature, timestamp.ToString(), CancellationToken.None);
        result.Should().BeTrue();

        // 2. Check updated entitlement
        var entitlement = await service.GetEntitlementsAsync(userId, CancellationToken.None);
        entitlement.Plan.Should().Be("Enterprise");
        entitlement.MaxProjects.Should().Be(50);
        entitlement.MaxPagesPerMonth.Should().Be(100000);

        // 3. Tampered signature should be rejected
        var invalidResult = await service.ProcessWebhookAsync(json, "sha256=invalid", timestamp.ToString(), CancellationToken.None);
        invalidResult.Should().BeFalse();

        // 4. Expired timestamp (>600s drift) should be rejected
        var oldTimestamp = timestamp - 1000;
        var oldSignature = WebhookSignature.Sign(oldTimestamp, json, webhookSecret);
        var expiredResult = await service.ProcessWebhookAsync(json, oldSignature, oldTimestamp.ToString(), CancellationToken.None);
        expiredResult.Should().BeFalse();
    }
}
