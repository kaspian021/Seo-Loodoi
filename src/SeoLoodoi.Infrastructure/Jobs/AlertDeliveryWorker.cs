using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SeoLoodoi.Application.Monitoring;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Infrastructure.Jobs;

/// <summary>
/// Claims and delivers persisted alert work. Claims use a lease so a crashed worker
/// becomes retryable, while the conditional update prevents two PostgreSQL workers
/// from processing the same delivery at the same time.
/// </summary>
public sealed class AlertDeliveryWorker(IServiceScopeFactory scopes, ILogger<AlertDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await ProcessBatchAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Alert delivery batch failed"); }
        }
    }

    private static async Task ProcessBatchAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        for (var i = 0; i < 20; i++)
        {
            var now = DateTimeOffset.UtcNow;
            var candidate = await db.AlertDeliveries.AsNoTracking()
                .Where(x => (x.Status == "Pending" || x.Status == "Failed" || x.Status == "Processing") && x.NextAttemptAt <= now && (x.LockedUntil == null || x.LockedUntil <= now))
                .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            if (candidate is null) break;

            var lease = Guid.NewGuid();
            var lockedUntil = now.AddMinutes(10);
            AlertDelivery? tracked = null;
            var relational = db.Database.IsRelational();
            var claimed = 0;
            if (relational)
            {
                claimed = await db.AlertDeliveries.Where(x => x.Id == candidate.Id &&
                        (x.Status == "Pending" || x.Status == "Failed" || x.Status == "Processing") && x.NextAttemptAt <= now && (x.LockedUntil == null || x.LockedUntil <= now))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, "Processing")
                        .SetProperty(x => x.LeaseId, (Guid?)lease)
                        .SetProperty(x => x.LockedUntil, (DateTimeOffset?)lockedUntil)
                        .SetProperty(x => x.UpdatedAt, now), ct);
            }
            else
            {
                tracked = await db.AlertDeliveries.SingleOrDefaultAsync(x => x.Id == candidate.Id, ct);
                if (tracked is null || !tracked.IsDue(now)) continue;
                tracked.MarkProcessing(lease, lockedUntil);
                claimed = await db.SaveChangesAsync(ct);
            }
            if (claimed == 0) continue;

            var delivery = tracked ?? await db.AlertDeliveries.AsNoTracking().SingleAsync(x => x.LeaseId == lease, ct);
            try
            {
                await DeliverAsync(delivery, services, ct);
                if (relational)
                {
                    await db.AlertDeliveries.Where(x => x.Id == delivery.Id && x.LeaseId == lease)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "Delivered").SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LockedUntil, (DateTimeOffset?)null).SetProperty(x => x.LastError, (string?)null).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
                }
                else
                {
                    tracked!.MarkDelivered(); await db.SaveChangesAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var attempt = delivery.Attempts + 1;
                var status = attempt >= 10 ? "DeadLetter" : "Failed";
                var next = DateTimeOffset.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(6, Math.Max(0, attempt - 1)))));
                var message = string.IsNullOrWhiteSpace(ex.Message) ? "Unknown delivery failure." : ex.Message[..Math.Min(2000, ex.Message.Length)];
                if (relational)
                {
                    await db.AlertDeliveries.Where(x => x.Id == delivery.Id && x.LeaseId == lease)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, status).SetProperty(x => x.Attempts, attempt).SetProperty(x => x.NextAttemptAt, next).SetProperty(x => x.LastError, message).SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LockedUntil, (DateTimeOffset?)null).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
                }
                else
                {
                    tracked!.MarkFailed(message, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct);
                }
            }
        }
    }

    private static async Task DeliverAsync(AlertDelivery delivery, IServiceProvider services, CancellationToken ct)
    {
        if (delivery.Channel == "email")
        {
            await services.GetRequiredService<IEmailDelivery>().SendAsync(delivery.Destination, "SEO Loodoi alert", $"<p>یک هشدار برای پروژه ثبت شد.</p><pre>{System.Net.WebUtility.HtmlEncode(delivery.PayloadJson)}</pre>", ct);
            return;
        }
        if (delivery.Channel != "webhook" || !Uri.TryCreate(delivery.Destination, UriKind.Absolute, out var current)) throw new InvalidOperationException("Alert delivery destination is invalid.");
        var guard = services.GetRequiredService<IOutboundUrlGuard>();
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("AlertDelivery");
        for (var hop = 0; hop <= 5; hop++)
        {
            await guard.ValidateAsync(current, ct);
                using var request = new HttpRequestMessage(HttpMethod.Post, current) { Content = new StringContent(delivery.PayloadJson, System.Text.Encoding.UTF8, "application/json") };
                request.Headers.TryAddWithoutValidation("Idempotency-Key", delivery.Id.ToString("N"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location is { } location)
            {
                if (hop == 5) throw new HttpRequestException("Alert webhook redirect limit exceeded.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Alert webhook returned {(int)response.StatusCode}.");
            return;
        }
    }
}
