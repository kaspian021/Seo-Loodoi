using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SeoLoodoi.Application.Crawling;

namespace SeoLoodoi.E2E.Host;

/// <summary>
/// Test-only executable. Starts the unmodified API on real Kestrel/PostgreSQL
/// with actual Identity, authorization, jobs, crawling, extraction and analysis.
/// Only the outbound page transport is a deterministic fixture. Never deploy.
/// </summary>
internal static class E2EHost
{
    public static async Task Main(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? throw new InvalidOperationException("An isolated E2E PostgreSQL connection is required.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        if (string.IsNullOrWhiteSpace(builder.Database)
            || !builder.Database.StartsWith("seoloodoi_e2e_", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to run: database name must start with seoloodoi_e2e_.");
        if (args.Length == 1 && args[0] == "--create-database")
        {
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = "postgres" }.ConnectionString);
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{builder.Database}\"";
            try
            {
                await create.ExecuteNonQueryAsync();
                Console.WriteLine($"Created isolated E2E database {builder.Database}.");
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == "42P04") // already exists
            {
                Console.WriteLine($"Isolated E2E database {builder.Database} already exists.");
                return;
            }
        }
        if (Environment.GetEnvironmentVariable("SEO_LOODOI_E2E") != "1")
            throw new InvalidOperationException("Test host requires explicit SEO_LOODOI_E2E=1 opt-in.");
        Environment.SetEnvironmentVariable("DatabaseProvider", "Postgres");
        Environment.SetEnvironmentVariable("Database__ApplyMigrations", "true");
        Environment.SetEnvironmentVariable("Identity__RequireConfirmedEmail", "false");
        Environment.SetEnvironmentVariable("Email__Enabled", "false");
        Environment.SetEnvironmentVariable("AI__Enabled", "false");

        await using var factory = new BrowserFactory();
        factory.UseKestrel(options => options.ListenAnyIP(5080));
        factory.StartServer();
        Console.WriteLine("E2E API ready on port 5080 (fixture transport; isolated PostgreSQL).");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try { await Task.Delay(Timeout.Infinite, stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}

internal sealed class BrowserFactory : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The SDK-embedded content root attribute resolves relative to the
        // process working directory, which is not stable for a standalone
        // executable. Pin the API project directory from this assembly's
        // location so configuration, migrations and static files always load.
        builder.UseContentRoot(FindApiProjectRoot());
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPageFetcher>();
            services.AddSingleton<IPageFetcher, FixtureTransport>();
        });
    }

    private static string FindApiProjectRoot()
    {
        var directory = new DirectoryInfo(typeof(E2EHost).Assembly.Location);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "SeoLoodoi.Api");
            if (File.Exists(Path.Combine(candidate, "appsettings.json"))) return candidate;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate src/SeoLoodoi.Api relative to the E2E host assembly.");
    }
}

/// <summary>
/// Controlled evidence, never a simulated API response or precomputed score.
/// Requests for any other host fail closed. Production SSRF guards, quotas,
/// credentials and authorization are not replaced by this fixture.
/// </summary>
internal sealed class FixtureTransport : IPageFetcher, IConfigurablePageFetcher
{
    public Task<FetchResult> FetchAsync(Uri uri, int maxResponseBytes, CancellationToken ct) =>
        FetchAsync(uri, maxResponseBytes, "SEO-LoodoiBot/1.0", true, 20, ct);

    public Task<FetchResult> FetchAsync(Uri uri, int maxResponseBytes, string userAgent, bool followRedirects, int timeoutSeconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (uri.Scheme != "https" || uri.Host != "example.com" || !uri.IsDefaultPort)
            throw new InvalidOperationException("E2E transport only serves https://example.com fixtures.");
        var (status, type, body) = uri.AbsolutePath switch
        {
            "/robots.txt" => (200, "text/plain", "User-agent: SEO-LoodoiBot\nAllow: /\n\nUser-agent: GPTBot\nDisallow: /\n\nUser-agent: *\nAllow: /\nSitemap: https://example.com/sitemap.xml"),
            "/sitemap.xml" => (200, "application/xml", "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><url><loc>https://example.com/</loc></url><url><loc>https://example.com/guide</loc></url></urlset>"),
            "/" => (200, "text/html", "<html lang=\"en\"><head><title>Controlled E2E website home page</title></head><body><h1>Home fixture</h1><p>Test-only observed text for the browser workflow.</p><a href=\"/guide\">Guide fixture</a></body></html>"),
            "/guide" => (200, "text/html", "<html lang=\"en\"><head></head><body><h1>Guide fixture</h1><p>This controlled page deliberately has no title so the real rule engine must produce title evidence.</p><a href=\"/\">Home fixture</a></body></html>"),
            _ => (404, "text/plain", "Not present in the E2E fixture")
        };
        var bytes = Encoding.UTF8.GetBytes(body);
        if (bytes.Length > maxResponseBytes) throw new HttpRequestException("Fixture exceeds requested response bound.");
        return Task.FromResult(new FetchResult(uri, uri, status, type,
            new Dictionary<string, string[]> { ["Content-Type"] = [type] }, bytes, TimeSpan.FromMilliseconds(25), []));
    }
}
