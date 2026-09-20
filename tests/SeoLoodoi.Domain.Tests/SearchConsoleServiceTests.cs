using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.SearchConsole;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;
using SeoLoodoi.Infrastructure.SearchConsole;

namespace SeoLoodoi.Domain.Tests;

/// <summary>Google wire-contract fixtures, not a claim of live Google verification.</summary>
public sealed class SearchConsoleServiceTests
{
    private sealed class Transport(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Harness(string json) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
        public Guid Owner { get; } = Guid.NewGuid();
        public SeoProject Project { get; private set; } = null!;
        public Transport Handler { get; } = new(json);
        public HttpClient Client { get; private set; } = null!;
        public SearchConsoleService Service { get; private set; } = null!;
        public async Task InitializeAsync(bool connected = false)
        {
            Project = new SeoProject(Owner, "Search Console contract", new Uri("https://example.com"));
            Db.SeoProjects.Add(Project);
            if (connected)
            {
                var protector = Protection.CreateProtector("seo-loodoi/search-console/tokens/v1");
                Db.ExternalConnections.Add(new ExternalConnection(Project.Id, ExternalProvider.GoogleSearchConsole,
                    protector.Protect("fixture-access"), protector.Protect("fixture-refresh"), DateTimeOffset.UtcNow.AddHours(1)));
            }
            await Db.SaveChangesAsync();
            Client = new HttpClient(Handler);
            var access = new ProjectAccessService(Db);
            Service = new SearchConsoleService(Db, access, new QuotaService(Db, Options.Create(new QuotaOptions())), Client,
                Options.Create(new SearchConsoleOptions { ClientId = "fixture-client", ClientSecret = "fixture-secret", RedirectUri = "https://app.example/callback" }),
                Protection, new AuditLogService(Db, access));
        }
        public async ValueTask DisposeAsync() { Client?.Dispose(); await Db.DisposeAsync(); }
    }
    private static readonly SearchConsoleSyncRequest Range = new(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2));

    [Fact]
    public async Task OAuth_GoogleSnakeCaseTokenResponse_IsAcceptedAndEncrypted()
    {
        await using var h = new Harness("""{"access_token":"fixture-access","refresh_token":"fixture-refresh","expires_in":3600,"token_type":"Bearer"}""");
        await h.InitializeAsync();
        var url = await h.Service.GetAuthorizationUrlAsync(h.Project.Id, h.Owner, default);
        Assert.NotNull(url);
        var state = Uri.UnescapeDataString(url.Split("&state=", StringSplitOptions.None)[1]);
        Assert.True(await h.Service.CompleteAuthorizationAsync(state, "fixture-code", default));
        var connection = Assert.Single(await h.Db.ExternalConnections.ToListAsync());
        Assert.NotEqual("fixture-access", connection.EncryptedAccessToken);
        Assert.NotEqual("fixture-refresh", connection.EncryptedRefreshToken);
        var protector = h.Protection.CreateProtector("seo-loodoi/search-console/tokens/v1");
        Assert.Equal("fixture-access", protector.Unprotect(connection.EncryptedAccessToken));
        Assert.Equal("fixture-refresh", protector.Unprotect(connection.EncryptedRefreshToken!));
        Assert.Equal(1, h.Handler.Calls);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("")]
    public async Task OAuth_InvalidState_NeverCallsProvider(string state)
    {
        await using var h = new Harness("{}"); await h.InitializeAsync();
        Assert.False(await h.Service.CompleteAuthorizationAsync(state, "code", default));
        Assert.Equal(0, h.Handler.Calls);
        Assert.Empty(await h.Db.ExternalConnections.ToListAsync());
    }

    [Fact]
    public async Task OAuth_ExpiredState_NeverCallsProvider()
    {
        await using var h = new Harness("{}"); await h.InitializeAsync();
        var state = h.Protection.CreateProtector("seo-loodoi/search-console/state/v1")
            .Protect($"{h.Project.Id:N}|{h.Owner:N}|{DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeSeconds()}");
        Assert.False(await h.Service.CompleteAuthorizationAsync(state, "code", default));
        Assert.Equal(0, h.Handler.Calls);
    }

    [Fact]
    public async Task ForeignTenant_CannotConnectReadOrSync()
    {
        await using var h = new Harness("{}"); await h.InitializeAsync(true);
        var other = Guid.NewGuid();
        Assert.Null(await h.Service.GetAuthorizationUrlAsync(h.Project.Id, other, default));
        Assert.Null(await h.Service.StatusAsync(h.Project.Id, other, default));
        Assert.Null(await h.Service.SyncAsync(h.Project.Id, other, Range, default));
        Assert.Equal(0, h.Handler.Calls);
    }

    [Fact]
    public async Task Sync_ObservedRowsPersistWithSource_AndReplayDoesNotDuplicate()
    {
        const string rows = """{"rows":[{"keys":["سئو","https://example.com/guide","2026-09-01"],"clicks":4,"impressions":100,"ctr":0.04,"position":8.5}]}""";
        await using var h = new Harness(rows); await h.InitializeAsync(true);
        var first = await h.Service.SyncAsync(h.Project.Id, h.Owner, Range, default);
        var second = await h.Service.SyncAsync(h.Project.Id, h.Owner, Range, default);
        Assert.NotNull(first); Assert.NotNull(second);
        Assert.Equal(1, first.RowsReceived);
        Assert.Equal(1, first.KeywordsUpdated);
        Assert.Equal(0, second.KeywordsUpdated);
        Assert.False(first.Partial);
        Assert.Equal("Bearer fixture-access", h.Handler.Authorization);
        Assert.Single(await h.Db.Keywords.ToListAsync());
        var metric = Assert.Single(await h.Db.KeywordMetrics.ToListAsync());
        Assert.Equal(8.5m, metric.AveragePosition);
        Assert.Equal(100, metric.Impressions);
        Assert.Equal("search-console", metric.Source);
    }

    [Fact]
    public async Task Sync_EmptyResponse_DoesNotCreateZeroMetrics()
    {
        await using var h = new Harness("{\"rows\":[]}"); await h.InitializeAsync(true);
        var result = await h.Service.SyncAsync(h.Project.Id, h.Owner, Range, default);
        Assert.NotNull(result);
        Assert.Equal(0, result.RowsReceived);
        Assert.Empty(await h.Db.Keywords.ToListAsync());
        Assert.Empty(await h.Db.KeywordMetrics.ToListAsync());
    }

    [Fact]
    public async Task Sync_InvalidDateRange_DoesNotCallProvider()
    {
        await using var h = new Harness("{}"); await h.InitializeAsync(true);
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.SyncAsync(h.Project.Id, h.Owner,
            new SearchConsoleSyncRequest(Range.EndDate, Range.StartDate), default));
        Assert.Equal(0, h.Handler.Calls);
    }
}
