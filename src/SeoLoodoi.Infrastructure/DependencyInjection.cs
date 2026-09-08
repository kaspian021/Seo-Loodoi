using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeoLoodoi.Application.AI;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Competitors;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Keywords;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Monitoring;
using SeoLoodoi.Application.Reports;
using SeoLoodoi.Application.SearchConsole;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Infrastructure.AI;
using SeoLoodoi.Infrastructure.Analysis;
using SeoLoodoi.Infrastructure.Competitors;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Keywords;
using SeoLoodoi.Infrastructure.Identity;
using SeoLoodoi.Infrastructure.Jobs;
using SeoLoodoi.Infrastructure.Monitoring;
using SeoLoodoi.Infrastructure.Reports;
using SeoLoodoi.Infrastructure.SearchConsole;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["DatabaseProvider"];
        if (string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("seo-loodoi-preview"));
        else
        {
            var connection = config.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        }
        services.AddIdentityApiEndpoints<ApplicationUser>(o =>
        {
            o.Password.RequiredLength = 10;
            o.Password.RequireDigit = true;
            o.Password.RequireLowercase = true;
            o.Password.RequireUppercase = true;
            o.Password.RequireNonAlphanumeric = true;
            o.User.RequireUniqueEmail = true;
            o.SignIn.RequireConfirmedEmail = config.GetValue("Identity:RequireConfirmedEmail", false);
        })
            .AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>();
        services.AddOptions<EmailOptions>().BindConfiguration("Email");
        services.AddTransient<IEmailSender<ApplicationUser>, SmtpEmailSender>();
        services.AddTransient<IEmailDelivery, SmtpEmailSender>();
        services.AddOptions<QuotaOptions>().BindConfiguration("Quota");
        services.AddOptions<AiOptions>().BindConfiguration("AI");
        services.AddOptions<SearchConsoleOptions>().BindConfiguration("SearchConsole");
        services.AddDataProtection();
        services.AddScoped<ISeoProjectRepository, SeoProjectRepository>();
        services.AddScoped<IProjectAccessService, ProjectAccessService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IQuotaService, QuotaService>();
        services.AddScoped<IRecommendationQueryService, RecommendationQueryService>();
        services.AddScoped<IKeywordService, KeywordService>();
        services.AddScoped<ICompetitorService, CompetitorService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IAlertService, AlertService>();
        services.AddHttpClient("AlertDelivery", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<ISearchConsoleService, SearchConsoleService>(client => client.Timeout = TimeSpan.FromSeconds(60));
        services.AddScoped<IAiAnalysisService, AiAnalysisService>();
        services.AddHttpClient<IAiSeoExpert, AiSeoExpert>((provider, client) =>
        {
            var configured = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(configured.TimeoutSeconds, 5, 180));
        });
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<ICrawlFrontierStore, CrawlFrontierStore>();
        services.AddScoped<ISeoJobQueue, SeoJobQueue>();
        services.AddSingleton(TimeProvider.System);
        services.AddHostedService<DurableJobWorker>();
        services.AddHostedService<ScheduledCrawlWorker>();
        services.AddHostedService<MonitoringWorker>();
        services.AddHostedService<AlertDeliveryWorker>();
        services.AddSingleton<IOutboundUrlGuard, OutboundUrlGuard>();
        services.AddSingleton<IHostRequestCoordinator, HostRequestCoordinator>();
        services.AddMemoryCache();
        services.AddSingleton<IRobotsParser, RobotsParser>();
        services.AddSingleton<ISitemapParser, SitemapParser>();
        services.AddSingleton<IHtmlExtractor, HtmlExtractor>();
        services.AddScoped<IRobotsService, RobotsService>();
        services.AddScoped<ISitemapDiscoveryService, SitemapDiscoveryService>();
        services.AddScoped<ICrawlCommandService, CrawlCommandService>();
        services.AddScoped<ICrawlQueryService, CrawlQueryService>();
        services.AddScoped<ICrawlBatchRunner, CrawlBatchRunner>();
        services.AddScoped<ISeoJobHandler, InitialCrawlJobHandler>();
        services.AddScoped<ISeoJobHandler, ContinueCrawlJobHandler>();
        services.AddScoped<ISeoJobHandler, AnalyzeCrawlJobHandler>();
        services.AddSingleton<ISeoRule, TitleMissingRule>();
        services.AddSingleton<ISeoRule, TitleLengthRule>();
        services.AddSingleton<ISeoRule, MetaDescriptionMissingRule>();
        services.AddSingleton<ISeoRule, H1MissingRule>();
        services.AddSingleton<ISeoRule, MultipleH1Rule>();
        services.AddSingleton<ISeoRule, CanonicalMissingRule>();
        services.AddSingleton<ISeoRule, LowWordCountRule>();
        services.AddSingleton<ISeoRule, MissingAltRule>();
        services.AddSingleton<ISeoRule, SlowResponseRule>();
        services.AddSingleton<ISeoRule, MetaDescriptionLengthRule>();
        services.AddSingleton<ISeoRule, CanonicalInvalidRule>();
        services.AddSingleton<ISeoRule, CanonicalMismatchRule>();
        services.AddSingleton<ISeoRule, NoIndexRule>();
        services.AddSingleton<ISeoRule, HttpsIssueRule>();
        services.AddSingleton<ISeoRule, HeadingStructureRule>();
        services.AddSingleton<ISeoRule, BrokenStatusRule>();
        services.AddSingleton<ISeoRule, RedirectedStatusRule>();
        services.AddSingleton<ISeoRule, XRobotsNoIndexRule>();
        services.AddSingleton<ISeoRule, EmptyContentTypeRule>();
        services.AddHttpClient<IPageFetcher, SafePageFetcher>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate });
        return services;
    }
}
