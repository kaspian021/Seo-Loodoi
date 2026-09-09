using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Keywords;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Keywords;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Domain.Tests.Postgres;

/// <summary>
/// F-01 regression on a REAL PostgreSQL server (the InMemory provider does not
/// enforce unique indexes, so only these tests can prove the 23505 backstop and
/// the concurrent-insert behavior). CI provides postgres:16 and sets
/// ConnectionStrings__Postgres; otherwise each test skips.
/// </summary>
[Collection("postgres")]
public class KeywordServicePostgresTests(PostgresFixture fixture)
{
    private static KeywordService ServiceFor(AppDbContext db) =>
        new(db, new AccessStub(), new QuotaStub(), new AuditStub());

    [Fact]
    public async Task Sequential_duplicate_create_is_a_clean_duplicate_not_a_500_source()
    {
        fixture.ReportUnavailable();
        if (!fixture.DatabaseAvailable) return;
        var project = Guid.NewGuid();
        using var db = fixture.CreateContext();
        try
        {
            var svc = ServiceFor(db);
            await svc.CreateAsync(project, Guid.NewGuid(), new CreateKeywordRequest("SEO Audit"), CancellationToken.None);
            var act = async () => await svc.CreateAsync(project, Guid.NewGuid(), new CreateKeywordRequest("seo audit"), CancellationToken.None);
            await act.Should().ThrowAsync<DuplicateEntityException>();
            (await db.Keywords.CountAsync(x => x.ProjectId == project)).Should().Be(1);
        }
        finally
        {
            await db.Keywords.Where(x => x.ProjectId == project).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Concurrent_duplicate_creates_exactly_one_wins_and_the_rest_are_clean_duplicates()
    {
        fixture.ReportUnavailable();
        if (!fixture.DatabaseAvailable) return;
        var project = Guid.NewGuid();
        var user = Guid.NewGuid();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            using var db = fixture.CreateContext();
            try
            {
                var svc = ServiceFor(db);
                await svc.CreateAsync(project, user, new CreateKeywordRequest("race keyword"), CancellationToken.None);
                return "created";
            }
            catch (DuplicateEntityException)
            {
                return "duplicate";
            }
        }));
        using var check = fixture.CreateContext();
        try
        {
            (await check.Keywords.CountAsync(x => x.ProjectId == project)).Should().Be(1, "exactly one keyword row for the project");
            outcomes.Count(o => o == "created").Should().Be(1, "exactly one concurrent writer wins");
            outcomes.Count(o => o == "duplicate").Should().Be(4, "losers get the clean duplicate signal, never a raw 23505");
        }
        finally
        {
            await check.Keywords.Where(x => x.ProjectId == project).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Unique_index_backstop_still_enforced_when_bypassing_the_service()
    {
        fixture.ReportUnavailable();
        if (!fixture.DatabaseAvailable) return;
        var project = Guid.NewGuid();
        using var db = fixture.CreateContext();
        try
        {
            // The backstop the pre-check relies on must exist in the schema.
            var indexExists = await db.Database
                .SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'loodoi' AND indexname = 'IX_Keywords_ProjectId_NormalizedPhrase_Country')")
                .SingleAsync();
            indexExists.Should().BeTrue();

            var normalized = Keyword.Normalize("Backstop Phrase");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO loodoi."Keywords" ("Id", "ProjectId", "Phrase", "NormalizedPhrase", "Language", "Country", "IsTracked", "CreatedAt", "UpdatedAt")
                VALUES ({Guid.NewGuid()}, {project}, 'Backstop Phrase', {normalized}, 'fa', 'IR', true, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow})
                """);
            DbUpdateException? caught = null;
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO loodoi."Keywords" ("Id", "ProjectId", "Phrase", "NormalizedPhrase", "Language", "Country", "IsTracked", "CreatedAt", "UpdatedAt")
                    VALUES ({Guid.NewGuid()}, {project}, 'Backstop Phrase', {normalized}, 'fa', 'IR', true, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow})
                    """);
            }
            catch (DbUpdateException ex)
            {
                caught = ex;
            }
            caught.Should().NotBeNull("the unique index must still reject a raw duplicate insert");
            DbExceptionClassifier.IsUniqueViolation(caught!).Should().BeTrue();
        }
        finally
        {
            await db.Keywords.Where(x => x.ProjectId == project).ExecuteDeleteAsync();
        }
    }
}
