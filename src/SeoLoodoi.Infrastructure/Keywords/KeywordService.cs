using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Application.Keywords;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Keywords;

public sealed class KeywordService(AppDbContext db, IProjectAccessService access, IQuotaService quota, IAuditLogService audit) : IKeywordService
{
    public async Task<IReadOnlyList<KeywordDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return [];
        var keywords = await db.Keywords.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Phrase).ToListAsync(ct);
        var metrics = await db.KeywordMetrics.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        return keywords.Select(x => ToDto(x, metrics.Where(m => m.KeywordId == x.Id))).ToArray();
    }

    public async Task<KeywordDto?> CreateAsync(Guid projectId, Guid userId, CreateKeywordRequest request, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct)) return null;
        await quota.EnsureCanAddKeywordAsync(projectId, userId, ct);
        var keyword = new Keyword(projectId, request.Phrase, request.Language, request.Country); keyword.SetTracking(request.IsTracked);
        // Pre-check mirrors the unique index (ProjectId, NormalizedPhrase, Country) so the
        // common duplicate case is a clean 409 on every provider (InMemory does not enforce
        // unique indexes) and never a 500. The unique index remains the race-condition
        // backstop below, so concurrent inserts that slip past this check are still mapped
        // to 409 instead of surfacing the 23505.
        var duplicate = await db.Keywords.AnyAsync(x => x.ProjectId == projectId && x.NormalizedPhrase == keyword.NormalizedPhrase && x.Country == keyword.Country, ct);
        if (duplicate) throw new DuplicateEntityException("این کلیدواژه قبلاً برای همین پروژه و کشور ثبت شده است.");
        db.Keywords.Add(keyword);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbExceptionClassifier.IsUniqueViolation(ex))
        {
            throw new DuplicateEntityException("این کلیدواژه قبلاً برای همین پروژه و کشور ثبت شده است.");
        }
        await audit.RecordAsync(projectId, userId, "KEYWORD_CREATED", "Keyword", keyword.Id.ToString(), "{}", null, ct);
        return ToDto(keyword, []);
    }

    public async Task<bool> DeleteAsync(Guid projectId, Guid keywordId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct)) return false;
        var keyword = await db.Keywords.SingleOrDefaultAsync(x => x.Id == keywordId && x.ProjectId == projectId, ct);
        if (keyword is null) return false;
        db.KeywordMetrics.RemoveRange(await db.KeywordMetrics.Where(x => x.KeywordId == keywordId).ToListAsync(ct));
        db.Keywords.Remove(keyword); await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, userId, "KEYWORD_DELETED", "Keyword", keywordId.ToString(), "{}", null, ct);
        return true;
    }

    public async Task<bool> AddMetricAsync(Guid projectId, Guid keywordId, Guid userId, ImportKeywordMetricRequest request, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct)) return false;
        var keyword = await db.Keywords.SingleOrDefaultAsync(x => x.Id == keywordId && x.ProjectId == projectId, ct);
        if (keyword is null) return false;
        var duplicate = await db.KeywordMetrics.AnyAsync(x => x.KeywordId == keywordId && x.Date == request.Date && x.Country == request.Country.ToUpperInvariant() && x.Device == request.Device.ToUpperInvariant() && x.PageUrl == request.PageUrl, ct);
        if (duplicate) return false;
        db.KeywordMetrics.Add(new KeywordMetric(projectId, keywordId, request.Date, request.Clicks, request.Impressions, request.Ctr, request.AveragePosition, request.Source, request.PageUrl, request.Country, request.Device));
        keyword.TouchMetrics(new DateTimeOffset(request.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, userId, "KEYWORD_METRIC_IMPORTED", "KeywordMetric", keywordId.ToString(), System.Text.Json.JsonSerializer.Serialize(new { request.Date, request.Source }), null, ct);
        return true;
    }

    public async Task<IReadOnlyList<KeywordOpportunityDto>> OpportunitiesAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return [];
        var keywords = await db.Keywords.AsNoTracking().Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Id, ct);
        var metrics = await db.KeywordMetrics.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        return metrics.GroupBy(x => x.KeywordId).Where(g => keywords.ContainsKey(g.Key)).Select(g =>
        {
            var impressions = g.Sum(x => x.Impressions); var clicks = g.Sum(x => x.Clicks); var ctr = impressions == 0 ? 0 : (decimal)clicks / impressions;
            var average = g.Sum(x => x.AveragePosition * x.Impressions) / Math.Max(1, impressions);
            var best = g.OrderByDescending(x => x.Clicks).FirstOrDefault();
            var inOpportunityRange = average is >= 4m and <= 15m && impressions >= 10;
            var score = inOpportunityRange ? (int)Math.Clamp(100m - (average - 4m) * 5m + Math.Min(20m, impressions / 100m), 0m, 100m) : 0;
            return new KeywordOpportunityDto(g.Key, keywords[g.Key].Phrase, impressions, clicks, decimal.Round(ctr, 4), decimal.Round(average, 2), best?.PageUrl, score, inOpportunityRange ? "نمایش بالا و رتبه بین ۴ تا ۱۵؛ بهبود CTR فرصت رشد است." : "با این داده‌ها فرصت قطعی در بازه ۴ تا ۱۵ شناسایی نشد.");
        }).Where(x => x.OpportunityScore > 0).OrderByDescending(x => x.OpportunityScore).ToArray();
    }

    private static KeywordDto ToDto(Keyword keyword, IEnumerable<KeywordMetric> source)
    {
        var metrics = source.ToArray(); var impressions = metrics.Sum(x => x.Impressions); var clicks = metrics.Sum(x => x.Clicks);
        var ctr = impressions == 0 ? 0 : (decimal)clicks / impressions;
        var position = impressions == 0 ? 0 : metrics.Sum(x => x.AveragePosition * x.Impressions) / impressions;
        var best = metrics.OrderByDescending(x => x.Clicks).FirstOrDefault();
        return new KeywordDto(keyword.Id, keyword.Phrase, keyword.Language, keyword.Country, keyword.IsTracked, keyword.LastMetricAt, metrics.Length == 0 ? null : clicks, metrics.Length == 0 ? null : impressions, metrics.Length == 0 ? null : decimal.Round(ctr, 4), metrics.Length == 0 ? null : decimal.Round(position, 2), best?.PageUrl, metrics.OrderByDescending(x => x.Date).FirstOrDefault()?.Source ?? "none");
    }
}
