using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Application.Competitors;
using SeoLoodoi.Application.Keywords;
using SeoLoodoi.Application.SearchConsole;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Api.Tests;

/// <summary>
/// HTTP contracts through the real authenticated API, with explicitly seeded test evidence.
/// InMemory is not a substitute for the PostgreSQL integration or browser suites.
/// Phase names refer to the session's product track (9 backlinks, 10 SERP, 11 AEO).
/// </summary>
[Collection("phase-contracts")]
public sealed class Phase1To11ContractTests(ApiFixture fixture)
{
    // Seed unique projects without public DNS or quota-consuming setup HTTP calls.
    private async Task<Guid> SeedAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = new SeoProject(fixture.Owner.UserId, "Contract evidence", new Uri("https://example.com"));
        db.SeoProjects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }
    private static string Path(Guid project, string suffix) => $"/api/seo/projects/{project}{suffix}";

    [Theory]
    [InlineData("")]
    [InlineData("/settings")]
    [InlineData("/dashboard")]
    [InlineData("/pages")]
    [InlineData("/members")]
    [InlineData("/search-console/status")]
    [InlineData("/backlinks/status")]
    [InlineData("/serp/status")]
    public async Task ForeignProject_SingleResourceIsNotDisclosed(string suffix)
    {
        var id = await SeedAsync();
        using var response = await fixture.Other.Client.GetAsync(Path(id, suffix));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/crawls")]
    [InlineData("/issues")]
    [InlineData("/scores/history")]
    [InlineData("/recommendations")]
    [InlineData("/keywords")]
    [InlineData("/keywords/opportunities")]
    [InlineData("/keywords/cannibalization")]
    [InlineData("/competitors")]
    [InlineData("/backlinks/history")]
    [InlineData("/audit-logs")]
    public async Task ForeignProject_CollectionsAreEmpty(string suffix)
    {
        var id = await SeedAsync();
        using var response = await fixture.Other.Client.GetAsync(Path(id, suffix));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.Equal(0, rows.GetArrayLength());
    }

    [Theory]
    [InlineData("/projects")]
    [InlineData("/aeo/crawlers")]
    [InlineData("/usage")]
    public async Task AnonymousRequests_RequireAuthentication(string suffix)
    {
        using var client = fixture.Factory.CreateClient();
        using var response = await client.GetAsync("/api/seo" + suffix);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("x", "https://example.com")]
    [InlineData("Valid name", "file:///etc/passwd")]
    [InlineData("Valid name", "http://127.0.0.1")]
    public async Task ProjectCreation_RejectsInvalidNameOrUnsafeUrl(string name, string baseUrl)
    {
        using var response = await fixture.Owner.Client.PostAsJsonAsync("/api/seo/projects", new { name, baseUrl });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProjectCreation_DuplicateOwnerHost_ReturnsConflictInsteadOfServerError()
    {
        using var first = await fixture.Owner.Client.PostAsJsonAsync("/api/seo/projects", new { name = "Original host", baseUrl = "https://example.org/first" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var duplicate = await fixture.Owner.Client.PostAsJsonAsync("/api/seo/projects", new { name = "Duplicate host", baseUrl = "https://example.org/other" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var problem = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("دامنه", problem.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Settings_PersistPartialUpdate_RejectInvalidValueWithoutChangingStoredSettings()
    {
        var id = await SeedAsync();
        var path = Path(id, "/settings");
        using var update = await fixture.Owner.Client.PutAsJsonAsync(path, new { maxPages = 50, maxDepth = 2 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var invalid = await fixture.Owner.Client.PutAsJsonAsync(path, new { maxPages = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var settings = await fixture.Owner.Client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(50, settings.GetProperty("maxPages").GetInt32());
        Assert.Equal(2, settings.GetProperty("maxDepth").GetInt32());
    }

    [Fact]
    public async Task FreshDashboard_DoesNotInventScoreOrCrawl()
    {
        var id = await SeedAsync();
        var dashboard = await fixture.Owner.Client.GetFromJsonAsync<DashboardDto>(Path(id, "/dashboard"));
        Assert.NotNull(dashboard);
        Assert.Equal(id, dashboard.ProjectId);
        Assert.Null(dashboard.Score);
        Assert.Null(dashboard.LatestCrawlStatus);
        Assert.Null(dashboard.LastCrawlAt);
        Assert.Empty(dashboard.TopIssues);
        using var latest = await fixture.Owner.Client.GetAsync(Path(id, "/scores/latest"));
        Assert.Equal(HttpStatusCode.NotFound, latest.StatusCode);
    }

    [Fact]
    public async Task ContentWithoutEvidence_HasNullReadabilityAndNoInventedPages()
    {
        var id = await SeedAsync();
        var body = await fixture.Owner.Client.GetFromJsonAsync<JsonElement>(Path(id, $"/crawls/{Guid.NewGuid()}/content/analysis"));
        Assert.Equal(0, body.GetProperty("pages").GetArrayLength());
        var summary = body.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("pagesAnalyzed").GetInt32());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("averageReadabilityScore").ValueKind);
    }

    [Fact]
    public async Task Keyword_CreateImportReadDelete_PreservesObservedMetricsAndSource()
    {
        var id = await SeedAsync();
        var path = Path(id, "/keywords");
        using var created = await fixture.Owner.Client.PostAsJsonAsync(path, new CreateKeywordRequest("راهنمای تست"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var keyword = await created.Content.ReadFromJsonAsync<KeywordDto>();
        Assert.NotNull(keyword);
        Assert.Null(keyword.AveragePosition);
        Assert.Null(keyword.Impressions);
        Assert.Null(keyword.LastMetricAt);
        var metric = new ImportKeywordMetricRequest(new DateOnly(2026, 9, 1), 5, 100, .05m, 8m, "Manual", "https://example.com/guide");
        using var imported = await fixture.Owner.Client.PostAsJsonAsync($"{path}/{keyword.Id}/metrics", metric);
        Assert.Equal(HttpStatusCode.NoContent, imported.StatusCode);
        var observed = Assert.Single((await fixture.Owner.Client.GetFromJsonAsync<KeywordDto[]>(path))!);
        Assert.Equal(8m, observed.AveragePosition);
        Assert.Equal(100, observed.Impressions);
        Assert.Equal(5, observed.Clicks);
        Assert.Equal("Manual", observed.Source);
        var opportunities = await fixture.Owner.Client.GetFromJsonAsync<KeywordOpportunityDto[]>(Path(id, "/keywords/opportunities"));
        Assert.Equal(keyword.Id, Assert.Single(opportunities!).KeywordId);
        using var deleted = await fixture.Owner.Client.DeleteAsync($"{path}/{keyword.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await fixture.Owner.Client.GetFromJsonAsync<KeywordDto[]>(path))!);
    }

    [Fact]
    public async Task KeywordBatch_DeduplicatesAndDoesNotLeakToOtherTenant()
    {
        var id = await SeedAsync();
        using var response = await fixture.Owner.Client.PostAsJsonAsync(Path(id, "/keywords/batch"),
            new BatchCreateKeywordsRequest(["سئو", "سئو", "تحلیل", " "]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<BatchCreateKeywordsResult>();
        Assert.NotNull(result);
        Assert.Equal(2, result.AddedCount);
        Assert.Empty((await fixture.Other.Client.GetFromJsonAsync<KeywordDto[]>(Path(id, "/keywords")))!);
        using var forbidden = await fixture.Other.Client.DeleteAsync(Path(id, $"/keywords/{result.AddedKeywords[0].Id}"));
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Equal(2, (await fixture.Owner.Client.GetFromJsonAsync<KeywordDto[]>(Path(id, "/keywords")))!.Length);
    }

    [Fact]
    public async Task SearchConsole_DisconnectedStatusAndSyncFailure_DoNotInventData()
    {
        var id = await SeedAsync();
        var status = await fixture.Owner.Client.GetFromJsonAsync<SearchConsoleConnectionStatus>(Path(id, "/search-console/status"));
        Assert.NotNull(status);
        Assert.False(status.Connected);
        Assert.Null(status.ExpiresAt);
        using var sync = await fixture.Owner.Client.PostAsJsonAsync(Path(id, "/search-console/sync"),
            new SearchConsoleSyncRequest(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2)));
        Assert.Equal(HttpStatusCode.Conflict, sync.StatusCode);
        Assert.Empty((await fixture.Owner.Client.GetFromJsonAsync<KeywordDto[]>(Path(id, "/keywords")))!);
    }

    [Theory]
    [InlineData("/backlinks/status")]
    [InlineData("/serp/status")]
    public async Task DisabledProviders_ReportNotConfigured(string suffix)
    {
        var id = await SeedAsync();
        var body = await fixture.Owner.Client.GetFromJsonAsync<JsonElement>(Path(id, suffix));
        Assert.False(body.GetProperty("isConfigured").GetBoolean());
    }

    [Theory]
    [InlineData("/crawls")]
    [InlineData("/backlinks/refresh")]
    public async Task ForeignProject_CannotQueueWork(string suffix)
    {
        var id = await SeedAsync();
        using var response = await fixture.Other.Client.PostAsync(Path(id, suffix), null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/links/graph")]
    [InlineData("/content/analysis")]
    [InlineData("/aeo")]
    public async Task ForeignProject_CannotReadCrawlAnalysis(string suffix)
    {
        var id = await SeedAsync();
        using var response = await fixture.Other.Client.GetAsync(Path(id, $"/crawls/{Guid.NewGuid()}{suffix}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Membership_ViewerCannotWrite_EditorCanWrite_RemovalRevokesAccess()
    {
        var id = await SeedAsync();
        var members = Path(id, "/members");
        using var add = await fixture.Owner.Client.PostAsJsonAsync(members,
            new AddProjectMemberRequest(fixture.Other.Email, ProjectMemberRole.Viewer));
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var member = await add.Content.ReadFromJsonAsync<ProjectMemberDto>();
        Assert.NotNull(member);
        using var read = await fixture.Other.Client.GetAsync(Path(id, "/dashboard"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var denied = await fixture.Other.Client.PostAsJsonAsync(Path(id, "/keywords"), new CreateKeywordRequest("viewer denied"));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var promote = await fixture.Owner.Client.PatchAsJsonAsync($"{members}/{member.Id}",
            new ChangeProjectMemberRoleRequest(ProjectMemberRole.Editor));
        Assert.Equal(HttpStatusCode.NoContent, promote.StatusCode);
        using var allowed = await fixture.Other.Client.PostAsJsonAsync(Path(id, "/keywords"), new CreateKeywordRequest("editor allowed"));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        using var removed = await fixture.Owner.Client.DeleteAsync($"{members}/{member.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var revoked = await fixture.Other.Client.GetAsync(Path(id, "/dashboard"));
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [Fact]
    public async Task CompetitorComparison_WithoutCrawl_HasUnknownMetrics_AndUpdateDeletePersist()
    {
        var id = await SeedAsync();
        var competitor = new Competitor(id, "Fixture competitor", new Uri("https://competitor.example"));
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Competitors.Add(competitor);
            await db.SaveChangesAsync();
        }
        var comparison = await fixture.Owner.Client.GetFromJsonAsync<CompetitorComparisonDto>(Path(id, "/competitors/compare"));
        Assert.NotNull(comparison);
        Assert.Null(comparison.ProjectMetrics);
        var item = Assert.Single(comparison.Competitors);
        Assert.Null(item.Metrics);
        Assert.Null(item.LatestCrawl);
        var path = Path(id, $"/competitors/{competitor.Id}");
        using var update = await fixture.Owner.Client.PatchAsJsonAsync(path, new UpdateCompetitorRequest(false));
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var listed = Assert.Single((await fixture.Owner.Client.GetFromJsonAsync<CompetitorDto[]>(Path(id, "/competitors")))!);
        Assert.False(listed.IsActive);
        using var foreignDelete = await fixture.Other.Client.DeleteAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
        using var delete = await fixture.Owner.Client.DeleteAsync(path);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty((await fixture.Owner.Client.GetFromJsonAsync<CompetitorDto[]>(Path(id, "/competitors")))!);
    }

    [Fact]
    public async Task Dashboard_DoesNotLabelOlderScoreAsCurrentCrawlScore()
    {
        var id = await SeedAsync();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var oldCrawl = new Crawl(id, CrawlTrigger.Manual);
            var breakdown = new ScoringEngine().Calculate([new SeoRuleResult("TITLE_MISSING", true, IssueSeverity.High, IssueCategory.OnPage, null)]);
            db.SeoScores.Add(new SeoScoreSnapshot(id, oldCrawl.Id, breakdown.Overall, breakdown.Categories, breakdown.Version, breakdown.IsPartial));
            // The only listed crawl is newer than the historical score's crawl.
            db.Crawls.Add(new Crawl(id, CrawlTrigger.Manual));
            await db.SaveChangesAsync();
        }
        var dashboard = await fixture.Owner.Client.GetFromJsonAsync<DashboardDto>(Path(id, "/dashboard"));
        Assert.NotNull(dashboard);
        Assert.Null(dashboard.Score);
        var history = Assert.Single((await fixture.Owner.Client.GetFromJsonAsync<ScoreDto[]>(Path(id, "/scores/history")))!);
        Assert.Equal(75m, history.Overall);
        Assert.Null(history.Performance);
        Assert.True(history.IsPartial);
    }

    [Fact]
    public async Task Aeo_MissingCrawlCannotBeAnalyzedOrRead()
    {
        var id = await SeedAsync();
        var path = Path(id, $"/crawls/{Guid.NewGuid()}/aeo");
        using var read = await fixture.Owner.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using var analyze = await fixture.Owner.Client.PostAsync(path + "/analyze", null);
        Assert.Equal(HttpStatusCode.NotFound, analyze.StatusCode);
    }
}

[CollectionDefinition("phase-contracts")]
public sealed class PhaseContractCollection : ICollectionFixture<ApiFixture> { }
