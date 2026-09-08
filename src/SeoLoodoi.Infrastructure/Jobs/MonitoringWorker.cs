using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SeoLoodoi.Application.Monitoring;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Jobs;

public sealed class MonitoringWorker(IServiceScopeFactory scopes, ILogger<MonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var alerts = scope.ServiceProvider.GetRequiredService<IAlertService>();
                var projectIds = await db.AlertRules.AsNoTracking().Where(x => x.IsEnabled).Select(x => x.ProjectId).Distinct().ToListAsync(stoppingToken);
                foreach (var projectId in projectIds) await alerts.CheckAsync(projectId, await db.SeoProjects.Where(x => x.Id == projectId).Select(x => x.OwnerId).SingleAsync(stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Monitoring check failed"); }
        }
    }
}
