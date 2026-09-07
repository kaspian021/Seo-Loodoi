using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Infrastructure.Analysis;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Jobs;
using SeoLoodoi.Infrastructure.Persistence;
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
            o.SignIn.RequireConfirmedEmail = false;
        })
            .AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>();
        services.AddScoped<ISeoProjectRepository, SeoProjectRepository>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<ICrawlFrontierStore, CrawlFrontierStore>();
        services.AddScoped<ISeoJobQueue, SeoJobQueue>();
        services.AddSingleton(TimeProvider.System);
        services.AddHostedService<DurableJobWorker>();
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
        services.AddHttpClient<IPageFetcher, SafePageFetcher>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate });
        return services;
    }
}
