using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using SeoLoodoi.Application.Billing;

namespace SeoLoodoi.Api.Tests;

[Collection("api")]
public sealed class BillingContractTests(ApiFixture fixture)
{
    [Fact]
    public async Task GetEntitlements_AuthenticatedUser_ReturnsOkWithPlanAndLimits()
    {
        var response = await fixture.Owner.Client.GetAsync("/api/seo/billing/entitlements");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TenantEntitlementDto>();
        body.Should().NotBeNull();
        body!.Plan.Should().NotBeNullOrWhiteSpace();
        body.MaxProjects.Should().BeGreaterThan(0);
        body.MaxPagesPerMonth.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetPlans_ReturnsAllFourStandardTiers()
    {
        var response = await fixture.Owner.Client.GetAsync("/api/seo/billing/plans");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plans = await response.Content.ReadFromJsonAsync<List<PlanDefinitionDto>>();
        plans.Should().NotBeNull();
        plans!.Should().HaveCount(4);
        plans.Select(p => p.PlanId).Should().Contain(["Free", "Starter", "Pro", "Enterprise"]);
    }

    [Fact]
    public async Task Checkout_ValidPlan_ReturnsSessionWithCheckoutUrl()
    {
        var response = await fixture.Owner.Client.PostAsJsonAsync("/api/seo/billing/checkout", new
        {
            targetPlan = "Pro",
            returnUrl = "/billing"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await response.Content.ReadFromJsonAsync<CheckoutSessionResponse>();
        session.Should().NotBeNull();
        session!.CheckoutUrl.Should().NotBeNullOrWhiteSpace();
        session.SessionToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Checkout_InvalidPlan_ReturnsValidationProblem()
    {
        var response = await fixture.Owner.Client.PostAsJsonAsync("/api/seo/billing/checkout", new
        {
            targetPlan = "SuperUltimateNonExistentPlan"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Webhook_WithoutValidSignature_ReturnsUnauthorized()
    {
        using var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/billing/webhook", new
        {
            eventId = "evt_fake",
            eventType = "subscription.updated",
            plan = "Pro"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
