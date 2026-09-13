using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace SeoLoodoi.Integration.Tests;

/// <summary>
/// One real PostgreSQL 16 container per collection: migrations are applied once,
/// every test receives its own DbContext over the same database. This executes
/// the raw-SQL queue/frontier paths that the InMemory preview provider never
/// reaches.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
