using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.AI;
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

    private static async Task<(AppDbContext db, Guid projectId, Guid viewer, CountingExpert expert, AiAnalysisService service)> SetupAsync(bool canEdit)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var owner = Guid.NewGuid();
        var viewer = Guid.NewGuid();
        var project = new SeoProject(owner, "AI access", new Uri("https://ai-access.example"));
        db.SeoProjects.Add(project);
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
        crawl.Start(DateTimeOffset.UtcNow);
        crawl.Complete(DateTimeOffset.UtcNow);
        db.Crawls.Add(crawl);
        await db.SaveChangesAsync();
        var expert = new CountingExpert();
        var service = new AiAnalysisService(db, new FixedAccess(view: true, edit: canEdit), expert, Options.Create(new AiOptions()));
        return (db, project.Id, viewer, expert, service);
    }

    [Fact]
    public async Task Viewer_CannotTriggerAiAnalysis()
    {
        var (db, projectId, viewer, expert, service) = await SetupAsync(canEdit: false);
        await using var _ = db;

        var result = await service.AnalyzeProjectAsync(projectId, viewer, null, CancellationToken.None);

        result.Should().BeNull("a Viewer must not be able to start an AI analysis");
        expert.Calls.Should().Be(0, "the AI provider must not be invoked for unauthorized users");
        (await db.AiAnalyses.CountAsync()).Should().Be(0, "no analysis row may be persisted for unauthorized users");
    }

    [Fact]
    public async Task Editor_CanTriggerAiAnalysis()
    {
        var (db, projectId, editor, expert, service) = await SetupAsync(canEdit: true);
        await using var _ = db;

        var result = await service.AnalyzeProjectAsync(projectId, editor, null, CancellationToken.None);

        result.Should().NotBeNull();
        expert.Calls.Should().Be(1);
        (await db.AiAnalyses.CountAsync()).Should().Be(1);
    }
}
