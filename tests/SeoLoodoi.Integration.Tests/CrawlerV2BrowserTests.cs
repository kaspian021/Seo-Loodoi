using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Crawling.Rendering;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Crawling.Rendering;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Integration.Tests;

/// <summary>
/// Local fixture site on loopback (HttpListener). Only its exact origin is allow-listed.
/// Every other destination goes through the production <see cref="OutboundUrlGuard"/>,
/// so SSRF behaviour matches production.
/// </summary>
public sealed class FixtureSite : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, Func<HttpListenerContext, Task>> _routes = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentBag<string> Hits { get; } = [];
    private int _inFlight; public int MaxInFlight;
    public Uri Origin { get; }

    public FixtureSite()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        Origin = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(Origin.AbsoluteUri);
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public FixtureSite Html(string path, string html, int delayMs = 0) => Route(path, async ctx =>
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8"; ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    });
    public FixtureSite Redirect(string path, string location) => Route(path, ctx => { ctx.Response.StatusCode = 302; ctx.Response.RedirectLocation = location; return Task.CompletedTask; });
    public FixtureSite Route(string path, Func<HttpListenerContext, Task> handler) { _routes[path] = handler; return this; }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                var path = ctx.Request.Url!.AbsolutePath;
                Hits.Add(path);
                var now = Interlocked.Increment(ref _inFlight);
                int seen; do { seen = MaxInFlight; } while (now > seen && Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen);
                try
                {
                    if (_routes.TryGetValue(path, out var handler)) await handler(ctx); else ctx.Response.StatusCode = 404;
                }
                catch { /* client went away */ }
                finally { Interlocked.Decrement(ref _inFlight); try { ctx.Response.Close(); } catch { } }
            });
        }
    }

    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
}

/// <summary>Allows exactly one fixture origin; everything else is judged by the real production guard.</summary>
public sealed class FixtureScopedGuard(Uri allowedOrigin) : IOutboundUrlGuard
{
    private readonly OutboundUrlGuard _real = new();
    public Task ValidateAsync(Uri uri, CancellationToken ct) =>
        uri.Scheme == allowedOrigin.Scheme && uri.Host == allowedOrigin.Host && uri.Port == allowedOrigin.Port ? Task.CompletedTask : _real.ValidateAsync(uri, ct);
}

public sealed class ChromiumFixture : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        // Playwright's own installer guarantees the browser revision matches the driver.
        var exit = Microsoft.Playwright.Program.Main(["install", "--with-deps", "chromium"]);
        if (exit != 0) exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exit != 0) throw new InvalidOperationException($"Playwright Chromium install failed with exit code {exit}. Browser rendering tests cannot run.");
        return Task.CompletedTask;
    }
    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("chromium")]
public sealed class ChromiumCollection : ICollectionFixture<ChromiumFixture> { }

/// <summary>
/// Crawler v2 E tests against real headless Chromium and fixture sites. Covered cases:
/// <list type="bullet">
/// <item>page types: static, SPA, dynamic title/canonical/links, JS-only content, malformed markup;</item>
/// <item>navigation: client redirect;</item>
/// <item>failures: timeout, crash recovery, oversized DOM;</item>
/// <item>SSRF: blocked sub-resources, redirect to a private IP;</item>
/// <item>render concurrency cap.</item>
/// </list>
/// </summary>
[Collection("chromium")]
public sealed class CrawlerV2BrowserTests
{
    private static PlaywrightPageRenderer Renderer(FixtureSite site, RenderingOptions? options = null)
    {
        var o = options ?? new RenderingOptions();
        o.Enabled = true;
        var opts = Options.Create(o);
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        return new PlaywrightPageRenderer(opts, new RenderGate(opts, TimeProvider.System), new FixtureScopedGuard(site.Origin), () => client,
            new HostRequestCoordinator(TimeProvider.System, 8, TimeSpan.Zero), NullLogger<PlaywrightPageRenderer>.Instance);
    }

    private static RenderRequest Request(FixtureSite site, string path, int timeoutSeconds = 20, int maxDom = 5_000_000, ViewportProfile? viewport = null) =>
        new(new Uri(site.Origin, path), "SEO-LoodoiBot/2.0-test", viewport ?? ViewportProfile.Desktop, timeoutSeconds, maxDom, Guid.NewGuid(), Guid.NewGuid());

    private static async Task<RenderDiff> Diff(FixtureSite site, string path, string rawHtml, RenderResult rendered)
    {
        var extractor = new HtmlExtractor();
        var uri = new Uri(site.Origin, path);
        return RawVsRenderedComparer.Compare(await extractor.ExtractAsync(rawHtml, uri, default), await extractor.ExtractAsync(rendered.Html!, rendered.FinalUrl ?? uri, default));
    }

    private const string StaticHtml = "<!doctype html><html lang='en'><head><title>Static page</title><link rel='canonical' href='/static'></head><body><h1>Static</h1><p>Plain server rendered text.</p><a href='/a'>A</a></body></html>";
    private const string SpaHtml = """
        <!doctype html><html><head><title>Loading</title><link rel="canonical" href="/wrong"></head><body><div id="root"></div>
        <script>
          document.title = 'Real SPA Title';
          document.querySelector('link[rel=canonical]').setAttribute('href', '/spa');
          const root = document.getElementById('root');
          root.innerHTML = '<h1>SPA Heading</h1><p>' + 'hydrated '.repeat(200) + '</p><a href="/js-link">JS link</a>';
        </script></body></html>
        """;

    [Fact]
    public async Task StaticPage_RendersWithoutCriticalDifferences()
    {
        using var site = new FixtureSite().Html("/static", StaticHtml);
        await using var renderer = Renderer(site);
        var result = await renderer.RenderAsync(Request(site, "/static"), default);
        result.Success.Should().BeTrue(result.FailureMessage);
        (await Diff(site, "/static", StaticHtml, result)).CriticalCount.Should().Be(0);
    }

    [Fact]
    public async Task Spa_DynamicTitleCanonicalHeadingsLinksAndJsOnlyContent_AreDetected()
    {
        using var site = new FixtureSite().Html("/spa", SpaHtml);
        await using var renderer = Renderer(site);
        var result = await renderer.RenderAsync(Request(site, "/spa"), default);
        result.Success.Should().BeTrue(result.FailureMessage);
        var diff = await Diff(site, "/spa", SpaHtml, result);
        diff.Get("title")!.Rendered.Should().Be("Real SPA Title");
        diff.Get("canonical")!.Rendered.Should().EndWith("/spa");
        diff.Get("h1")!.OnlyInRendered.Should().Equal("SPA Heading");
        diff.Get("internalLinks")!.OnlyInRendered.Should().ContainSingle(x => x.EndsWith("/js-link"));
        diff.Get("textWordCount")!.Changed.Should().BeTrue();
    }

    [Fact]
    public async Task ExternalScript_IsFetchedThroughTheServer_AndRecordedAsResourceMetadata()
    {
        using var site = new FixtureSite()
            .Html("/ext", "<html><head><title>x</title><script defer src='/app.js'></script></head><body><div id='root'></div></body></html>")
            .Route("/app.js", async ctx => { ctx.Response.ContentType = "application/javascript"; await ctx.Response.OutputStream.WriteAsync("document.getElementById('root').innerHTML='<h1>From external script</h1>';"u8.ToArray()); });
        await using var renderer = Renderer(site);
        var result = await renderer.RenderAsync(Request(site, "/ext"), default);
        result.Success.Should().BeTrue(result.FailureMessage);
        var evidence = string.Join("; ", result.Resources.Select(r => $"{r.ResourceType} {r.Url} status={r.Status} blocked={r.Blocked} reason={r.BlockReason}"));
        site.Hits.Should().Contain("/app.js", "the server-side fetcher must request the script from the origin. Resources: " + evidence);
        result.Html.Should().Contain("From external script", "the fulfilled script must execute in Chromium. Resources: " + evidence);
        result.JsErrorCount.Should().Be(0, "the fulfilled script must run cleanly");
        result.Resources.Should().Contain(r => r.Url.EndsWith("/app.js") && r.ResourceType == "script" && r.Status == 200 && r.SizeBytes > 0 && !r.Blocked);
    }

    [Fact]
    public async Task ClientRedirect_ChangesFinalUrl()
    {
        using var site = new FixtureSite()
            .Html("/old", "<html><head><script>location.replace('/new')</script></head><body></body></html>")
            .Html("/new", "<html><head><title>New</title></head><body><h1>New</h1></body></html>");
        await using var renderer = Renderer(site);
        var result = await renderer.RenderAsync(Request(site, "/old"), default);
        result.Success.Should().BeTrue(result.FailureMessage);
        result.FinalUrl!.AbsolutePath.Should().Be("/new");
    }

    [Fact]
    public async Task InfiniteScript_TimesOut_WithinBudget()
    {
        using var site = new FixtureSite().Html("/hang", "<html><body><script>while(true){}</script></body></html>");
        await using var renderer = Renderer(site);
        var started = DateTime.UtcNow;
        var result = await renderer.RenderAsync(Request(site, "/hang", timeoutSeconds: 3), default);
        result.Success.Should().BeFalse();
        result.Failure.Should().Be(RenderFailureKind.Timeout);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task RendererCrash_IsRecovered_NextRenderSucceeds()
    {
        using var site = new FixtureSite()
            .Html("/oom", "<html><body><script>const a=[];while(true){a.push(new Array(1e6).fill(Math.random()));}</script></body></html>")
            .Html("/static", StaticHtml);
        await using var renderer = Renderer(site, new RenderingOptions { JsHeapMegabytes = 64 });
        var crash = await renderer.RenderAsync(Request(site, "/oom", timeoutSeconds: 20), default);
        crash.Success.Should().BeFalse();
        crash.Failure.Should().BeOneOf(RenderFailureKind.Crash, RenderFailureKind.Timeout, RenderFailureKind.NavigationFailed);
        var next = await renderer.RenderAsync(Request(site, "/static"), default);
        next.Success.Should().BeTrue("the renderer must recover after a page crash: " + next.FailureMessage);
    }

    [Fact]
    public async Task OversizedDom_IsRejected_BeforeCopyingItOut()
    {
        using var site = new FixtureSite().Html("/big", "<html><body><script>document.body.innerHTML='<p>'+'x'.repeat(400000)+'</p>';</script></body></html>");
        await using var renderer = Renderer(site);
        var result = await renderer.RenderAsync(Request(site, "/big", maxDom: 100_000), default);
        result.Failure.Should().Be(RenderFailureKind.Oversized);
        result.Html.Should().BeNull();
    }

    [Fact]
    public async Task MalformedHtml_StillRenders()
    {
        const string html = "<html><head><title>Broken<body><h1>Unclosed <b>tags<p>text <a href='/x'>link";
        using var site = new FixtureSite().Html("/bad", html);
        await using var renderer = Renderer(site);
        var result = await renderer.RenderAsync(Request(site, "/bad"), default);
        result.Success.Should().BeTrue(result.FailureMessage);
    }

    [Fact]
    public async Task SubresourcesToPrivateNetworks_AreBlockedByTheSsrfGuard_AndNeverReachTheTarget()
    {
        using var secret = new FixtureSite().Html("/secret", "top secret");   // another loopback origin: not allow-listed
        using var site = new FixtureSite().Html("/ssrf", $$"""
            <html><head><title>ssrf</title>
            <script src="{{secret.Origin}}secret"></script>
            <script src="http://169.254.169.254/latest/meta-data/"></script>
            <script>fetch('{{secret.Origin}}secret').catch(()=>{}); fetch('http://10.0.0.1/').catch(()=>{});</script>
            </head><body><h1>ok</h1></body></html>
            """);
        await using var renderer = Renderer(site);
        var result = await renderer.RenderAsync(Request(site, "/ssrf"), default);
        result.Success.Should().BeTrue(result.FailureMessage);
        secret.Hits.Should().BeEmpty("the SSRF guard must stop requests before any connection is made");
        result.Resources.Where(r => r.Blocked && r.BlockReason == "ssrf-guard").Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task RedirectToPrivateIp_IsRevalidatedPerHop_AndBlocked()
    {
        using var site = new FixtureSite().Redirect("/go", "http://10.1.2.3/admin");
        await using var renderer = Renderer(site);
        var result = await renderer.RenderAsync(Request(site, "/go"), default);
        result.Success.Should().BeFalse("a redirect into a private network must never be rendered");
        result.Failure.Should().BeOneOf(RenderFailureKind.NavigationBlocked, RenderFailureKind.NavigationFailed);
        result.Html.Should().BeNull();
    }

    [Fact]
    public async Task GlobalRenderConcurrency_IsEnforced()
    {
        using var site = new FixtureSite().Html("/slow", StaticHtml, delayMs: 700);
        await using var renderer = Renderer(site, new RenderingOptions { MaxConcurrentRenders = 1, MaxConcurrentRendersPerProject = 1, MaxQueuedRenders = 8 });
        var project = Guid.NewGuid();
        var tasks = Enumerable.Range(0, 3).Select(_ => renderer.RenderAsync(Request(site, "/slow") with { ProjectId = project }, default)).ToArray();
        var results = await Task.WhenAll(tasks);
        results.Should().OnlyContain(r => r.Success);
        site.MaxInFlight.Should().Be(1, "only one render may run at a time with MaxConcurrentRenders=1");
    }

    [Fact]
    public async Task MobileViewport_IsApplied()
    {
        using var site = new FixtureSite().Html("/vp", "<html><head><meta name='viewport' content='width=device-width, initial-scale=1'></head><body><script>document.body.innerHTML='<h1>'+window.innerWidth+'</h1>'</script></body></html>");
        await using var renderer = Renderer(site);
        var result = await renderer.RenderAsync(Request(site, "/vp", viewport: ViewportProfile.Mobile), default);
        result.Success.Should().BeTrue(result.FailureMessage);
        result.Html.Should().Contain($"<h1>{ViewportProfile.Mobile.Width}</h1>");
    }
}
