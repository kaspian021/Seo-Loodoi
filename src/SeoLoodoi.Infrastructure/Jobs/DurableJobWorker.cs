using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Crawling;

namespace SeoLoodoi.Infrastructure.Jobs;

public sealed class DurableJobWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DurableJobWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var queue = scope.ServiceProvider.GetRequiredService<ISeoJobQueue>();
                var job = await queue.TryLeaseAsync(_workerId, clock.GetUtcNow(), TimeSpan.FromMinutes(2), stoppingToken);
                if (job is null) { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue; }
                using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id, ["JobType"] = job.Type, ["Attempt"] = job.Attempts });
                var handler = scope.ServiceProvider.GetServices<ISeoJobHandler>().SingleOrDefault(x => x.Type == job.Type);
                if (handler is null)
                {
                    logger.LogError("No handler is registered for SEO job type {JobType}", job.Type);
                    job.Retry(clock.GetUtcNow(), TimeSpan.Zero, "No registered handler.", 1);
                }
                else
                {
                    try { await handler.HandleAsync(job, stoppingToken); job.Succeed(clock.GetUtcNow()); logger.LogInformation("SEO job completed"); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, job.Attempts) * 5));
                        var error = ex.GetType().Name + ": " + ex.Message;
                        job.Retry(clock.GetUtcNow(), delay, error, 5);
                        if (job.Status == SeoJobStatus.Failed && (job.Type is SeoJobType.InitialCrawl or SeoJobType.ContinueCrawl))
                        {
                            try
                            {
                                var payload = JsonSerializer.Deserialize<CrawlJobPayload>(job.PayloadJson);
                                if (payload is not null)
                                {
                                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                                    var crawl = await db.Crawls.SingleOrDefaultAsync(x => x.Id == payload.CrawlId && x.ProjectId == payload.ProjectId, stoppingToken);
                                    crawl?.Fail(error, clock.GetUtcNow());
                                }
                            }
                            catch (Exception recoveryError) { logger.LogError(recoveryError, "Could not mark terminal crawl failure"); }
                        }
                        logger.LogError(ex, "SEO job failed and was scheduled for retry");
                    }
                }
                await queue.SaveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Durable SEO worker loop failed"); await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        }
    }
}
