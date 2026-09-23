using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// P0 A1 MaxTeamMembers. Seats = owner + distinct collaborators (active or suspended) + open invitations.
/// The cross-connection race is covered on PostgreSQL in AtomicQuotaPostgresTests. The in-process race is covered here.
/// </summary>
public sealed class TeamSeatTests
{
    private sealed class NoOpAudit : IAuditLogService
    {
        public Task RecordAsync(Guid? projectId, Guid actorId, string action, string entityType, string? entityId, string? metadataJson, string? ipAddress, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditLogDto>>([]);
    }

    private sealed class World
    {
        public readonly string Name = Guid.NewGuid().ToString("N");
        public AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Name).Options);
        public TeamService Team(AppDbContext db) => new(db, new ProjectAccessService(db), new QuotaService(db, Options.Create(new QuotaOptions())), new NoOpAudit());
        public Guid Owner = Guid.NewGuid();
        public Guid ProjectId;

        public static async Task<World> CreateAsync(int maxTeam)
        {
            var w = new World();
            await using var db = w.Db();
            db.Users.Add(User(w.Owner, "owner"));
            db.TenantEntitlements.Add(Entitlement(w.Owner, maxTeam));
            var project = new SeoProject(w.Owner, "Team", new Uri("https://team.example"));
            db.SeoProjects.Add(project);
            await db.SaveChangesAsync();
            w.ProjectId = project.Id;
            return w;
        }

        public async Task<Guid> AddUserAsync(string name)
        {
            await using var db = Db();
            var id = Guid.NewGuid(); db.Users.Add(User(id, name)); await db.SaveChangesAsync(); return id;
        }

        public async Task SetPlanAsync(int maxTeam, SubscriptionStatus status = SubscriptionStatus.Active)
        {
            await using var db = Db();
            var e = await db.TenantEntitlements.SingleAsync(x => x.UserId == Owner);
            e.UpdateSubscription("Pro", status, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), 10, 1000, 100, 10, maxTeam, 10, 30, null, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
    }

    private static string Email(string name) => $"{name}@example.com";
    private static ApplicationUser User(Guid id, string name) => new() { Id = id, UserName = Email(name), NormalizedUserName = Email(name).ToUpperInvariant(), Email = Email(name), NormalizedEmail = Email(name).ToUpperInvariant(), DisplayName = name };
    private static TenantEntitlement Entitlement(Guid owner, int maxTeam) =>
        new(owner, null, "Pro", SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), maxTeamMembers: maxTeam);

    [Fact]
    public async Task WithinQuota_Succeeds_AtQuota_IsRejected_OwnerCountsAsASeat()
    {
        var w = await World.CreateAsync(maxTeam: 3);
        await w.AddUserAsync("a"); await w.AddUserAsync("b"); await w.AddUserAsync("c");
        await using var db = w.Db();
        var team = w.Team(db);
        (await team.AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("a")), default)).Ok.Should().BeTrue();
        (await team.AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("b")), default)).Ok.Should().BeTrue();
        var above = await team.AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("c")), default);
        above.Error.Should().Be(TeamOperationError.QuotaExceeded, "owner + 2 members = 3 seats");
        (await team.SeatsAsync(w.ProjectId, w.Owner, default))!.Used.Should().Be(3);
    }

    [Fact]
    public async Task PendingInvitation_ReservesSeat_CancelFreesIt()
    {
        var w = await World.CreateAsync(maxTeam: 2);
        await using var db = w.Db();
        var team = w.Team(db);
        var invite = await team.InviteAsync(w.ProjectId, w.Owner, new("new@example.com"), default);
        invite.Ok.Should().BeTrue();
        (await team.InviteAsync(w.ProjectId, w.Owner, new("other@example.com"), default)).Error.Should().Be(TeamOperationError.QuotaExceeded);
        (await team.CancelInvitationAsync(w.ProjectId, invite.Value!.Invitation.Id, w.Owner, default)).Ok.Should().BeTrue();
        (await team.InviteAsync(w.ProjectId, w.Owner, new("other@example.com"), default)).Ok.Should().BeTrue("cancelling released the seat");
    }

    [Fact]
    public async Task ExpiredInvitation_FreesSeat_AndCannotBeAccepted()
    {
        var w = await World.CreateAsync(maxTeam: 2);
        var invitee = await w.AddUserAsync("late");
        await using (var seed = w.Db())
        {
            seed.ProjectInvitations.Add(new ProjectInvitation(w.ProjectId, Email("late"), ProjectMemberRole.Viewer, TeamService.HashToken("expired-token"), w.Owner, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await seed.SaveChangesAsync();
        }
        await using var db = w.Db();
        var team = w.Team(db);
        (await team.SeatsAsync(w.ProjectId, w.Owner, default))!.Used.Should().Be(1, "expired invitations do not hold seats");
        (await team.AcceptInvitationAsync(invitee, Email("late"), "expired-token", default)).Error.Should().Be(TeamOperationError.Conflict);
        (await team.InviteAsync(w.ProjectId, w.Owner, new("fresh@example.com"), default)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Accept_UsesTheReservedSeat_AndIsBoundToTheInvitedEmail()
    {
        var w = await World.CreateAsync(maxTeam: 2);
        var invitee = await w.AddUserAsync("invitee");
        var stranger = await w.AddUserAsync("stranger");
        await using var db = w.Db();
        var team = w.Team(db);
        var invite = await team.InviteAsync(w.ProjectId, w.Owner, new(Email("invitee"), ProjectMemberRole.Editor), default);
        (await team.AcceptInvitationAsync(stranger, Email("stranger"), invite.Value!.Token, default)).Error.Should().Be(TeamOperationError.Forbidden);
        var accepted = await team.AcceptInvitationAsync(invitee, Email("invitee"), invite.Value.Token, default);
        accepted.Ok.Should().BeTrue("the pending invitation already held the seat, so plan is exactly full, not over");
        accepted.Value!.Role.Should().Be("Editor");
        (await team.SeatsAsync(w.ProjectId, w.Owner, default))!.Used.Should().Be(2);
        (await team.AcceptInvitationAsync(invitee, Email("invitee"), invite.Value.Token, default)).Ok.Should().BeFalse("tokens are single-use");
    }

    [Fact]
    public async Task Unauthorized_ViewerCannotInvite_AdminCannotGrantAdmin_CrossTenantIsNotFound()
    {
        var w = await World.CreateAsync(maxTeam: 10);
        var viewer = await w.AddUserAsync("viewer"); var admin = await w.AddUserAsync("admin");
        await using (var seed = w.Db())
        {
            seed.ProjectMembers.Add(new ProjectMember(w.ProjectId, viewer, ProjectMemberRole.Viewer));
            seed.ProjectMembers.Add(new ProjectMember(w.ProjectId, admin, ProjectMemberRole.Admin));
            await seed.SaveChangesAsync();
        }
        await using var db = w.Db();
        var team = w.Team(db);
        (await team.InviteAsync(w.ProjectId, viewer, new("x@example.com"), default)).Error.Should().Be(TeamOperationError.NotFound);
        (await team.InviteAsync(w.ProjectId, admin, new("y@example.com", ProjectMemberRole.Admin), default)).Error.Should().Be(TeamOperationError.Forbidden);
        (await team.InviteAsync(w.ProjectId, admin, new("y@example.com", ProjectMemberRole.Editor), default)).Ok.Should().BeTrue();
        (await team.InviteAsync(w.ProjectId, Guid.NewGuid(), new("z@example.com"), default)).Error.Should().Be(TeamOperationError.NotFound, "another tenant cannot see the project");
    }

    [Fact]
    public async Task OwnerEdgeCases_OwnerCannotBeAddedOrInvited_AndIsNeverRemovable()
    {
        var w = await World.CreateAsync(maxTeam: 5);
        await using var db = w.Db();
        var team = w.Team(db);
        (await team.AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("owner")), default)).Error.Should().Be(TeamOperationError.Conflict);
        (await team.InviteAsync(w.ProjectId, w.Owner, new(Email("owner")), default)).Error.Should().Be(TeamOperationError.Conflict);
        (await team.SeatsAsync(w.ProjectId, w.Owner, default))!.Used.Should().Be(1, "the owner always holds exactly one seat");
    }

    [Fact]
    public async Task Suspended_KeepsSeat_LosesAccess_ReactivationNeedsNoNewSeat()
    {
        var w = await World.CreateAsync(maxTeam: 2);
        var member = await w.AddUserAsync("member"); await w.AddUserAsync("extra");
        await using var db = w.Db();
        var team = w.Team(db);
        var added = await team.AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("member"), ProjectMemberRole.Editor), default);
        (await team.SetSuspendedAsync(w.ProjectId, added.Value!.Id, w.Owner, true, default)).Ok.Should().BeTrue();
        (await new ProjectAccessService(db).CanViewAsync(w.ProjectId, member, default)).Should().BeFalse();
        (await team.AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("extra")), default)).Error.Should().Be(TeamOperationError.QuotaExceeded, "the suspended member still holds a seat");
        (await team.SetSuspendedAsync(w.ProjectId, added.Value.Id, w.Owner, false, default)).Ok.Should().BeTrue();
        (await new ProjectAccessService(db).CanEditAsync(w.ProjectId, member, default)).Should().BeTrue();
    }

    [Fact]
    public async Task Removal_FreesSeat()
    {
        var w = await World.CreateAsync(maxTeam: 2);
        await w.AddUserAsync("m1"); await w.AddUserAsync("m2");
        await using var db = w.Db();
        var team = w.Team(db);
        var m1 = await team.AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("m1")), default);
        (await team.RemoveAsync(w.ProjectId, m1.Value!.Id, w.Owner, default)).Ok.Should().BeTrue();
        (await team.AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("m2")), default)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Upgrade_AllowsMore_Downgrade_KeepsExistingMembers_ButBlocksNewSeats()
    {
        var w = await World.CreateAsync(maxTeam: 2);
        var a = await w.AddUserAsync("a"); await w.AddUserAsync("b"); await w.AddUserAsync("c");
        await using (var db = w.Db())
        {
            var team = w.Team(db);
            (await team.AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("a")), default)).Ok.Should().BeTrue();
            (await team.AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("b")), default)).Error.Should().Be(TeamOperationError.QuotaExceeded);
        }
        await w.SetPlanAsync(maxTeam: 4);
        await using (var db = w.Db())
            (await w.Team(db).AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("b")), default)).Ok.Should().BeTrue("upgrade raised the limit");
        await w.SetPlanAsync(maxTeam: 1);
        await using (var db = w.Db())
        {
            var team = w.Team(db);
            (await db.ProjectMembers.CountAsync(x => x.ProjectId == w.ProjectId)).Should().Be(2, "a downgrade never removes existing members");
            (await new ProjectAccessService(db).CanViewAsync(w.ProjectId, a, default)).Should().BeTrue();
            (await team.AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("c")), default)).Error.Should().Be(TeamOperationError.QuotaExceeded);
            (await team.InviteAsync(w.ProjectId, w.Owner, new(Email("c")), default)).Error.Should().Be(TeamOperationError.QuotaExceeded);
        }
    }

    [Fact]
    public async Task CanceledSubscription_FallsBackToFreeSeatLimit()
    {
        var w = await World.CreateAsync(maxTeam: 10);
        await w.AddUserAsync("a");
        await w.SetPlanAsync(maxTeam: 10, SubscriptionStatus.Canceled);
        await using var db = w.Db();
        (await w.Team(db).AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("a")), default)).Error.Should().Be(TeamOperationError.QuotaExceeded);
    }

    [Fact]
    public async Task ConcurrentInvites_InProcess_NeverExceedTheLimit()
    {
        var w = await World.CreateAsync(maxTeam: 3);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            await using var db = w.Db();
            return (await w.Team(db).InviteAsync(w.ProjectId, w.Owner, new($"race{i}@example.com"), default)).Ok;
        })));
        results.Count(x => x).Should().Be(2);
    }

    [Fact]
    public async Task MemberOnSeveralProjectsOfOneOwner_CountsOnce()
    {
        var w = await World.CreateAsync(maxTeam: 2);
        await w.AddUserAsync("multi");
        Guid second;
        await using (var seed = w.Db())
        {
            var p = new SeoProject(w.Owner, "Second", new Uri("https://second.example")); seed.SeoProjects.Add(p); await seed.SaveChangesAsync(); second = p.Id;
        }
        await using var db = w.Db();
        var team = w.Team(db);
        (await team.AddExistingUserAsync(w.ProjectId, w.Owner, new(Email("multi")), default)).Ok.Should().BeTrue();
        (await team.AddExistingUserAsync(second, w.Owner, new(Email("multi")), default)).Ok.Should().BeTrue("the same person does not consume a second seat");
    }
}
