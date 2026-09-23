using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Application.Keywords;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Billing;
using SeoLoodoi.Infrastructure.Keywords;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;

namespace SeoLoodoi.Integration.Tests;

/// <summary>
/// P0 A1/A2 stress tests on real PostgreSQL. Every task has its own DbContext and connection,
/// so these exercise genuine cross-connection races against the advisory quota lock and the
/// conditional credit UPDATE. A count-then-insert implementation fails these tests.
/// </summary>
[Collection("postgres")]
public sealed class AtomicQuotaPostgresTests(PostgresFixture fixture)
{
    private const int Contenders = 20;

    private sealed class NoOpAudit : IAuditLogService
    {
        public Task RecordAsync(Guid? projectId, Guid actorId, string action, string entityType, string? entityId, string? metadataJson, string? ipAddress, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditLogDto>>([]);
    }

    private static TenantEntitlement Plan(Guid owner, int maxProjects = 5, int maxKeywords = 5, int maxTeam = 3, int maxAi = 5, SubscriptionStatus status = SubscriptionStatus.Active) =>
        new(owner, null, "Pro", status, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), maxProjects: maxProjects, maxKeywords: maxKeywords, maxTeamMembers: maxTeam, maxAiCreditsPerMonth: maxAi);

    private static QuotaService Quota(AppDbContext db) => new(db, Options.Create(new QuotaOptions()));

    /// <summary>Starts all contenders together, each with its own DbContext and connection.</summary>
    private async Task<int[]> RaceAsync(Func<AppDbContext, Task<bool>> attempt)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, Contenders).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            await using var ctx = fixture.CreateContext();
            return await attempt(ctx) ? 1 : 0;
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Projects_Limit5_4Used_20ConcurrentCreates_ExactlyOneSucceeds()
    {
        var owner = Guid.NewGuid();
        await using (var seed = fixture.CreateContext())
        {
            seed.TenantEntitlements.Add(Plan(owner, maxProjects: 5));
            for (var i = 0; i < 4; i++) seed.SeoProjects.Add(new SeoProject(owner, $"Seed {i}", new Uri($"https://race-seed-{owner:N}-{i}.example")));
            await seed.SaveChangesAsync();
        }

        var counter = 0;
        var results = await RaceAsync(async db =>
        {
            var n = Interlocked.Increment(ref counter);
            try
            {
                await Quota(db).WithTenantLockAsync(owner, async (lease, ct) =>
                {
                    await lease.EnsureAvailableAsync(QuotaDimension.Projects, 1, ct);
                    db.SeoProjects.Add(new SeoProject(owner, $"Race {n}", new Uri($"https://race-{owner:N}-{n}.example")));
                    await db.SaveChangesAsync(ct);
                    return true;
                }, CancellationToken.None);
                return true;
            }
            catch (QuotaExceededException) { return false; }
        });

        results.Sum().Should().Be(1);
        await using var verify = fixture.CreateContext();
        (await verify.SeoProjects.CountAsync(x => x.OwnerId == owner)).Should().Be(5);
    }

    [Fact]
    public async Task Keywords_Limit5_4Used_20ConcurrentCreates_ExactlyOneSucceeds()
    {
        var owner = Guid.NewGuid();
        Guid projectId;
        await using (var seed = fixture.CreateContext())
        {
            seed.TenantEntitlements.Add(Plan(owner, maxKeywords: 5));
            var project = new SeoProject(owner, "Keyword race", new Uri($"https://kw-{owner:N}.example"));
            seed.SeoProjects.Add(project);
            for (var i = 0; i < 4; i++) seed.Keywords.Add(new Keyword(project.Id, $"seed keyword {i}"));
            await seed.SaveChangesAsync();
            projectId = project.Id;
        }

        var counter = 0;
        var results = await RaceAsync(async db =>
        {
            var n = Interlocked.Increment(ref counter);
            var service = new KeywordService(db, new ProjectAccessService(db), Quota(db), new NoOpAudit());
            try { return await service.CreateAsync(projectId, owner, new CreateKeywordRequest($"race keyword {n}"), CancellationToken.None) is not null; }
            catch (QuotaExceededException) { return false; }
        });

        results.Sum().Should().Be(1);
        await using var verify = fixture.CreateContext();
        (await verify.Keywords.CountAsync(x => x.ProjectId == projectId)).Should().Be(5);
    }

    [Fact]
    public async Task AiCredits_Limit5_4Used_20ConcurrentSpends_ExactlyOneSucceeds()
    {
        var owner = Guid.NewGuid();
        await using (var seed = fixture.CreateContext())
        {
            var ent = Plan(owner, maxAi: 5);
            ent.TryConsumeAiCredits(4, DateTimeOffset.UtcNow);
            seed.TenantEntitlements.Add(ent);
            seed.SeoProjects.Add(new SeoProject(owner, "AI race", new Uri($"https://ai-{owner:N}.example")));
            await seed.SaveChangesAsync();
        }

        var results = await RaceAsync(db =>
            new EntitlementService(db, Options.Create(new LoodoiBillingOptions()), new NoOpAudit(), NullLogger<EntitlementService>.Instance)
                .ConsumeAiCreditsAsync(owner, 1, CancellationToken.None));

        results.Sum().Should().Be(1);
        await using var verify = fixture.CreateContext();
        (await verify.TenantEntitlements.SingleAsync(x => x.UserId == owner)).AiCreditsUsed.Should().Be(5);
    }

    [Fact]
    public async Task AiCredits_CanceledSubscription_CannotSpend()
    {
        var owner = Guid.NewGuid();
        await using (var seed = fixture.CreateContext())
        {
            seed.TenantEntitlements.Add(Plan(owner, maxAi: 50, status: SubscriptionStatus.Canceled));
            await seed.SaveChangesAsync();
        }
        await using var db = fixture.CreateContext();
        var service = new EntitlementService(db, Options.Create(new LoodoiBillingOptions()), new NoOpAudit(), NullLogger<EntitlementService>.Instance);
        (await service.ConsumeAiCreditsAsync(owner, 1, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task TeamSeats_Limit3_OwnerPlusOne_20ConcurrentInvites_ExactlyOneSucceeds()
    {
        var owner = Guid.NewGuid();
        var existing = Guid.NewGuid();
        Guid projectId;
        await using (var seed = fixture.CreateContext())
        {
            seed.Users.Add(User(owner)); seed.Users.Add(User(existing));
            seed.TenantEntitlements.Add(Plan(owner, maxTeam: 3));
            var project = new SeoProject(owner, "Seat race", new Uri($"https://seat-{owner:N}.example"));
            seed.SeoProjects.Add(project);
            seed.ProjectMembers.Add(new ProjectMember(project.Id, existing, ProjectMemberRole.Viewer));
            await seed.SaveChangesAsync();
            projectId = project.Id;
        }

        var counter = 0;
        var results = await RaceAsync(async db =>
        {
            var n = Interlocked.Increment(ref counter);
            var team = new TeamService(db, new ProjectAccessService(db), Quota(db), new NoOpAudit());
            var result = await team.InviteAsync(projectId, owner, new InviteProjectMemberRequest($"race{n}-{owner:N}@example.com"), CancellationToken.None);
            return result.Ok;
        });

        results.Sum().Should().Be(1, "owner + 1 member + 1 pending invitation = 3 seats");
        await using var verify = fixture.CreateContext();
        (await verify.ProjectInvitations.CountAsync(x => x.ProjectId == projectId)).Should().Be(1);
    }

    [Fact]
    public async Task TeamSeats_AreIsolatedPerTenant()
    {
        var ownerA = Guid.NewGuid(); var ownerB = Guid.NewGuid();
        Guid projectA;
        await using (var seed = fixture.CreateContext())
        {
            seed.Users.Add(User(ownerA)); seed.Users.Add(User(ownerB));
            seed.TenantEntitlements.Add(Plan(ownerA, maxTeam: 2));
            seed.TenantEntitlements.Add(Plan(ownerB, maxTeam: 2));
            var a = new SeoProject(ownerA, "Iso A", new Uri($"https://iso-{ownerA:N}.example"));
            var b = new SeoProject(ownerB, "Iso B", new Uri($"https://iso-{ownerB:N}.example"));
            seed.SeoProjects.AddRange(a, b);
            // Tenant B is at its limit; this must not affect tenant A.
            seed.ProjectInvitations.Add(new ProjectInvitation(b.Id, $"x-{ownerB:N}@example.com", ProjectMemberRole.Viewer, TeamService.HashToken(Guid.NewGuid().ToString()), ownerB, DateTimeOffset.UtcNow.AddDays(7)));
            await seed.SaveChangesAsync();
            projectA = a.Id;
        }
        await using var db = fixture.CreateContext();
        var team = new TeamService(db, new ProjectAccessService(db), Quota(db), new NoOpAudit());
        (await team.InviteAsync(projectA, ownerA, new InviteProjectMemberRequest($"a-{ownerA:N}@example.com"), CancellationToken.None)).Ok.Should().BeTrue();
        // Owner B cannot manage owner A's project (cross-tenant).
        (await team.InviteAsync(projectA, ownerB, new InviteProjectMemberRequest($"b-{ownerB:N}@example.com"), CancellationToken.None)).Error.Should().Be(TeamOperationError.NotFound);
    }

    [Fact]
    public async Task LoodoiAccountId_IsUniqueAcrossTenants()
    {
        var shared = $"acc_{Guid.NewGuid():N}";
        await using var db = fixture.CreateContext();
        db.TenantEntitlements.Add(new TenantEntitlement(Guid.NewGuid(), shared));
        await db.SaveChangesAsync();
        db.TenantEntitlements.Add(new TenantEntitlement(Guid.NewGuid(), shared));
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("one Loodoi account can own only one workspace");
        db.ChangeTracker.Clear();
        // Unbound tenants (null) do not collide.
        db.TenantEntitlements.Add(new TenantEntitlement(Guid.NewGuid(), null));
        db.TenantEntitlements.Add(new TenantEntitlement(Guid.NewGuid(), null));
        await db.SaveChangesAsync();
    }

    private static ApplicationUser User(Guid id) => new()
    {
        Id = id, UserName = $"u{id:N}@example.com", NormalizedUserName = $"U{id:N}@EXAMPLE.COM",
        Email = $"u{id:N}@example.com", NormalizedEmail = $"U{id:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = "Race user"
    };
}
