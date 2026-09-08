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
    public DbSet<KeywordMetric> KeywordMetrics => Set<KeywordMetric>();
    public DbSet<Competitor> Competitors => Set<Competitor>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ExternalConnection> ExternalConnections => Set<ExternalConnection>();
    public DbSet<AiAnalysis> AiAnalyses => Set<AiAnalysis>();
    public DbSet<SeoReport> Reports => Set<SeoReport>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<AlertDelivery> AlertDeliveries => Set<AlertDelivery>();
    public DbSet<CrawlFrontierItem> CrawlFrontierItems => Set<CrawlFrontierItem>();
    public DbSet<SeoBackgroundJob> SeoBackgroundJobs => Set<SeoBackgroundJob>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

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
        b.Entity<Keyword>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.NormalizedPhrase, x.Country }).IsUnique();
            e.Property(x => x.Phrase).HasMaxLength(200);
            e.Property(x => x.NormalizedPhrase).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(10);
            e.Property(x => x.Country).HasMaxLength(10);
        });
        b.Entity<KeywordMetric>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.KeywordId, x.Date, x.Country, x.Device, x.PageUrl }).IsUnique();
            e.Property(x => x.PageUrl).HasMaxLength(2048);
            e.Property(x => x.Source).HasMaxLength(40);
            e.Property(x => x.Country).HasMaxLength(10);
            e.Property(x => x.Device).HasMaxLength(20);
            e.HasOne<Keyword>().WithMany().HasForeignKey(x => x.KeywordId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Competitor>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.NormalizedHost }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(160);
            e.Property(x => x.BaseUrl).HasMaxLength(2048);
        });
        b.Entity<ProjectMember>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<SeoProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ExternalConnection>().HasIndex(x => new { x.ProjectId, x.Provider }).IsUnique();
        b.Entity<AiAnalysis>().HasIndex(x => new { x.ProjectId, x.InputEvidenceHash, x.PromptVersion });
        b.Entity<SeoReport>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            e.Property(x => x.Type).HasMaxLength(40);
            e.Property(x => x.Format).HasMaxLength(10);
        });
        b.Entity<AlertRule>(e =>
        {
            e.HasIndex(x => x.ProjectId);
            e.Property(x => x.Channel).HasMaxLength(20);
            e.Property(x => x.Destination).HasMaxLength(2048);
        });
        b.Entity<AlertEvent>().HasIndex(x => new { x.ProjectId, x.DetectedAt });
        b.Entity<AlertDelivery>(e =>
        {
            e.HasIndex(x => new { x.Status, x.NextAttemptAt, x.LockedUntil });
            e.HasIndex(x => x.AlertEventId);
            e.Property(x => x.Channel).HasMaxLength(20);
            e.Property(x => x.Destination).HasMaxLength(2048);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasOne<AlertEvent>().WithMany().HasForeignKey(x => x.AlertEventId).OnDelete(DeleteBehavior.Cascade);
        });
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
        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            e.HasIndex(x => new { x.ActorId, x.CreatedAt });
            e.Property(x => x.Action).HasMaxLength(80);
            e.Property(x => x.EntityType).HasMaxLength(80);
            e.Property(x => x.EntityId).HasMaxLength(120);
            e.Property(x => x.IpAddress).HasMaxLength(64);
        });
    }
}
