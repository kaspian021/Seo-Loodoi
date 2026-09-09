using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Keywords;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Keywords;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// F-01 regression: duplicate keyword create must be a DuplicateEntityException
/// (mapped to HTTP 409) on every provider — previously a silent duplicate row on
/// InMemory and an unhandled 23505 (HTTP 500) on Postgres.
/// </summary>
public class KeywordServiceTests
{
    private static (AppDbContext Db, KeywordService Svc) BuildService(bool canEdit = true)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"keywords-{Guid.NewGuid():N}")
            .Options;
        var db = new AppDbContext(options);
        var svc = new KeywordService(db, new AccessStub(canEdit), new QuotaStub(), new AuditStub());
        return (db, svc);
    }

    [Fact]
    public async Task CreateAsync_returns_dto_and_persists_the_keyword()
    {
        var (db, svc) = BuildService();
        var project = Guid.NewGuid();
        var dto = await svc.CreateAsync(project, Guid.NewGuid(), new CreateKeywordRequest("سئو لودوی"), CancellationToken.None);
        dto.Should().NotBeNull();
        dto!.Phrase.Should().Be("سئو لودوی");
        (await db.Keywords.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_same_phrase_differing_only_by_case_and_persian_ye_keh_throws_duplicate()
    {
        var (db, svc) = BuildService();
        var project = Guid.NewGuid();
        var user = Guid.NewGuid();
        await svc.CreateAsync(project, user, new CreateKeywordRequest("SEO Audit"), CancellationToken.None);

        // "seo audit" normalizes identically to "SEO Audit"
        var act = async () => await svc.CreateAsync(project, user, new CreateKeywordRequest("seo audit"), CancellationToken.None);
        await act.Should().ThrowAsync<DuplicateEntityException>();
        (await db.Keywords.CountAsync()).Should().Be(1);

        // "كتاب" (initial kaf) and "کتاب" (farsi kaf) normalize to the same phrase
        await svc.CreateAsync(project, user, new CreateKeywordRequest("كتاب"), CancellationToken.None);
        var act2 = async () => await svc.CreateAsync(project, user, new CreateKeywordRequest("کتاب"), CancellationToken.None);
        await act2.Should().ThrowAsync<DuplicateEntityException>();
        (await db.Keywords.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task CreateAsync_same_phrase_different_country_is_allowed()
    {
        var (db, svc) = BuildService();
        var project = Guid.NewGuid();
        var user = Guid.NewGuid();
        await svc.CreateAsync(project, user, new CreateKeywordRequest("SEO Audit", "en", "US"), CancellationToken.None);
        var dto = await svc.CreateAsync(project, user, new CreateKeywordRequest("SEO Audit", "en", "IR"), CancellationToken.None);
        dto.Should().NotBeNull();
        (await db.Keywords.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task CreateAsync_same_phrase_different_project_is_allowed()
    {
        var (db, svc) = BuildService();
        var user = Guid.NewGuid();
        await svc.CreateAsync(Guid.NewGuid(), user, new CreateKeywordRequest("SEO Audit"), CancellationToken.None);
        var dto = await svc.CreateAsync(Guid.NewGuid(), user, new CreateKeywordRequest("SEO Audit"), CancellationToken.None);
        dto.Should().NotBeNull();
        (await db.Keywords.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task CreateAsync_without_edit_access_returns_null_instead_of_throwing()
    {
        var (db, svc) = BuildService(canEdit: false);
        var dto = await svc.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), new CreateKeywordRequest("SEO Audit"), CancellationToken.None);
        dto.Should().BeNull();
        (await db.Keywords.CountAsync()).Should().Be(0);
    }
}
