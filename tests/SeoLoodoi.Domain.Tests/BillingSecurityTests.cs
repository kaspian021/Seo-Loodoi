using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Billing;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// P0 billing/entitlement hardening. Pins that SEO Loodoi never grants a paid plan
/// on a client-reachable path unless the explicit development adapter is enabled,
/// that checkout state is single-use, that webhooks are idempotent, and that the
/// production configuration validator refuses unsafe defaults.
/// </summary>
public sealed class BillingSecurityTests
{
    private const string SigningKey = "test_signing_key_secret_for_hmac_32_chars!";
    private const string WebhookSecret = "test_webhook_secret_key_32_characters_long!";

    private sealed class NoOpAudit : IAuditLogService
    {
        public Task RecordAsync(Guid? projectId, Guid actorId, string action, string entityType, string? entityId, string? metadataJson, string? ipAddress, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditLogDto>>([]);
    }

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static EntitlementService Service(AppDbContext db, bool devMock, params string[] origins) =>
        new(db, Options.Create(new LoodoiBillingOptions { SecretKey = SigningKey, WebhookSecret = WebhookSecret, EnableDevMock = devMock, AllowedReturnOrigins = [.. origins] }),
            new NoOpAudit(), NullLogger<EntitlementService>.Instance, new DevelopmentLoodoiIdentityProvider());

    /// <summary>Binds a tenant to a Loodoi account the way checkout does in production (A3). Webhooks only resolve bound accounts.</summary>
    private static async Task LinkAsync(AppDbContext db, Guid userId, string accountId)
    {
        db.TenantEntitlements.Add(new TenantEntitlement(userId, accountId, PlanCatalog.Free));
        await db.SaveChangesAsync();
    }

    private static (string Json, string Signature, string Timestamp) SignedWebhook(BillingWebhookPayload payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (json, WebhookSignature.Sign(ts, json, WebhookSecret), ts.ToString());
    }

    [Fact]
    public void Defaults_DevMockIsOff()
    {
        new LoodoiBillingOptions().EnableDevMock.Should().BeFalse("the payment-free adapter must be opt-in");
    }

    [Fact]
    public async Task NewTenant_StartsOnFreePlan_NotAPaidPlan()
    {
        await using var db = Db();
        var ent = await Service(db, devMock: false).GetEntitlementsAsync(Guid.NewGuid(), CancellationToken.None);
        ent.Plan.Should().Be(PlanCatalog.Free);
    }

    [Fact]
    public async Task ProductionReturn_DoesNotGrantPlan_WithoutWebhook()
    {
        await using var db = Db();
        var service = Service(db, devMock: false);
        var userId = Guid.NewGuid();

        var session = await service.CreateCheckoutSessionAsync(userId, new CheckoutSessionRequest("Enterprise", "/"), CancellationToken.None);
        session.CheckoutUrl.Should().StartWith("https://billing.loodoi.com/checkout?");

        var afterReturn = await service.ProcessCheckoutReturnAsync(userId, new CheckoutReturnRequest(session.SessionToken), CancellationToken.None);

        afterReturn.Plan.Should().Be(PlanCatalog.Free, "a signed return state proves who started checkout, never that they paid");
    }

    [Fact]
    public async Task CheckoutReturn_IsSingleUse()
    {
        await using var db = Db();
        var service = Service(db, devMock: true);
        var userId = Guid.NewGuid();
        var session = await service.CreateCheckoutSessionAsync(userId, new CheckoutSessionRequest("Pro"), CancellationToken.None);

        (await service.ProcessCheckoutReturnAsync(userId, new CheckoutReturnRequest(session.SessionToken), CancellationToken.None)).Plan.Should().Be("Pro");

        var replay = () => service.ProcessCheckoutReturnAsync(userId, new CheckoutReturnRequest(session.SessionToken), CancellationToken.None);
        await replay.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CheckoutReturn_ForgedTokenWithoutServerSession_IsRejected()
    {
        await using var db = Db();
        var service = Service(db, devMock: true);
        var userId = Guid.NewGuid();
        await service.GetEntitlementsAsync(userId, CancellationToken.None);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var state = $"{userId:N}|Enterprise|deadbeef|{ts}";
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{state}|{WebhookSignature.Sign(ts, state, SigningKey)}"));

        var act = () => service.ProcessCheckoutReturnAsync(userId, new CheckoutReturnRequest(token), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>("even a validly signed state needs a matching unused server-side session");
    }

    [Fact]
    public async Task CheckoutReturn_OtherUsersToken_IsRejected()
    {
        await using var db = Db();
        var service = Service(db, devMock: true);
        var session = await service.CreateCheckoutSessionAsync(Guid.NewGuid(), new CheckoutSessionRequest("Pro"), CancellationToken.None);

        var act = () => service.ProcessCheckoutReturnAsync(Guid.NewGuid(), new CheckoutReturnRequest(session.SessionToken), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("//evil.example/steal")]
    [InlineData("javascript:alert(1)")]
    public async Task Checkout_RejectsOpenRedirectReturnUrls(string returnUrl)
    {
        await using var db = Db();
        var act = () => Service(db, false, "https://seo.loodoi.com").CreateCheckoutSessionAsync(Guid.NewGuid(), new CheckoutSessionRequest("Pro", returnUrl), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Checkout_AcceptsAllowlistedOriginAndRelativePath()
    {
        await using var db = Db();
        var service = Service(db, false, "https://seo.loodoi.com");
        (await service.CreateCheckoutSessionAsync(Guid.NewGuid(), new CheckoutSessionRequest("Pro", "https://seo.loodoi.com/"), CancellationToken.None)).CheckoutUrl.Should().NotBeEmpty();
        (await service.CreateCheckoutSessionAsync(Guid.NewGuid(), new CheckoutSessionRequest("Pro", "/settings"), CancellationToken.None)).CheckoutUrl.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Checkout_UnknownPlan_Throws()
    {
        await using var db = Db();
        var act = () => Service(db, false).CreateCheckoutSessionAsync(Guid.NewGuid(), new CheckoutSessionRequest("Platinum"), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Webhook_IsIdempotentByEventId()
    {
        await using var db = Db();
        var service = Service(db, false);
        var userId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        await LinkAsync(db, userId, "acc_1");
        var (json, sig, ts) = SignedWebhook(new BillingWebhookPayload("evt_1", "subscription.activated", "acc_1", userId, "Pro", "Active", start, start.AddMonths(1)));

        (await service.ProcessWebhookAsync(json, sig, ts, CancellationToken.None)).Should().BeTrue();
        (await service.ConsumeAiCreditsAsync(userId, 3, CancellationToken.None)).Should().BeTrue();
        (await service.ProcessWebhookAsync(json, sig, ts, CancellationToken.None)).Should().BeTrue("replays are acknowledged");

        var ent = await service.GetEntitlementsAsync(userId, CancellationToken.None);
        ent.AiCreditsUsed.Should().Be(3, "a replayed event must not be re-applied (e.g. resetting credits)");
        (await db.ProcessedBillingEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Webhook_CanceledSubscription_DeactivatesEntitlement()
    {
        await using var db = Db();
        var service = Service(db, false);
        var userId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        await LinkAsync(db, userId, "acc_2");
        var a = SignedWebhook(new BillingWebhookPayload("evt_a", "subscription.activated", "acc_2", userId, "Pro", "Active", start, start.AddMonths(1)));
        var b = SignedWebhook(new BillingWebhookPayload("evt_b", "subscription.canceled", "acc_2", userId, "Pro", "Canceled", start, start.AddMonths(1)));

        await service.ProcessWebhookAsync(a.Json, a.Signature, a.Timestamp, CancellationToken.None);
        await service.ProcessWebhookAsync(b.Json, b.Signature, b.Timestamp, CancellationToken.None);

        var ent = await service.GetEntitlementsAsync(userId, CancellationToken.None);
        ent.IsActive.Should().BeFalse();
        (await service.ConsumeAiCreditsAsync(userId, 1, CancellationToken.None)).Should().BeFalse("inactive subscriptions cannot spend credits");
    }

    [Theory]
    [InlineData("Platinum", "Active")]
    [InlineData("Pro", "Bogus")]
    public async Task Webhook_UnknownPlanOrStatus_IsRejected(string plan, string status)
    {
        await using var db = Db();
        var start = DateTimeOffset.UtcNow;
        var tenant = Guid.NewGuid();
        await LinkAsync(db, tenant, "acc_3");
        var w = SignedWebhook(new BillingWebhookPayload("evt_x", "subscription.updated", "acc_3", tenant, plan, status, start, start.AddMonths(1)));
        (await Service(db, false).ProcessWebhookAsync(w.Json, w.Signature, w.Timestamp, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Webhook_AccountUserMismatch_IsRejected()
    {
        await using var db = Db();
        var service = Service(db, false);
        var owner = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        await LinkAsync(db, owner, "acc_owner");
        var first = SignedWebhook(new BillingWebhookPayload("evt_m1", "subscription.activated", "acc_owner", owner, "Pro", "Active", start, start.AddMonths(1)));
        await service.ProcessWebhookAsync(first.Json, first.Signature, first.Timestamp, CancellationToken.None);

        var hijack = SignedWebhook(new BillingWebhookPayload("evt_m2", "subscription.activated", "acc_owner", Guid.NewGuid(), "Enterprise", "Active", start, start.AddMonths(1)));
        (await service.ProcessWebhookAsync(hijack.Json, hijack.Signature, hijack.Timestamp, CancellationToken.None)).Should().BeFalse();
        (await service.GetEntitlementsAsync(owner, CancellationToken.None)).Plan.Should().Be("Pro");
    }

    // ------------------------------------------------ deployment validation

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> SafeProduction() => new()
    {
        ["DatabaseProvider"] = "Postgres",
        ["ConnectionStrings:Postgres"] = "Host=db;Database=seo;Username=seo;Password=s3cure",
        ["Application:PublicBaseUrl"] = "https://api.seo.loodoi.com",
        ["Application:WebBaseUrl"] = "https://seo.loodoi.com",
        ["Billing:SecretKey"] = new string('a', 40),
        ["Billing:WebhookSecret"] = new string('b', 40),
        ["Billing:CheckoutEndpoint"] = "https://billing.loodoi.com/checkout",
        ["Billing:EnableDevMock"] = "false",
        ["Identity:RequireConfirmedEmail"] = "true",
        ["Email:Enabled"] = "true",
        ["Email:Host"] = "smtp.loodoi.com",
        ["Email:FromAddress"] = "no-reply@loodoi.com",
    };

    [Fact]
    public void Validator_AcceptsSafeProductionConfig()
    {
        DeploymentConfigurationValidator.Validate(Config(SafeProduction())).Should().BeEmpty();
    }

    [Fact]
    public void Validator_RejectsEmptyConfig_WithoutLeakingValues()
    {
        var errors = DeploymentConfigurationValidator.Validate(Config([]));
        errors.Should().Contain(e => e.Contains("Billing:SecretKey"));
        errors.Should().Contain(e => e.Contains("Billing:WebhookSecret"));
        errors.Should().Contain(e => e.Contains("Email:Enabled"));
        errors.Should().NotContain(e => e.Contains(LoodoiBillingOptions.DevSigningKey));
    }

    [Theory]
    [InlineData("Billing:EnableDevMock", "true")]
    [InlineData("Billing:SecretKey", LoodoiBillingOptions.DevSigningKey)]
    [InlineData("Billing:WebhookSecret", "short")]
    [InlineData("Email:Enabled", "false")]
    [InlineData("Email:Host", "")]
    [InlineData("Identity:RequireConfirmedEmail", "false")]
    [InlineData("DatabaseProvider", "InMemory")]
    [InlineData("ConnectionStrings:Postgres", "Host=x;Password=CHANGE_ME")]
    [InlineData("Application:PublicBaseUrl", "http://api.seo.loodoi.com")]
    public void Validator_RejectsEachUnsafeSetting(string key, string value)
    {
        var cfg = SafeProduction();
        cfg[key] = value;
        DeploymentConfigurationValidator.Validate(Config(cfg)).Should().NotBeEmpty();
    }

    [Fact]
    public void Validator_AllowsExplicitUnverifiedOptOut()
    {
        var cfg = SafeProduction();
        cfg["Email:Enabled"] = "false";
        cfg["Identity:RequireConfirmedEmail"] = "false";
        cfg["Identity:AllowUnverifiedAccountsInProduction"] = "true";
        DeploymentConfigurationValidator.Validate(Config(cfg)).Should().BeEmpty();
    }
}
