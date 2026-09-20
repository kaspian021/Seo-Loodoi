using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SeoLoodoi.Application.Aeo;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Api.Tests;

public sealed class AeoWorkflowContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private sealed class FixtureRobots : IRobotsService
    {
        public Task<RobotsPolicy> GetPolicyAsync(Uri uri, CancellationToken ct)
        {
            const string text = "User-agent: *\nDisallow: /";
            return Task.FromResult(new RobotsPolicy(new RobotsParser().Parse(text, uri), 200, DateTimeOffset.UtcNow, false, text));
        }
    }

    [Fact]
    public async Task Authenticate_Analyze_ReadIssues_Ignore_Reanalyze_PreservesEvidenceAndUserDecision()
    {
        await using var root = new SeoLoodoiFactory();
        await using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRobotsService>();
            services.AddSingleton<IRobotsService, FixtureRobots>(); // no live network in this contract test
        }));
        using var client = factory.CreateClient();
        var email = $"aeo-{Guid.NewGuid():N}@test.example";
        using var register = await client.PostAsJsonAsync("/api/account/register", new
        {
            fullName = "AEO Test", email, password = "Secure@2026x", confirmPassword = "Secure@2026x", acceptTerms = true, preferredLanguage = "fa"
        });
        register.EnsureSuccessStatusCode();
        var owner = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var login = await client.PostAsJsonAsync("/api/auth/login?useCookies=false", new { email, password = "Secure@2026x" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Guid projectId, crawlId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var project = new SeoProject(owner, "AEO workflow", new Uri("https://example.com"));
            var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
            crawl.Start(DateTimeOffset.UtcNow); crawl.Complete(DateTimeOffset.UtcNow);
            var page = new CrawledUrl(crawl.Id, project.Id, "https://example.com/guide", null, 200, "text/html", 1, 100, true, 200, null);
            db.AddRange(project, crawl, page,
                new PageSnapshot(page.Id, crawl.Id, "Guide", null, null, "[]", null, null, "en", "[]", "Observed text", 0, 0, 0, 0));
            // Keep the test independent of whether preview seed data was initialized.
            db.AiCrawlerProfiles.Add(new AiCrawlerProfile($"fixture-{Guid.NewGuid():N}", "Test bot", "TestBot", AiCrawlerPurpose.AnswerEngine, 1m));
            await db.SaveChangesAsync();
            projectId = project.Id; crawlId = crawl.Id;
        }
        var path = $"/api/seo/projects/{projectId}/crawls/{crawlId}/aeo";
        using var absent = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var analyze = await client.PostAsync(path + "/analyze", null);
        Assert.Equal(HttpStatusCode.OK, analyze.StatusCode);
        var report = await analyze.Content.ReadFromJsonAsync<AiVisibilityReportDto>(Json);
        Assert.NotNull(report);
        Assert.True(report.RobotsAvailable);
        Assert.Equal(1, report.Signals.PagesAnalyzed);
        Assert.Equal(0m, report.AiCrawlabilityScore);
        var saved = await client.GetFromJsonAsync<AiVisibilityReportDto>(path, Json);
        Assert.NotNull(saved);
        Assert.Equal(report.Signals, saved.Signals);
        var issuesPath = $"/api/seo/projects/{projectId}/issues?crawlId={crawlId}";
        var issues = (await client.GetFromJsonAsync<IssueDto[]>(issuesPath))!;
        Assert.Equal(2, issues.Length);
        var blocked = Assert.Single(issues, x => x.RuleCode == AeoIssueRules.Blocked);
        using var evidence = JsonDocument.Parse(blocked.EvidenceJson);
        Assert.Equal(projectId, evidence.RootElement.GetProperty("ProjectId").GetGuid());
        using var ignored = await client.PatchAsJsonAsync($"/api/seo/projects/{projectId}/issues/{blocked.Id}", new { status = "Ignored" });
        Assert.Equal(HttpStatusCode.NoContent, ignored.StatusCode);
        using var repeat = await client.PostAsync(path + "/analyze", null);
        repeat.EnsureSuccessStatusCode();
        var after = (await client.GetFromJsonAsync<IssueDto[]>(issuesPath))!;
        Assert.Equal(2, after.Length);
        Assert.Equal("Ignored", Assert.Single(after, x => x.Id == blocked.Id).Status);
        using var anonymous = factory.CreateClient();
        using var denied = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }
}
