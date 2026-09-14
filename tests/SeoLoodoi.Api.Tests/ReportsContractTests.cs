using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;

namespace SeoLoodoi.Api.Tests;

/// <summary>
/// Contract tests for the report endpoints. A report can only be built from a
/// completed crawl; the API must refuse with a clean client error, never a 500.
/// </summary>
[Collection("api")]
public sealed class ReportsContractTests(ApiFixture fixture)
{
    [Fact]
    public async Task CreateReport_WithoutCompletedCrawl_ReturnsConflict_NotServerError()
    {
        var projectId = await TestProject.CreateAsync(fixture.Owner, "گزارش بدون خزش");

        var response = await fixture.Owner.Client.PostAsJsonAsync($"/api/seo/projects/{projectId}/reports", new
        {
            type = "Executive",
            format = "json",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        body.Should().NotBeNull();
        body!.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateReport_UnknownProject_ReturnsNotFound()
    {
        var response = await fixture.Owner.Client.PostAsJsonAsync($"/api/seo/projects/{Guid.NewGuid()}/reports", new
        {
            type = "Executive",
            format = "json",
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateReport_InvalidFormat_ReturnsValidationProblem()
    {
        var projectId = await TestProject.CreateAsync(fixture.Owner, "گزارش فرمت نامعتبر");

        var response = await fixture.Owner.Client.PostAsJsonAsync($"/api/seo/projects/{projectId}/reports", new
        {
            type = "Executive",
            format = "xlsx",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record ErrorEnvelope(string? Error);
}
