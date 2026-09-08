using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Projects;

public sealed record ProjectAccess(Guid ProjectId, Guid UserId, bool IsOwner, ProjectMemberRole Role)
{
    public bool CanView => true;
    public bool CanEdit => IsOwner || Role is ProjectMemberRole.Editor or ProjectMemberRole.Admin;
    public bool CanManage => IsOwner || Role == ProjectMemberRole.Admin;
}

public interface IProjectAccessService
{
    Task<ProjectAccess?> GetAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<bool> CanViewAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<bool> CanEditAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<bool> CanManageAsync(Guid projectId, Guid userId, CancellationToken ct);
}

public sealed record ProjectMemberDto(Guid Id, Guid UserId, string Email, string DisplayName, string Role, DateTimeOffset CreatedAt);
public sealed record AddProjectMemberRequest(string Email, ProjectMemberRole Role = ProjectMemberRole.Viewer);
public sealed record ChangeProjectMemberRoleRequest(ProjectMemberRole Role);
