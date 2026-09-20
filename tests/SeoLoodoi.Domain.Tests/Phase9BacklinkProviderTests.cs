using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Backlinks;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Backlinks;
using SeoLoodoi.Infrastructure.Billing;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// PHASE 9 — backlink provider architecture.
/// <para>
/// The central invariant under test is "do not fabricate backlink data". Every
/// test here would still pass against an implementation that invents plausible
/// domains unless it also respects the availability states and the null-vs-zero
/// distinction, so that distinction is asserted directly.
/// </para>
/// </summary>
public sealed class Phase9BacklinkProviderTests
{
    private const string Host = "example.com";

    private static AppDbContext CreateInMemoryDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static SeoProject SeedProject(AppDbContext db, Guid ownerId, string host = Host)
    {
        var project = new SeoProject(ownerId, "backlink project", new Uri($"https://{host}"));
        db.SeoProjects.Add(project);
        db.SaveChanges();
        return project;
    }

    private static BacklinkObservationDto Link(string source, string target, bool isNew = false, bool isLost = false) =>
        new(source, new Uri(source).Host, target, "anchor", BacklinkRelAttribute.Follow, null, null, isNew, isLost);

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

    private sealed class FakeBacklinkProvider(
        IReadOnlyList<BacklinkObservationDto> links,
        BacklinkAvailability availability = BacklinkAvailability.Available,
        bool truncated = false) : IBacklinkProvider
    {
        public string Name => "fake";
        public bool IsConfigured => true;
        public BacklinkProviderCapabilities Capabilities => new(true, true, true, true, true, true, 10_000);

        public Task<BacklinkSnapshotResult> FetchAsync(BacklinkQuery query, CancellationToken ct) => Task.FromResult(
            new BacklinkSnapshotResult(
                Name, availability, null,
                ReferringDomains: links.Count,
                TotalBacklinks: links.Count,
                FollowCount: links.Count,
                NoFollowCount: 0,
                NewCount: links.Count(link => link.IsNew),
                LostCount: links.Count(link => link.IsLost),
                AuthorityScore: 42.5m,
                AuthorityMetricName: "fake_authority",
                PeriodStart: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
                PeriodEnd: DateOnly.FromDateTime(DateTime.UtcNow),
                Observations: links,
                ObservationsTruncated: truncated));
    }

    private sealed class ThrowingBacklinkProvider : IBacklinkProvider
    {
        public string Name => "throwing";
        public bool IsConfigured => true;
        public BacklinkProviderCapabilities Capabilities => new(true, true, true, true, true, true, 10_000);
        public Task<BacklinkSnapshotResult> FetchAsync(BacklinkQuery query, CancellationToken ct) => throw new TimeoutException("upstream did not answer in time");
    }

    private static BacklinkService CreateService(
        AppDbContext db,
        IBacklinkProvider provider,
        RecordingJobQueue jobs,
        out EntitlementService entitlements)
    {
        entitlements = new EntitlementService(db, Options.Create(new LoodoiBillingOptions()), new NoOpAudit(), NullLogger<EntitlementService>.Instance);
        return new BacklinkService(db, new ProjectAccessService(db), provider, entitlements, jobs, new NoOpAudit());
    }

    private static async Task<BacklinkSnapshot> SeedSnapshotAsync(AppDbContext db, Guid projectId, params (string Source, string Target)[] links)
    {
        var snapshot = new BacklinkSnapshot(projectId, Host, "fake");
        db.BacklinkSnapshots.Add(snapshot);
        snapshot.RecordSuccess(
            BacklinkAvailability.Available,
            new BacklinkCounts(ReferringDomains: links.Length, TotalBacklinks: links.Length),
            links.Length, false, null, DateTimeOffset.UtcNow);
        foreach (var (source, target) in links)
            db.BacklinkObservations.Add(new BacklinkObservation(snapshot.Id, source, new Uri(source).Host, target, "anchor", BacklinkRelAttribute.Follow, null, null, false, false));
        await db.SaveChangesAsync();
        return snapshot;
    }

    // ---------------------------------------------------------------- provider

    [Fact]
    public async Task NullProvider_NeverFabricates_ReportsNotConfiguredWithoutCounts()
    {
        var provider = new NullBacklinkProvider();

        provider.IsConfigured.Should().BeFalse();
        var result = await provider.FetchAsync(new BacklinkQuery(Host, 100), CancellationToken.None);

        result.Availability.Should().Be(BacklinkAvailability.NotConfigured);
        result.Observations.Should().BeEmpty();
        result.Note.Should().NotBeNullOrWhiteSpace();
        result.ReferringDomains.Should().BeNull("an unmeasured count is not a count of zero");
        result.TotalBacklinks.Should().BeNull();
        result.AuthorityScore.Should().BeNull();
    }

    [Fact]
    public void NullProvider_Capabilities_AdvertiseNoSupport()
    {
        var capabilities = new NullBacklinkProvider().Capabilities;

        capabilities.SupportsReferringDomains.Should().BeFalse();
        capabilities.SupportsAnchorText.Should().BeFalse();
        capabilities.SupportsAuthorityMetrics.Should().BeFalse();
        capabilities.MaxObservationsPerRequest.Should().Be(0);
    }

    // ----------------------------------------------------------- domain model

    [Fact]
    public void Snapshot_Unavailable_KeepsEveryCounterNull_NotZero()
    {
        var snapshot = new BacklinkSnapshot(Guid.NewGuid(), Host, "none");

        snapshot.RecordUnavailable(BacklinkAvailability.NotConfigured, "no provider", DateTimeOffset.UtcNow);

        snapshot.HasData.Should().BeFalse();
        snapshot.ReferringDomains.Should().BeNull();
        snapshot.TotalBacklinks.Should().BeNull();
        snapshot.FollowCount.Should().BeNull();
        snapshot.NewCount.Should().BeNull();
        snapshot.LostCount.Should().BeNull();
        snapshot.AuthorityScore.Should().BeNull();
        snapshot.ObservationCount.Should().Be(0);
    }

    [Fact]
    public void Snapshot_RejectedStateTransitions_GuardAgainstInventedData()
    {
        var snapshot = new BacklinkSnapshot(Guid.NewGuid(), Host, "fake");
        var counts = new BacklinkCounts(TotalBacklinks: 10);

        var successWithBadState = () => snapshot.RecordSuccess(BacklinkAvailability.NotConfigured, counts, 0, false, null, DateTimeOffset.UtcNow);
        var unavailableWithBadState = () => snapshot.RecordUnavailable(BacklinkAvailability.Available, "reason", DateTimeOffset.UtcNow);
        var unavailableWithoutReason = () => snapshot.RecordUnavailable(BacklinkAvailability.Unavailable, "   ", DateTimeOffset.UtcNow);

        successWithBadState.Should().Throw<ArgumentException>();
        unavailableWithBadState.Should().Throw<ArgumentException>();
        unavailableWithoutReason.Should().Throw<ArgumentException>("every empty result must carry an explainable reason");
    }

    [Fact]
    public void Snapshot_Constructor_RejectsEmptyTargetOrProvider()
    {
        var emptyHost = () => new BacklinkSnapshot(Guid.NewGuid(), " ", "fake");
        var emptyProvider = () => new BacklinkSnapshot(Guid.NewGuid(), Host, "");

        emptyHost.Should().Throw<ArgumentException>();
        emptyProvider.Should().Throw<ArgumentException>();
    }

    // ------------------------------------------------------------ job handler

    [Fact]
    public async Task RefreshJob_NoProvider_StoresNotConfigured_AndPersistsNoLinks()
    {
        await using var db = CreateInMemoryDb();
        var project = SeedProject(db, Guid.NewGuid());
        var snapshot = new BacklinkSnapshot(project.Id, Host, "none");
        db.BacklinkSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var handler = new BacklinkRefreshJobHandler(
            db, new NullBacklinkProvider(), Options.Create(new BacklinkProviderOptions()),
            TimeProvider.System, NullLogger<BacklinkRefreshJobHandler>.Instance);
        var job = new SeoBackgroundJob(SeoJobType.BacklinkRefresh, "k", JsonSerializer.Serialize(new BacklinkRefreshJobPayload(snapshot.Id, project.Id, Host)));

        await handler.HandleAsync(job, CancellationToken.None);

        var stored = await db.BacklinkSnapshots.SingleAsync();
        stored.Availability.Should().Be(BacklinkAvailability.NotConfigured);
        stored.HasData.Should().BeFalse();
        stored.TotalBacklinks.Should().BeNull();
        stored.Note.Should().NotBeNullOrWhiteSpace();
        stored.FetchedAt.Should().NotBeNull();
        (await db.BacklinkObservations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RefreshJob_ProviderFailure_StoresUnavailable_NotAnEmptySuccess()
    {
        await using var db = CreateInMemoryDb();
        var project = SeedProject(db, Guid.NewGuid());
        var snapshot = new BacklinkSnapshot(project.Id, Host, "throwing");
        db.BacklinkSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var handler = new BacklinkRefreshJobHandler(
            db, new ThrowingBacklinkProvider(), Options.Create(new BacklinkProviderOptions()),
            TimeProvider.System, NullLogger<BacklinkRefreshJobHandler>.Instance);
        var job = new SeoBackgroundJob(SeoJobType.BacklinkRefresh, "k", JsonSerializer.Serialize(new BacklinkRefreshJobPayload(snapshot.Id, project.Id, Host)));

        await handler.HandleAsync(job, CancellationToken.None);

        var stored = await db.BacklinkSnapshots.SingleAsync();
        stored.Availability.Should().Be(BacklinkAvailability.Unavailable);
        stored.HasData.Should().BeFalse();
        stored.TotalBacklinks.Should().BeNull("a failed call measured nothing");
    }

    [Fact]
    public async Task RefreshJob_WithProvider_PersistsLinks_AndHonoursTheObservationCap()
    {
        await using var db = CreateInMemoryDb();
        var project = SeedProject(db, Guid.NewGuid());
        var snapshot = new BacklinkSnapshot(project.Id, Host, "fake");
        db.BacklinkSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var links = new[]
        {
            Link("https://a.example/page", $"https://{Host}/1"),
            Link("https://b.example/page", $"https://{Host}/2"),
            Link("https://c.example/page", $"https://{Host}/3"),
            Link("https://d.example/page", $"https://{Host}/4"),
            Link("https://e.example/page", $"https://{Host}/5"),
        };

        var handler = new BacklinkRefreshJobHandler(
            db, new FakeBacklinkProvider(links), Options.Create(new BacklinkProviderOptions { MaxObservations = 3 }),
            TimeProvider.System, NullLogger<BacklinkRefreshJobHandler>.Instance);
        var job = new SeoBackgroundJob(SeoJobType.BacklinkRefresh, "k", JsonSerializer.Serialize(new BacklinkRefreshJobPayload(snapshot.Id, project.Id, Host)));

        await handler.HandleAsync(job, CancellationToken.None);

        var stored = await db.BacklinkSnapshots.SingleAsync();
        stored.Availability.Should().Be(BacklinkAvailability.Available);
        stored.ObservationCount.Should().Be(3, "the provider returned 5 but the cap is 3");
        stored.ObservationsTruncated.Should().BeTrue();
        stored.TotalBacklinks.Should().Be(5, "provider-reported totals are kept verbatim");
        stored.AuthorityMetricName.Should().Be("fake_authority");
        stored.ProviderPayloadHash.Should().NotBeNullOrWhiteSpace();
        (await db.BacklinkObservations.CountAsync()).Should().Be(3);
    }

    // ---------------------------------------------------------------- service

    [Fact]
    public async Task RequestRefresh_IsIdempotent_WhileOneRefreshIsPending()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullBacklinkProvider(), jobs, out _);

        var first = await service.RequestRefreshAsync(project.Id, owner, CancellationToken.None);
        var second = await service.RequestRefreshAsync(project.Id, owner, CancellationToken.None);

        first.Should().NotBeNull();
        second!.Id.Should().Be(first!.Id);
        (await db.BacklinkSnapshots.CountAsync()).Should().Be(1);
        jobs.Enqueued.Should().HaveCount(1);
    }

    [Fact]
    public async Task RequestRefresh_DeniesUsersWithoutProjectAccess()
    {
        await using var db = CreateInMemoryDb();
        var project = SeedProject(db, Guid.NewGuid());
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullBacklinkProvider(), jobs, out _);

        var result = await service.RequestRefreshAsync(project.Id, Guid.NewGuid(), CancellationToken.None);

        result.Should().BeNull();
        jobs.Enqueued.Should().BeEmpty();
        (await db.BacklinkSnapshots.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Observations_WithoutProviderData_ReturnsEmptyPage_NotAZeroLinkClaim()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullBacklinkProvider(), jobs, out _);
        var snapshot = (await service.RequestRefreshAsync(project.Id, owner, CancellationToken.None))!;

        var page = await service.ObservationsAsync(project.Id, snapshot.Id, owner, 100, CancellationToken.None);

        page.Should().NotBeNull();
        page!.Availability.Should().Be(BacklinkAvailability.NotConfigured);
        page.Observations.Should().BeEmpty();
        page.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Compare_DetectsNewAndLostLinks_AndNullDeltasWhenUnmeasured()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullBacklinkProvider(), jobs, out _);

        var before = await SeedSnapshotAsync(db, project.Id,
            ("https://a.example/1", $"https://{Host}/kept"),
            ("https://b.example/1", $"https://{Host}/dropped"));
        var after = await SeedSnapshotAsync(db, project.Id,
            ("https://a.example/1", $"https://{Host}/kept"),
            ("https://c.example/1", $"https://{Host}/gained"));

        var diff = await service.CompareAsync(project.Id, owner, before.Id, after.Id, CancellationToken.None);

        diff.Should().NotBeNull();
        diff!.NewLinks.Should().HaveCount(1);
        diff.NewLinks[0].SourceUrl.Should().Be("https://c.example/1");
        diff.LostLinks.Should().HaveCount(1);
        diff.LostLinks[0].SourceUrl.Should().Be("https://b.example/1");
        // Both snapshots reported referring-domain counts, so a delta is meaningful.
        diff.ReferringDomainDelta.Should().Be(0);
        diff.TotalBacklinkDelta.Should().Be(0);
    }

    [Fact]
    public async Task Compare_RefusesToDiff_WhenEitherSnapshotHasNoProviderData()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullBacklinkProvider(), jobs, out _);

        var measured = await SeedSnapshotAsync(db, project.Id, ("https://a.example/1", $"https://{Host}/1"));
        var failed = new BacklinkSnapshot(project.Id, Host, "none");
        db.BacklinkSnapshots.Add(failed);
        failed.RecordUnavailable(BacklinkAvailability.Unavailable, "provider outage", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        // Diffing against a snapshot with no data would report the single known
        // link as "lost". The service must refuse rather than invent that fact.
        var diff = await service.CompareAsync(project.Id, owner, measured.Id, failed.Id, CancellationToken.None);

        diff.Should().BeNull();
    }

    [Fact]
    public async Task Compare_LeavesDeltaNull_WhenOneSnapshotDidNotMeasureTheMetric()
    {
        await using var db = CreateInMemoryDb();
        var owner = Guid.NewGuid();
        var project = SeedProject(db, owner);
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullBacklinkProvider(), jobs, out _);

        // Both snapshots have data, but the older one never reported referring
        // domains, so the delta for that metric is unknown rather than zero.
        var withoutCounts = new BacklinkSnapshot(project.Id, Host, "fake");
        db.BacklinkSnapshots.Add(withoutCounts);
        withoutCounts.RecordSuccess(BacklinkAvailability.Available, new BacklinkCounts(TotalBacklinks: 4), 1, false, null, DateTimeOffset.UtcNow);
        db.BacklinkObservations.Add(new BacklinkObservation(withoutCounts.Id, "https://a.example/1", "a.example", $"https://{Host}/1", "anchor", BacklinkRelAttribute.Follow, null, null, false, false));

        var withCounts = await SeedSnapshotAsync(db, project.Id,
            ("https://a.example/1", $"https://{Host}/1"),
            ("https://c.example/1", $"https://{Host}/2"));

        var diff = await service.CompareAsync(project.Id, owner, withoutCounts.Id, withCounts.Id, CancellationToken.None);

        diff.Should().NotBeNull();
        diff!.NewLinks.Should().HaveCount(1);
        diff.ReferringDomainDelta.Should().BeNull("the baseline never measured referring domains");
        diff.TotalBacklinkDelta.Should().Be(-2);
    }

    [Fact]
    public async Task ProviderStatus_ExposesConfigurationAndCapabilities()
    {
        await using var db = CreateInMemoryDb();
        var jobs = new RecordingJobQueue();
        var service = CreateService(db, new NullBacklinkProvider(), jobs, out _);

        var status = await service.ProviderStatusAsync(CancellationToken.None);

        status.IsConfigured.Should().BeFalse();
        status.ProviderName.Should().Be("none");
        status.Capabilities.SupportsAuthorityMetrics.Should().BeFalse();
    }
}
