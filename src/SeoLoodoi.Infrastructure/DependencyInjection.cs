using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeoLoodoi.Application.Aeo;
using SeoLoodoi.Application.AI;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Backlinks;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Application.Competitors;
using SeoLoodoi.Application.Content;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Keywords;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Monitoring;
using SeoLoodoi.Application.Reports;
using SeoLoodoi.Application.SearchConsole;
using SeoLoodoi.Application.Serp;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Infrastructure.Aeo;
using SeoLoodoi.Infrastructure.AI;
using SeoLoodoi.Infrastructure.Analysis;
using SeoLoodoi.Infrastructure.Backlinks;
using SeoLoodoi.Infrastructure.Billing;
using SeoLoodoi.Infrastructure.Competitors;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Keywords;
using SeoLoodoi.Infrastructure.Identity;
using SeoLoodoi.Infrastructure.Jobs;
using SeoLoodoi.Infrastructure.Monitoring;
using SeoLoodoi.Infrastructure.Reports;
using SeoLoodoi.Infrastructure.SearchConsole;
using SeoLoodoi.Infrastructure.Serp;
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
        services.AddOptions<LoodoiBillingOptions>().BindConfiguration("Billing");
        services.AddOptions<AiOptions>().BindConfiguration("AI");
        services.AddOptions<SearchConsoleOptions>().BindConfiguration("SearchConsole");
        services.AddOptions<RetentionOptions>().BindConfiguration("Retention");
        services.AddDataProtection();
        services.AddScoped<ISeoProjectRepository, SeoProjectRepository>();
        services.AddScoped<IProjectAccessService, ProjectAccessService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IEntitlementService, EntitlementService>();
        services.AddScoped<IQuotaService, QuotaService>();
        services.AddScoped<IRecommendationQueryService, RecommendationQueryService>();
        services.AddScoped<IKeywordService, KeywordService>();
        services.AddScoped<ICompetitorService, CompetitorService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IAlertService, AlertService>();
        services.AddHttpClient("AlertDelivery", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => SsrfPinnedHandler.Create());
        services.AddHttpClient<ISearchConsoleService, SearchConsoleService>(client => client.Timeout = TimeSpan.FromSeconds(60));
        services.AddScoped<IAiAnalysisService, AiAnalysisService>();
        services.AddHttpClient<IAiSeoExpert, AiSeoExpert>((provider, client) =>
        {
            var configured = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(configured.TimeoutSeconds, 5, 180));
        });
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<IAnalysisStatusService, AnalysisStatusService>();
        services.AddScoped<ICompetitorCrawlRunner, CompetitorCrawlRunner>();
        services.AddScoped<ICrawlFrontierStore, CrawlFrontierStore>();
        services.AddScoped<ISeoJobQueue, SeoJobQueue>();
        services.AddSingleton(TimeProvider.System);
        services.AddHostedService<DurableJobWorker>();
        services.AddHostedService<StalledCrawlRecoveryService>();
        services.AddHostedService<ScheduledCrawlWorker>();
        services.AddHostedService<MonitoringWorker>();
        services.AddHostedService<AlertDeliveryWorker>();
        services.AddHostedService<CleanupWorker>();
        services.AddSingleton<IOutboundUrlGuard, OutboundUrlGuard>();
        services.AddSingleton<IHostRequestCoordinator, HostRequestCoordinator>();
        services.AddMemoryCache();
        services.AddSingleton<IRobotsParser, RobotsParser>();
        services.AddSingleton<ISitemapParser, SitemapParser>();
        services.AddSingleton<IHtmlExtractor, HtmlExtractor>();
        services.AddSingleton<IContentQualityEngine, ContentQualityEngine>();
        services.AddScoped<IRobotsService, RobotsService>();
        services.AddScoped<ISitemapDiscoveryService, SitemapDiscoveryService>();
        services.AddScoped<ICrawlCommandService, CrawlCommandService>();
        services.AddScoped<ICrawlQueryService, CrawlQueryService>();
        services.AddScoped<ICrawlBatchRunner, CrawlBatchRunner>();
        services.AddScoped<ISeoJobHandler, InitialCrawlJobHandler>();
        services.AddScoped<ISeoJobHandler, ContinueCrawlJobHandler>();
        services.AddScoped<ISeoJobHandler, AnalyzeCrawlJobHandler>();
        services.AddScoped<ISeoJobHandler, CompetitorCrawlJobHandler>();
        services.AddScoped<ISeoJobHandler, CleanupJobHandler>();
        services.AddScoped<ISeoJobHandler, BacklinkRefreshJobHandler>();
        services.AddScoped<ISeoJobHandler, SerpRefreshJobHandler>();
        // Backlink data comes from an external vendor only. The default provider is
        // deliberately a no-op that reports NotConfigured: the platform must never
        // synthesize or estimate backlinks when no provider is wired up.
        services.AddOptions<BacklinkProviderOptions>().BindConfiguration("Backlinks");
        services.AddSingleton<IBacklinkProvider, NullBacklinkProvider>();
        services.AddScoped<IBacklinkService, BacklinkService>();
        // SERP capture is provider-driven for the same reason backlinks are: rank
        // history built on anything other than observed results is fiction.
        services.AddOptions<SerpProviderOptions>().BindConfiguration("Serp");
        services.AddSingleton<ISerpProvider, NullSerpProvider>();
        services.AddScoped<ISerpService, SerpService>();
        // AEO/GEO is deterministic: it reads stored crawl evidence and the live
        // robots.txt, so it needs no external provider to be useful.
        services.AddSingleton<IAeoAnalyzer, AeoAnalyzer>();
        services.AddScoped<IAeoService, AeoService>();
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
        services.AddSingleton<ISeoRule, RedirectChainLongRule>();
        services.AddSingleton<ISeoRule, MixedContentAssetsRule>();
        services.AddSingleton<ISeoRule, ExcessiveResourcesRule>();
        services.AddSingleton<ISeoRule, SchemaSyntaxRule>();
        services.AddSingleton<ISeoRule, SchemaMissingRequiredRule>();
        services.AddSingleton<ISeoRule, StructuredDataNoticeRule>();
        services.AddSingleton<ISeoRule, HreflangNoSelfRule>();
        services.AddSingleton<ISeoRule, HreflangInvalidLanguageRule>();
        services.AddSingleton<ISeoRule, HstsMissingRule>();
        services.AddSingleton<ISeoRule, SecurityHeadersMissingRule>();
        services.AddSingleton<ISeoRule, CacheControlMissingRule>();
        services.AddSingleton<ISeoRule, RenderBlockingResourcesRule>();
        services.AddSingleton<ISeoRule, ImageDimensionsMissingRule>();
        services.AddSingleton<ISeoRule, DeepClickDepthRule>();
        services.AddSingleton<ISeoRule, KeywordStuffingRule>();
        services.AddSingleton<ISeoRule, LongSentencesRule>();
        services.AddSingleton<ISeoRule, ThinContentRule>();
        services.AddHttpClient<IPageFetcher, SafePageFetcher>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => SsrfPinnedHandler.Create(handler => handler.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate));
        return services;
    }
}
