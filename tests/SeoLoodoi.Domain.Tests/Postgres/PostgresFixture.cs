using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Domain.Tests.Postgres;

/// <summary>
/// Shared fixture for real-Postgres integration tests. Reads the standard
/// ConnectionStrings__Postgres (same key as the app and the CI workflow).
/// Tests are skipped — not failed — when the variable is absent, so the suite
/// stays green in environments without a database (e.g. this sandbox, which
/// cannot install the .NET SDK at all).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new Xunit.SkipException("Postgres integration tests skipped: ConnectionStrings__Postgres is not set.");
        // Apply the repo's own migrations (no-op when the schema is current).
        // The role must own the database, as in CI (seo_loodoi owns seo_loodoi).
        using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new AppDbContext(options);
    }
}

/// <summary>
/// All Postgres test classes share one collection so the fixture (and its
/// MigrateAsync) runs exactly once — concurrent migration DDL would race.
/// </summary>
[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
