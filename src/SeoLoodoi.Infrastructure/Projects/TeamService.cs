using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Projects;

/// <summary>
/// Team seats (A1). Rules:
/// <list type="bullet">
/// <item>Seats = owner (always 1) + distinct collaborators across all of the owner's projects
/// (Active and Suspended) + distinct open invitations for e-mails that are not already collaborators.</item>
/// <item>A pending, unexpired invitation reserves a seat. Cancelled, expired and accepted invitations do not.</item>
/// <item>A suspended member keeps the seat and loses all access. Reactivation needs no new seat.</item>
/// <item>Removing a member frees the seat immediately.</item>
/// <item>The owner can never be added, invited, suspended or removed. Admins manage Editors and Viewers.
/// Only the owner can change, suspend or remove an Admin.</item>
/// <item>A plan change never removes existing members. It only blocks new seats (add, invite, and accept
/// after a downgrade) while usage is over the limit.</item>
/// </list>
/// </summary>
public sealed class TeamService(AppDbContext db, IProjectAccessService access, IQuotaService quota, IAuditLogService audit) : ITeamService
{
    public async Task<TeamResult<ProjectMemberDetailsDto>> AddExistingUserAsync(Guid projectId, Guid actorId, AddProjectMemberRequest request, CancellationToken ct)
    {
        var ctx = await ManageContextAsync(projectId, actorId, ct);
        if (ctx is null) return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.NotFound, "Project not found.");
        if (string.IsNullOrWhiteSpace(request.Email)) return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.Validation, "Email is required.");
        if (!Enum.IsDefined(request.Role)) return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.Validation, "Invalid role.");
        if (request.Role == ProjectMemberRole.Admin && !ctx.Value.Access.IsOwner) return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.Forbidden, "Only the owner can grant the Admin role.");
        var normalized = ProjectInvitation.NormalizeEmail(request.Email);
        var target = await db.Users.AsNoTracking().Where(u => u.NormalizedEmail == normalized).Select(u => new { u.Id, u.Email, u.DisplayName }).SingleOrDefaultAsync(ct);
        if (target is null) return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.Validation, "No account exists for this e-mail.");
        if (target.Id == ctx.Value.OwnerId) return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.Conflict, "The project owner cannot be added as a member.");

        try
        {
            return await quota.WithTenantLockAsync(ctx.Value.OwnerId, async (lease, token) =>
            {
                if (await db.ProjectMembers.AnyAsync(x => x.ProjectId == projectId && x.UserId == target.Id, token))
                    return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.Conflict, "This user is already a project member.");
                if (!await AlreadyHoldsSeatAsync(ctx.Value.OwnerId, target.Id, normalized, token))
                    await lease.EnsureAvailableAsync(QuotaDimension.TeamSeats, 1, token);
                var member = new ProjectMember(projectId, target.Id, request.Role);
                db.ProjectMembers.Add(member);
                // An open invitation for the same person on this project is fulfilled by the direct add.
                foreach (var inv in await db.ProjectInvitations.Where(i => i.ProjectId == projectId && i.NormalizedEmail == normalized && i.Status == ProjectInvitationStatus.Pending).ToListAsync(token))
                    inv.Cancel(DateTimeOffset.UtcNow);
                await SaveUniqueAsync(token);
                await audit.RecordAsync(projectId, actorId, "PROJECT_MEMBER_ADDED", "ProjectMember", member.Id.ToString(), JsonSerializer.Serialize(new { member.UserId, role = member.Role.ToString() }), null, token);
                return TeamResult<ProjectMemberDetailsDto>.Success(new ProjectMemberDetailsDto(member.Id, member.UserId, target.Email ?? "", target.DisplayName, member.Role.ToString(), member.Status.ToString(), member.CreatedAt));
            }, ct);
        }
        catch (QuotaExceededException ex) { return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.QuotaExceeded, ex.Message); }
        catch (DuplicateMembershipException) { return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.Conflict, "This user is already a project member."); }
    }

    public async Task<TeamResult<CreatedInvitation>> InviteAsync(Guid projectId, Guid actorId, InviteProjectMemberRequest request, CancellationToken ct)
    {
        var ctx = await ManageContextAsync(projectId, actorId, ct);
        if (ctx is null) return TeamResult<CreatedInvitation>.Fail(TeamOperationError.NotFound, "Project not found.");
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Trim().Length > 256 || !System.Net.Mail.MailAddress.TryCreate(request.Email.Trim(), out _))
            return TeamResult<CreatedInvitation>.Fail(TeamOperationError.Validation, "A valid e-mail is required.");
        if (!Enum.IsDefined(request.Role)) return TeamResult<CreatedInvitation>.Fail(TeamOperationError.Validation, "Invalid role.");
        if (request.Role == ProjectMemberRole.Admin && !ctx.Value.Access.IsOwner) return TeamResult<CreatedInvitation>.Fail(TeamOperationError.Forbidden, "Only the owner can grant the Admin role.");
        var normalized = ProjectInvitation.NormalizeEmail(request.Email);
        var ownerEmail = await db.Users.AsNoTracking().Where(u => u.Id == ctx.Value.OwnerId).Select(u => u.NormalizedEmail).SingleOrDefaultAsync(ct);
        if (ownerEmail == normalized) return TeamResult<CreatedInvitation>.Fail(TeamOperationError.Conflict, "The project owner cannot be invited.");

        try
        {
            return await quota.WithTenantLockAsync(ctx.Value.OwnerId, async (lease, token) =>
            {
                var now = DateTimeOffset.UtcNow;
                var alreadyMember = await (from m in db.ProjectMembers join u in db.Users on m.UserId equals u.Id where m.ProjectId == projectId && u.NormalizedEmail == normalized select m.Id).AnyAsync(token);
                if (alreadyMember) return TeamResult<CreatedInvitation>.Fail(TeamOperationError.Conflict, "This user is already a project member.");
                if (await db.ProjectInvitations.AnyAsync(i => i.ProjectId == projectId && i.NormalizedEmail == normalized && i.Status == ProjectInvitationStatus.Pending && i.ExpiresAt > now, token))
                    return TeamResult<CreatedInvitation>.Fail(TeamOperationError.Conflict, "An invitation for this e-mail is already pending.");
                if (!await AlreadyHoldsSeatAsync(ctx.Value.OwnerId, null, normalized, token))
                    await lease.EnsureAvailableAsync(QuotaDimension.TeamSeats, 1, token);
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var invitation = new ProjectInvitation(projectId, request.Email, request.Role, HashToken(secret), actorId, now.Add(ProjectInvitation.DefaultLifetime));
                db.ProjectInvitations.Add(invitation);
                await db.SaveChangesAsync(token);
                // The e-mail address is personal data; the audit log records only the invitation id and role.
                await audit.RecordAsync(projectId, actorId, "PROJECT_INVITATION_CREATED", "ProjectInvitation", invitation.Id.ToString(), JsonSerializer.Serialize(new { role = invitation.Role.ToString(), invitation.ExpiresAt }), null, token);
                return TeamResult<CreatedInvitation>.Success(new CreatedInvitation(ToDto(invitation, now), secret));
            }, ct);
        }
        catch (QuotaExceededException ex) { return TeamResult<CreatedInvitation>.Fail(TeamOperationError.QuotaExceeded, ex.Message); }
    }

    public async Task<TeamResult<bool>> CancelInvitationAsync(Guid projectId, Guid invitationId, Guid actorId, CancellationToken ct)
    {
        if (await ManageContextAsync(projectId, actorId, ct) is null) return TeamResult<bool>.Fail(TeamOperationError.NotFound, "Project not found.");
        var invitation = await db.ProjectInvitations.SingleOrDefaultAsync(x => x.Id == invitationId && x.ProjectId == projectId, ct);
        if (invitation is null) return TeamResult<bool>.Fail(TeamOperationError.NotFound, "Invitation not found.");
        if (invitation.Status != ProjectInvitationStatus.Pending) return TeamResult<bool>.Fail(TeamOperationError.Conflict, "Only a pending invitation can be cancelled.");
        invitation.Cancel(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, actorId, "PROJECT_INVITATION_CANCELLED", "ProjectInvitation", invitation.Id.ToString(), "{}", null, ct);
        return TeamResult<bool>.Success(true);
    }

    public async Task<TeamResult<ProjectMemberDetailsDto>> AcceptInvitationAsync(Guid userId, string userEmail, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.NotFound, "Invitation not found.");
        var hash = HashToken(token.Trim());
        var probe = await db.ProjectInvitations.AsNoTracking().Where(x => x.TokenHash == hash).Select(x => new { x.Id, x.ProjectId }).SingleOrDefaultAsync(ct);
        if (probe is null) return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.NotFound, "Invitation not found.");
        var ownerId = await db.SeoProjects.Where(p => p.Id == probe.ProjectId).Select(p => (Guid?)p.OwnerId).SingleOrDefaultAsync(ct);
        if (ownerId is null) return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.NotFound, "Invitation not found.");

        try
        {
            return await quota.WithTenantLockAsync(ownerId.Value, async (lease, lockToken) =>
            {
                var now = DateTimeOffset.UtcNow;
                var invitation = await db.ProjectInvitations.SingleAsync(x => x.Id == probe.Id, lockToken);
                if (!invitation.IsOpen(now)) return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.Conflict, "Invitation has expired or is no longer valid.");
                // Tokens are bound to the invited address: a leaked link cannot be redeemed by another account.
                if (ProjectInvitation.NormalizeEmail(userEmail ?? "") != invitation.NormalizedEmail) return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.Forbidden, "This invitation was issued to a different e-mail address.");
                if (userId == ownerId.Value) return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.Conflict, "The project owner cannot accept a member invitation.");
                if (await db.ProjectMembers.AnyAsync(x => x.ProjectId == invitation.ProjectId && x.UserId == userId, lockToken))
                    return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.Conflict, "You are already a member of this project.");
                // The open invitation already holds a seat. Accepting it moves that seat to a member,
                // unless a downgrade has pushed usage above the limit.
                var seats = await lease.CheckAsync(QuotaDimension.TeamSeats, lockToken);
                if (seats.Used > seats.Limit) throw new QuotaExceededException($"Team seat quota for {seats.Plan} plan has been reached ({seats.Limit}).") { Dimension = QuotaDimension.TeamSeats };
                invitation.Accept(userId, now);
                var member = new ProjectMember(invitation.ProjectId, userId, invitation.Role);
                db.ProjectMembers.Add(member);
                await SaveUniqueAsync(lockToken);
                await audit.RecordAsync(invitation.ProjectId, userId, "PROJECT_INVITATION_ACCEPTED", "ProjectMember", member.Id.ToString(), JsonSerializer.Serialize(new { invitationId = invitation.Id, role = member.Role.ToString() }), null, lockToken);
                var profile = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Email, u.DisplayName }).SingleAsync(lockToken);
                return TeamResult<ProjectMemberDetailsDto>.Success(new ProjectMemberDetailsDto(member.Id, userId, profile.Email ?? "", profile.DisplayName, member.Role.ToString(), member.Status.ToString(), member.CreatedAt));
            }, ct);
        }
        catch (QuotaExceededException ex) { return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.QuotaExceeded, ex.Message); }
        catch (DuplicateMembershipException) { return TeamResult<ProjectMemberDetailsDto>.Fail(TeamOperationError.Conflict, "You are already a member of this project."); }
    }

    public Task<TeamResult<bool>> ChangeRoleAsync(Guid projectId, Guid memberId, Guid actorId, ProjectMemberRole role, CancellationToken ct) =>
        MutateMemberAsync(projectId, memberId, actorId, ct, (member, isOwner) =>
        {
            if (!Enum.IsDefined(role)) return "Invalid role.";
            if (role == ProjectMemberRole.Admin && !isOwner) return "Only the owner can grant the Admin role.";
            member.ChangeRole(role); return null;
        }, "PROJECT_MEMBER_ROLE_CHANGED", m => JsonSerializer.Serialize(new { role = m.Role.ToString() }));

    public Task<TeamResult<bool>> SetSuspendedAsync(Guid projectId, Guid memberId, Guid actorId, bool suspended, CancellationToken ct) =>
        MutateMemberAsync(projectId, memberId, actorId, ct, (member, _) =>
        {
            if (member.UserId == actorId) return "You cannot suspend yourself.";
            if (suspended) member.Suspend(DateTimeOffset.UtcNow); else member.Reactivate(DateTimeOffset.UtcNow);
            return null;
        }, suspended ? "PROJECT_MEMBER_SUSPENDED" : "PROJECT_MEMBER_REACTIVATED", _ => "{}");

    public async Task<TeamResult<bool>> RemoveAsync(Guid projectId, Guid memberId, Guid actorId, CancellationToken ct)
    {
        var ctx = await ManageContextAsync(projectId, actorId, ct);
        if (ctx is null) return TeamResult<bool>.Fail(TeamOperationError.NotFound, "Project not found.");
        var member = await db.ProjectMembers.SingleOrDefaultAsync(x => x.Id == memberId && x.ProjectId == projectId, ct);
        if (member is null) return TeamResult<bool>.Fail(TeamOperationError.NotFound, "Member not found.");
        if (member.Role == ProjectMemberRole.Admin && !ctx.Value.Access.IsOwner && member.UserId != actorId) return TeamResult<bool>.Fail(TeamOperationError.Forbidden, "Only the owner can remove an Admin.");
        db.ProjectMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, actorId, "PROJECT_MEMBER_REMOVED", "ProjectMember", memberId.ToString(), "{}", null, ct);
        return TeamResult<bool>.Success(true);
    }

    public async Task<IReadOnlyList<ProjectInvitationDto>?> ListInvitationsAsync(Guid projectId, Guid actorId, CancellationToken ct)
    {
        if (await ManageContextAsync(projectId, actorId, ct) is null) return null;
        var now = DateTimeOffset.UtcNow;
        var rows = await db.ProjectInvitations.AsNoTracking().Where(x => x.ProjectId == projectId && x.Status == ProjectInvitationStatus.Pending).OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(ct);
        return rows.Select(x => ToDto(x, now)).ToArray();
    }

    public async Task<TeamSeatUsageDto?> SeatsAsync(Guid projectId, Guid actorId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, actorId, ct)) return null;
        var ownerId = await db.SeoProjects.Where(p => p.Id == projectId).Select(p => p.OwnerId).SingleAsync(ct);
        var seats = await quota.WithTenantLockAsync(ownerId, (lease, token) => lease.CheckAsync(QuotaDimension.TeamSeats, token), ct);
        return new TeamSeatUsageDto(seats.Plan, seats.Limit, seats.Used, seats.Remaining);
    }

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private async Task<TeamResult<bool>> MutateMemberAsync(Guid projectId, Guid memberId, Guid actorId, CancellationToken ct, Func<ProjectMember, bool, string?> mutate, string auditAction, Func<ProjectMember, string> metadata)
    {
        var ctx = await ManageContextAsync(projectId, actorId, ct);
        if (ctx is null) return TeamResult<bool>.Fail(TeamOperationError.NotFound, "Project not found.");
        var member = await db.ProjectMembers.SingleOrDefaultAsync(x => x.Id == memberId && x.ProjectId == projectId, ct);
        if (member is null) return TeamResult<bool>.Fail(TeamOperationError.NotFound, "Member not found.");
        if (member.Role == ProjectMemberRole.Admin && !ctx.Value.Access.IsOwner) return TeamResult<bool>.Fail(TeamOperationError.Forbidden, "Only the owner can change an Admin.");
        var error = mutate(member, ctx.Value.Access.IsOwner);
        if (error is not null) return TeamResult<bool>.Fail(error.StartsWith("Only", StringComparison.Ordinal) ? TeamOperationError.Forbidden : TeamOperationError.Validation, error);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, actorId, auditAction, "ProjectMember", member.Id.ToString(), metadata(member), null, ct);
        return TeamResult<bool>.Success(true);
    }

    private async Task<(Guid OwnerId, ProjectAccess Access)?> ManageContextAsync(Guid projectId, Guid actorId, CancellationToken ct)
    {
        var accessInfo = await access.GetAsync(projectId, actorId, ct);
        if (accessInfo is null || !accessInfo.CanManage) return null;
        var ownerId = await db.SeoProjects.Where(p => p.Id == projectId).Select(p => (Guid?)p.OwnerId).SingleOrDefaultAsync(ct);
        return ownerId is null ? null : (ownerId.Value, accessInfo);
    }

    /// <summary>True when the person is already counted toward the owner's seats (member of any owner project or holder of an open invitation).</summary>
    private async Task<bool> AlreadyHoldsSeatAsync(Guid ownerId, Guid? userId, string normalizedEmail, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var member = await (from m in db.ProjectMembers
                            join p in db.SeoProjects on m.ProjectId equals p.Id
                            join u in db.Users on m.UserId equals u.Id
                            where p.OwnerId == ownerId && (u.NormalizedEmail == normalizedEmail || (userId != null && m.UserId == userId))
                            select m.Id).AnyAsync(ct);
        if (member) return true;
        return await (from i in db.ProjectInvitations
                      join p in db.SeoProjects on i.ProjectId equals p.Id
                      where p.OwnerId == ownerId && i.NormalizedEmail == normalizedEmail && i.Status == ProjectInvitationStatus.Pending && i.ExpiresAt > now
                      select i.Id).AnyAsync(ct);
    }

    private async Task SaveUniqueAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbExceptionClassifier.IsUniqueViolation(ex)) { throw new DuplicateMembershipException(); }
    }

    private static ProjectInvitationDto ToDto(ProjectInvitation x, DateTimeOffset now) =>
        new(x.Id, x.Email, x.Role.ToString(), x.Status == ProjectInvitationStatus.Pending && x.ExpiresAt <= now ? "Expired" : x.Status.ToString(), x.ExpiresAt, x.CreatedAt);

    private sealed class DuplicateMembershipException : Exception;
}
