using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;

namespace SeoLoodoi.Api.Tests;

/// <summary>In-memory sink for every formatted log line the app emits.</summary>
public sealed class CapturedLogs
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();
    public void Add(string message) => _messages.Enqueue(message);
    public IReadOnlyCollection<string> Snapshot() => _messages.ToArray();
}

/// <summary>Funnel all categories into <see cref="CapturedLogs"/> so contract tests can assert on observability output.</summary>
public sealed class CapturingLoggerProvider(CapturedLogs sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SinkLogger(sink);
    public void Dispose() { }

    private sealed class SinkLogger(CapturedLogs sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.Add(formatter(state, exception));
    }
}

/// <summary>
/// Boots the real API pipeline (Minimal API + Identity + durable workers) on the
/// InMemory preview provider. Postgres-only raw-SQL paths are exercised by the
/// Postgres suite instead; this harness pins contract/authorization behavior.
/// </summary>
public sealed class SeoLoodoiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseProvider"] = "InMemory",
                ["Identity:RequireConfirmedEmail"] = "false",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<CapturedLogs>();
            services.AddSingleton<ILoggerProvider, CapturingLoggerProvider>();
            // Test classes share one fixture user; the Starter quota of 3 projects
            // is exhausted mid-suite and surfaces as a legitimate-looking 429.
            // Lift every quota so contract tests measure contracts, not plan limits.
            // Since Phase 8 the authoritative limits come from tenant entitlements
            // (IEntitlementService) and these static options are only the fallback
            // used when no entitlement record exists, so the fixture also upgrades
            // both users to Enterprise in <see cref="ApiFixture.InitializeAsync"/>.
            services.Configure<QuotaOptions>(o =>
            {
                o.MaxProjects = 100;
                o.PagesPerMonth = 100_000;
                o.MaxKeywords = 1_000;
                o.MaxCompetitors = 100;
            });
        });
    }
}

public sealed record AuthenticatedUser(HttpClient Client, string Email, Guid UserId);

/// <summary>
/// Shared across the collection so the 10/min authentication rate limiter is only
/// hit twice (two registers + two logins) instead of once per test class.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly SeoLoodoiFactory _factory = new();
    public AuthenticatedUser Owner { get; private set; } = default!;
    public AuthenticatedUser Other { get; private set; } = default!;
    /// <summary>Unauthenticated clients for endpoints that take no bearer token (e.g. signed billing webhooks).</summary>
    public SeoLoodoiFactory Factory => _factory;
    public CapturedLogs Logs => _factory.Services.GetRequiredService<CapturedLogs>();

    public async Task InitializeAsync()
    {
        Owner = await CreateUserAsync("owner");
        Other = await CreateUserAsync("other");
        await GrantEnterpriseEntitlementAsync(Owner.UserId);
        await GrantEnterpriseEntitlementAsync(Other.UserId);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// Moves a fixture tenant onto the Enterprise plan the same way the real
    /// billing authority would. Without this the tenant is seeded on Starter
    /// (3 projects) and the fourth <c>TestProject.CreateAsync</c> call across the
    /// shared collection fails with a 429 that looks like a contract violation
    /// but is really an exhausted plan quota.
    /// </summary>
    private async Task GrantEnterpriseEntitlementAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plan = PlanCatalog.GetPlan(PlanCatalog.Enterprise);
        var now = DateTimeOffset.UtcNow;

        var entitlement = await db.TenantEntitlements.SingleOrDefaultAsync(x => x.UserId == userId);
        if (entitlement is null)
        {
            entitlement = new TenantEntitlement(userId, $"loodoi_acc_{userId:N}");
            db.TenantEntitlements.Add(entitlement);
        }

        entitlement.UpdateSubscription(
            plan: plan.PlanId,
            status: SubscriptionStatus.Active,
            periodStart: now,
            periodEnd: now.AddMonths(1),
            maxProjects: plan.MaxProjects,
            maxPagesPerMonth: plan.MaxPagesPerMonth,
            maxKeywords: plan.MaxKeywords,
            maxCompetitors: plan.MaxCompetitors,
            maxTeamMembers: plan.MaxTeamMembers,
            maxAiCreditsPerMonth: plan.MaxAiCreditsPerMonth,
            retentionDays: plan.RetentionDays,
            featuresJson: JsonSerializer.Serialize(plan.Features),
            now: now);

        await db.SaveChangesAsync();
    }

    private async Task<AuthenticatedUser> CreateUserAsync(string role)
    {
        var anonymous = _factory.CreateClient();
        var email = $"{role}-{Guid.NewGuid():N}@test.loodoi.example";
        var register = await anonymous.PostAsJsonAsync("/api/account/register", new
        {
            fullName = $"Test {role}",
            email,
            companyName = (string?)null,
            password = "Secure@2026x",
            confirmPassword = "Secure@2026x",
            acceptTerms = true,
            preferredLanguage = "fa",
        });
        register.EnsureSuccessStatusCode();
        var userId = (await register.Content.ReadFromJsonAsync<UserEnvelope>())!.Id;

        var login = await anonymous.PostAsJsonAsync("/api/auth/login?useCookies=false", new { email, password = "Secure@2026x" });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<TokenEnvelope>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return new AuthenticatedUser(client, email, userId);
    }

    private sealed record UserEnvelope(Guid Id);
    private sealed record TokenEnvelope(string AccessToken);
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
}

public static class TestProject
{
    /// <summary>Creates a project owned by <paramref name="user"/>; base URL must pass the real SSRF guard.</summary>
    public static async Task<Guid> CreateAsync(AuthenticatedUser user, string name, string baseUrl = "https://example.com")
    {
        var response = await user.Client.PostAsJsonAsync("/api/seo/projects", new { name, baseUrl });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }
}
