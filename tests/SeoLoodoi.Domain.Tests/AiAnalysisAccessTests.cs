using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.AI;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.AI;
using SeoLoodoi.Infrastructure.Persistence;
using AwesomeAssertions;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// C1 regression: POST /ai/analyze mutates state (calls the AI provider and
/// persists an AiAnalysis row), so it must require Editor access, not the
/// read-only Viewer level used by the GET endpoints.
/// Phase 12 Stage 1 extends the same guard to the AI credit meter: rejected and
/// crawl-less requests consume zero credits.
/// </summary>
public sealed class AiAnalysisAccessTests
{
    private sealed class FixedAccess(bool view, bool edit) : IProjectAccessService
    {
        public Task<ProjectAccess?> GetAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult<ProjectAccess?>(null);
        public Task<bool> CanViewAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult(view);
        public Task<bool> CanEditAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult(edit);
        public Task<bool> CanManageAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult(edit);
    }

    private sealed class CountingExpert : IAiSeoExpert
    {
        public int Calls { get; private set; }

        public Task<AiSeoResponse> AnalyzeAsync(AiEvidencePacket packet, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new AiSeoResponse("summary", [], [], [], [], 0.9m, [], "test", "1.0.0"));
        }
    }

    /// <summary>
    /// Full IEntitlementService stub (GetEntitlementsAsync / GetAvailablePlansAsync /
    /// CreateCheckoutSessionAsync / ProcessCheckoutReturnAsync / ProcessWebhookAsync /
    /// ConsumeAiCreditsAsync) recording every credit tap.
    /// </summary>
    private sealed class StubEntitlements : IEntitlementService
    {
        public List<(Guid UserId, int Amount)> Consumed { get; } = [];

        public Task<bool> ConsumeAiCreditsAsync(Guid userId, int amount, CancellationToken ct)
        {
            Consumed.Add((userId, amount));
            return Task.FromResult(true);
        }

        public Task<TenantEntitlementDto> GetEntitlementsAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException("not part of the AI analysis flow");
        public Task<IReadOnlyList<PlanDefinitionDto>> GetAvailablePlansAsync(CancellationToken ct) => throw new NotSupportedException("not part of the AI analysis flow");
        public Task<CheckoutSessionResponse> CreateCheckoutSessionAsync(Guid userId, CheckoutSessionRequest request, CancellationToken ct) => throw new NotSupportedException("not part of the AI analysis flow");
        public Task<TenantEntitlementDto> ProcessCheckoutReturnAsync(Guid userId, CheckoutReturnRequest request, CancellationToken ct) => throw new NotSupportedException("not part of the AI analysis flow");
        public Task<bool> ProcessWebhookAsync(string payloadJson, string? signatureHeader, string? timestampHeader, CancellationToken ct) => throw new NotSupportedException("not part of the AI analysis flow");
    }

    private static async Task<(AppDbContext db, Guid projectId, Guid viewer, CountingExpert expert, StubEntitlements entitlements, AiAnalysisService service)> SetupAsync(bool canEdit, bool completedCrawl = true)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var owner = Guid.NewGuid();
        var viewer = Guid.NewGuid();
        var project = new SeoProject(owner, "AI access", new Uri("https://ai-access.example"));
        db.SeoProjects.Add(project);
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
        if (completedCrawl)
        {
            crawl.Start(DateTimeOffset.UtcNow);
            crawl.Complete(DateTimeOffset.UtcNow);
        }
        db.Crawls.Add(crawl);
        await db.SaveChangesAsync();
        var expert = new CountingExpert();
        var entitlements = new StubEntitlements();
        var service = new AiAnalysisService(db, new FixedAccess(view: true, edit: canEdit), expert, Options.Create(new AiOptions()), entitlements);
        return (db, project.Id, viewer, expert, entitlements, service);
    }

    [Fact]
    public async Task Viewer_CannotTriggerAiAnalysis()
    {
        var (db, projectId, viewer, expert, entitlements, service) = await SetupAsync(canEdit: false);
        await using var _ = db;

        var result = await service.AnalyzeProjectAsync(projectId, viewer, null, CancellationToken.None);

        result.Should().BeNull("a Viewer must not be able to start an AI analysis");
        expert.Calls.Should().Be(0, "the AI provider must not be invoked for unauthorized users");
        (await db.AiAnalyses.CountAsync()).Should().Be(0, "no analysis row may be persisted for unauthorized users");
    }

    [Fact]
    public async Task Editor_CanTriggerAiAnalysis()
    {
        var (db, projectId, editor, expert, entitlements, service) = await SetupAsync(canEdit: true);
        await using var _ = db;

        var result = await service.AnalyzeProjectAsync(projectId, editor, null, CancellationToken.None);

        result.Should().NotBeNull();
        expert.Calls.Should().Be(1);
        (await db.AiAnalyses.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Viewer_Analysis_DoesNotConsumeAiCredits()
    {
        var (db, projectId, viewer, expert, entitlements, service) = await SetupAsync(canEdit: false);
        await using var _ = db;

        var result = await service.AnalyzeProjectAsync(projectId, viewer, null, CancellationToken.None);

        result.Should().BeNull();
        entitlements.Consumed.Should().BeEmpty("a rejected request must never touch the AI credit meter");
        expert.Calls.Should().Be(0);
    }

    [Fact]
    public async Task NoCompletedCrawl_Analysis_DoesNotConsumeAiCredits()
    {
        var (db, projectId, viewer, expert, entitlements, service) = await SetupAsync(canEdit: true, completedCrawl: false);
        await using var _ = db;

        var result = await service.AnalyzeProjectAsync(projectId, viewer, null, CancellationToken.None);

        result.Should().BeNull("only a complete crawl carries analyzable evidence");
        entitlements.Consumed.Should().BeEmpty("a request without a complete crawl must consume zero credits");
        expert.Calls.Should().Be(0);
        (await db.AiAnalyses.CountAsync()).Should().Be(0);
    }
}
