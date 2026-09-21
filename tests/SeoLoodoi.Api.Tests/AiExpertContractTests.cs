using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Api.Tests;

/// <summary>
/// Phase 12 Stage 1 end-to-end API contract for the AI expert: the AI credit is
/// ordered after the CanEdit guard and a complete crawl (rejected/no-crawl = 0
/// credits), every accepted analyze request is metered exactly one credit and
/// returns the full structured output (summary, observations, rootCauses,
/// recommendations, actions, confidence, missingEvidence) with the provider badge
/// and promptVersion, and an exhausted monthly allowance maps to HTTP 429 with the
/// Persian problem title. Runs against the deterministic expert engine
/// (AI:Enabled=false in the test host).
/// </summary>
[Collection("api")]
public sealed class AiExpertContractTests(ApiFixture fixture)
{
    private const string PersianCreditExhaustedTitle = "اعتبار تحلیل هوش مصنوعی شما برای دوره جاری به پایان رسیده است. لطفاً پلن خود را ارتقا دهید.";

    [Fact]
    public async Task Analyze_OrdersCreditsAfterGuardAndCompleteCrawl_PersistsStructuredOutput_AndMapsExhaustedAllowanceTo429()
    {
        var owner = fixture.Owner.UserId;

        // Two projects with distinct hosts so this contract tests AI behavior only (duplicate owner/host now returns 409 — never
        // reuse one host for a second project of the same owner) and one complete
        // crawl each. Project one carries a 500 page so the deterministic expert has
        // recommendations and actions to synthesize; project two is cache-cold and
        // only used for the exhausted-allowance leg.
        Guid projectIdOne, projectIdTwo;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var projectOne = new SeoProject(owner, "AI expert one", new Uri("https://ai-one.test.example"));
            var crawlOne = new Crawl(projectOne.Id, CrawlTrigger.Manual);
            crawlOne.Start(DateTimeOffset.UtcNow);
            crawlOne.Complete(DateTimeOffset.UtcNow);
            var healthyPage = new CrawledUrl(crawlOne.Id, projectOne.Id, "https://ai-one.test.example/guide", null, 200, "text/html", 1, 120, true, 800, null);
            var errorPage = new CrawledUrl(crawlOne.Id, projectOne.Id, "https://ai-one.test.example/gone", null, 500, "text/html", 1, 90, false, 0, null);

            var projectTwo = new SeoProject(owner, "AI expert two", new Uri("https://ai-two.test.example"));
            var crawlTwo = new Crawl(projectTwo.Id, CrawlTrigger.Manual);
            crawlTwo.Start(DateTimeOffset.UtcNow);
            crawlTwo.Complete(DateTimeOffset.UtcNow);
            var secondPage = new CrawledUrl(crawlTwo.Id, projectTwo.Id, "https://ai-two.test.example/blog", null, 200, "text/html", 1, 150, true, 400, null);

            db.AddRange(projectOne, crawlOne, healthyPage, errorPage, projectTwo, crawlTwo, secondPage);
            await db.SaveChangesAsync();
            projectIdOne = projectOne.Id;
            projectIdTwo = projectTwo.Id;
        }

        // (1) Credit ordering: a request whose crawl is not complete/known is
        // rejected with 404 and consumes ZERO credits.
        using (var missing = await fixture.Owner.Client.PostAsync($"/api/seo/projects/{projectIdOne}/ai/analyze?crawlId={Guid.NewGuid()}", null))
        {
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        var entitlements = await fixture.Owner.Client.GetFromJsonAsync<TenantEntitlementDto>("/api/seo/billing/entitlements");
        entitlements.Should().NotBeNull();
        entitlements!.MaxAiCreditsPerMonth.Should().Be(500, "the shared api fixture upgrades its users to Enterprise");
        entitlements.AiCreditsUsed.Should().Be(0, "rejected and crawl-less requests must consume zero AI credits");

        // (2) Editor + complete crawl → one credit and the full structured expert output.
        JsonElement firstBody;
        using (var analyze = await fixture.Owner.Client.PostAsync($"/api/seo/projects/{projectIdOne}/ai/analyze", null))
        {
            analyze.StatusCode.Should().Be(HttpStatusCode.OK);
            firstBody = await analyze.Content.ReadFromJsonAsync<JsonElement>();
        }
        firstBody.GetProperty("summary").GetString().Should().NotBeNullOrWhiteSpace();
        firstBody.GetProperty("observations").GetArrayLength().Should().BeGreaterThan(0);
        firstBody.GetProperty("rootCauses").GetArrayLength().Should().BeGreaterThan(0);
        firstBody.GetProperty("recommendations").GetArrayLength().Should().BeGreaterThan(0);
        firstBody.GetProperty("actions").GetArrayLength().Should().BeGreaterThan(0);
        firstBody.GetProperty("missingEvidence").GetArrayLength().Should().BeGreaterThan(0);
        firstBody.GetProperty("confidence").GetDecimal().Should().BeInRange(0m, 1m);
        firstBody.GetProperty("provider").GetString().Should().Be("deterministic-expert-engine", "the test host runs without a live AI provider");
        firstBody.GetProperty("promptVersion").GetString().Should().Be("1.0.0", "the configured AI:PromptVersion is disclosed to the client");
        entitlements = await fixture.Owner.Client.GetFromJsonAsync<TenantEntitlementDto>("/api/seo/billing/entitlements");
        entitlements!.AiCreditsUsed.Should().Be(1, "exactly one credit per accepted analyze request");

        // (3) The structured output is retrievable again from the cache and every
        // accepted request is still metered one credit.
        using (var replay = await fixture.Owner.Client.PostAsync($"/api/seo/projects/{projectIdOne}/ai/analyze", null))
        {
            replay.StatusCode.Should().Be(HttpStatusCode.OK);
            var cached = await replay.Content.ReadFromJsonAsync<JsonElement>();
            cached.GetProperty("summary").GetString().Should().Be(firstBody.GetProperty("summary").GetString());
            cached.GetProperty("provider").GetString().Should().Be("deterministic-expert-engine");
        }
        entitlements = await fixture.Owner.Client.GetFromJsonAsync<TenantEntitlementDto>("/api/seo/billing/entitlements");
        entitlements!.AiCreditsUsed.Should().Be(2, "each accepted analyze request is metered, including cache replays");

        // (4) Anonymous callers are rejected before any metering.
        using (var denied = await fixture.Factory.CreateClient().PostAsync($"/api/seo/projects/{projectIdTwo}/ai/analyze", null))
        {
            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // (5) An exhausted monthly allowance maps to 429 with the Persian title and
        // neither burns credits nor persists an analysis row.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entitlement = await db.TenantEntitlements.SingleAsync(x => x.UserId == owner);
            while (entitlement.TryConsumeAiCredits(1, DateTimeOffset.UtcNow)) { }
            await db.SaveChangesAsync();
        }
        using (var exhausted = await fixture.Owner.Client.PostAsync($"/api/seo/projects/{projectIdTwo}/ai/analyze", null))
        {
            exhausted.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            var problem = await exhausted.Content.ReadFromJsonAsync<JsonElement>();
            problem.GetProperty("status").GetInt32().Should().Be(429);
            problem.GetProperty("title").GetString().Should().Be(PersianCreditExhaustedTitle);
        }
        entitlements = await fixture.Owner.Client.GetFromJsonAsync<TenantEntitlementDto>("/api/seo/billing/entitlements");
        entitlements!.AiCreditsUsed.Should().Be(500, "a refused analyze request must not burn credits");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.AiAnalyses.CountAsync(x => x.ProjectId == projectIdTwo)).Should().Be(0, "no analysis row may be persisted when the allowance is exhausted");
        }
    }
}
