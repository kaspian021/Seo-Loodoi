using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Monitoring;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// F-10 regression: the /alerts/check endpoint gates on CanManage. The
/// matrix below pins exactly which roles pass that gate (owner/Admin yes;
/// Editor/Viewer/stranger no — the endpoint answers 404 to the rest, the
/// same denial pattern as the member endpoints) and that the service itself
/// remains a no-op without manage access.
/// </summary>
public class ProjectAccessServiceTests
{
    private static (AppDbContext Db, ProjectAccessService Access, AlertService Alerts, Guid Project, Guid Owner, Guid Editor, Guid Viewer) Build()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"access-{Guid.NewGuid():N}")
            .Options;
        var db = new AppDbContext(options);
        var owner = Guid.NewGuid();
        var editor = Guid.NewGuid();
        var viewer = Guid.NewGuid();
        var project = new SeoProject(owner, "Access Project", new Uri("https://access.example.com"));
        db.SeoProjects.Add(project);
        db.ProjectMembers.Add(new ProjectMember(project.Id, editor, ProjectMemberRole.Editor));
        db.ProjectMembers.Add(new ProjectMember(project.Id, viewer, ProjectMemberRole.Viewer));
        db.SaveChanges();
        var access = new ProjectAccessService(db);
        var alerts = new AlertService(db, access, new OutboundUrlGuard(), new AuditStub());
        return (db, access, alerts, project.Id, owner, editor, viewer);
    }

    [Fact]
    public async Task Role_matrix()
    {
        var (_, access, _, project, owner, editor, viewer) = Build();
        var stranger = Guid.NewGuid();
        (await access.CanManageAsync(project, owner, CancellationToken.None)).Should().BeTrue("owner");
        (await access.CanEditAsync(project, owner, CancellationToken.None)).Should().BeTrue("owner");
        (await access.CanManageAsync(project, editor, CancellationToken.None)).Should().BeFalse("editor cannot manage (F-10)");
        (await access.CanEditAsync(project, editor, CancellationToken.None)).Should().BeTrue("editor can edit");
        (await access.CanManageAsync(project, viewer, CancellationToken.None)).Should().BeFalse("viewer cannot manage (F-10)");
        (await access.CanEditAsync(project, viewer, CancellationToken.None)).Should().BeFalse("viewer cannot edit");
        (await access.CanViewAsync(project, viewer, CancellationToken.None)).Should().BeTrue("viewer can view");
        (await access.CanViewAsync(project, stranger, CancellationToken.None)).Should().BeFalse("stranger has no access");
    }

    [Fact]
    public async Task Admin_member_can_manage()
    {
        var (db, access, _, project, _, _, _) = Build();
        var admin = Guid.NewGuid();
        db.ProjectMembers.Add(new ProjectMember(project, admin, ProjectMemberRole.Admin));
        db.SaveChanges();
        (await access.CanManageAsync(project, admin, CancellationToken.None)).Should().BeTrue("admin");
    }

    [Fact]
    public async Task Alert_check_service_is_a_no_op_without_manage_access()
    {
        var (_, _, alerts, project, _, editor, viewer) = Build();
        (await alerts.CheckAsync(project, editor, CancellationToken.None)).Should().Be(0, "editor");
        (await alerts.CheckAsync(project, viewer, CancellationToken.None)).Should().Be(0, "viewer");
        (await alerts.CheckAsync(project, Guid.NewGuid(), CancellationToken.None)).Should().Be(0, "stranger");
    }
}
