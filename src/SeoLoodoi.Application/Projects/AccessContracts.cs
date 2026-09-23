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

public sealed record ProjectMemberDto(Guid Id, Guid UserId, string Email, string DisplayName, string Role, DateTimeOffset CreatedAt, string Status = "Active");
public sealed record AddProjectMemberRequest(string Email, ProjectMemberRole Role = ProjectMemberRole.Viewer);
public sealed record ChangeProjectMemberRoleRequest(ProjectMemberRole Role);

public sealed record ProjectMemberDetailsDto(Guid Id, Guid UserId, string Email, string DisplayName, string Role, string Status, DateTimeOffset CreatedAt);
public sealed record ProjectInvitationDto(Guid Id, string Email, string Role, string Status, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);
public sealed record InviteProjectMemberRequest(string Email, ProjectMemberRole Role = ProjectMemberRole.Viewer);
public sealed record AcceptInvitationRequest(string Token);
public sealed record TeamSeatUsageDto(string Plan, int Limit, int Used, int Remaining);

/// <summary>Result of creating an invitation. <see cref="Token"/> is the only copy of the secret: e-mail it and never log or return it to clients.</summary>
public sealed record CreatedInvitation(ProjectInvitationDto Invitation, string Token);

public enum TeamOperationError { None, NotFound, Forbidden, Conflict, Validation, QuotaExceeded }

public sealed record TeamResult<T>(T? Value, TeamOperationError Error = TeamOperationError.None, string? Message = null)
{
    public bool Ok => Error == TeamOperationError.None;
    public static TeamResult<T> Success(T value) => new(value);
    public static TeamResult<T> Fail(TeamOperationError error, string message) => new(default, error, message);
}

/// <summary>
/// Team membership with plan-enforced seats (MaxTeamMembers). The seat rule is documented in docs/P0-AUDIT-AND-HARDENING.md (A1).
/// All seat-consuming operations run under the tenant quota lock.
/// </summary>
public interface ITeamService
{
    Task<TeamResult<ProjectMemberDetailsDto>> AddExistingUserAsync(Guid projectId, Guid actorId, AddProjectMemberRequest request, CancellationToken ct);
    Task<TeamResult<CreatedInvitation>> InviteAsync(Guid projectId, Guid actorId, InviteProjectMemberRequest request, CancellationToken ct);
    Task<TeamResult<bool>> CancelInvitationAsync(Guid projectId, Guid invitationId, Guid actorId, CancellationToken ct);
    Task<TeamResult<ProjectMemberDetailsDto>> AcceptInvitationAsync(Guid userId, string userEmail, string token, CancellationToken ct);
    Task<TeamResult<bool>> ChangeRoleAsync(Guid projectId, Guid memberId, Guid actorId, ProjectMemberRole role, CancellationToken ct);
    Task<TeamResult<bool>> SetSuspendedAsync(Guid projectId, Guid memberId, Guid actorId, bool suspended, CancellationToken ct);
    Task<TeamResult<bool>> RemoveAsync(Guid projectId, Guid memberId, Guid actorId, CancellationToken ct);
    Task<IReadOnlyList<ProjectInvitationDto>?> ListInvitationsAsync(Guid projectId, Guid actorId, CancellationToken ct);
    Task<TeamSeatUsageDto?> SeatsAsync(Guid projectId, Guid actorId, CancellationToken ct);
}
