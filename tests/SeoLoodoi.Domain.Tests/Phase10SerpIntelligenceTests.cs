using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Application.Serp;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Billing;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;
using SeoLoodoi.Infrastructure.Serp;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// PHASE 10 — SERP intelligence.
/// <para>
/// The invariant under test is the same one that governs backlinks: rank history
/// must be built only from observed results. An empty SERP snapshot recorded as a
/// fact reads as "this keyword ranks nowhere", so the platform records the reason
/// it could not look instead.
/// </para>
/// </summary>
public sealed class Phase10SerpIntelligenceTests
{
    private const string Phrase = "technical seo";
    private const string Host = "example.com";

    private static AppDbContext CreateInMemoryDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static SeoProject SeedProject(AppDbContext db, Guid ownerId, string host = Host)
    {
        var project = new SeoProject(ownerId, "serp project", new Uri($"https://{host}"));
        db.SeoProjects.Add(project);
        db.SaveChanges();
        return project;
    }

    private static Keyword SeedKeyword(AppDbContext db, Guid projectId, string phrase = Phrase)
    {
        var keyword = new Keyword(projectId, phrase, "en", "US");
        db.Keywords.Add(keyword);
        db.SaveChanges();
        return keyword;
    }

    private static SerpResultDto Result(int position, string url, bool isPaid = false) =>
        new(position, url, new Uri(url).Host, $"title {position}", null, isPaid);

    private sealed class NoOpAudit : IAuditLogService
    {
        public Task RecordAsync(Guid? projectId, Guid actorId, string action, string entityType, string? entityId, string? metadataJson, string? ipAddress, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditLogDto>>([]);
    }

    private sealed class RecordingJobQueue : ISeoJobQueue
    {
        public List<string> Enqueued { get; } = [];
        public Task<Guid> EnqueueOnceAsync(SeoJobType type, string idempotencyKey, string payloadJson, CancellationToken ct, DateTimeOffset? notBefore = null)
        {
            Enqueued.Add(idempotencyKey);
            return Task.FromResult(Guid.NewGuid());
        }
        public Task<SeoBackgroundJob?> TryLeaseAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct) => Task.FromResult<SeoBackgroundJob?>(null);
        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSerpProvider(
        IReadOnlyList<SerpResultDto> results,
        SerpFeatureKind[]? features = null,
        SerpAvailability availability = SerpAvailability.Available,
        bool truncated = false) : ISerpProvider
    {
        public string Name => "fake";
        public bool IsConfigured => true;
        public SerpProviderCapabilities Capabilities => new(true, true, true, true, true, true, true, 500);

        public Task<SerpSnapshotResult> FetchAsync(SerpQuery query, CancellationToken ct) => Task.FromResult(
            new SerpSnapshotResult(
                Name, availability, null, results,
                (features ?? []).Select(f => new SerpFeatureDto(f, null, null)).ToArray(),
                truncated));
    }

    private sealed class ThrowingSerpProvider : ISerpProvider
    {
        public string Name => "throwing";
        public bool IsConfigured => true;
        public SerpProviderCapabilities Capabilities => new(true, true, true, true, true, true, true, 500);
        public Task<SerpSnapshotResult> FetchAsync(SerpQuery query, CancellationToken ct) => throw new TimeoutException("upstream did not answer in time");
    }

    private static SerpService CreateService(AppDbContext db, ISerpProvider provider, RecordingJobQueue jobs) =>
        new(db, new ProjectAccessService(db), provider,
            new EntitlementService(db, Options.Create(new LoodoiBillingOptions()), new NoOpAudit(), NullLogger<EntitlementService>.Instance),
            jobs, new NoOpAudit());

    private static async Task<SerpSnapshot> SeedSnapshotAsync(
        AppDbContext db,
        Guid projectId,
        Guid? keywordId,
        string projectHost,
        (int Position, string Url)[] results,
        SerpFeatureKind[]? features = null)
    {
        var owned = results.Where(r => SerpRefreshJobHandler.MatchesHost(new Uri(r.Url).Host, r.Url, projectHost))
            .OrderBy(r => r.Position)
            .ToArray();

        var snapshot = new SerpSnapshot(projectId, keywordId, Phrase, Phrase, "US", "en", SerpDevice.Desktop, SerpSurface.Organic, "fake");
        db.SerpSnapshots.Add(snapshot);
        snapshot.RecordSuccess(
            SerpAvailability.Available,
            results.Length,
            false,
            JsonSerializer.Serialize((features ?? []).Select(f => new SerpFeatureDto(f, null, null)).ToArray()),
            owned.Length > 0 ? owned[0].Position : null,
            owned.Length > 0 ? owned[0].Url : null,
            null,
            DateTimeOffset.UtcNow);

        foreach (var r in results)
            db.SerpResultEntries.Add(new SerpResultEntry(
                snapshot.Id, r.Position, r.Url, new Uri(r.Url).Host, $"title {r.Position}", null, false,
                SerpRefreshJobHandler.MatchesHost(new Uri(r.Url).Host, r.Url, projectHost)));

        await db.SaveChangesAsync();
        return snapshot;
    }

    // ---------------------------------------------------------------- provider

    [Fact]
    public async Task NullSerpProvider_NeverFabricates_ReportsNotConfigured()
    {
        var provider = new NullSerpProvider();

        provider.IsConfigured.Should().BeFalse();
        var result = await provider.FetchAsync(new SerpQuery(Phrase, "US", "en", SerpDevice.Desktop, SerpSurface.Organic, 50), CancellationToken.None);

        result.Availability.Should().Be(SerpAvailability.NotConfigured);
        result.Results.Should().BeEmpty();
        result.Features.Should().BeEmpty();
        result.Note.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void NullSerpProvider_Capabilities_AdvertiseNoSupport()
    {
        var capabilities = new NullSerpProvider().Capabilities;

        capabilities.SupportsOrganicResults.Should().BeFalse();
        capabilities.SupportsFeatures.Should().BeFalse();
        capabilities.SupportsAiSurfaces.Should().BeFalse();
        capabilities.MaxResultsPerQuery.Should().Be(0);
    }

    // ----------------------------------------------------------- domain model

    [Fact]
    public void Snapshot_Unavailable_KeepsOwnPositionNull_NotZeroOrLast()
    {
        var snapshot = new SerpSnapshot(Guid.NewGuid(), Guid.NewGuid(), Phrase, Phrase, "US", "en", SerpDevice.Desktop, SerpSurface.Organic, "none");

        snapshot.RecordUnavailable(SerpAvailability.NotConfigured, "no provider", DateTimeOffset.UtcNow);

        snapshot.HasData.Should().BeFalse();
        snapshot.OwnPosition.Should().BeNull("an unobserved rank is not a rank");
        snapshot.OwnUrl.Should().BeNull();
        snapshot.ResultCount.Should().Be(0);
        snapshot.FeaturesJson.Should().Be("[]");
    }

    [Fact]
    public void Snapshot_RejectedStateTransitions_GuardAgainstInventedRankings()
    {
        var snapshot = new SerpSnapshot(Guid.NewGuid(), Guid.NewGuid(), Phrase, Phrase, "US", "en", SerpDevice.Desktop, SerpSurface.Organic, "fake");

        var successWithBadState = () => snapshot.RecordSuccess(SerpAvailability.NotConfigured, 0, false, "[]", null, null, null, DateTimeOffset.UtcNow);
        var unavailableWithBadState = () => snapshot.RecordUnavailable(SerpAvailability.Available, "reason", DateTimeOffset.UtcNow);
        var unavailableWithoutReason = () => snapshot.RecordUnavailable(SerpAvailability.Unavailable, "  ", DateTimeOffset.UtcNow);

        successWithBadState.Should().Throw<ArgumentException>();
        unavailableWithBadState.Should().Throw<ArgumentException>();
        unavailableWithoutReason.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ResultEntry_RejectsPositionBelowOne()
    {
        var invalid = () => new SerpResultEntry(Guid.NewGuid(), 0, $"https://{Host}/a", Host, null, null, false, false);

        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ------------------------------------------------------------ job handler

    [Fact]
    public async Task RefreshJob_NoProvider_StoresNotConfigured_AndPersistsNoResults()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var keyword = SeedKeyword(db, project.Id);
        var snapshot = new SerpSnapshot(project.Id, keyword.Id, Phrase, Phrase, "US", "en", SerpDevice.Desktop, SerpSurface.Organic, "none");
        db.SerpSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var handler = new SerpRefreshJobHandler(db, new NullSerpProvider(), Options.Create(new SerpProviderOptions()), TimeProvider.System, NullLogger<SerpRefreshJobHandler>.Instance);
        var job = new SeoBackgroundJob(SeoJobType.SerpRefresh, "k", Payload(snapshot.Id, project.Id, keyword.Id));

        await handler.HandleAsync(job, CancellationToken.None);

        var stored = await db.SerpSnapshots.SingleAsync();
        stored.Availability.Should().Be(SerpAvailability.NotConfigured);
        stored.HasData.Should().BeFalse();
        stored.OwnPosition.Should().BeNull();
        stored.Note.Should().NotBeNullOrWhiteSpace();
        stored.CapturedAt.Should().NotBeNull();
        (await db.SerpResultEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RefreshJob_ProviderFailure_StoresUnavailable_NotAnEmptySuccess()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var keyword = SeedKeyword(db, project.Id);
        var snapshot = new SerpSnapshot(project.Id, keyword.Id, Phrase, Phrase, "US", "en", SerpDevice.Desktop, SerpSurface.Organic, "throwing");
        db.SerpSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var handler = new SerpRefreshJobHandler(db, new ThrowingSerpProvider(), Options.Create(new SerpProviderOptions()), TimeProvider.System, NullLogger<SerpRefreshJobHandler>.Instance);
        var job = new SeoBackgroundJob(SeoJobType.SerpRefresh, "k", Payload(snapshot.Id, project.Id, keyword.Id));

        await handler.HandleAsync(job, CancellationToken.None);

        var stored = await db.SerpSnapshots.SingleAsync();
        stored.Availability.Should().Be(SerpAvailability.Unavailable);
        stored.HasData.Should().BeFalse();
        stored.OwnPosition.Should().BeNull("a failed call observed no rank");
    }

    [Fact]
    public async Task RefreshJob_WithProvider_DerivesOwnPositionOnlyFromObservedResults()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var keyword = SeedKeyword(db, project.Id);
        var snapshot = new SerpSnapshot(project.Id, keyword.Id, Phrase, Phrase, "US", "en", SerpDevice.Desktop, SerpSurface.Organic, "fake");
        db.SerpSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var results = new[]
        {
            Result(1, "https://competitor-a.com/a"),
            Result(2, "https://competitor-b.com/b"),
            Result(3, $"https://www.{Host}/blog/seo"),
            Result(4, $"https://{Host}/technical-seo"),
            Result(5, "https://competitor-c.com/c"),
        };

        var handler = new SerpRefreshJobHandler(db, new FakeSerpProvider(results), Options.Create(new SerpProviderOptions()), TimeProvider.System, NullLogger<SerpRefreshJobHandler>.Instance);
        var job = new SeoBackgroundJob(SeoJobType.SerpRefresh, "k", Payload(snapshot.Id, project.Id, keyword.Id));

        await handler.HandleAsync(job, CancellationToken.None);

        var stored = await db.SerpSnapshots.SingleAsync();
        stored.Availability.Should().Be(SerpAvailability.Available);
        // The best observed position for the tracked host, not an estimate.
        stored.OwnPosition.Should().Be(3);
        stored.OwnUrl.Should().Be($"https://www.{Host}/blog/seo");
        stored.ResultCount.Should().Be(5);
        stored.ProviderPayloadHash.Should().NotBeNullOrWhiteSpace();

        var entries = await db.SerpResultEntries.OrderBy(x => x.Position).ToListAsync();
        entries.Should().HaveCount(5);
        entries.Count(x => x.IsOwned).Should().Be(2, "both the apex host and its www subdomain belong to the project");
    }

    [Fact]
    public async Task RefreshJob_ProjectNotRanking_LeavesOwnPositionNull()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var keyword = SeedKeyword(db, project.Id);
        var snapshot = new SerpSnapshot(project.Id, keyword.Id, Phrase, Phrase, "US", "en", SerpDevice.Desktop, SerpSurface.Organic, "fake");
        db.SerpSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var results = new[] { Result(1, "https://competitor-a.com/a"), Result(2, "https://competitor-b.com/b") };
        var handler = new SerpRefreshJobHandler(db, new FakeSerpProvider(results), Options.Create(new SerpProviderOptions()), TimeProvider.System, NullLogger<SerpRefreshJobHandler>.Instance);
        var job = new SeoBackgroundJob(SeoJobType.SerpRefresh, "k", Payload(snapshot.Id, project.Id, keyword.Id));

        await handler.HandleAsync(job, CancellationToken.None);

        var stored = await db.SerpSnapshots.SingleAsync();
        stored.HasData.Should().BeTrue("the SERP itself was captured successfully");
        stored.OwnPosition.Should().BeNull("the tracked site simply was not in the observed results");
        (await db.SerpResultEntries.CountAsync(x => x.IsOwned)).Should().Be(0);
    }

    [Fact]
    public async Task RefreshJob_WithProvider_HonoursTheResultCap()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var keyword = SeedKeyword(db, project.Id);
        var snapshot = new SerpSnapshot(project.Id, keyword.Id, Phrase, Phrase, "US", "en", SerpDevice.Desktop, SerpSurface.Organic, "fake");
        db.SerpSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var results = new[]
        {
            Result(1, "https://a.com/1"), Result(2, "https://b.com/2"),
            Result(3, "https://c.com/3"), Result(4, "https://d.com/4"),
            Result(5, "https://e.com/5"),
        };

        var handler = new SerpRefreshJobHandler(db, new FakeSerpProvider(results), Options.Create(new SerpProviderOptions { MaxResults = 2 }), TimeProvider.System, NullLogger<SerpRefreshJobHandler>.Instance);
        var job = new SeoBackgroundJob(SeoJobType.SerpRefresh, "k", Payload(snapshot.Id, project.Id, keyword.Id));

        await handler.HandleAsync(job, CancellationToken.None);

        var stored = await db.SerpSnapshots.SingleAsync();
        stored.ResultCount.Should().Be(2, "the provider returned 5 but the cap is 2");
        stored.ResultsTruncated.Should().BeTrue();
        (await db.SerpResultEntries.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task RefreshJob_PersistsSerpFeatures_ForHistoricalComparison()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var keyword = SeedKeyword(db, project.Id);
        var snapshot = new SerpSnapshot(project.Id, keyword.Id, Phrase, Phrase, "US", "en", SerpDevice.Desktop, SerpSurface.Organic, "fake");
        db.SerpSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var handler = new SerpRefreshJobHandler(
            db,
            new FakeSerpProvider([Result(1, "https://a.com/1")], [SerpFeatureKind.FeaturedSnippet, SerpFeatureKind.PeopleAlsoAsk, SerpFeatureKind.AiOverview]),
            Options.Create(new SerpProviderOptions()), TimeProvider.System, NullLogger<SerpRefreshJobHandler>.Instance);
        var job = new SeoBackgroundJob(SeoJobType.SerpRefresh, "k", Payload(snapshot.Id, project.Id, keyword.Id));

        await handler.HandleAsync(job, CancellationToken.None);

        var stored = await db.SerpSnapshots.SingleAsync();
        var features = JsonSerializer.Deserialize<List<SerpFeatureDto>>(stored.FeaturesJson)!;
        features.Select(f => f.Kind).Should().Contain([SerpFeatureKind.FeaturedSnippet, SerpFeatureKind.PeopleAlsoAsk, SerpFeatureKind.AiOverview]);
    }

    [Fact]
    public void MatchesHost_TreatsSubdomainsAsTheSameSite_AndRejectsLookalikes()
    {
        SerpRefreshJobHandler.MatchesHost(Host, $"https://{Host}/a", Host).Should().BeTrue();
        SerpRefreshJobHandler.MatchesHost($"www.{Host}", $"https://www.{Host}/a", Host).Should().BeTrue();
        SerpRefreshJobHandler.MatchesHost("notexample.com", "https://notexample.com/a", Host).Should().BeFalse("a suffix match must not swallow lookalike domains");
        SerpRefreshJobHandler.MatchesHost("", "https://other.com/a", Host).Should().BeFalse();
        SerpRefreshJobHandler.MatchesHost("other.com", "https://other.com/a", "").Should().BeFalse();
    }

    // ---------------------------------------------------------------- service

    [Fact]
    public async Task RequestRefresh_IsIdempotent_WhileOneCaptureIsPending()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var keyword = SeedKeyword(db, project.Id);
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullSerpProvider(), jobs);

        var first = await service.RequestRefreshAsync(project.Id, keyword.Id, owner, null, null, CancellationToken.None);
        var second = await service.RequestRefreshAsync(project.Id, keyword.Id, owner, null, null, CancellationToken.None);

        first.Should().NotBeNull();
        second!.Id.Should().Be(first!.Id);
        (await db.SerpSnapshots.CountAsync()).Should().Be(1);
        jobs.Enqueued.Should().HaveCount(1);
    }

    [Fact]
    public async Task RequestRefresh_DeniesUsersWithoutProjectAccess()
    {
        await using var db = CreateInMemoryDb();
        var project = SeedProject(db, Guid.NewGuid());
        var keyword = SeedKeyword(db, project.Id);
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullSerpProvider(), jobs);

        var result = await service.RequestRefreshAsync(project.Id, keyword.Id, Guid.NewGuid(), null, null, CancellationToken.None);

        result.Should().BeNull();
        jobs.Enqueued.Should().BeEmpty();
        (await db.SerpSnapshots.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Results_WithoutProviderData_ReturnsEmptyPage_NotARankOfZero()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var keyword = SeedKeyword(db, project.Id);
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullSerpProvider(), jobs);
        var snapshot = (await service.RequestRefreshAsync(project.Id, keyword.Id, owner, null, null, CancellationToken.None))!;

        var page = await service.ResultsAsync(project.Id, snapshot.Id, owner, 50, CancellationToken.None);

        page.Should().NotBeNull();
        page!.Availability.Should().Be(SerpAvailability.NotConfigured);
        page.Results.Should().BeEmpty();
        page.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Compare_DetectsRankMovement_EnteredAndDroppedOut()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var keyword = SeedKeyword(db, project.Id);
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullSerpProvider(), jobs);

        var before = await SeedSnapshotAsync(db, project.Id, keyword.Id, Host,
        [
            (1, "https://a.com/a"),
            (2, "https://b.com/b"),
            (7, $"https://{Host}/seo"),
        ]);
        var after = await SeedSnapshotAsync(db, project.Id, keyword.Id, Host,
        [
            (1, "https://b.com/b"),
            (2, "https://c.com/c"),
            (3, $"https://{Host}/seo"),
        ]);

        var comparison = await service.CompareAsync(project.Id, owner, before.Id, after.Id, CancellationToken.None);

        comparison.Should().NotBeNull();
        // Own rank moved 7 -> 3: four places better.
        comparison!.FromPosition.Should().Be(7);
        comparison.ToPosition.Should().Be(3);
        comparison.PositionDelta.Should().Be(4, "delta is expressed as places gained");
        comparison.OwnRankMovement.Should().Be("improved");

        var own = comparison.Movements.Single(x => x.IsOwned);
        own.Delta.Should().Be(4);
        own.Movement.Should().Be("improved");

        comparison.Entered.Select(x => x.Url).Should().ContainSingle().Which.Should().Be("https://c.com/c");
        comparison.DroppedOut.Select(x => x.Url).Should().ContainSingle().Which.Should().Be("https://a.com/a");
    }

    [Fact]
    public async Task Compare_DetectsSerpFeatureChanges()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var keyword = SeedKeyword(db, project.Id);
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullSerpProvider(), jobs);

        var before = await SeedSnapshotAsync(db, project.Id, keyword.Id, Host,
            [(1, "https://a.com/a")], [SerpFeatureKind.FeaturedSnippet, SerpFeatureKind.LocalPack]);
        var after = await SeedSnapshotAsync(db, project.Id, keyword.Id, Host,
            [(1, "https://a.com/a")], [SerpFeatureKind.FeaturedSnippet, SerpFeatureKind.AiOverview]);

        var comparison = await service.CompareAsync(project.Id, owner, before.Id, after.Id, CancellationToken.None);

        comparison!.FeaturesAdded.Should().ContainSingle().Which.Should().Be("AiOverview");
        comparison.FeaturesRemoved.Should().ContainSingle().Which.Should().Be("LocalPack");
    }

    [Fact]
    public async Task Compare_RefusesToDiff_WhenEitherSnapshotHasNoCapturedData()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var keyword = SeedKeyword(db, project.Id);
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullSerpProvider(), jobs);

        var captured = await SeedSnapshotAsync(db, project.Id, keyword.Id, Host, [(1, "https://a.com/a")]);
        var failed = new SerpSnapshot(project.Id, keyword.Id, Phrase, Phrase, "US", "en", SerpDevice.Desktop, SerpSurface.Organic, "none");
        db.SerpSnapshots.Add(failed);
        failed.RecordUnavailable(SerpAvailability.Unavailable, "provider outage", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        // Diffing against a failed capture would report every result as "dropped
        // out" and the rank as lost. The service must refuse.
        var comparison = await service.CompareAsync(project.Id, owner, captured.Id, failed.Id, CancellationToken.None);

        comparison.Should().BeNull();
    }

    [Fact]
    public async Task ProviderStatus_ExposesConfigurationAndCapabilities()
    {
        await using var db = CreateInMemoryDb();
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullSerpProvider(), jobs);

        var status = await service.ProviderStatusAsync(CancellationToken.None);

        status.IsConfigured.Should().BeFalse();
        status.ProviderName.Should().Be("none");
        status.Capabilities.SupportsAiSurfaces.Should().BeFalse();
    }

    private static string Payload(Guid snapshotId, Guid projectId, Guid keywordId) =>
        JsonSerializer.Serialize(new SerpRefreshJobPayload(
            snapshotId, projectId, keywordId, Phrase, Phrase, "US", "en",
            SerpDevice.Desktop, SerpSurface.Organic, Host));
}
