using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;

namespace SeoLoodoi.Api.Tests;

/// <summary>
/// Webhook alert rules must be delivered with a verifiable signature. The API
/// therefore issues a per-rule secret (shown once at create time) and never
/// echoes it back in list responses.
/// </summary>
[Collection("api")]
public sealed class WebhookSignatureContractTests(ApiFixture fixture)
{
    [Fact]
    public async Task CreateWebhookRule_ReturnsASigningSecret()
    {
        var projectId = await TestProject.CreateAsync(fixture.Owner, "هشدار وب‌هوک امضاشده");

        var response = await fixture.Owner.Client.PostAsJsonAsync($"/api/seo/projects/{projectId}/alerts/rules", new
        {
            type = "CRAWL_FAILURE",
            threshold = 0,
            channel = "webhook",
            destination = "https://example.com/hooks/seo",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var rule = await response.Content.ReadFromJsonAsync<RuleEnvelope>();
        rule.Should().NotBeNull();
        rule!.WebhookSecret.Should().NotBeNullOrWhiteSpace();
        rule.WebhookSecret.Should().MatchRegex("^[0-9a-f]{64}$", "the secret is 32 random bytes in hex");
    }

    [Fact]
    public async Task CreateDashboardRule_HasNoSecret_AndListNeverLeaksSecrets()
    {
        var projectId = await TestProject.CreateAsync(fixture.Owner, "هشدار داشبورد");
        var created = await fixture.Owner.Client.PostAsJsonAsync($"/api/seo/projects/{projectId}/alerts/rules", new
        {
            type = "SCORE_DROP",
            threshold = 5,
            channel = "dashboard",
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await created.Content.ReadFromJsonAsync<RuleEnvelope>())!.WebhookSecret.Should().BeNull();

        var list = await fixture.Owner.Client.GetFromJsonAsync<List<RuleEnvelope>>($"/api/seo/projects/{projectId}/alerts/rules");
        list.Should().NotBeNull();
        list!.Should().OnlyContain(x => x.WebhookSecret is null, "list responses must never expose signing secrets");
    }

    private sealed record RuleEnvelope(Guid Id, string Type, string Channel, string? Destination, string? WebhookSecret);
}
