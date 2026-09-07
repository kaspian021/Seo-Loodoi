using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<SeoProject> SeoProjects => Set<SeoProject>();
    public DbSet<Crawl> Crawls => Set<Crawl>();
    public DbSet<CrawledUrl> CrawledUrls => Set<CrawledUrl>();
    public DbSet<PageSnapshot> PageSnapshots => Set<PageSnapshot>();
    public DbSet<PageLink> PageLinks => Set<PageLink>();
    public DbSet<SeoIssue> SeoIssues => Set<SeoIssue>();
    public DbSet<SeoScoreSnapshot> SeoScores => Set<SeoScoreSnapshot>();
    public DbSet<Recommendation> Recommendations => Set<Recommendation>();
    public DbSet<Keyword> Keywords => Set<Keyword>();
    public DbSet<Competitor> Competitors => Set<Competitor>();
    public DbSet<ExternalConnection> ExternalConnections => Set<ExternalConnection>();
    public DbSet<AiAnalysis> AiAnalyses => Set<AiAnalysis>();
    public DbSet<SeoReport> Reports => Set<SeoReport>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<CrawlFrontierItem> CrawlFrontierItems => Set<CrawlFrontierItem>();
    public DbSet<SeoBackgroundJob> SeoBackgroundJobs => Set<SeoBackgroundJob>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasDefaultSchema("loodoi");
        b.Entity<ApplicationUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.CompanyName).HasMaxLength(160);
            e.Property(x => x.PreferredLanguage).HasMaxLength(10);
        });
        b.Entity<SeoProject>(e => { e.HasIndex(x => new { x.OwnerId, x.NormalizedHost }).IsUnique(); e.Property(x => x.Name).HasMaxLength(160); e.Property(x => x.BaseUrl).HasMaxLength(2048); e.OwnsOne(x => x.Settings, owned => owned.ToJson()); });
        b.Entity<Crawl>(e => e.HasIndex(x => new { x.ProjectId, x.Status }));
        b.Entity<CrawledUrl>(e => { e.HasIndex(x => new { x.CrawlId, x.Url }).IsUnique(); e.Property(x => x.Url).HasMaxLength(2048); });
        b.Entity<PageSnapshot>().HasIndex(x => x.CrawlId);
        b.Entity<PageLink>(e => { e.HasIndex(x => x.CrawlId); e.Property(x => x.TargetUrl).HasMaxLength(2048); });
        b.Entity<SeoIssue>(e => { e.HasIndex(x => new { x.ProjectId, x.CrawlId, x.RuleCode }); e.HasIndex(x => new { x.ProjectId, x.Severity, x.Status }); });
        b.Entity<SeoScoreSnapshot>().HasIndex(x => new { x.ProjectId, x.CreatedAt });
        b.Entity<Recommendation>().HasIndex(x => new { x.ProjectId, x.Status });
        b.Entity<Keyword>(e => e.HasIndex(x => new { x.ProjectId, x.NormalizedPhrase, x.Country }).IsUnique());
        b.Entity<Competitor>().HasIndex(x => new { x.ProjectId, x.NormalizedHost }).IsUnique();
        b.Entity<ExternalConnection>().HasIndex(x => new { x.ProjectId, x.Provider }).IsUnique();
        b.Entity<AiAnalysis>().HasIndex(x => new { x.ProjectId, x.InputEvidenceHash, x.PromptVersion });
        b.Entity<SeoReport>().HasIndex(x => new { x.ProjectId, x.CreatedAt });
        b.Entity<AlertRule>().HasIndex(x => x.ProjectId);
        b.Entity<AlertEvent>().HasIndex(x => new { x.ProjectId, x.DetectedAt });
        b.Entity<CrawlFrontierItem>(e =>
        {
            e.HasIndex(x => new { x.CrawlId, x.NormalizedUrl }).IsUnique();
            e.HasIndex(x => new { x.CrawlId, x.Status, x.NotBefore, x.Depth });
            e.Property(x => x.Url).HasMaxLength(2048); e.Property(x => x.NormalizedUrl).HasMaxLength(2048);
        });
        b.Entity<SeoBackgroundJob>(e =>
        {
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.NotBefore, x.CreatedAt });
            e.Property(x => x.IdempotencyKey).HasMaxLength(300);
        });
    }
}
