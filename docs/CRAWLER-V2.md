# Crawler v2 — JavaScript rendering, crawl modes, render evidence

Crawler v2 **extends** the existing crawler rather than replacing it. The durable frontier, job queue, batch runner, robots, sitemaps, `SafePageFetcher`, SSRF guard, IP pinning, host coordinator and analysis pipeline are all unchanged in role. The additions are:

- a render stage;
- new settings;
- one table;
- one endpoint;
- host backoff.

## Existing capabilities (mapped before any change)

| Area | Before v2 |
|---|---|
| Orchestration | `CrawlBatchRunner`: batches of 20, parallel fetch waves sized by `Concurrency` with serial persistence, 2-minute frontier leases, unique continuation jobs, completion under a row lock |
| Politeness | robots.txt (UA groups, crawl-delay, cache), `DelayMilliseconds`, per-host semaphore + spacing (`HostRequestCoordinator`) |
| Safety | `OutboundUrlGuard` (scheme, credentials, private ranges), `SsrfPinnedHandler` (resolve → validate → connect to the same IP), manual redirects re-validated per hop (max 10), streaming size cap, timeout |
| Discovery | seed + sitemap (XXE-safe) + links, host scope re-checked after redirects, duplicate final URLs skipped |
| Missing | JS rendering, crawl modes, raw vs rendered comparison, host backoff and circuit breaker, render quota, crawler metrics |

## Settings (all optional; legacy projects behave exactly as before)

| Field | Values | Default |
|---|---|---|
| `renderMode` | `html` never renders · `js` renders every HTML page · `auto` HTML-first, renders only when SPA triggers fire | `html` |
| `discoveryMode` | `hybrid` (seed + sitemap + links) · `spider` (seed + links) · `sitemap` (sitemap URLs, no link following) · `list` (explicit `urlList`, no link following) | `hybrid` |
| `viewport` | `desktop` 1366×900 · `mobile` 412×915 @2.625, touch, mobile UA suffix | `desktop` |
| `maxRendersPerCrawl` | 0–50,000 | 100 |
| `urlList` | newline-separated absolute http(s) URLs, max 1,000 entries and 200 KB; still host-scoped and SSRF-checked | — |

## Deployment configuration (`Rendering` section)

Rendering is **off by default**. When it is off, `js` and `auto` projects crawl raw HTML and store `Disabled` evidence; nothing pretends to render. To enable it:

```json
"Rendering": { "Enabled": true, "MaxConcurrentRenders": 2, "MaxConcurrentRendersPerProject": 1, "MaxQueuedRenders": 16,
               "MaxDomBytes": 5000000, "MaxSubresourceRequests": 150, "MaxSubresourceBytes": 5000000,
               "MaxSubresourceBytesPerRender": 25000000, "RecycleBrowserAfterRenders": 100, "JsHeapMegabytes": 512,
               "CircuitFailureThreshold": 5, "CircuitOpenSeconds": 60, "NetworkIdleWaitMilliseconds": 3000 }
```

The host needs Chromium. Run `pwsh bin/.../playwright.ps1 install --with-deps chromium`, or set `ExecutablePath`.

## Render pipeline for one page

1. The raw fetch goes through `SafePageFetcher`, unchanged. The raw HTML is extracted with `HtmlExtractor`.
2. `CrawlRenderStage` decides whether to render. `html` never renders. `auto` renders only if `RenderTriggers.ShouldRender` fires.
3. Quota reservation happens under the tenant `pg_advisory_xact_lock` (the same lock as every other quota). Two limits apply: the per-crawl cap, then the monthly plan allowance. A `Reserved` evidence row is committed before rendering. The row is unique per (crawl, normalized URL), so a resumed batch reuses it and is never charged twice.
4. `PlaywrightPageRenderer`:
   - Admission goes through `RenderGate`: global cap, per-project cap, and a bounded queue that rejects when full. A circuit breaker opens after repeated timeouts or crashes.
   - Each render gets a fresh browser context with no shared cookies or storage. Service workers are blocked and downloads are off.
   - **Every request is intercepted** and fulfilled by the server through `OutboundUrlGuard`, the IP-pinned client and the host coordinator. Chromium's own resolver is disabled (`--host-resolver-rules=MAP * ~NOTFOUND`) and a dead proxy catches anything unrouted. Redirects re-enter interception, so every hop is re-validated.
   - Only GET and HEAD are allowed. Images, fonts and media are recorded as metadata but not downloaded. Request and byte budgets apply per render, plus a size cap per resource.
   - The timeout covers navigation plus a bounded network-idle wait. The DOM length is measured inside the page before it is copied out. The browser is recycled every N renders and relaunched after a crash.
5. The rendered DOM is extracted with the **same** `HtmlExtractor`. `RawVsRenderedComparer` then produces a deterministic diff with ordinal-sorted sets, bounded evidence lists and no AI. The page snapshot uses the rendered DOM, which is what search engines index. The diff, trigger signals and resource metadata are stored in `PageRenderEvidences`.
6. When a render fails, times out, crashes or is refused for quota or capacity reasons, the raw page is used. The evidence records why. Quota and capacity refusals are not charged.
7. Analysis turns critical differences into `JS_RENDER_MISMATCH` issues. The stored diff is their evidence.

## Evidence statuses

| Status | Charged | Meaning |
|---|---|---|
| Reserved | yes | quota taken, render in progress (or the worker died; resume reuses it) |
| Rendered | yes | diff stored |
| Failed | yes | the renderer ran and failed (Timeout / Crash / Oversized / NavigationBlocked / NavigationFailed) |
| QuotaExceeded | no | per-crawl cap or monthly plan allowance reached |
| CapacityRejected | no | queue full or circuit open |
| Disabled | no | rendering not enabled in this deployment |

## API

`GET /api/seo/projects/{projectId}/crawls/{crawlId}/rendering?mismatchesOnly=&page=&pageSize=` is tenant-scoped and uses `CanViewAsync`. It returns a summary by status plus paginated evidence.

`GET /api/seo/usage` now also returns `rendersPerMonth` and `rendersUsed`, which are shown in the sidebar.

## Metrics (`SeoLoodoi.Crawler` meter)

- `crawler.pages.fetched{status_class}`
- `crawler.fetch.errors{error}`
- `crawler.renders{outcome}`
- `crawler.render.duration` (ms)
- `crawler.render.rejected{reason}`
- `crawler.render.circuit_opened`
- `crawler.render.blocked_requests{reason}`
- `crawler.render.critical_mismatches`
- `crawler.host.circuit_opened`

Tags are low-cardinality only. Per-crawl correlation comes from the log scopes (`CrawlId`, `ProjectId`, `JobId`).

## Known limitations (not claimed as done)

- No process-wide cap on raw HTTP fetches. Raw concurrency is per project wave and per host. Renders do have a global cap.
- Render quota for plans lives in `RenderQuotaPolicy`. Loodoi billing does not send a render entitlement yet.
- The renderer's own sub-requests do not re-check robots.txt (the document URL is checked). This matches how search engines fetch page resources.
- Metrics have no exporter configured.
- `JS_RENDER_MISMATCH` issue generation has no end-to-end pipeline test yet.
- Browser tests run only in CI, because Chromium cannot be installed in the dev sandbox.
