using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Application.Keywords;
using SeoLoodoi.Domain.Seo;
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
        db.Keywords.Add(keyword); await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, userId, "KEYWORD_CREATED", "Keyword", keyword.Id.ToString(), "{}", null, ct);
        return ToDto(keyword, []);
    }

    public async Task<BatchCreateKeywordsResult> BatchCreateAsync(Guid projectId, Guid userId, BatchCreateKeywordsRequest request, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct))
            return new(0, 0, []);

        var phrases = request.Phrases
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();

        if (phrases.Length == 0)
            return new(0, 0, []);

        var existingNormalized = (await db.Keywords.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .Select(x => x.NormalizedPhrase)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedList = new List<Keyword>();
        var skippedCount = 0;

        foreach (var phrase in phrases)
        {
            var normalized = Keyword.Normalize(phrase);
            if (existingNormalized.Contains(normalized) || phrase.Length > 200)
            {
                skippedCount++;
                continue;
            }

            try
            {
                await quota.EnsureCanAddKeywordAsync(projectId, userId, ct);
            }
            catch
            {
                // Quota reached, stop adding further keywords
                break;
            }

            var keyword = new Keyword(projectId, phrase, request.Language, request.Country);
            keyword.SetTracking(true);
            db.Keywords.Add(keyword);
            existingNormalized.Add(normalized);
            addedList.Add(keyword);
        }

        if (addedList.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync(projectId, userId, "KEYWORD_BATCH_IMPORTED", "Keyword", projectId.ToString(),
                System.Text.Json.JsonSerializer.Serialize(new { count = addedList.Count, skipped = skippedCount }), null, ct);
        }

        var dtos = addedList.Select(k => ToDto(k, [])).ToArray();
        return new BatchCreateKeywordsResult(addedList.Count, skippedCount, dtos);
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

    public async Task<IReadOnlyList<KeywordCannibalizationDto>> CannibalizationAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return [];
        var keywords = await db.Keywords.AsNoTracking().Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Id, ct);
        var metrics = await db.KeywordMetrics.AsNoTracking()
            .Where(x => x.ProjectId == projectId && !string.IsNullOrWhiteSpace(x.PageUrl))
            .ToListAsync(ct);

        var list = new List<KeywordCannibalizationDto>();

        foreach (var group in metrics.GroupBy(x => x.KeywordId))
        {
            if (!keywords.TryGetValue(group.Key, out var keyword)) continue;

            var pageGroups = group
                .GroupBy(x => x.PageUrl!.Trim())
                .Select(pg =>
                {
                    var pageImpr = pg.Sum(x => x.Impressions);
                    var pageClicks = pg.Sum(x => x.Clicks);
                    var pageAvgPos = pageImpr == 0 ? pg.Average(x => x.AveragePosition) : pg.Sum(x => x.AveragePosition * x.Impressions) / pageImpr;
                    return new
                    {
                        PageUrl = pg.Key,
                        Impressions = pageImpr,
                        Clicks = pageClicks,
                        AveragePosition = decimal.Round(pageAvgPos, 1)
                    };
                })
                .Where(x => x.Impressions > 0 || x.Clicks > 0)
                .OrderByDescending(x => x.Impressions)
                .ToArray();

            if (pageGroups.Length < 2) continue;

            var totalImpr = pageGroups.Sum(x => x.Impressions);
            if (totalImpr < 10) continue;

            var primary = pageGroups[0];
            var secondary = pageGroups[1];
            var secondaryShare = totalImpr > 0 ? (decimal)secondary.Impressions / totalImpr * 100m : 0m;

            if (secondaryShare >= 15m)
            {
                var severity = secondaryShare >= 30m && secondary.AveragePosition <= 30m ? "High" : "Medium";
                var competingList = pageGroups.Select(p => new CompetingPageDto(
                    p.PageUrl,
                    p.Impressions,
                    p.Clicks,
                    p.AveragePosition,
                    totalImpr > 0 ? decimal.Round((decimal)p.Impressions / totalImpr * 100m, 1) : 0m
                )).ToArray();

                var explanation = $"{competingList.Length} صفحه مختلف برای عبارت '{keyword.Phrase}' رقابت می‌کنند. صفحه نخست '{primary.PageUrl}' سهم {competingList[0].ImpressionSharePercentage}% و صفحه رقیب '{secondary.PageUrl}' سهم {secondaryShare:F1}% دارد. پیشنهاد: تعیین تگ Canonical، تجمیع صفحات یا اصلاح محتوا و کلمات کلیدی هدف.";

                list.Add(new KeywordCannibalizationDto(keyword.Id, keyword.Phrase, competingList, severity, explanation));
            }
        }

        return list.OrderByDescending(x => x.Severity == "High").ThenByDescending(x => x.CompetingPages.Count).ToArray();
    }

    public async Task<KeywordRankingSummaryDto> SummaryAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct))
            return new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        var list = await ListAsync(projectId, userId, ct);
        var cannibalizations = await CannibalizationAsync(projectId, userId, ct);

        var totalTracked = list.Count(x => x.IsTracked);
        var withPos = list.Where(x => x.CurrentPosition.HasValue).ToArray();
        var top3 = withPos.Count(x => x.CurrentPosition!.Value <= 3.0m);
        var top10 = withPos.Count(x => x.CurrentPosition!.Value <= 10.0m);
        var top20 = withPos.Count(x => x.CurrentPosition!.Value <= 20.0m);
        var top100 = withPos.Count(x => x.CurrentPosition!.Value <= 100.0m);
        var improved = list.Count(x => x.Trend == "up");
        var declined = list.Count(x => x.Trend == "down");
        var totalImpr = list.Sum(x => x.Impressions ?? 0);
        var totalClicks = list.Sum(x => x.Clicks ?? 0);

        return new KeywordRankingSummaryDto(
            totalTracked,
            top3,
            top10,
            top20,
            top100,
            improved,
            declined,
            cannibalizations.Count,
            totalImpr,
            totalClicks);
    }

    private static KeywordDto ToDto(Keyword keyword, IEnumerable<KeywordMetric> source)
    {
        var metrics = source.ToArray();
        var impressions = metrics.Sum(x => x.Impressions);
        var clicks = metrics.Sum(x => x.Clicks);
        var ctr = impressions == 0 ? 0 : (decimal)clicks / impressions;
        var position = impressions == 0 ? 0 : metrics.Sum(x => x.AveragePosition * x.Impressions) / impressions;
        var best = metrics.OrderByDescending(x => x.Clicks).FirstOrDefault();

        var dateGroups = metrics
            .GroupBy(x => x.Date)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var gImpr = g.Sum(x => x.Impressions);
                var gClicks = g.Sum(x => x.Clicks);
                var gPos = gImpr == 0 ? g.Average(x => x.AveragePosition) : g.Sum(x => x.AveragePosition * x.Impressions) / gImpr;
                return new KeywordHistoryPointDto(g.Key, decimal.Round(gPos, 2), gImpr, gClicks);
            })
            .ToArray();

        decimal? currentPos = null;
        decimal? prevPos = null;
        decimal? delta = null;
        var trend = "stable";

        if (dateGroups.Length > 0)
        {
            currentPos = dateGroups[^1].Position;
            if (dateGroups.Length > 1)
            {
                prevPos = dateGroups[^2].Position;
                // Higher rank in SEO means lower numerical position (1 is best).
                // E.g. went from 7 to 4 => delta = +3.
                delta = decimal.Round(prevPos.Value - currentPos.Value, 1);
                if (delta > 0.5m) trend = "up";
                else if (delta < -0.5m) trend = "down";
                else trend = "stable";
            }
            else
            {
                trend = "new";
            }
        }

        var recentHistory = dateGroups.TakeLast(14).ToArray();

        return new KeywordDto(
            keyword.Id,
            keyword.Phrase,
            keyword.Language,
            keyword.Country,
            keyword.IsTracked,
            keyword.LastMetricAt,
            metrics.Length == 0 ? null : clicks,
            metrics.Length == 0 ? null : impressions,
            metrics.Length == 0 ? null : decimal.Round(ctr, 4),
            metrics.Length == 0 ? null : decimal.Round(position, 2),
            best?.PageUrl,
            metrics.OrderByDescending(x => x.Date).FirstOrDefault()?.Source ?? "none",
            currentPos,
            prevPos,
            delta,
            trend,
            recentHistory);
    }
}
