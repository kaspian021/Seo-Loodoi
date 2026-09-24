using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using SeoLoodoi.Application.Crawling.Rendering;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Infrastructure.Crawling.Rendering;

/// <summary>
/// Headless Chromium renderer (Crawler v2, D2).
/// <para><b>Network isolation.</b> Chromium never talks to the network itself. Two things enforce this:</para>
/// <list type="bullet">
/// <item>It is launched with <c>--host-resolver-rules=MAP * ~NOTFOUND</c>, so any request that escaped interception fails to resolve.</item>
/// <item>Every request is intercepted and fulfilled by the server through <see cref="IOutboundUrlGuard"/>
/// plus the IP-pinned <see cref="SsrfPinnedHandler"/> client. Each redirect the page follows re-enters the route, so it is re-validated.</item>
/// </list>
/// <para>The same protections as the raw fetcher therefore apply: private IPs, DNS rebinding, schemes, credentials and size caps.
/// WebSockets (not routable, and unresolvable under the resolver rule), service workers, downloads and non-GET requests are refused.</para>
/// <para><b>Lifecycle.</b></para>
/// <list type="bullet">
/// <item>One browser per process, launched lazily.</item>
/// <item>One fresh <c>BrowserContext</c> per render, so cookies, storage and cache are never shared between pages or tenants.</item>
/// <item>The browser is recycled after N renders, and relaunched after a crash or disconnect.</item>
/// <item>Admission goes through <see cref="RenderGate"/>, which handles global and per-project caps, a bounded queue and a circuit breaker.</item>
/// </list>
/// </summary>
public sealed class PlaywrightPageRenderer : IPageRenderer, IAsyncDisposable
{
    public const string HttpClientName = "RenderSubresources";
    private static readonly HashSet<string> SkippedResourceTypes = new(StringComparer.OrdinalIgnoreCase) { "image", "media", "font", "beacon", "ping", "manifest", "texttrack", "eventsource", "websocket", "other" };
    private static readonly string[] ForwardedRequestHeaders = ["accept", "accept-language", "content-type", "cookie", "referer"];
    private static readonly HashSet<string> DroppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase) { "content-encoding", "content-length", "transfer-encoding", "connection", "keep-alive", "strict-transport-security", "alt-svc" };

    private readonly RenderingOptions _options;
    private readonly RenderGate _gate;
    private readonly IOutboundUrlGuard _guard;
    private readonly Func<HttpClient> _client;
    private readonly IHostRequestCoordinator _coordinator;
    private readonly ILogger<PlaywrightPageRenderer> _logger;
    private readonly SemaphoreSlim _launch = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private int _rendersOnBrowser;
    private int _activeRenders;

    public PlaywrightPageRenderer(IOptions<RenderingOptions> options, RenderGate gate, IOutboundUrlGuard guard, IHttpClientFactory clients, IHostRequestCoordinator coordinator, ILogger<PlaywrightPageRenderer> logger)
        : this(options, gate, guard, () => clients.CreateClient(HttpClientName), coordinator, logger) { }

    /// <summary>Test seam: supply the sub-resource client directly (fixture servers on loopback need a non-pinned client and a scoped guard).</summary>
    public PlaywrightPageRenderer(IOptions<RenderingOptions> options, RenderGate gate, IOutboundUrlGuard guard, Func<HttpClient> client, IHostRequestCoordinator coordinator, ILogger<PlaywrightPageRenderer> logger)
    {
        _options = options.Value; _options.Validate();
        _gate = gate; _guard = guard; _client = client; _coordinator = coordinator; _logger = logger;
    }

    public bool IsAvailable => _options.Enabled;

    public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken ct)
    {
        if (!_options.Enabled) return RenderResult.Failed(RenderFailureKind.NavigationFailed, "Rendering is disabled in this deployment.", TimeSpan.Zero);
        await using var slot = await _gate.EnterAsync(request.ProjectId, ct); // throws RenderCapacityException on backpressure / open circuit
        var timer = Stopwatch.StartNew();
        RenderResult result;
        try { result = await RenderCoreAsync(request, timer, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or ObjectDisposedException)
        {
            // Browser-level failure (crash, disconnect, launch failure): recycle, report, fall back.
            _logger.LogWarning(ex, "Renderer crashed while rendering; browser will be relaunched");
            await ResetBrowserAsync();
            result = RenderResult.Failed(RenderFailureKind.Crash, $"Renderer failure: {ex.GetType().Name}", timer.Elapsed);
        }
        _gate.Report(result.Failure);
        CrawlerMetrics.Renders.Add(1, new KeyValuePair<string, object?>("outcome", result.Success ? "rendered" : result.Failure.ToString().ToLowerInvariant()));
        CrawlerMetrics.RenderDuration.Record(timer.Elapsed.TotalMilliseconds);
        return result;
    }

    private async Task<RenderResult> RenderCoreAsync(RenderRequest request, Stopwatch timer, CancellationToken ct)
    {
        var browser = await GetBrowserAsync(ct);
        Interlocked.Increment(ref _activeRenders);
        var budget = new RenderBudget(_options.MaxSubresourceRequests, _options.MaxSubresourceBytesPerRender);
        var resources = new List<RenderedResource>();
        var jsErrors = 0; var crashed = false;
        IBrowserContext? context = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 2, 120)));
        try
        {
            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = request.Viewport.Width, Height = request.Viewport.Height },
                DeviceScaleFactor = (float)request.Viewport.DeviceScaleFactor,
                IsMobile = request.Viewport.IsMobile,
                HasTouch = request.Viewport.IsMobile,
                UserAgent = request.UserAgent + request.Viewport.UserAgentSuffix,
                JavaScriptEnabled = true,
                AcceptDownloads = false,
                ServiceWorkers = ServiceWorkerPolicy.Block,
                IgnoreHTTPSErrors = false,
            });
            await context.RouteAsync("**/*", route => HandleRouteAsync(route, request, budget, resources, timeout.Token));
            var page = await context.NewPageAsync();
            page.PageError += (_, _) => Interlocked.Increment(ref jsErrors);
            page.Crash += (_, _) => crashed = true;
            // Browser-side failures (Chromium refused or aborted a request after or outside our route)
            // are recorded as resource evidence, so a missing script is never silent.
            page.RequestFailed += (_, failed) =>
            {
                lock (resources)
                {
                    if (resources.Count < 500 && !resources.Any(r => r.Blocked && r.Url == Trunc(failed.Url)))
                        resources.Add(new RenderedResource(Trunc(failed.Url), failed.ResourceType, null, null, true, "browser-failed:" + Trunc(failed.Failure ?? "unknown")));
                }
            };
            page.SetDefaultTimeout((float)Math.Max(1000, (request.TimeoutSeconds * 1000) - timer.ElapsedMilliseconds));

            try
            {
                try
                {
                    await page.GotoAsync(request.Url.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = request.TimeoutSeconds * 1000f }).WaitAsync(timeout.Token);
                }
                catch (PlaywrightException ex) when (!crashed && ex.Message.Contains("interrupted by another navigation", StringComparison.OrdinalIgnoreCase))
                {
                    // Client-side redirect (location.replace, meta refresh) during load: follow it
                    // and render the destination; the final-URL change becomes evidence.
                    var left = Math.Max(1000, (request.TimeoutSeconds * 1000) - (int)timer.ElapsedMilliseconds);
                    await page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions { Timeout = left }).WaitAsync(timeout.Token);
                }
            }
            catch (System.TimeoutException) { return RenderResult.Failed(RenderFailureKind.Timeout, "Navigation timed out.", timer.Elapsed, Snapshot(resources)); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return RenderResult.Failed(RenderFailureKind.Timeout, "Render timed out.", timer.Elapsed, Snapshot(resources)); }
            catch (PlaywrightException ex) when (!crashed)
            {
                var blocked = Snapshot(resources).FirstOrDefault(x => x.ResourceType == "document" && x.Blocked);
                return blocked is not null
                    ? RenderResult.Failed(RenderFailureKind.NavigationBlocked, $"Navigation blocked: {blocked.BlockReason}", timer.Elapsed, Snapshot(resources))
                    : RenderResult.Failed(RenderFailureKind.NavigationFailed, Trunc(ex.Message.Split('\n')[0]), timer.Elapsed, Snapshot(resources));
            }
            if (crashed) return RenderResult.Failed(RenderFailureKind.Crash, "Page crashed during navigation.", timer.Elapsed, Snapshot(resources));

            // Give client-side rendering a bounded chance to settle; never wait past the budget.
            var remaining = (request.TimeoutSeconds * 1000) - (int)timer.ElapsedMilliseconds;
            var idleWait = Math.Min(_options.NetworkIdleWaitMilliseconds, remaining - 500);
            if (idleWait > 0)
            {
                try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = idleWait }).WaitAsync(timeout.Token); }
                catch (System.TimeoutException) { /* long-polling pages never go idle; the DOM so far is the evidence */ }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return RenderResult.Failed(RenderFailureKind.Timeout, "Render timed out while settling.", timer.Elapsed, Snapshot(resources)); }
            }
            if (crashed) return RenderResult.Failed(RenderFailureKind.Crash, "Page crashed while rendering.", timer.Elapsed, Snapshot(resources));

            // Memory protection: measure the DOM inside the page before copying it out.
            var domLength = await page.EvaluateAsync<double>("() => document.documentElement ? document.documentElement.outerHTML.length : 0").WaitAsync(timeout.Token);
            if (domLength > request.MaxDomBytes) return RenderResult.Failed(RenderFailureKind.Oversized, $"Rendered DOM exceeds {request.MaxDomBytes} characters.", timer.Elapsed, Snapshot(resources));
            var html = await page.ContentAsync().WaitAsync(timeout.Token);
            if (html.Length > request.MaxDomBytes) return RenderResult.Failed(RenderFailureKind.Oversized, $"Rendered DOM exceeds {request.MaxDomBytes} characters.", timer.Elapsed, Snapshot(resources));
            Uri.TryCreate(page.Url, UriKind.Absolute, out var finalUrl);
            return new RenderResult(true, RenderFailureKind.None, null, finalUrl, html, Snapshot(resources), jsErrors, timer.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return RenderResult.Failed(RenderFailureKind.Timeout, "Render timed out.", timer.Elapsed, Snapshot(resources));
        }
        catch (System.TimeoutException)
        {
            return RenderResult.Failed(RenderFailureKind.Timeout, "Render timed out.", timer.Elapsed, Snapshot(resources));
        }
        finally
        {
            if (context is not null)
            {
                try { await context.CloseAsync(); } catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException) { crashed = true; }
            }
            Interlocked.Decrement(ref _activeRenders);
            if (crashed) await ResetBrowserAsync();
        }
    }

    private async Task HandleRouteAsync(IRoute route, RenderRequest render, RenderBudget budget, List<RenderedResource> resources, CancellationToken ct)
    {
        var request = route.Request;
        var type = request.ResourceType;
        void Record(RenderedResource r) { lock (resources) if (resources.Count < 500) resources.Add(r); }
        async Task Block(string reason, int? status = null)
        {
            Record(new RenderedResource(Trunc(request.Url), type, status, null, true, reason));
            CrawlerMetrics.RenderBlockedRequests.Add(1, new KeyValuePair<string, object?>("reason", reason));
            try { await route.AbortAsync("blockedbyclient"); } catch (PlaywrightException) { }
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)) { await Block("invalid-url"); return; }
        if (uri.Scheme is "data" or "blob") { try { await route.ContinueAsync(); } catch (PlaywrightException) { } return; }
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) { await Block("non-get-method"); return; }
        var isDocument = type == "document";
        // Byte-heavy types are not needed to build the DOM: record their URL as metadata only (D4).
        if (!isDocument && SkippedResourceTypes.Contains(type)) { Record(new RenderedResource(Trunc(uri.AbsoluteUri), type, null, null, true, "not-fetched-type")); try { await route.AbortAsync("blockedbyclient"); } catch (PlaywrightException) { } return; }
        if (!isDocument && !budget.TryTakeRequest()) { await Block("request-budget"); return; }
        try { await _guard.ValidateAsync(uri, ct); }
        catch (InvalidOperationException) { await Block("ssrf-guard"); return; }

        var maxBytes = isDocument ? render.MaxDomBytes : _options.MaxSubresourceBytes;
        try
        {
            await using var hostLease = await _coordinator.AcquireAsync(uri, ct);
            using var message = new HttpRequestMessage(request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Head : HttpMethod.Get, uri);
            message.Headers.UserAgent.ParseAdd(render.UserAgent);
            foreach (var header in request.Headers.Where(h => ForwardedRequestHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase)))
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var response = await _client().SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            _coordinator.Report(uri, status, response.Headers.RetryAfter?.Delta);
            if (response.Content.Headers.ContentLength > maxBytes) { await Block("size-limit", status); return; }
            var body = await ReadCappedAsync(response, maxBytes, ct);
            if (body is null) { await Block("size-limit", status); return; }
            if (!isDocument && !budget.TryTakeBytes(body.Length)) { await Block("byte-budget", status); return; }
            var headers = new List<KeyValuePair<string, string>>();
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (DroppedResponseHeaders.Contains(header.Key)) continue;
                if (header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) && response.Headers.Location is { } location)
                {
                    headers.Add(new(header.Key, (location.IsAbsoluteUri ? location : new Uri(uri, location)).AbsoluteUri));
                    continue;
                }
                headers.Add(new(header.Key, string.Join(header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ? "\n" : ", ", header.Value)));
            }
            Record(new RenderedResource(Trunc(uri.AbsoluteUri), type, status, body.Length, false, null));
            await route.FulfillAsync(new RouteFulfillOptions { Status = status, Headers = headers, BodyBytes = body });
        }
        catch (OperationCanceledException) { try { await route.AbortAsync("timedout"); } catch (PlaywrightException) { } }
        catch (InvalidOperationException) { await Block("ssrf-guard"); }   // pinned connect refused a protected address (DNS rebinding)
        catch (HttpRequestException) { _coordinator.Report(uri, null, null); await Block("fetch-failed"); }
        catch (PlaywrightException) { /* page or context already gone */ }
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > maxBytes) return null;
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken ct)
    {
        await _launch.WaitAsync(ct);
        try
        {
            if (_browser is { IsConnected: true } && _rendersOnBrowser < _options.RecycleBrowserAfterRenders)
            {
                _rendersOnBrowser++;
                return _browser;
            }
            // Recycle only once no render is still using the old browser; otherwise keep it one more time.
            if (_browser is { IsConnected: true } && Volatile.Read(ref _activeRenders) > 0) { _rendersOnBrowser++; return _browser; }
            if (_browser is not null) { try { await _browser.CloseAsync(); } catch (PlaywrightException) { } }
            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                ExecutablePath = string.IsNullOrWhiteSpace(_options.ExecutablePath) ? null : _options.ExecutablePath,
                Args =
                [
                    "--host-resolver-rules=MAP * ~NOTFOUND",
                    // IP literals skip the resolver; a dead proxy (including loopback) catches any non-routed request.
                    "--proxy-server=http://127.0.0.1:9", "--proxy-bypass-list=<-loopback>",
                    "--disable-background-networking", "--disable-component-update", "--disable-domain-reliability",
                    "--disable-sync", "--no-first-run", "--disable-default-apps", "--mute-audio",
                    "--webrtc-ip-handling-policy=disable_non_proxied_udp", "--force-webrtc-ip-handling-policy",
                    "--disable-dev-shm-usage", $"--js-flags=--max-old-space-size={_options.JsHeapMegabytes}",
                ],
            });
            _browser.Disconnected += (_, _) => _logger.LogWarning("Render browser disconnected");
            _rendersOnBrowser = 1;
            return _browser;
        }
        finally { _launch.Release(); }
    }

    private async Task ResetBrowserAsync()
    {
        await _launch.WaitAsync();
        try
        {
            if (_browser is not null) { try { await _browser.CloseAsync(); } catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException) { } }
            _browser = null; _rendersOnBrowser = 0;
        }
        finally { _launch.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetBrowserAsync();
        _playwright?.Dispose();
        _launch.Dispose();
    }

    private static IReadOnlyList<RenderedResource> Snapshot(List<RenderedResource> resources) { lock (resources) return resources.ToArray(); }
    private static string Trunc(string value) => value.Length <= 2048 ? value : value[..2048];

    private sealed class RenderBudget(int maxRequests, long maxBytes)
    {
        private int _requests; private long _bytes;
        public bool TryTakeRequest() => Interlocked.Increment(ref _requests) <= maxRequests;
        public bool TryTakeBytes(long bytes) => Interlocked.Add(ref _bytes, bytes) <= maxBytes;
    }
}
