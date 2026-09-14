using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Reports;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Reports;

public sealed class ReportService(AppDbContext db, IProjectAccessService access, IAuditLogService audit) : IReportService
{
    public async Task<IReadOnlyList<ReportDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return [];
        return await db.Reports.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.CreatedAt)
            .Select(x => new ReportDto(x.Id, x.Type, x.Format, x.Status, x.CrawlId, x.CreatedAt)).ToListAsync(ct);
    }

    public async Task<ReportDto?> CreateAsync(Guid projectId, Guid userId, CreateReportRequest request, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct)) return null;
        var format = (request.Format ?? string.Empty).Trim().ToLowerInvariant();
        if (format is not ("json" or "csv" or "pdf")) throw new ArgumentException("Report format must be json, csv, or pdf.");
        var type = string.IsNullOrWhiteSpace(request.Type) ? "Executive" : request.Type.Trim();
        var crawl = request.CrawlId is null
            ? await db.Crawls.AsNoTracking().Where(x => x.ProjectId == projectId && x.Status == Domain.Seo.CrawlStatus.Completed).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct)
            : await db.Crawls.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.CrawlId && x.ProjectId == projectId && x.Status == Domain.Seo.CrawlStatus.Completed, ct);
        if (crawl is null) throw new InvalidOperationException("برای ساخت گزارش، ابتدا به یک خزش کامل‌شده نیاز است.");
        var project = await db.SeoProjects.AsNoTracking().SingleAsync(x => x.Id == projectId, ct);
        var score = await db.SeoScores.AsNoTracking().Where(x => x.ProjectId == projectId && x.CrawlId == crawl.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        var issues = await db.SeoIssues.AsNoTracking().Where(x => x.ProjectId == projectId && x.CrawlId == crawl.Id).OrderByDescending(x => x.Severity).ThenBy(x => x.RuleCode).ToListAsync(ct);
        var snapshot = new ReportSnapshot(project.Name, project.BaseUrl, crawl.Id, crawl.FinishedAt, crawl.PagesDiscovered, crawl.PagesCrawled, crawl.Errors, score?.OverallScore, score?.CalculationVersion, issues.Select(x => new ReportIssue(x.RuleCode, x.Severity.ToString(), x.Category.ToString(), x.Title, x.EvidenceJson)).ToArray());
        var content = format switch
        {
            "json" => JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            "csv" => ToCsv(snapshot),
            "pdf" => Convert.ToBase64String(ToPdf(snapshot)),
            _ => throw new ArgumentException("Unsupported report format.")
        };
        var report = new Domain.Seo.SeoReport(projectId, type, format, crawl.Id, content); db.Reports.Add(report); await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, userId, "REPORT_CREATED", "SeoReport", report.Id.ToString(), System.Text.Json.JsonSerializer.Serialize(new { report.Format, report.CrawlId }), null, ct);
        return new ReportDto(report.Id, report.Type, report.Format, report.Status, report.CrawlId, report.CreatedAt);
    }

    public async Task<ReportFile?> DownloadAsync(Guid projectId, Guid reportId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return null;
        var report = await db.Reports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportId && x.ProjectId == projectId, ct); if (report is null) return null;
        var extension = report.Format; var contentType = report.Format switch { "json" => "application/json; charset=utf-8", "csv" => "text/csv; charset=utf-8", "pdf" => "application/pdf", _ => "application/octet-stream" };
        var bytes = report.Format == "pdf" ? Convert.FromBase64String(report.Content) : Encoding.UTF8.GetBytes(report.Content);
        return new ReportFile(report.Id, $"seo-loodoi-{report.Id:N}.{extension}", contentType, bytes);
    }

    private static string ToCsv(ReportSnapshot snapshot)
    {
        var builder = new StringBuilder(); builder.AppendLine("project,baseUrl,crawlId,finishedAt,pagesDiscovered,pagesCrawled,errors,overallScore,ruleCode,severity,category,title,evidence");
        foreach (var issue in snapshot.Issues.DefaultIfEmpty()) builder.AppendLine(string.Join(',', new[] { Csv(snapshot.Project), Csv(snapshot.BaseUrl), Csv(snapshot.CrawlId.ToString()), Csv(snapshot.FinishedAt?.ToString("O")), snapshot.PagesDiscovered.ToString(), snapshot.PagesCrawled.ToString(), snapshot.Errors.ToString(), snapshot.OverallScore?.ToString() ?? "", Csv(issue?.RuleCode), Csv(issue?.Severity), Csv(issue?.Category), Csv(issue?.Title), Csv(issue?.EvidenceJson) }));
        return builder.ToString();
        static string Csv(string? value)
        {
            var safe = value ?? string.Empty;
            if (safe.Length > 0 && "=+-@".Contains(safe[0])) safe = "'" + safe;
            return $"\"{safe.Replace("\"", "\"\"")}\"";
        }
    }

    private static byte[] ToPdf(ReportSnapshot snapshot)
    {
        var lines = new[] { "گزارش سئو لودویی — SEO Loodoi report", snapshot.Project, snapshot.BaseUrl, $"Crawl: {snapshot.CrawlId}", $"Pages: {snapshot.PagesCrawled}/{snapshot.PagesDiscovered}", $"Errors: {snapshot.Errors}", $"Overall score: {snapshot.OverallScore?.ToString() ?? "not calculated"}" };

        var font = PersianFont.Value;
        // Glyph plan per line: null keeps the Helvetica path; a glyph-id list
        // renders through the embedded Vazirmatn (F2) right to left.
        var planned = lines.Select(line => line.Any(ArabicTextShaper.IsArabicShaped) ? LayoutRightToLeft(line, font) : null).ToArray();
        var usedGlyphs = planned.Where(p => p is not null).SelectMany(p => p!).Distinct().ToArray();
        var cidFont = new PdfCidFont(font, usedGlyphs);
        var compressedFont = PdfCidFont.CompressFontFile(font);

        // Object numbers: 1 catalog, 2 pages, 3 page, 4 F1 (Helvetica),
        // 5 contents, 6 F2 (Type0), 7 CIDFont, 8 descriptor, 9 ToUnicode,
        // 10 FontFile2.
        var content = new StringBuilder("BT /F1 12 Tf 50 790 Td ");
        string currentFont = "F1";
        for (var i = 0; i < lines.Length; i++)
        {
            var usePersian = planned[i] is not null;
            var fontName = usePersian ? "F2" : "F1";
            if (fontName != currentFont) { content.Append($"/{fontName} 12 Tf "); currentFont = fontName; }
            if (usePersian)
            {
                var hex = string.Concat(planned[i]!.Select(g => g.ToString("X4")));
                content.Append('<').Append(hex).Append("> Tj ");
            }
            else
            {
                content.Append('(').Append(EscapePdf(lines[i])).Append(") Tj ");
            }
            content.Append("0 -22 Td ");
        }
        content.Append("ET");
        var contentBytes = Encoding.ASCII.GetBytes(content.ToString());
        var toUnicodeBytes = Encoding.ASCII.GetBytes(cidFont.ToUnicode());

        using var output = new MemoryStream();
        output.Write("%PDF-1.4\n"u8);
        output.Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }); // binary marker comment
        output.Write("\n"u8);
        var offsets = new List<long>();

        void WriteTextObject(int number, string body)
        {
            offsets.Add(output.Position);
            var prefix = Encoding.ASCII.GetBytes($"{number} 0 obj\n{body}\nendobj\n");
            output.Write(prefix);
        }
        void WriteStreamObject(int number, string dictionary, byte[] payload)
        {
            offsets.Add(output.Position);
            var prefix = Encoding.ASCII.GetBytes($"{number} 0 obj\n{dictionary}\nstream\n");
            output.Write(prefix);
            output.Write(payload);
            output.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        }

        WriteTextObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteTextObject(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteTextObject(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R /F2 6 0 R >> >> /Contents 5 0 R >>");
        WriteTextObject(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        WriteStreamObject(5, $"<< /Length {contentBytes.Length} >>", contentBytes);
        WriteTextObject(6, cidFont.Type0("7 0 R", "9 0 R"));
        WriteTextObject(7, cidFont.CidFont("8 0 R"));
        WriteTextObject(8, cidFont.Descriptor("10 0 R"));
        WriteStreamObject(9, $"<< /Length {toUnicodeBytes.Length} >>", toUnicodeBytes);
        WriteStreamObject(10, $"<< /Length {compressedFont.Length} /Length1 {font.Raw.Length} /Filter /FlateDecode >>", compressedFont);

        var xref = output.Position;
        var cross = new StringBuilder($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) cross.Append($"{offset:0000000000} 00000 n \n");
        cross.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        output.Write(Encoding.ASCII.GetBytes(cross.ToString()));
        return output.ToArray();

        // RTL layout: split into Arabic and non-Arabic runs, shape the Arabic
        // runs into presentation forms, then emit runs in reverse order with
        // each Arabic run reversed for display.
        static int[] LayoutRightToLeft(string line, TtfFont font)
        {
            var runs = new List<(bool Arabic, string Text)>();
            var current = new StringBuilder();
            var currentArabic = line.Length > 0 && IsArabicRunChar(line[0]);
            foreach (var ch in line)
            {
                var arabic = IsArabicRunChar(ch);
                if (arabic != currentArabic && current.Length > 0)
                {
                    runs.Add((currentArabic, current.ToString())); current.Clear();
                }
                currentArabic = arabic;
                current.Append(ch);
            }
            if (current.Length > 0) runs.Add((currentArabic, current.ToString()));

            var glyphs = new List<int>();
            foreach (var (arabic, text) in runs.AsEnumerable().Reverse())
            {
                if (arabic)
                {
                    foreach (var shaped in ArabicTextShaper.Shape(text).Reverse()) glyphs.Add(font.GlyphFor(shaped));
                }
                else
                {
                    foreach (var ch in text) glyphs.Add(font.GlyphFor(ch));
                }
            }
            return glyphs.ToArray();

            static bool IsArabicRunChar(char c) => c is >= '\u0600' and <= '\u06FF' or >= '\uFB50' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF';
        }

        static string EscapePdf(string value) => new string(value.Select(c => c is >= ' ' and <= '~' ? c : '?').SelectMany(c => c is '(' or ')' or '\\' ? new[] { '\\', c } : new[] { c }).ToArray());
    }

    private static readonly Lazy<TtfFont> PersianFont = new(TtfFont.LoadVazirmatn);

    private sealed record ReportSnapshot(string Project, string BaseUrl, Guid CrawlId, DateTimeOffset? FinishedAt, int PagesDiscovered, int PagesCrawled, int Errors, decimal? OverallScore, string? ScoreVersion, IReadOnlyList<ReportIssue> Issues);
    private sealed record ReportIssue(string RuleCode, string Severity, string Category, string Title, string EvidenceJson);
}
