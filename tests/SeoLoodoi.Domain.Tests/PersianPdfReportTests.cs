using System.Text;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Application.Reports;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Reports;
using AwesomeAssertions;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// F11 regression: PDF reports must render real Persian glyphs. The old
/// writer used Helvetica and replaced every non-ASCII character with '?',
/// so Persian project names and issue titles were unreadable. The fix
/// embeds Vazirmatn (SIL OFL 1.1) and shapes Arabic script into its
/// connected presentation forms.
/// </summary>
public sealed class PersianPdfReportTests
{
    private sealed class AllowAllAccess : IProjectAccessService
    {
        public Task<ProjectAccess?> GetAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult<ProjectAccess?>(null);
        public Task<bool> CanViewAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> CanEditAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> CanManageAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class NoopAudit : IAuditLogService
    {
        public Task RecordAsync(Guid? projectId, Guid actorId, string action, string entityType, string? entityId, string? metadataJson, string? ipAddress, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditLogDto>>([]);
    }

    private static async Task<(ReportService service, Guid projectId, Guid userId)> SetupAsync(string projectName)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var userId = Guid.NewGuid();
        var project = new SeoProject(userId, projectName, new Uri("https://persian-pdf.example"));
        db.SeoProjects.Add(project);
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
        crawl.Start(DateTimeOffset.UtcNow);
        crawl.Complete(DateTimeOffset.UtcNow);
        db.Crawls.Add(crawl);
        await db.SaveChangesAsync();
        return (new ReportService(db, new AllowAllAccess(), new NoopAudit()), project.Id, userId);
    }

    [Fact]
    public async Task PdfReport_EmbedsVazirmatn_AndRendersPersianText()
    {
        var (service, projectId, userId) = await SetupAsync("فروشگاه نمونه");

        var created = await service.CreateAsync(projectId, userId, new CreateReportRequest(Format: "pdf"), CancellationToken.None);
        created.Should().NotBeNull();
        var file = await service.DownloadAsync(projectId, created!.Id, userId, CancellationToken.None);

        file.Should().NotBeNull();
        var text = Encoding.Latin1.GetString(file!.Bytes);
        text.Should().StartWith("%PDF");
        text.Should().Contain("/FontFile2", "the Vazirmatn font must be embedded in the PDF");
        text.Should().Contain("Vazirmatn", "the embedded font must be identifiable");
        text.Should().Contain("/ToUnicode", "embedded glyphs must stay selectable/copyable");
        text.Should().Contain("/F2", "the content stream must use the embedded Persian-capable font");
    }

    [Theory]
    [InlineData("سلام", 0xFEB5, 0xFEE4, 0xFE8E, 0xFEE5)]       // س initial, ل medial (FEE1-FEE4 block), ا final, م isolated
    [InlineData("اب", 0xFE8D, 0xFE8F)]                          // nothing joins across ا
    [InlineData("پاژ", 0xFB58, 0xFE8E, 0xFB8D)]                 // Persian letters join the same way
    [InlineData("باران", 0xFE91, 0xFE8E, 0xFEAD, 0xFE8D, 0xFEE9)] // بـاران: ب initial, ر/ن isolated
    public void ArabicShaper_ProducesCorrectPresentationForms(string input, params int[] expected)
    {
        var shaped = ArabicTextShaper.Shape(input);

        shaped.Select(x => (int)x).Should().Equal(expected);
    }

    [Fact]
    public void ArabicShaper_LeavesDigitsAndLatinUntouched()
    {
        var shaped = ArabicTextShaper.Shape("abc ۱۲");

        shaped.Should().Equal('a', 'b', 'c', ' ', '۱', '۲');
    }
}
