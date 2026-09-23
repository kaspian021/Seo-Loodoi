using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Crawling.Rendering;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Crawling.Rendering;

/// <summary>Result of the render stage for one fetched HTML page.</summary>
public sealed record RenderStageResult(ExtractedPage Page, ExtractedPage RawPage, PageRenderEvidence? Evidence, RenderDiff? Diff, Uri? ClientRedirectTarget)
{
    public bool Rendered => Evidence?.Status == RenderEvidenceStatus.Rendered;
}

public interface ICrawlRenderStage
{
    /// <summary>
    /// Decides, per the project's render mode, whether to render this page. It reserves render
    /// quota atomically, renders, extracts and compares. It never throws for render problems;
    /// on any failure the raw page is returned, with evidence recording why.
    /// </summary>
    Task<RenderStageResult> ProcessAsync(Crawl crawl, SeoProject project, string normalizedUrl, Uri finalUri, string rawHtml, ExtractedPage rawPage, CancellationToken ct);
}

/// <summary>
/// HTML-first render orchestration (D1, D2, D3, D5, D9). It runs inside the serial
/// persistence phase of <see cref="CrawlBatchRunner"/>, so it may use the DbContext.
/// Admission (global and per-project caps, backpressure, circuit) is enforced by the
/// renderer's gate.
/// <para>Quota reservation is idempotent per (crawl, normalized URL). A resumed batch that
/// re-processes a page reuses its evidence row and is never charged twice.</para>
/// </summary>
public sealed class CrawlRenderStage(AppDbContext db, IPageRenderer renderer, IHtmlExtractor extractor, IQuotaService quota, IOptions<RenderingOptions> options, ILogger<CrawlRenderStage> logger) : ICrawlRenderStage
{
    public async Task<RenderStageResult> ProcessAsync(Crawl crawl, SeoProject project, string normalizedUrl, Uri finalUri, string rawHtml, ExtractedPage rawPage, CancellationToken ct)
    {
        var settings = project.Settings;
        var mode = settings.EffectiveRenderMode;
        if (mode == "html") return new(rawPage, rawPage, null, null, null);

        var signals = RenderTriggers.Detect(new RenderTriggers.Input(rawHtml, rawPage.Title, rawPage.Headings.Count(x => x.Level == 1), rawPage.WordCount,
            (rawPage.Assets ?? []).Count(x => x.Type == AssetType.Script), rawPage.Links.Count));
        if (mode == "auto" && !RenderTriggers.ShouldRender(signals)) return new(rawPage, rawPage, null, null, null);
        var signalsJson = JsonSerializer.Serialize(signals);

        var evidence = await db.PageRenderEvidences.SingleOrDefaultAsync(x => x.CrawlId == crawl.Id && x.NormalizedUrl == normalizedUrl, ct);
        if (!renderer.IsAvailable)
        {
            evidence ??= Track(new PageRenderEvidence(crawl.Id, project.Id, normalizedUrl, mode, settings.EffectiveViewport, signalsJson));
            evidence.MarkNotRendered(RenderEvidenceStatus.Disabled, "Rendering is not enabled in this deployment (Rendering:Enabled=false); raw HTML used.", 0, DateTimeOffset.UtcNow);
            return new(rawPage, rawPage, evidence, null, null);
        }

        if (evidence is null || !evidence.IsCharged)
        {
            var reservation = await ReserveAsync(crawl, project, normalizedUrl, mode, signalsJson, evidence, ct);
            if (reservation.Denied is { } denied) return new(rawPage, rawPage, denied, null, null);
            evidence = reservation.Evidence!;
        }

        RenderResult result;
        try
        {
            result = await renderer.RenderAsync(new RenderRequest(finalUri, settings.UserAgent, ViewportProfile.For(settings.EffectiveViewport), settings.TimeoutSeconds, options.Value.MaxDomBytes, crawl.Id, project.Id), ct);
        }
        catch (RenderCapacityException ex)
        {
            // Backpressure / open circuit: the reservation is released (not charged) and raw HTML is used.
            evidence.MarkNotRendered(RenderEvidenceStatus.CapacityRejected, ex.Message, 0, DateTimeOffset.UtcNow);
            logger.LogWarning("Render rejected by capacity control: {Reason}", ex.Message);
            return new(rawPage, rawPage, evidence, null, null);
        }

        if (!result.Success || result.Html is null)
        {
            evidence.MarkNotRendered(RenderEvidenceStatus.Failed, $"{result.Failure}: {result.FailureMessage}", (long)result.Duration.TotalMilliseconds, DateTimeOffset.UtcNow);
            logger.LogWarning("Render failed ({Failure}) for {Url}; raw HTML used", result.Failure, finalUri);
            return new(rawPage, rawPage, evidence, null, null);
        }

        var renderedUri = result.FinalUrl ?? finalUri;
        var renderedPage = await extractor.ExtractAsync(result.Html, renderedUri, ct);
        var diff = RawVsRenderedComparer.Compare(rawPage, renderedPage);
        Uri? clientRedirect = null;
        var fields = diff.Fields.ToList();
        if (!string.Equals(renderedUri.AbsoluteUri, finalUri.AbsoluteUri, StringComparison.Ordinal))
        {
            clientRedirect = renderedUri;
            fields.Add(new FieldDiff("finalUrl", true, DiffSeverity.Critical, finalUri.AbsoluteUri, renderedUri.AbsoluteUri));
            diff = diff with { Fields = fields };
        }
        var blocked = result.Resources.Count(x => x.Blocked);
        evidence.MarkRendered(renderedUri.AbsoluteUri, rawPage.WordCount, renderedPage.WordCount, diff.CriticalCount, diff.ToJson(),
            JsonSerializer.Serialize(result.Resources.Take(300)), result.Resources.Count, blocked, result.JsErrorCount, (long)result.Duration.TotalMilliseconds, DateTimeOffset.UtcNow);
        if (diff.CriticalCount > 0) CrawlerMetrics.RenderMismatches.Add(1);
        return new(renderedPage, rawPage, evidence, diff, clientRedirect);
    }

    private PageRenderEvidence Track(PageRenderEvidence evidence) { db.PageRenderEvidences.Add(evidence); return evidence; }

    private sealed record Reservation(PageRenderEvidence? Evidence, PageRenderEvidence? Denied);

    /// <summary>
    /// Atomic reservation under the tenant quota lock (same PostgreSQL advisory lock as every
    /// other quota dimension). Two limits are checked:
    /// <list type="bullet">
    /// <item>the per-crawl project cap (<c>MaxRendersPerCrawl</c>);</item>
    /// <item>the plan's monthly render allowance.</item>
    /// </list>
    /// The Reserved row is committed before the render starts, so concurrent workers see it.
    /// </summary>
    private async Task<Reservation> ReserveAsync(Crawl crawl, SeoProject project, string normalizedUrl, string mode, string signalsJson, PageRenderEvidence? existing, CancellationToken ct)
    {
        var perCrawlCap = project.Settings.EffectiveMaxRendersPerCrawl;
        return await quota.WithTenantLockAsync(project.OwnerId, async (lease, token) =>
        {
            var chargedThisCrawl = await db.PageRenderEvidences.CountAsync(x => x.CrawlId == crawl.Id &&
                (x.Status == RenderEvidenceStatus.Reserved || x.Status == RenderEvidenceStatus.Rendered || x.Status == RenderEvidenceStatus.Failed), token);
            var evidence = existing ?? Track(new PageRenderEvidence(crawl.Id, project.Id, normalizedUrl, mode, project.Settings.EffectiveViewport, signalsJson));
            string? denial = null;
            if (chargedThisCrawl >= perCrawlCap) denial = $"Project render cap reached ({perCrawlCap} renders per crawl); raw HTML used.";
            else
            {
                var check = await lease.CheckAsync(QuotaDimension.Renders, token);
                if (check.Remaining < 1) denial = $"Monthly render quota for {check.Plan} plan reached ({check.Limit}); raw HTML used.";
            }
            if (denial is not null)
            {
                evidence.MarkNotRendered(RenderEvidenceStatus.QuotaExceeded, denial, 0, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(token);
                logger.LogInformation("Render skipped: {Reason}", denial);
                return new Reservation(null, evidence);
            }
            if (existing is not null) existing.Reserve(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
            return new Reservation(evidence, null);
        }, ct);
    }
}
