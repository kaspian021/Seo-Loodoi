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
        if (crawl is null) throw new InvalidOperationException("A completed crawl is required before generating a report.");
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
        var lines = new[] { "SEO Loodoi report", snapshot.Project, snapshot.BaseUrl, $"Crawl: {snapshot.CrawlId}", $"Pages: {snapshot.PagesCrawled}/{snapshot.PagesDiscovered}", $"Errors: {snapshot.Errors}", $"Overall score: {snapshot.OverallScore?.ToString() ?? "not calculated"}" };
        var stream = new StringBuilder("BT /F1 12 Tf 50 790 Td ");
        foreach (var line in lines) stream.Append('(').Append(EscapePdf(line)).Append(") Tj 0 -22 Td ");
        stream.Append("ET");
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {Encoding.ASCII.GetByteCount(stream.ToString())} >>\nstream\n{stream}\nendstream" };
        using var output = new MemoryStream(); var header = Encoding.ASCII.GetBytes("%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n"); output.Write(header);
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Length; i++) { offsets.Add(output.Position); var bytes = Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); output.Write(bytes); }
        var xref = output.Position; var cross = new StringBuilder($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n"); foreach (var offset in offsets.Skip(1)) cross.Append($"{offset:0000000000} 00000 n \n"); cross.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF"); output.Write(Encoding.ASCII.GetBytes(cross.ToString())); return output.ToArray();
        static string EscapePdf(string value) => new string(value.Select(c => c is >= ' ' and <= '~' ? c : '?').SelectMany(c => c is '(' or ')' or '\\' ? new[] { '\\', c } : new[] { c }).ToArray());
    }

    private sealed record ReportSnapshot(string Project, string BaseUrl, Guid CrawlId, DateTimeOffset? FinishedAt, int PagesDiscovered, int PagesCrawled, int Errors, decimal? OverallScore, string? ScoreVersion, IReadOnlyList<ReportIssue> Issues);
    private sealed record ReportIssue(string RuleCode, string Severity, string Category, string Title, string EvidenceJson);
}
