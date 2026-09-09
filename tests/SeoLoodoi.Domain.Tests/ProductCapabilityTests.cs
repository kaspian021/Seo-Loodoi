using AwesomeAssertions;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

public class ProductCapabilityTests
{
    [Fact]
    public void Keyword_normalizes_persian_variants_and_metrics_validate()
    {
        var keyword = new Keyword(Guid.NewGuid(), " كتاب‌هايِ خوب ", "fa", "ir");
        keyword.NormalizedPhrase.Should().Be("کتاب‌هایِ خوب");
        var metric = new KeywordMetric(keyword.ProjectId, keyword.Id, new DateOnly(2026, 9, 7), 12, 100, .12m, 8.4m, "search-console", "https://example.com/p", "IR", "MOBILE");
        metric.Ctr.Should().Be(.12m);
        var invalid = () => new KeywordMetric(keyword.ProjectId, keyword.Id, DateOnly.FromDateTime(DateTime.UtcNow), 1, 1, 1.2m, 1, "manual");
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Schedule_is_explicit_and_disabled_by_default()
    {
        var project = new SeoProject(Guid.NewGuid(), "Example", new Uri("https://example.com"));
        project.NextCrawlAt.Should().BeNull();
        project.UpdateSettings(project.Settings with { Schedule = "daily", ScheduleHourUtc = 2 });
        project.NextCrawlAt.Should().NotBeNull();
    }

    [Fact]
    public void Audit_log_is_normalized_and_bounded()
    {
        var actor = Guid.NewGuid();
        var log = new AuditLog(Guid.NewGuid(), actor, "project_created", "SeoProject", "  entity  ", "{\"ok\":true}", "127.0.0.1");
        log.Action.Should().Be("PROJECT_CREATED");
        log.EntityId.Should().Be("entity");
        log.MetadataJson.Should().Be("{\"ok\":true}");
        log.ActorId.Should().Be(actor);
    }

    [Fact]
    public void Alert_delivery_retries_then_enters_dead_letter_state()
    {
        var delivery = new AlertDelivery(Guid.NewGuid(), Guid.NewGuid(), "webhook", "https://example.com/hook", "{\"event\":\"SCORE_DROP\"}");
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        delivery.IsDue(now).Should().BeTrue();
        delivery.MarkProcessing(Guid.NewGuid(), now.AddMinutes(10));
        delivery.MarkFailed("temporary failure", now, maxAttempts: 2);
        delivery.Status.Should().Be("Failed");
        delivery.Attempts.Should().Be(1);
        delivery.MarkProcessing(Guid.NewGuid(), now.AddMinutes(10));
        delivery.MarkFailed("permanent failure", now, maxAttempts: 2);
        delivery.Status.Should().Be("DeadLetter");
        delivery.Attempts.Should().Be(2);
    }

    [Fact]
    public void Technical_rules_use_stored_http_evidence()
    {
        var context = new PageAnalysisContext("http://example.com", "Title", "Description", ["H1"], "http://example.com", 500, 0, 0, 50, true, [1], 503, "text/html", "noindex");
        new BrokenStatusRule().Evaluate(context).Triggered.Should().BeTrue();
        new XRobotsNoIndexRule().Evaluate(context).Severity.Should().Be(IssueSeverity.High);
    }

    [Fact]
    public void Broken_status_5xx_is_critical_and_4xx_stays_medium()
    {
        var ctx = new PageAnalysisContext("http://example.com", "Title", "Description", ["H1"], "http://example.com", 500, 0, 0, 50, true, [1], 200, "text/html", null);
        new BrokenStatusRule().Evaluate(ctx with { StatusCode = 503 }).Severity.Should().Be(IssueSeverity.Critical, "5xx = broken server (F-02)");
        new BrokenStatusRule().Evaluate(ctx with { StatusCode = 500 }).Triggered.Should().BeTrue();
        new BrokenStatusRule().Evaluate(ctx with { StatusCode = 404 }).Severity.Should().Be(IssueSeverity.Medium);
        new BrokenStatusRule().Evaluate(ctx with { StatusCode = 200 }).Triggered.Should().BeFalse();
    }
}
