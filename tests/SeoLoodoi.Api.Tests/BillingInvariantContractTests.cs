using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using SeoLoodoi.Infrastructure.Billing;

namespace SeoLoodoi.Api.Tests;

/// <summary>Part B regression tests for billing invariants at the HTTP boundary.</summary>
[Collection("api")]
public sealed class BillingInvariantContractTests(ApiFixture fixture)
{
    [Fact]
    public async Task Webhook_OversizedBody_Returns413()
    {
        using var client = fixture.Factory.CreateClient();
        using var content = new StringContent(new string('x', 70 * 1024), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/billing/webhook", content);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Webhook_WithoutSignatureHeaders_IsRejected_EvenIfBodyIsWellFormed()
    {
        using var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/billing/webhook", new { eventId = "evt_nosig", eventType = "subscription.activated", loodoiAccountId = "acc_x", plan = "Enterprise", status = "Active" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Entitlements_Response_ContainsNoSecrets()
    {
        var body = await fixture.Owner.Client.GetStringAsync("/api/seo/billing/entitlements");
        body.Should().NotContainAny(LoodoiBillingOptions.DevSigningKey, LoodoiBillingOptions.DevWebhookSecret, "secretKey", "webhookSecret");
    }

    [Fact]
    public async Task ClientSuppliedPlanFields_CannotGrantEntitlements()
    {
        var before = await fixture.Other.Client.GetFromJsonAsync<Dictionary<string, object>>("/api/seo/billing/entitlements");
        // A forged return token or query-string plan must never change the plan.
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{fixture.Other.UserId:N}|Enterprise|nonce|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|sha256=00"));
        var response = await fixture.Other.Client.PostAsJsonAsync("/api/seo/billing/checkout/return", new { signedToken = forged, plan = "Enterprise", status = "Active" });
        ((int)response.StatusCode).Should().BeOneOf(400, 409);
        var after = await fixture.Other.Client.GetFromJsonAsync<Dictionary<string, object>>("/api/seo/billing/entitlements");
        after!["plan"].ToString().Should().Be(before!["plan"].ToString());
    }

    [Fact]
    public async Task Logs_NeverContainBillingSecrets()
    {
        using var client = fixture.Factory.CreateClient();
        await client.PostAsJsonAsync("/api/billing/webhook", new { eventId = "evt_log" });
        await fixture.Owner.Client.PostAsJsonAsync("/api/seo/billing/checkout", new { targetPlan = "Pro", returnUrl = "/" });
        fixture.Logs.Snapshot().Should().NotContain(m => m.Contains(LoodoiBillingOptions.DevSigningKey) || m.Contains(LoodoiBillingOptions.DevWebhookSecret));
    }
}
