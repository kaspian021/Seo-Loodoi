namespace SeoLoodoi.Application.Reports;

public sealed record ReportDto(Guid Id, string Type, string Format, string Status, Guid? CrawlId, DateTimeOffset CreatedAt);
public sealed record CreateReportRequest(string Type = "Executive", string Format = "json", Guid? CrawlId = null);
public sealed record ReportFile(Guid Id, string FileName, string ContentType, byte[] Bytes);
public interface IReportService
{
    Task<IReadOnlyList<ReportDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<ReportDto?> CreateAsync(Guid projectId, Guid userId, CreateReportRequest request, CancellationToken ct);
    Task<ReportFile?> DownloadAsync(Guid projectId, Guid reportId, Guid userId, CancellationToken ct);
}
