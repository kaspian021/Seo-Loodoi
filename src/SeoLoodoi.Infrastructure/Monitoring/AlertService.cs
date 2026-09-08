using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Monitoring;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Infrastructure.Monitoring;

public sealed class AlertService(AppDbContext db, IProjectAccessService access, IOutboundUrlGuard guard, IAuditLogService audit) : IAlertService
{
    public async Task<IReadOnlyList<AlertRuleDto>> ListRulesAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return [];
        return await db.AlertRules.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Type).Select(ToRule).ToListAsync(ct);
    }

    public async Task<AlertRuleDto?> CreateRuleAsync(Guid projectId, Guid userId, CreateAlertRuleRequest request, CancellationToken ct)
    {
        if (!await access.CanManageAsync(projectId, userId, ct)) return null;
        var allowed = new[] { "SCORE_DROP", "CRITICAL_ISSUE", "CRAWL_FAILURE" };
        var type = (request.Type ?? string.Empty).Trim().ToUpperInvariant(); if (!allowed.Contains(type)) throw new ArgumentException("Unsupported alert type.");
        var channel = (request.Channel ?? string.Empty).Trim().ToLowerInvariant();
        ValidateChannel(channel, request.Destination);
        if (channel == "webhook") await guard.ValidateAsync(new Uri(request.Destination!), ct);
        var rule = new AlertRule(projectId, type, request.Threshold, channel, request.Destination); db.AlertRules.Add(rule); await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, userId, "ALERT_RULE_CREATED", "AlertRule", rule.Id.ToString(), System.Text.Json.JsonSerializer.Serialize(new { rule.Type, rule.Channel }), null, ct);
        return ToDto(rule);
    }

    public async Task<bool> UpdateRuleAsync(Guid projectId, Guid ruleId, Guid userId, UpdateAlertRuleRequest request, CancellationToken ct)
    {
        if (!await access.CanManageAsync(projectId, userId, ct)) return false;
        var rule = await db.AlertRules.SingleOrDefaultAsync(x => x.Id == ruleId && x.ProjectId == projectId, ct); if (rule is null) return false;
        var channel = (request.Channel ?? string.Empty).Trim().ToLowerInvariant();
        ValidateChannel(channel, request.Destination);
        if (channel == "webhook") await guard.ValidateAsync(new Uri(request.Destination!), ct);
        rule.Configure(request.IsEnabled, request.Threshold, channel, request.Destination); await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, userId, "ALERT_RULE_UPDATED", "AlertRule", ruleId.ToString(), System.Text.Json.JsonSerializer.Serialize(new { rule.Channel, rule.IsEnabled }), null, ct);
        return true;
    }

    public async Task<IReadOnlyList<AlertEventDto>> ListEventsAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return [];
        return await db.AlertEvents.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.DetectedAt).Take(100).Select(x => new AlertEventDto(x.Id, x.AlertRuleId, x.EventType, x.PayloadJson, x.DetectedAt, x.IsRead)).ToListAsync(ct);
    }

    public async Task<int> CheckAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanManageAsync(projectId, userId, ct)) return 0;
        var rules = await db.AlertRules.Where(x => x.ProjectId == projectId && x.IsEnabled).ToListAsync(ct);
        var scores = await db.SeoScores.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.CreatedAt).Take(2).ToListAsync(ct);
        var crawl = await db.Crawls.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        var created = 0; var createdEvents = new List<(AlertRule Rule, AlertEvent Event)>();
        foreach (var rule in rules)
        {
            var triggered = rule.Type switch
            {
                "SCORE_DROP" => scores.Count == 2 && scores[0].OverallScore is { } current && scores[1].OverallScore is { } previous && current < previous - rule.Threshold,
                "CRITICAL_ISSUE" => crawl is not null && await db.SeoIssues.AnyAsync(x => x.ProjectId == projectId && x.CrawlId == crawl.Id && x.Status == IssueStatus.Open && x.Severity == IssueSeverity.Critical, ct),
                "CRAWL_FAILURE" => crawl?.Status == CrawlStatus.Failed,
                _ => false
            };
            if (!triggered) continue;
            var eventType = rule.Type; var recent = await db.AlertEvents.AnyAsync(x => x.AlertRuleId == rule.Id && x.EventType == eventType && x.DetectedAt >= DateTimeOffset.UtcNow.AddHours(-24), ct); if (recent) continue;
            var payload = JsonSerializer.Serialize(new { rule = rule.Type, score = scores.FirstOrDefault()?.OverallScore, previousScore = scores.Skip(1).FirstOrDefault()?.OverallScore, crawlId = crawl?.Id });
            var alertEvent = new AlertEvent(projectId, rule.Id, eventType, payload); db.AlertEvents.Add(alertEvent); createdEvents.Add((rule, alertEvent)); created++;
        }
        if (created > 0)
        {
            foreach (var (rule, alertEvent) in createdEvents.Where(x => x.Rule.Channel is "webhook" or "email"))
            {
                // The event and its delivery work are committed together. A worker owns
                // retries and delivery failures without changing the factual event.
                db.AlertDeliveries.Add(new AlertDelivery(projectId, alertEvent.Id, rule.Channel, rule.Destination!, alertEvent.PayloadJson));
            }
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync(projectId, userId, "ALERTS_CHECKED", "AlertEvent", null, JsonSerializer.Serialize(new { created }), null, ct);
        }
        return created;
    }

    private static void ValidateChannel(string channel, string? destination)
    {
        if (channel is not ("dashboard" or "webhook" or "email")) throw new ArgumentException("Unsupported alert channel.");
        if (channel == "webhook" && (!Uri.TryCreate(destination, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))) throw new ArgumentException("A valid webhook URL is required.");
        if (channel == "email")
        {
            if (string.IsNullOrWhiteSpace(destination) || !MailAddress.TryCreate(destination.Trim(), out var email) || email is null || !string.Equals(email.Address, destination.Trim(), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A valid email destination is required.");
        }
    }

    private static readonly System.Linq.Expressions.Expression<Func<AlertRule, AlertRuleDto>> ToRule = x => new AlertRuleDto(x.Id, x.Type, x.Threshold, x.Channel, x.Destination, x.IsEnabled, x.CreatedAt);
    private static AlertRuleDto ToDto(AlertRule x) => new(x.Id, x.Type, x.Threshold, x.Channel, x.Destination, x.IsEnabled, x.CreatedAt);
}
