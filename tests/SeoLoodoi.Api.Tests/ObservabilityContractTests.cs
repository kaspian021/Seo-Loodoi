using AwesomeAssertions;

namespace SeoLoodoi.Api.Tests;

/// <summary>
/// Observability contract: every request must leave one structured access log
/// line (method, path, status, duration) and never a request/response body, so
/// credentials can only leak through logging if someone explicitly logs them.
/// </summary>
[Collection("api")]
public sealed class ObservabilityContractTests(ApiFixture fixture)
{
    [Fact]
    public async Task EveryRequest_EmitsOneAccessLogLine_WithMethodPathStatusAndDuration()
    {
        var response = await fixture.Owner.Client.GetAsync("/health");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        fixture.Logs.Snapshot().Should().Contain(m =>
            m.Contains("GET") && m.Contains("/health") && m.Contains("200") && m.Contains("ms"),
            "the request-logging middleware must record method, path, status, and duration");
    }

    [Fact]
    public async Task FailedRequests_StillEmitAccessLog_WithTheirStatusCode()
    {
        var response = await fixture.Owner.Client.GetAsync("/api/seo/projects/00000000-0000-0000-0000-000000000000");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        fixture.Logs.Snapshot().Should().Contain(m =>
            m.Contains("GET") && m.Contains("/api/seo/projects/") && m.Contains("404"),
            "error responses must be observable too");
    }
}
