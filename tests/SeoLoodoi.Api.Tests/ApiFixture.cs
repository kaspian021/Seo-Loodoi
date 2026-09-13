using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        // Rate limiting is production behavior, but the fixed windows make the
        // suite wall-clock dependent and flaky; lift the limits for tests only.
        builder.ConfigureServices(services =>
        {
            // Rate limiting is production behavior, but the fixed windows make the
            // suite wall-clock dependent and flaky; lift the limits for tests only.
            services.Configure<RateLimiterOptions>(options =>
            {
                options.AddPolicy("api", _ => RateLimitPartition.GetFixedWindowLimiter("api-tests", _ => new FixedWindowRateLimiterOptions { PermitLimit = 100_000, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
                options.AddPolicy("auth", _ => RateLimitPartition.GetFixedWindowLimiter("auth-tests", _ => new FixedWindowRateLimiterOptions { PermitLimit = 100_000, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });
            services.AddSingleton<CapturedLogs>();
            services.AddSingleton<ILoggerProvider, CapturingLoggerProvider>();
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
    public CapturedLogs Logs => _factory.Services.GetRequiredService<CapturedLogs>();

    public async Task InitializeAsync()
    {
        Owner = await CreateUserAsync("owner");
        Other = await CreateUserAsync("other");
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

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
