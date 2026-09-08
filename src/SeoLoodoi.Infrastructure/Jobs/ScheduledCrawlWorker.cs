using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Jobs;

/// <summary>Small scheduler intentionally delegates execution to the same quota-checked crawl command used by the API.</summary>
public sealed class ScheduledCrawlWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ScheduledCrawlWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var commands = scope.ServiceProvider.GetRequiredService<ICrawlCommandService>();
                var now = clock.GetUtcNow();
                var projects = await db.SeoProjects.Where(x => x.Status == Domain.Seo.ProjectStatus.Active && x.NextCrawlAt != null && x.NextCrawlAt <= now).ToListAsync(stoppingToken);
                foreach (var project in projects)
                {
                    try
                    {
                        await commands.StartAsync(project.Id, project.OwnerId, stoppingToken, Domain.Seo.CrawlTrigger.Scheduled);
                        project.ScheduleNext(now);
                    }
                    catch (InvalidOperationException ex)
                    {
                        logger.LogInformation("Scheduled crawl for {ProjectId} was skipped: {Reason}", project.Id, ex.Message);
                        project.ScheduleNext(now.AddMinutes(10));
                    }
                }
                if (projects.Count > 0) await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Scheduled crawl check failed"); }
        }
    }
}
