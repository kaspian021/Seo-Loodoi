using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.AI;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.AI;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// Phase 12 Stage 1 credit ordering inside <see cref="AiAnalysisService"/>: the AI
/// credit charge happens after the CanEdit guard and a complete crawl, an exhausted
/// allowance raises <see cref="AiCreditsExhaustedException"/> (mapped to 429), and
/// every result — including deterministic fallback output — is cached under
/// (evidence hash, promptVersion) with its provider badge intact.
/// </summary>
public sealed class AiAnalysisServiceTests
{
    private const string FallbackNote = "این تحلیل با موتور قطعی تولید شده است؛ پاسخ معتبری از ارائه‌دهنده هوش مصنوعی دریافت نشد.";
    private const string PersianCreditExhaustedTitle = "اعتبار تحلیل هوش مصنوعی شما برای دوره جاری به پایان رسیده است. لطفاً پلن خود را ارتقا دهید.";

    private sealed class AllowAllAccess : IProjectAccessService
    {
        public Task<ProjectAccess?> GetAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult<ProjectAccess?>(null);
        public Task<bool> CanViewAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> CanEditAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> CanManageAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class RecordingExpert : IAiSeoExpert
    {
        public int Calls { get; private set; }
        public AiSeoResponse Response { get; set; } = new("s", ["o"], ["r"], ["rec"], ["a"], 0.7m, [], "test-expert", "1.0.0");

        public Task<AiSeoResponse> AnalyzeAsync(AiEvidencePacket packet, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Response);
        }
    }

    /// <summary>
    /// Full IEntitlementService stub (GetEntitlementsAsync / GetAvailablePlansAsync /
    /// CreateCheckoutSessionAsync / ProcessCheckoutReturnAsync / ProcessWebhookAsync /
    /// ConsumeAiCreditsAsync). Only the credit tap is live; the rest must never be
    /// reached by the analysis flow.
    /// </summary>
    private sealed class StubEntitlements : IEntitlementService
    {
        public bool Allow { get; set; } = true;
        public List<(Guid UserId, int Amount)> Consumed { get; } = [];

        public Task<bool> ConsumeAiCreditsAsync(Guid userId, int amount, CancellationToken ct)
        {
            Consumed.Add((userId, amount));
            return Task.FromResult(Allow);
        }

        public Task<TenantEntitlementDto> GetEntitlementsAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException("not part of the AI analysis flow");
        public Task<IReadOnlyList<PlanDefinitionDto>> GetAvailablePlansAsync(CancellationToken ct) => throw new NotSupportedException("not part of the AI analysis flow");
        public Task<CheckoutSessionResponse> CreateCheckoutSessionAsync(Guid userId, CheckoutSessionRequest request, CancellationToken ct) => throw new NotSupportedException("not part of the AI analysis flow");
        public Task<TenantEntitlementDto> ProcessCheckoutReturnAsync(Guid userId, CheckoutReturnRequest request, CancellationToken ct) => throw new NotSupportedException("not part of the AI analysis flow");
        public Task<bool> ProcessWebhookAsync(string payloadJson, string? signatureHeader, string? timestampHeader, CancellationToken ct) => throw new NotSupportedException("not part of the AI analysis flow");
    }

    private static async Task<(AppDbContext db, Guid projectId, Guid userId, RecordingExpert expert, StubEntitlements entitlements, AiAnalysisService service)> SetupAsync(string promptVersion = "1.0.0")
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var userId = Guid.NewGuid();
        var project = new SeoProject(userId, "AI credit ordering", new Uri("https://credits.test"));
        db.SeoProjects.Add(project);
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
        crawl.Start(DateTimeOffset.UtcNow);
        crawl.Complete(DateTimeOffset.UtcNow);
        db.Crawls.Add(crawl);
        await db.SaveChangesAsync();
        var expert = new RecordingExpert();
        var entitlements = new StubEntitlements();
        var service = new AiAnalysisService(db, new AllowAllAccess(), expert, Options.Create(new AiOptions { PromptVersion = promptVersion }), entitlements);
        return (db, project.Id, userId, expert, entitlements, service);
    }

    [Fact]
    public async Task Analyze_ConsumesExactlyOneCredit_AfterGuardAndCompletedCrawl()
    {
        var (db, projectId, userId, expert, entitlements, service) = await SetupAsync();
        await using var _ = db;

        var result = await service.AnalyzeProjectAsync(projectId, userId, null, CancellationToken.None);

        result.Should().NotBeNull();
        entitlements.Consumed.Should().Equal(new[] { (userId, 1) }, "an accepted analyze request is metered exactly one AI credit");
        expert.Calls.Should().Be(1);
        (await db.AiAnalyses.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Analyze_CacheReplay_StillMetersEachAcceptedRequest()
    {
        var (db, projectId, userId, expert, entitlements, service) = await SetupAsync();
        await using var _ = db;

        var first = await service.AnalyzeProjectAsync(projectId, userId, null, CancellationToken.None);
        var second = await service.AnalyzeProjectAsync(projectId, userId, null, CancellationToken.None);

        expert.Calls.Should().Be(1, "the cached result must not re-invoke the provider");
        (await db.AiAnalyses.CountAsync()).Should().Be(1, "one evidence hash + promptVersion yields one cache row");
        entitlements.Consumed.Should().Equal(new[] { (userId, 1), (userId, 1) }, "each accepted analyze request is metered, including cache replays");
        second!.Summary.Should().Be(first!.Summary);
    }

    [Fact]
    public async Task Analyze_ExhaustedCredits_ThrowsAiCreditsExhaustedException_WithPersianTitleMessage()
    {
        var (db, projectId, userId, expert, entitlements, service) = await SetupAsync();
        await using var _ = db;
        entitlements.Allow = false;

        Func<Task> act = async () => await service.AnalyzeProjectAsync(projectId, userId, null, CancellationToken.None);

        await act.Should().ThrowAsync<AiCreditsExhaustedException>()
            .WithMessage(PersianCreditExhaustedTitle, "the message becomes the Persian title of the 429 problem response");
        expert.Calls.Should().Be(0, "the provider must not run without a paid credit");
        (await db.AiAnalyses.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Analyze_FallbackResult_IsCachedWithProviderBadgeAndClarificationNote()
    {
        var (db, projectId, userId, expert, entitlements, service) = await SetupAsync();
        await using var _ = db;
        expert.Response = new("تحلیل قطعی", ["o"], ["r"], ["rec"], ["a"], 0.95m, ["شواهد ناقص", FallbackNote], "deterministic-expert-engine", "1.0.0");

        var first = await service.AnalyzeProjectAsync(projectId, userId, null, CancellationToken.None);
        var replay = await service.AnalyzeProjectAsync(projectId, userId, null, CancellationToken.None);

        first!.Provider.Should().Be("deterministic-expert-engine");
        first.MissingEvidence.Should().Contain(FallbackNote);
        replay!.Provider.Should().Be("deterministic-expert-engine", "the cached replay must keep the fallback provider badge");
        replay.MissingEvidence.Should().Contain(FallbackNote, "fallback results are cached in full, transparency note included");
        expert.Calls.Should().Be(1);
        (await db.AiAnalyses.CountAsync()).Should().Be(1, "fallback output belongs in the cache like every other result");
    }

    [Fact]
    public async Task Analyze_CacheKey_IncludesEvidenceHashAndPromptVersion()
    {
        var (db, projectId, userId, expert, entitlements, firstService) = await SetupAsync("1.0.0");
        await using var _ = db;
        var secondService = new AiAnalysisService(db, new AllowAllAccess(), expert, Options.Create(new AiOptions { PromptVersion = "2.0.0" }), entitlements);

        await firstService.AnalyzeProjectAsync(projectId, userId, null, CancellationToken.None);
        await secondService.AnalyzeProjectAsync(projectId, userId, null, CancellationToken.None);

        expert.Calls.Should().Be(2, "a promptVersion bump must invalidate the cache for identical evidence");
        (await db.AiAnalyses.CountAsync()).Should().Be(2, "cache rows are keyed by evidence hash AND promptVersion");
        entitlements.Consumed.Should().HaveCount(2);
    }
}
