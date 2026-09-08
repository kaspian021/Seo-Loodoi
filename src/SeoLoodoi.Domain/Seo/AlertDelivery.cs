using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

/// <summary>
/// Durable delivery work for an alert event. The event is committed first and delivery
/// is retried independently so a transient SMTP or webhook failure cannot lose an alert.
/// </summary>
public sealed class AlertDelivery : Entity
{
    private AlertDelivery() { }

    public AlertDelivery(Guid projectId, Guid alertEventId, string channel, string destination, string payloadJson)
    {
        if (projectId == Guid.Empty || alertEventId == Guid.Empty) throw new ArgumentException("Project and alert event are required.");
        if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("Delivery channel is required.", nameof(channel));
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Delivery destination is required.", nameof(destination));
        ProjectId = projectId;
        AlertEventId = alertEventId;
        Channel = channel.Trim().ToLowerInvariant();
        Destination = destination.Trim();
        PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;
        NextAttemptAt = DateTimeOffset.UtcNow;
    }

    public Guid ProjectId { get; private set; }
    public Guid AlertEventId { get; private set; }
    public string Channel { get; private set; } = string.Empty;
    public string Destination { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = "{}";
    public string Status { get; private set; } = "Pending";
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public Guid? LeaseId { get; private set; }
    public string? LastError { get; private set; }

    public bool IsDue(DateTimeOffset now) => (Status is "Pending" or "Failed" or "Processing") && NextAttemptAt <= now && (LockedUntil is null || LockedUntil <= now);
    public void MarkProcessing(Guid leaseId, DateTimeOffset lockedUntil)
    {
        if (leaseId == Guid.Empty) throw new ArgumentException("A delivery lease is required.", nameof(leaseId));
        Status = "Processing"; LeaseId = leaseId; LockedUntil = lockedUntil; UpdatedAt = DateTimeOffset.UtcNow;
    }
    public void MarkDelivered()
    {
        Status = "Delivered"; LeaseId = null; LockedUntil = null; LastError = null; UpdatedAt = DateTimeOffset.UtcNow;
    }
    public void MarkFailed(string error, DateTimeOffset now, int maxAttempts = 10)
    {
        Attempts++;
        Status = Attempts >= maxAttempts ? "DeadLetter" : "Failed";
        LastError = string.IsNullOrWhiteSpace(error) ? "Unknown delivery failure." : error[..Math.Min(2000, error.Length)];
        NextAttemptAt = now.Add(Backoff(Attempts));
        LeaseId = null; LockedUntil = null; UpdatedAt = now;
    }

    private static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(6, Math.Max(0, attempts - 1)))));
}
