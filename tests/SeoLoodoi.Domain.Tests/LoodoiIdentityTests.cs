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

/// <summary>P0 A3: the stable Loodoi identity is resolved server-side, bound once, and never invented by the webhook.</summary>
public sealed class LoodoiIdentityTests
{
    private const string WebhookSecret = "test_webhook_secret_key_32_characters_long!";
    private sealed class NoOpAudit : IAuditLogService
    {
        public Task RecordAsync(Guid? projectId, Guid actorId, string action, string entityType, string? entityId, string? metadataJson, string? ipAddress, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditLogDto>>([]);
    }
    private sealed class FixedIdentity(string? id) : ILoodoiIdentityProvider
    {
        public bool IsDevelopmentAdapter => false;
        public Task<string?> ResolveAccountIdAsync(Guid authenticatedUserId, CancellationToken ct) => Task.FromResult(id);
    }

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
    private static EntitlementService Service(AppDbContext db, ILoodoiIdentityProvider? identity) =>
        new(db, Options.Create(new LoodoiBillingOptions { SecretKey = "test_signing_key_secret_for_hmac_32_chars!", WebhookSecret = WebhookSecret }), new NoOpAudit(), NullLogger<EntitlementService>.Instance, identity);

    private static Task<bool> SendAsync(EntitlementService s, string eventId, string account, Guid user, string plan, string status, DateTimeOffset start)
    {
        var json = JsonSerializer.Serialize(new BillingWebhookPayload(eventId, "subscription.updated", account, user, plan, status, start, start.AddMonths(1)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return s.ProcessWebhookAsync(json, WebhookSignature.Sign(ts, json, WebhookSecret), ts.ToString(), CancellationToken.None);
    }

    [Fact]
    public async Task NewTenant_HasNoInventedAccountId()
    {
        await using var db = Db();
        (await Service(db, null).GetEntitlementsAsync(Guid.NewGuid(), default)).LoodoiAccountId.Should().BeNull();
    }

    [Fact]
    public async Task Checkout_WithoutIdentityService_IsRefused_NotImprovised()
    {
        await using var db = Db();
        var act = () => Service(db, new UnconfiguredLoodoiIdentityProvider()).CreateCheckoutSessionAsync(Guid.NewGuid(), new CheckoutSessionRequest("Pro"), default);
        await act.Should().ThrowAsync<LoodoiIdentityUnavailableException>();
        (await db.TenantEntitlements.AnyAsync(x => x.LoodoiAccountId != null)).Should().BeFalse();
    }

    [Fact]
    public async Task Checkout_BindsResolvedId_AndPassesItToLoodoi()
    {
        await using var db = Db();
        var user = Guid.NewGuid();
        var session = await Service(db, new FixedIdentity("acc_central_42")).CreateCheckoutSessionAsync(user, new CheckoutSessionRequest("Pro"), default);
        session.CheckoutUrl.Should().Contain("account=acc_central_42");
        (await db.TenantEntitlements.SingleAsync(x => x.UserId == user)).LoodoiAccountId.Should().Be("acc_central_42");
    }

    [Fact]
    public async Task Checkout_IdentityChangingForSameTenant_IsRejected()
    {
        await using var db = Db();
        var user = Guid.NewGuid();
        await Service(db, new FixedIdentity("acc_first")).CreateCheckoutSessionAsync(user, new CheckoutSessionRequest("Pro"), default);
        var act = () => Service(db, new FixedIdentity("acc_second")).CreateCheckoutSessionAsync(user, new CheckoutSessionRequest("Pro"), default);
        await act.Should().ThrowAsync<LoodoiIdentityConflictException>();
    }

    [Fact]
    public async Task Checkout_AccountAlreadyOwnedByAnotherWorkspace_IsRejected()
    {
        await using var db = Db();
        await Service(db, new FixedIdentity("acc_shared")).CreateCheckoutSessionAsync(Guid.NewGuid(), new CheckoutSessionRequest("Pro"), default);
        var act = () => Service(db, new FixedIdentity("acc_shared")).CreateCheckoutSessionAsync(Guid.NewGuid(), new CheckoutSessionRequest("Pro"), default);
        await act.Should().ThrowAsync<LoodoiIdentityConflictException>();
    }

    [Fact]
    public async Task Checkout_ByNonOwnerCollaborator_IsRefused()
    {
        await using var db = Db();
        var owner = Guid.NewGuid(); var member = Guid.NewGuid();
        var project = new SeoProject(owner, "Owned", new Uri("https://owned.example"));
        db.SeoProjects.Add(project); db.ProjectMembers.Add(new ProjectMember(project.Id, member, ProjectMemberRole.Admin));
        await db.SaveChangesAsync();
        var act = () => Service(db, new FixedIdentity("acc_member")).CreateCheckoutSessionAsync(member, new CheckoutSessionRequest("Pro"), default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Webhook_ForUnknownAccount_IsRejected_AndCreatesNothing()
    {
        await using var db = Db();
        var service = Service(db, null);
        (await SendAsync(service, "evt_unknown", "acc_never_bound", Guid.NewGuid(), "Enterprise", "Active", DateTimeOffset.UtcNow)).Should().BeFalse();
        (await db.TenantEntitlements.CountAsync()).Should().Be(0, "the webhook must never invent a tenant or identity");
    }

    [Fact]
    public async Task Webhook_UserIdAlone_CannotBindAnAccount()
    {
        await using var db = Db();
        var user = Guid.NewGuid();
        var service = Service(db, null);
        await service.GetEntitlementsAsync(user, default); // unbound tenant exists
        (await SendAsync(service, "evt_bind", "acc_attacker", user, "Enterprise", "Active", DateTimeOffset.UtcNow)).Should().BeFalse();
        (await db.TenantEntitlements.SingleAsync(x => x.UserId == user)).LoodoiAccountId.Should().BeNull();
    }

    [Fact]
    public async Task AccountId_IsStable_AcrossPlanChangeRenewalCancelExpireFailedPaymentAndReactivation()
    {
        await using var db = Db();
        var user = Guid.NewGuid();
        var service = Service(db, new FixedIdentity("acc_stable"));
        await service.CreateCheckoutSessionAsync(user, new CheckoutSessionRequest("Pro"), default);
        var start = DateTimeOffset.UtcNow;
        var steps = new[] { ("Pro", "Active"), ("Enterprise", "Active"), ("Enterprise", "PastDue"), ("Enterprise", "Canceled"), ("Enterprise", "Expired"), ("Starter", "Active") };
        var i = 0;
        foreach (var (plan, status) in steps)
        {
            (await SendAsync(service, $"evt_s{i}", "acc_stable", user, plan, status, start.AddMonths(i))).Should().BeTrue();
            (await db.TenantEntitlements.AsNoTracking().SingleAsync(x => x.UserId == user)).LoodoiAccountId.Should().Be("acc_stable");
            i++;
        }
        // An e-mail or profile change has no input path into the identity: it is keyed by the account id, not the address.
        (await service.GetEntitlementsAsync(user, default)).Plan.Should().Be("Starter");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("has space")]
    [InlineData("user@example.com")]
    public void AccountId_Format_IsValidated(string value)
    {
        var act = () => TenantEntitlement.ValidateAccountId(value);
        act.Should().Throw<ArgumentException>();
    }
}
