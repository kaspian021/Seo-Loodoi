# MATRIX — Capability Map (SEO Loodoi @ acdbced)

Legend: **REAL** = implemented and wired end-to-end · **PARTIAL** = implemented with meaningful gaps · **STUB** = placeholder/disabled-by-default surface · **UNTESTED** = no test executes this path (unit/integration/E2E). A cell like `REAL / UNTESTED` means "code is real, coverage is absent".

## 1. Identity & account

| Capability | Endpoints | Service | Tables | Tests | Status |
|---|---|---|---|---|---|
| Registration (profile, terms, localized errors) | `POST /api/account/register` (`Program.cs:84`) | `UserManager` + `ValidateRegistration` (`Program.cs:382`) | `AspNetUsers` (+profile cols, migration `AddUserRegistrationProfile`) | none | REAL / UNTESTED |
| Default Identity register blocked | filter on `MapIdentityApi` (`Program.cs:81`) | endpoint filter | — | none | REAL / UNTESTED |
| Confirmation-link flow | register + `GET /api/auth/confirmEmail` (MapIdentityApi) | `SmtpEmailSender` (`Identity/SmtpEmailSender.cs:30`) | `AspNetUsers` | none | PARTIAL (Email.Enabled=false default → link never delivered) / UNTESTED |
| Login / refresh / bearer tokens | `/api/auth/login?useCookies=false`, `/refresh` (MapIdentityApi) | Identity JWT | `AspNetUsers` | none | REAL / UNTESTED |
| Forgot/reset password | `/api/auth/forgotPassword`, `/resetPassword` + reset-code mail w/ deep link | `SmtpEmailSender.SendPasswordResetCodeAsync` (`SmtpEmailSender.cs:33-37`) | users | none | PARTIAL (email off by default) / UNTESTED |
| Profile get/update | `GET/PUT /api/account/me` (`Program.cs:117,191`) | UserManager | users | none | REAL / UNTESTED |
| TOTP 2FA + recovery codes | `GET/POST /api/account/security/2fa` (`Program.cs:204-236`) | UserManager | users | none | REAL / UNTESTED |
| 2FA login flow (frontend regex on `detail`) | login | MapIdentityApi | — | none | REAL / UNTESTED (fragile coupling B1/B5) |
| Auth rate limiting | `auth` policy 10/min/IP (`Program.cs:35`) | RateLimiter | — | none | REAL / UNTESTED |

## 2. Projects, settings, quotas, members, audit

| Capability | Endpoints | Service | Tables | Tests | Status |
|---|---|---|---|---|---|
| Project CRUD-lite (list/get/create) | `GET/POST /api/seo/projects`, `GET /projects/{id}` | `SeoProjectRepository` (`Persistence/SeoProjectRepository.cs`) | `SeoProjects` | none | REAL / UNTESTED |
| Crawl policy (13 fields, server-validated) | `GET/PUT .../settings` | `CrawlSettings.Validate` (`Domain/Seo/CrawlSettings.cs:16-31`) | `SeoProjects.Settings` (ToJson) | CrawlConcurrencyTests | REAL / TESTED · `Concurrency` honored (F5 fixed Phase 5) |
| Dashboard aggregation | `GET .../dashboard` | `AuditQueryService.DashboardAsync` (`Analysis/AuditQueryService.cs:40`) | crawls/issues/scores | none | REAL / UNTESTED |
| Usage/quota (Starter: 3/500/25/3) | `GET /api/seo/usage` | `QuotaService` (`Projects/QuotaService.cs`) | projects/crawledurls/keywords/competitors | QuotaQueryShapePostgresTests (PG) | REAL / TESTED (F6 fixed Phase 4) |
| Quota enforcement on create/crawl/keyword/competitor | inside create/start endpoints | `QuotaService.Ensure*` | — | none | REAL / UNTESTED |
| Members invite/roles/remove | `GET/POST/PATCH/DELETE .../members` (`Program.cs:324-359`) | `ProjectAccessService` + inline | `ProjectMembers` | none | REAL / UNTESTED |
| Audit log (record + list, Admin-only list) | `GET .../audit-logs` | `AuditLogService` (`Projects/AuditLogService.cs`) | `AuditLogs` | none | REAL / UNTESTED |
| Cross-tenant denial | every scoped endpoint | `ProjectAccessService` (`Projects/ProjectAccessService.cs`) | members/projects | none | REAL / UNTESTED |

## 3. Crawl engine

| Capability | Endpoints | Service | Tables | Tests | Status |
|---|---|---|---|---|---|
| Start crawl (quota + active-crawl guard, serializable tx) | `POST .../crawls` | `CrawlCommandService.StartAsync` (`Crawling/CrawlCommandService.cs:14-41`) | `Crawls`, frontier, jobs | none | REAL / UNTESTED |
| Pause/resume/cancel | `POST .../pause|resume|cancel` | `CrawlCommandService.ChangeAsync` (FOR UPDATE) | `Crawls` | none | REAL / UNTESTED (race F4) |
| Crawl list + analysis join | `GET .../crawls` | `CrawlQueryService` | crawls+analyses | none | REAL / UNTESTED |
| Frontier (Postgres SKIP LOCKED / InMemory LINQ) | internal | `CrawlFrontierStore` (`Persistence/DurableQueues.cs`) | `CrawlFrontierItems` | `DurableStateTests` (domain ops only) | REAL / PARTIAL-COVERED (queue SQL never executed) |
| Job queue (idempotency/lease/retry/backoff) | internal | `SeoJobQueue` + `DurableJobWorker` (`Jobs/DurableJobWorker.cs`) | `SeoBackgroundJobs` | `DurableStateTests` | REAL / PARTIAL-COVERED |
| Batch runner (20/batch, reload per page, robots, redirect host-scope, wildcard skip, dedupe, evidence) | internal | `CrawlBatchRunner` (`Crawling/CrawlJobHandlers.cs:17-214`) | `CrawledUrls`, `PageSnapshots`, `PageLinks` | none | REAL / UNTESTED |
| Unique continuation keys (PR#3 stall fix) | internal | `CrawlJobHandlers.cs:168` | jobs | none | REAL / UNTESTED |
| Zero-progress +15 s recheck | internal | `CrawlJobHandlers.cs:170-175` | jobs | none | REAL / UNTESTED |
| Stalled-crawl recovery sweeper (30 s delay / 5 min sweep) | internal | `Jobs/StalledCrawlRecoveryService.cs` | crawls+jobs | none | REAL / UNTESTED |
| Restart-resume | via recovery sweeper + durable frontier | — | — | none | REAL-by-design / UNTESTED |
| SSRF-safe fetch (per-hop revalidate, size caps, redirect scheme check) | internal | `SafePageFetcher` + `OutboundUrlGuard` | — | `SafePageFetcherTests` (3), `OutboundUrlGuardTests` (2) | REAL / PARTIAL-COVERED (DNS-rebind F2 untested) |
| robots.txt (UA selection, allow/disallow, crawl-delay, 5-min/6-h cache) | internal | `RobotsService` + `RobotsParser` | — | `RobotsParserTests` (5), `RobotsServiceTests` (2) | REAL / TESTED-UNIT |
| Sitemap discovery (XXE-safe, index-aware, caps 20 docs/MaxPages) | internal | `SitemapDiscoveryService` + `SitemapParser` | — | `SitemapParserTests` (2), `SitemapDiscoveryTests` (2) | REAL / TESTED-UNIT |
| HTML evidence extraction (title/meta/h1s/canonical/links/imgs/JSON-LD/OG/Twitter/hreflang) | internal | `HtmlExtractor` | snapshots | `HtmlExtractorTests` (1) | REAL / PARTIAL-COVERED |
| URL normalization (Persian-safe, tracking-param strip) | internal | `UrlNormalizer` | — | `UrlNormalizerTests` (2) | REAL / TESTED-UNIT |
| Host concurrency/rate coordination | internal | `HostRequestCoordinator` (hard-coded 4/100ms) | — | `HostRequestCoordinatorTests` (1) | PARTIAL (project setting ignored, F5) |
| Scheduled crawls (off/daily/weekly/monthly + hour) | internal | `ScheduledCrawlWorker` (`Jobs/ScheduledCrawlWorker.cs`) + `SeoProject.ScheduleNext` | projects | none | REAL / UNTESTED (F8) |

## 4. Analysis, rules, scoring

| Capability | Endpoints | Service | Tables | Tests | Status |
|---|---|---|---|---|---|
| Analysis pipeline (purge→rules→issues→scores→recs, idempotent) | internal | `AnalyzeCrawlJobHandler` (`Analysis/AnalyzeCrawlJobHandler.cs`) | issues/scores/recs/analyses | `AnalysisLifecycleTests` (6, status-machine only) | REAL / PARTIAL-COVERED |
| Per-page rules (20 codes) | — | `SeoRules/TechnicalRules/ExtendedRules` | `SeoIssues` | `ScoringEngineTests` indirectly? No — rules themselves untested | REAL / UNTESTED (no rule-level unit tests) |
| Duplicate/near-duplicate clustering | — | `ContentSimilarityEngine` | issues | `ContentSimilarityTests` (3) | REAL / TESTED-UNIT |
| Orphan/internal-link graph | — | `InternalLinkGraph` | issues, `PageLinks` | `InternalLinkGraphTests` (2) | REAL / TESTED-UNIT |
| Scoring v2.0.0 (affected-ratio, partial, category caps) | — | `ScoringEngine` (`Analysis/ScoringEngine.cs`) | `SeoScores` | `ScoringEngineTests` (7) | REAL / TESTED-UNIT · doc drift F12 |
| NOINDEX correctness | — | `TechnicalRules.cs:33` | issues | none | PARTIAL (F3 double-fire) |
| Analysis status + self-heal + retry | `GET .../analysis-status`, `POST .../analysis/retry` | `AnalysisStatusService` | `CrawlAnalyses`, jobs | `AnalysisLifecycleTests` | REAL / PARTIAL-COVERED (F7 key collision) |
| Issues list + status PATCH | `GET .../issues`, `PATCH .../issues/{i}` | `AuditQueryService` + inline | issues | none | REAL / UNTESTED |
| Recommendations list + status PATCH | `GET .../recommendations`, `PATCH .../recommendations/{r}` | `RecommendationQueryService` | recommendations | none | REAL / UNTESTED |
| Scores latest/history (immutable snapshots) | `GET .../scores/latest|history` | `AuditQueryService` | scores | none | REAL / UNTESTED |
| Pages evidence browser | `GET .../pages` | inline LINQ (`Program.cs:255-263`) | crawledurls+snapshots | none | REAL / UNTESTED |

## 5. Satellite features

| Capability | Endpoints | Service | Tables | Tests | Status |
|---|---|---|---|---|---|
| Keywords CRUD + manual metrics | `GET/POST/DELETE .../keywords`, `POST .../keywords/{k}/metrics` | `KeywordService` (`Keywords/KeywordService.cs`) | `Keywords`, `KeywordMetrics` | none | REAL / UNTESTED |
| Opportunities (impr≥10, pos 4–15) | `GET .../keywords/opportunities` | `KeywordService.OpportunitiesAsync` | metrics | none | REAL / UNTESTED |
| Search Console OAuth (state-protected, 600 s) | `GET .../search-console/connect`, `GET /api/integrations/google/search-console/callback` | `SearchConsoleService` (`SearchConsole/SearchConsoleService.cs`) | `ExternalConnections` | none | STUB-IN-PROD (creds empty default) / UNTESTED |
| GSC 28-day sync (refresh rotation, pagination cap 100k, partial flag) | `POST .../search-console/sync` | `SearchConsoleService.SyncAsync` | keywords+metrics | none | STUB-IN-PROD / UNTESTED |
| GSC no-creds clean failure | sync/connect | connect returns null→404; sync throws InvalidOperationException→409 | — | none | REAL (error path) / UNTESTED |
| Competitors registry CRUD | `GET/POST/PATCH/DELETE .../competitors` | `CompetitorService` | `Competitors` | none | REAL / UNTESTED (PATCH has no UI) |
| Competitor bounded crawl (≤25 pages, depth≤1) | `POST .../competitors/{c}/crawl`, crawls, latest | `CompetitorCrawlRunner` (`Competitors/CompetitorCrawlRunner.cs`) | `CompetitorCrawls`, `CompetitorPages` | none | REAL / UNTESTED |
| Compare on observed metrics only | `GET .../competitors/compare` | `CompetitorService.CompareAsync` | pages/snapshots | none | REAL / UNTESTED |
| **Backlink provider architecture (PHASE 9)** | `GET .../backlinks[/history\|/status]`, `POST .../backlinks/refresh`, `GET .../backlinks/{id}/links`, `GET .../backlinks/compare?from&to` | `IBacklinkProvider` + `BacklinkService` (`Backlinks/*.cs`), `BacklinkRefreshJobHandler` | `BacklinkSnapshots`, `BacklinkObservations` | `Phase9BacklinkProviderTests` (12) | REAL / TESTED-UNIT · **provider-agnostic, no vendor SDK in Domain/Application** |
| — no-fabrication guard | all of the above | `BacklinkAvailability` (NotConfigured/Unavailable/Partial/Available) + null-vs-zero counters | — | 4 tests | REAL / TESTED-UNIT · default `NullBacklinkProvider` reports NotConfigured and never estimates links |
| **SERP intelligence (PHASE 10)** | `GET .../serp/status`, `GET .../keywords/{k}/serp[/history]`, `POST .../keywords/{k}/serp/refresh`, `GET .../serp/{id}/results`, `GET .../serp/compare?from&to` | `ISerpProvider` + `SerpService` (`Serp/*.cs`), `SerpRefreshJobHandler` | `SerpSnapshots`, `SerpResultEntries` | `Phase10SerpIntelligenceTests` (15) | REAL / TESTED-UNIT · **organic results, positions, SERP features incl. AI surfaces, device/surface segmentation** |
| — rank-history integrity | `GET .../serp/compare` | position deltas as "places gained", entered/dropped-out, feature added/removed | — | 4 tests | REAL / TESTED-UNIT · own rank derived only from observed results; comparison refuses when a capture failed |
| AI analyze (packet-only, cached, deterministic fallback) | `POST .../ai/analyze` | `AiAnalysisService` + `AiSeoExpert` (`AI/AiSeoExpert.cs`) | `AiAnalyses` | none | REAL (template mode) / REAL-PROVIDER UNTESTED (AI disabled default) |
| Reports JSON/CSV/PDF from official snapshot | `GET/POST .../reports`, `GET .../reports/{r}/download` | `ReportService` (`Reports/ReportService.cs`) | `Reports` | PersianPdfReportTests | REAL (F11 fixed Phase 5: Vazirmatn embedded, Persian shaped RTL) · F1 no-crawl 409 covered |
| Alert rules CRUD (3 types × 3 channels, SSRF-checked webhook) | `GET/POST/PATCH/DELETE .../alerts/rules` | `AlertService` (`Monitoring/AlertService.cs`) | `AlertRules` | none | REAL / UNTESTED (PATCH has no UI) |
| Alert check + events + read | `POST .../alerts/check`, `GET .../alerts/events`, `POST .../events/{e}/read` | `AlertService.CheckAsync` | `AlertEvents` | none | REAL / UNTESTED |
| Alert outbox (leased delivery, retry backoff, dead-letter @10) | internal | `AlertDeliveryWorker` (`Jobs/AlertDeliveryWorker.cs`) | `AlertDeliveries` | none | REAL / UNTESTED |
| Email delivery (SMTP real, disabled default) | internal | `SmtpEmailSender` | — | none | STUB-IN-PROD / UNTESTED |
| Monitoring sweep (5 min per enabled-rule project) | internal | `MonitoringWorker` | rules | none | REAL / UNTESTED |

## 6. Platform

| Capability | Endpoints | Service | Tables | Tests | Status |
|---|---|---|---|---|---|
| Health (`/health`, `/health/ready` DB probe) | yes | MapHealthChecks + inline | — | none | REAL / UNTESTED |
| Security headers middleware | all responses | inline (`Program.cs:66-73`) | — | none | REAL / UNTESTED |
| CORS allowlist + API rate limit (120/min/user) | all | inline | — | none | REAL / UNTESTED |
| Migrations (5) + aligned snapshot | — | `Persistence/Migrations/*` | `loodoi.*` | CI only *generates* script (never applies in tests) | REAL / UNTESTED-at-runtime |
| Frontend app (auth, dashboard, 11 sections, RTL) | SPA | `Web/src/App.tsx` + `api.ts` | — | 6 tests (`statusText` only) | REAL / PARTIAL-COVERED |

## 7. Test inventory (updated after Phases 2–3, HEAD `23dcad3`)

**Unit (SeoLoodoi.Domain.Tests, 75 tests):** ScoringEngine 7 · DurableState 6 · AnalysisLifecycle 6 · RobotsParser 5 · ProductCapability 5 · CrawlFrontierPlanner 4 · NoIndexRule 4 · SsrfPinnedHandler 6 · SafePageFetcher 3 · DbExceptionClassifier 3 · ContentSimilarity 3 · CrawlLifecycleRegression 3 · UrlNormalizer 2 · SitemapDiscovery 2 · SitemapParser 2 · RobotsService 2 · OutboundUrlGuard 2 · InternalLinkGraph 2 · AnalysisRetryKey 2 · HtmlExtractor 1 · HostRequestCoordinator 1 · **WebhookSignature 4** (stable HMAC digest, timestamp in signed content, tamper rejection, secret shape).

**API contract (SeoLoodoi.Api.Tests, 7 tests):** real pipeline via `WebApplicationFactory<Program>` on InMemory — report-no-crawl 409 contract, unknown project 404, invalid format 400 (fixture: register + bearer login for owner/member; Starter quotas lifted so tests measure contracts, not plan limits) · **signed webhooks: rule creation issues a signing secret, dashboard rules get none, list never leaks secrets (2)** · **observability: every request emits one access-log line with method/path/status/duration; error responses logged too (2)**.

**Integration (SeoLoodoi.Integration.Tests, 10 tests, real PostgreSQL 16 container):** frontier ON-CONFLICT dedupe · SKIP-LOCKED lease exclusivity + expiry reclaim · EnqueueOnce idempotence · NotBefore gating · retry backoff persistence · **full start→batch-crawl(example.com)→completion→analysis→idempotent re-analysis** · pause/resume/cancel state machine + cross-user denial + audit trail · migrations applied from scratch (this is what caught F15) · **signed webhook delivery carries the rule's HMAC secret end-to-end; dashboard rules enqueue zero deliveries (2)** · **retention cleanup deletes only rows backdated beyond each window on real Postgres (1)**.

**Frontend (6):** statusText only (unchanged; component tests remain a gap).

**Still UNTESTED at runtime:** recovery sweeper revival, restart-resume, scheduled-crawl firing, GSC token sync, quota concurrency races, member-role endpoint matrix beyond access service. **Now covered on every push:** the raw-SQL core, alert outbox loop, retention sweep, quota counting shape/semantics (Phase 4), scheduled-crawl quota backoff, AI access control, crawl Concurrency parallel-wave bound (Phase 5), Persian PDF shaping/embedding (Phase 5), and — on the frontend — i18n catalog completeness/formatting plus an accessibility smoke suite (localization + a11y shipped in Phase 4 after approval).
