# FINDINGS — Phase 0 Static Audit (SEO Loodoi @ acdbced)

> 📌 **"Phase" here means the hardening track**, not a product phase: Phase 0–5 below are
> audit iterations that close findings **F1–F15** (plus the i18n/a11y and Testcontainers
> work). They are unrelated to the master plan's PHASE 0–28 and to the capability
> phases referenced by `MATRIX.md`. See **`docs/PHASE-NUMBERING.md`** for the full key.

> **Phase 5 status (updated):** the last two deferred findings are closed. **F5** — the `Concurrency` crawl setting is now real: the batch runner leases up to `Settings.Concurrency` frontier items per wave and fetches them in parallel (per-host politeness still enforced by the fetcher's coordinator lease; all persistence and state transitions stay serial on the DbContext); `Concurrency=1` preserves the old sequential cadence; pinned by `CrawlConcurrencyTests` (RED observed peak concurrency 1, GREEN observes 4). **F11** — PDF reports now render real Persian: Vazirmatn Regular (SIL OFL 1.1, license committed alongside the embedded font) is embedded as CIDFontType2/Identity-H, Arabic script is shaped into connected Presentation Forms-B (golden values verified for سلام/باران), lines lay out right-to-left with Latin/digit runs preserved, and the binary header comment bug is fixed; pinned by `PersianPdfReportTests`. Remaining ledger: F13 is a documented design choice (no code change planned); C2 (analysis-status GET self-heal side effects) stays by design. Zero open Critical/High/Medium findings.
>
> **Phase 4 status (updated):** F6 (quota O(n²) correlated queries → join-based, pinned by QuotaQueryShapePostgresTests on real PG), F8 (scheduled-crawl quota churn → defer to monthly reset via new SeoProject.DeferCrawlUntil, pinned by ScheduledCrawlQuotaBackoffTests), B1/F10 (202 JSON bodies no longer discarded, pinned by api.test.ts), and C1 (AI analyze now requires Editor, pinned by AiAnalysisAccessTests) all FIXED with RED→GREEN evidence in CI. Also shipped: **full 11-language i18n** (fa canonical + en, ar, zh, es, fr, de, ru, pt, tr, hi; compile-time key completeness; RTL/LTR document sync; pre-login + in-app language switcher; localStorage persistence; profile/account preferredLanguage round-trip across all 11 codes) and an **accessibility pass** (translated accessible names on every icon-only button, aria-current on nav, role=alert on errors, aria-labels on placeholder-only inputs, labeled controls asserted by @testing-library/react render tests). Frontend tests now run in CI on Node 22 with JUnit annotations. Deferred: F5 (dead Concurrency setting — needs a product decision), F11 (Persian PDF typography — font shaping + license review), F13 (alert-delivery cancellation on rule delete — documented design choice). All CI at this revision green.
>
> **Phase 3 status (updated):** three approved items shipped with RED→GREEN evidence in CI — (1) Testcontainers/CI integration suite (item 1, caught F15); (2) **signed alert webhooks** — per-rule HMAC-SHA256 secret issued once at rule creation, never returned by list endpoints, signature `sha256=hex(HMAC("{ts}.{payload}"))` sent via `X-Loodoi-Timestamp`/`X-Loodoi-Signature`, verified end-to-end on PostgreSQL; (3) **retention cleanup** — configurable windows (30/90/30/365 days default) purge only terminal-state bookkeeping (Succeeded/Failed jobs, Delivered/DeadLetter alert deliveries, audit logs), queued/running work never deleted, scheduled hourly via the durable queue; plus **observability** — one structured access-log line per request (method/path/status/duration/trace; bodies never read), 4xx→Warning/5xx→Error, JSON console logs outside Development. **Deferred (need explicit approval / carry risk):** real Persian PDF typography (F11 — requires font shaping + license review) and fa/en localization + accessibility pass (touches the sacred `App.tsx`; per project rules any reformat there needs approval). CI at this revision: build `-warnaserror`, 75 unit + 7 API-contract + 10 PostgreSQL-integration tests, migrations script vs PG16, frontend lint/build/audit — all green (run 34777552604).
>
> **Phase 2 status (updated):** F1, F2, F3, F4, F7, F9, F12 **FIXED with regression tests** (RED→GREEN proven in CI runs 34772959577→34773108752, 34773197125→34773347532, 34773437490, 34773519799→34773582687). Zero Critical/High open. Deferred with rationale: F5 (dead `Concurrency` setting — needs product decision; the UI-side change is gated by the frontend-approval rule), F6 (quota query perf — no correctness impact), F8 (handled path, acceptable), F10/F11 (frontend behavior + Persian PDF typography — Phase 3 items), F13 (design choice, documented). F4's race itself is not deterministically unit-testable; the fix mirrors the already-proven `FOR UPDATE` pattern of `CrawlCommandService.ChangeAsync` and the full CI suite stays green.


Auditor: Arena Agent Mode (Principal SWE + QA + Security pass).
Method: full source read of `src/SeoLoodoi.Api`, `Application`, `Infrastructure`, `Domain`, `Web`, all 17 test files, CI workflow, docs. No code changes in this phase.
Severity scale: **Critical** (data loss/authZ breach/SSRF/secret exposure) > **High** (broken user-facing contract or likely prod-only failure) > **Medium** (correctness noise, races with self-heal, misleading behavior) > **Low** (cosmetic/doc/dead-config).

---

## A. InMemory-LINQ vs Postgres-raw-SQL drift audit

| Pair | Location | Verdict |
|---|---|---|
| Frontier enqueue | `src/SeoLoodoi.Infrastructure/Persistence/DurableQueues.cs:8-19` | Equivalent semantics (dup-check + insert vs `ON CONFLICT DO NOTHING`). InMemory check-then-add is racy under concurrency, but only one in-process worker exists; dev-preview risk only. |
| Frontier lease | `DurableQueues.cs:20-38` | Same ordering (`Depth`,`CreatedAt`), same expired/null-lease treatment. Postgres uses `FOR UPDATE SKIP LOCKED`; InMemory has no row lock — safe only because a single `DurableJobWorker` instance leases sequentially. **Drift risk: none today, fragile if a second worker is ever added.** |
| Job queue enqueue-once | `DurableQueues.cs:47-60` | Equivalent (first-writer-wins). Same racy-check caveat for InMemory. |
| Job queue lease | `DurableQueues.cs:61-80` | Equivalent. |
| Crawl pause/resume/cancel row lock | `src/SeoLoodoi.Infrastructure/Crawling/CrawlCommandService.cs:56-60` | Postgres `SELECT ... FOR UPDATE`; InMemory plain query. Acceptable (single process). |
| Alert delivery claim | `src/SeoLoodoi.Infrastructure/Jobs/AlertDeliveryWorker.cs:48-75` | Postgres conditional `ExecuteUpdateAsync`; InMemory tracked `IsDue`+`MarkProcessing`. Dead-letter threshold (10) and backoff formula are identical (`AlertDelivery.MarkFailed`, `src/SeoLoodoi.Domain/Seo/AlertDelivery.cs:47-56` vs worker lines 88-90). **No drift found.** |
| Analysis purge/rebuild | `src/SeoLoodoi.Infrastructure/Analysis/AnalyzeCrawlJobHandler.cs:64-80` | `ExecuteDeleteAsync` vs `RemoveRange` — equivalent result sets. |

**Conclusion (a):** no *behavioral* drift found statically; the structural risk (silent prod-only bugs) from the register is confirmed as real and remains the top argument for the Testcontainers suite (Phase 3 item 1). None of these paths is executed by any test today.

## B. Frontend ↔ backend contract mismatches

- **B1 — 202 responses discard JSON bodies.** `src/SeoLoodoi.Web/src/api.ts:41` returns `undefined` for *any* 202. Backend sends 202 **with bodies** from: start crawl (`Program.cs:131` `Results.Accepted(..., crawl)`), analysis retry (`Program.cs:149` `Results.Accepted(..., status)`), resume (`Results.Accepted()` empty — fine), competitor crawl (`Program.cs:222`). Callers mostly refetch afterwards so damage is masked; `Dashboard.retry` (`App.tsx`, retry handler) does `setAnalysis(await api.retryAnalysis(...))` → sets `undefined` before `loadProject` overwrites it. **Low** (masked), but it is a real type-safety hole (`Promise<AnalysisStatus>` actually resolves `undefined`).
- **B2 — frontend `TwoFactorStatus.recoveryCodes` only returned on POST** (`Program.cs:185-236`): matches usage. OK.
- **B3 — GSC sync response**: frontend type `{rowsReceived;keywordsUpdated;partial}` (`api.ts:80`) matches `SearchConsoleSyncResult(RowsReceived, KeywordsUpdated, ...)` (`src/SeoLoodoi.Application/SearchConsole/Contracts.cs:4`). OK (earlier suspicion refuted).
- **B4 — `faStatus` does not translate alert types** (`SCORE_DROP`, `CRITICAL_ISSUE`, `CRAWL_FAILURE` fall through raw, `App.tsx` alerts tab). Cosmetic. **Low.**
- **B5 — Error shapes:** backend mixes `Results.ValidationProblem` (`errors` map — handled), `Results.Problem(detail)` (`detail` — handled), `Results.Conflict(new{error})` (`error` — handled) and **unhandled exceptions → default ProblemDetails 500** (see F1). Frontend `request()` reads `error ?? detail ?? title` — acceptable, but 500s surface as raw exception-derived titles.

## C. Authorization / scoping audit

Every `/api/seo/...` endpoint routes through an owner/member scope check (`ProjectAccessService` + `SeoProjectRepository.FindOwnedAsync`, `src/SeoLoodoi.Infrastructure/Projects/ProjectAccessService.cs:8-20`). Verified endpoint-by-endpoint: projects/crawls/issues/scores/pages/recommendations/keywords/competitors/AI/reports/alerts/members/audit/settings/dashboard/analysis-status/search-console — all check `CanView/CanEdit/CanManage` before touching data. **No missing-scope endpoint found.**

Residual items:
- **C1 (Low) — FIXED (Phase 4):** `POST .../ai/analyze` now requires `CanEdit` (was `CanView`), so a Viewer can no longer invoke the AI provider or persist `AiAnalysis` rows. Pinned by `AiAnalysisAccessTests`.
- **C2 (Low):** `GET .../analysis-status` has **write side effects** (creates `CrawlAnalysis` row, enqueues job — `AnalysisStatusService.cs:23-34`) for any Viewer. Self-heal by design, but GET-with-effects is worth flagging.
- **C3 (OK):** GSC OAuth callback (`Program.cs:241-246`) is intentionally unauthenticated; `state` is Data-Protection-protected, carries project+user+timestamp, validated ≤600 s and re-checked with `CanManageAsync` (`SearchConsoleService.cs:42-48`). No CSRF found.

## D. Security regression audit (SSRF / secrets / tokens)

- **D1 — DNS-rebinding TOCTOU in SSRF guard (Medium).** `OutboundUrlGuard.ValidateAsync` resolves DNS and checks the IPs (`Security/OutboundUrlGuard.cs:11-20`), then `SafePageFetcher`/`AlertDeliveryWorker` connect **separately** — the name is re-resolved at connect time. An attacker-controlled authoritative DNS can flip A records between validation and connection (classic rebinding) to reach private ranges. Mitigations that reduce exposure: redirect hops are re-validated, robots/sitemap fetches share the guard. Full fix requires connecting through the validated IP (pinned resolution). Not exploitable via stored config; exploitable via user-submitted project/competitor/webhook URLs.
- **D2 — AI endpoint not guard-validated (Low).** `AiSeoExpert` posts to operator-configured `AI:Endpoint` with no `IOutboundUrlGuard` check (`Infrastructure/AI/AiSeoExpert.cs:26-31`). Config-origin (not user input), accepted per product rules, noted for completeness.
- **D3 — No token/secret logging found.** Searched all `Log*` call sites: tokens, SMTP password, AI key never logged; `ExternalConnection` stores only `Protect()`-ed blobs; APIs never return tokens (status endpoint returns `Status`/`ExpiresAt` only, `SearchConsoleService.cs:59-64`). Header evidence is allowlisted (`CrawlJobHandlers.cs:101`). OK.
- **D4 — appsettings placeholder password** (`src/SeoLoodoi.Api/appsettings.json` `CHANGE_ME`) — documented, `.env.example` says override. OK but keep blocking prod misconfig (Phase 3 obs item).
- **D5 — Webhook delivery** re-validates guard per redirect hop and caps hops at 5 (`AlertDeliveryWorker.cs:111-133`). OK.

## E. Bug-debt register — confirm/refute

| Register item | Verdict + evidence |
|---|---|
| 1. Dual-provider divergence risk | **Confirmed structurally** (`DurableQueues.cs` two branches per op; `AlertDeliveryWorker.cs` two branches). No concrete behavioral drift found statically (§A) — only execution tests can prove it. |
| 2. Persian → `?` in PDF | **Confirmed.** `ReportService.cs:78` `EscapePdf` maps every non-`' '..'~'` char to `?`; project names/titles in Persian render as `?` runs (`ReportService.cs:70-71`). |
| 3. One-file frontend | **Confirmed.** `src/SeoLoodoi.Web/src/App.tsx` = 95 lines / 65,791 bytes; zero component tests (only `statusText.test.ts`). |
| 4. Integrations default OFF | **Confirmed.** `appsettings.json` — `AI.Enabled=false`, `Email.Enabled=false`, `SearchConsole.ClientId=""`; these surfaces are never exercised end-to-end. |
| 5. Stale docs + drifts | **Confirmed.** `docs/01-04` still DigiStore/DigiSEO-era; `README.md:19` and `IMPLEMENTATION-STATUS.md` claim scoring **v1.1** while code is `ScoringEngine.Version = "2.0.0"` (`ScoringEngine.cs:8`). `HostAllowed` history note is accurate. |
| 6. Runtime-logic bug class | **Confirmed as risk class** (stall fix PR#3 shows the pattern); zero API/integration tests exist (`tests/` has 17 unit files only; CI never executes the raw-SQL paths). |

---

## Findings list (ordered)

### F1 — HIGH — `POST /reports` returns opaque 500 when no completed crawl exists
`ReportService.CreateAsync` throws `InvalidOperationException("A completed crawl is required...")` (`Infrastructure/Reports/ReportService.cs:28`), but the endpoint catches only `ArgumentException` (`Api/Program.cs:341-344`). Result: unhandled exception → HTTP 500 instead of a clean 4xx; UI shows generic failure (`App.tsx` ReportView catch). Also violates the product's own error-contract (Persian validation message).
Repro sketch: register → create project → (no crawl) → `POST /api/seo/projects/{id}/reports` → 500.
Fix direction (Phase 2): map to 409 with Persian message; regression test asserting status code.

### F2 — HIGH — SSRF guard DNS-rebinding window (D1 above)
`Infrastructure/Security/OutboundUrlGuard.cs:11-20` + `Crawling/SafePageFetcher.cs:29-34`. User-supplied URLs (project base, competitor base, alert webhook) pass validation, then get re-resolved at connect time.
Fix direction (Phase 2): pin resolved IPs into the connection path (custom DNS/connect callback or connect-to-IP with Host header), keeping per-hop revalidation.

### F3 — MEDIUM — `NOINDEX` rule double-fires on every non-200 page
`NoIndexRule` triggers on `!c.IsIndexable` (`Application/Analysis/TechnicalRules.cs:33-35`), and `IsIndexable` is false for **any** status ≠ 200 (`CrawlJobHandlers.cs:103`). A 404 page yields `BROKEN_STATUS` **and** `NOINDEX` issues; the NOINDEX guidance ("صفحه Noindex است") is factually wrong for broken pages and inflates Indexability penalties.
Fix direction: NOINDEX should trigger only when a noindex directive is present (meta/X-Robots), or the page is 200 and flagged non-indexable.

### F4 — MEDIUM — pause/complete race in the batch runner can drop a pause or log spurious failures
`CrawlJobHandlers.cs:157-186`: after the last `ReloadAsync`, a concurrent pause commit can (a) be overwritten by `crawl.Complete` (last-write-wins, no concurrency token) or (b) make `Complete()` throw → job retries with error noise. Self-heals (retry sees Paused and stops) but user-visible pause can be lost.
Fix direction (Phase 2): reload/re-check immediately before terminal transitions, or short transaction with row lock (parity with `CrawlCommandService.ChangeAsync`).

### F5 — MEDIUM — `Concurrency` crawl setting is dead configuration — FIXED (Phase 5)
`CrawlSettings.Concurrency` (1–16) is validated and editable in the UI ("هم‌زمانی میزبان", `App.tsx` settings form) but the crawl loop is strictly sequential (`CrawlJobHandlers.cs:59-156` leases and fetches one item at a time; the global `HostRequestCoordinator` semaphore is hard-coded to 4, `HostRequestCoordinator.cs` ctor default). Changing the setting has **no effect** — violates "limits are really enforced by the worker" as advertised in the UI copy.
Fix direction (Phase 2): either implement parallel leasing or remove/hide the field + doc note (behavioral honesty rule). **Done (Phase 5):** parallel fetch waves bounded by the setting; pinned by `CrawlConcurrencyTests` (RED peak=1 → GREEN peak=4, Concurrency=1 stays serial).

### F6 — MEDIUM — quota page-count query is O(n²) correlated and counts `StartedAt` per row — FIXED (Phase 4)
`QuotaService.GetAsync`/`EnsureCanStartCrawlAsync` (`Infrastructure/Projects/QuotaService.cs:21,49-52`) use two correlated `Any` subqueries per `CrawledUrls` row; correct today, scaling hazard at quota check time (every crawl start).
Fix direction: join-based count or cached usage row. Low urgency. **Done (Phase 4):** join-based counting for pages/keywords/competitors; pinned on real PostgreSQL by `QuotaQueryShapePostgresTests` (RED: shape assertion failed on the correlated-EXISTS plan, GREEN after the rewrite).

### F7 — MEDIUM — analysis retry key collision window
`AnalysisStatusService.cs:65` builds retry keys from `ToUnixTimeMilliseconds()`; two retries in the same millisecond collide → `EnqueueOnceAsync` no-ops → second `retry` returns 202 with no new job. Use `Guid` (same as continuation keys, `CrawlJobHandlers.cs:168`).

### F8 — MEDIUM — scheduled-crawl quota failure re-fires every minute → log spam + no backoff beyond 10 min — FIXED (Phase 4)
`ScheduledCrawlWorker.cs:27-40`: `QuotaExceededException` (subclass of `InvalidOperationException`) is caught and rescheduled `+10 min` forever while quota is exhausted. Not wrong, but unbounded churn; consider day-granular reschedule when quota-exceeded. (Behavior verified by reading; `StartAsync` throws the same exception type the worker catches — so it *is* handled, correcting an earlier suspicion.) **Done (Phase 4):** quota exhaustion now defers the project to the start of the next month via `SeoProject.DeferCrawlUntil` (no fabricated `LastCrawlAt`); pinned by `ScheduledCrawlQuotaBackoffTests`.

### F9 — LOW — `Crawl.Cancel` accepts `Failed` crawls and overwrites the failure state
`Domain/Seo/CrawlModels.cs:31` — cancelling a Failed crawl silently rewrites `Status` to Cancelled and drops error context from the UI. Terminal states should be immutable except `Completed/Cancelled` early-return.

### F10 — LOW — frontend discards 202 bodies (B1) and missing fa labels for alert types (B4) — FIXED (Phase 4)
`api.ts:41`, `App.tsx` faStatus map. B1 fixed: only 204 stays body-less, 202 payloads are parsed (api.test.ts). B4 fixed: alert-type labels now come from the i18n catalogs in all 11 languages.

### F11 — LOW — Persian text in PDFs becomes `?` (register #2; Phase 3 typography work) — FIXED (Phase 5)
`ReportService.cs:70-78`. Also `%PDF` binary comment is written through ASCII encoding (cosmetic). **Done (Phase 5):** Vazirmatn (SIL OFL 1.1) embedded via a minimal TTF reader + CIDFontType2/Identity-H, Arabic shaping into Presentation Forms-B with correct joining rules, RTL line layout, raw-byte binary marker. Pinned by `PersianPdfReportTests`.

### F12 — LOW — doc drift (register #5)
`README.md:19` + `IMPLEMENTATION-STATUS.md` "v1.1" vs `ScoringEngine.Version "2.0.0"` (`Application/Analysis/ScoringEngine.cs:8`).

### F13 — LOW — no cancellation of in-flight alert deliveries on rule delete
Deleting an alert rule leaves already-created `AlertDeliveries` queued (`Program.cs:315-321`); they still deliver. Arguably correct (event already happened) — documented, no change needed unless product decides otherwise.

### F14 — INFO/UNTESTED surface (feeds MATRIX.md)
No API/integration/E2E tests exist; raw-SQL paths, lease races, GSC pagination, alert outbox, quota concurrency, scheduler, recovery sweeper and restart-resume are all **UNTESTED** at runtime. This is the dominant residual risk (register #6).

---

### F15 — HIGH (found by the Phase 3 integration suite, FIXED) — missing `Competitors.LastCrawlAt` column in migrations
`Competitor.MarkCrawled` persists `LastCrawlAt` (`Domain/Seo/ExtendedModels.cs:104`) but no migration ever created the column; the checked-in snapshot was stale and EF 10's `MigrateAsync` refused to run (`PendingModelChangesWarning`). On PostgreSQL every competitor crawl would crash at its final `SaveChanges` (column not found), while InMemory preview worked — the exact dual-provider divergence class from register #1. **Fixed** by the `RealignModelSnapshot` migration + regenerated snapshot (commit `77e984b`, produced via CI design-time tooling), proven by `tests/SeoLoodoi.Integration.Tests` executing `MigrateAsync` on a real PostgreSQL 16 container.

## Summary counts

Critical: 0 · High: 3 (F1, F2, F15 — all fixed) · Medium: 6 (F3, F4, F5, F6, F7, F8 — all fixed; zero open Medium) · Low: 5 (F9, F10, F11, F12 fixed; F13 documented design choice) · Info: 1 (F14, closed by the new integration suite) · Access residual C1 fixed (Phase 4).
