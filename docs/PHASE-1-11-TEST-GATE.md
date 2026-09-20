# Phase 1–11 verification gate — NOT COMPLETE

Updated: 2026-09-20. **Phase 12 is blocked.** A green CI run is necessary, not sufficient, for full capability sign-off.

## Verified execution

- Baseline `f6db560`: 379 backend tests passed, 0 failed.
- Verification commit `6998f35`: **443 passed / 0 failed / 443 total**, as reported by `backend-test-summary` (not source-file counting).
- Run: https://github.com/kaspian021/Seo-Loodoi/actions/runs/35505581929
- Build with warnings as errors, PostgreSQL-backed existing integration tests, EF model/migration consistency and SQL generation, frontend lint, unit tests, production build and dependency audit all passed.
- This is **not** a browser E2E run, live Google/vendor validation, load test, or 100% branch-coverage claim. No aggregate coverage percentage was measured.
- All work is on `arena/01a0bdf6-seo-loodoi`. Pushes to that branch enable CI; they are not release approval or a merge to main.

## Numbering conflict — use capability names

`docs/PHASE-NUMBERING.md` describes a 0–28 master roadmap, but the actual PHASE PLAN in `docs/03-Master-AI-Coding-Prompt.md` currently contains phases 0–12 with different names. For example, that older document calls phase 9 reports, phase 10 subscriptions, and phase 11 hardening. The current source and user handoff call phases 9/10/11 backlinks/SERP/AEO and phase 12 AI expert. Do not silently equate these tracks or infer completion from a numbered test filename.

The inventory below follows the **current session's capability names** and includes scoring, link graph, security, reporting and quotas separately so the numbering conflict cannot exclude them.

## Capability / evidence / remaining acceptance work

All rows remain **partial** until their remaining acceptance work is executed or explicitly removed from scope. Existing tests are evidence for specific paths, not every path in a module.

| Session scope | Existing and newly executed evidence (test class names) | Remaining acceptance work / blockers |
|---|---|---|
| 1 — Projects, database, account/access foundation | `Phase1To11ContractTests`: invalid/unsafe creation; scoped reads/collections; persisted settings; invalid updates preserve settings; viewer→editor→removed permissions. API fixture executes real registration/login. Existing PostgreSQL migrations and tenant quota query tests. | Dedicated registration validation, refresh/reset/confirmation/TOTP flows; complete role×endpoint mutation matrix; quota boundaries for every create/import route; browser project/member journeys. |
| 2 — Crawler | `CrawlPipelinePostgresTests`, `DurableQueuesPostgresTests`, lifecycle/frontier/concurrency, robots/sitemap, URL normalization, SSRF/pinned-handler and fetcher suites. New API tests deny cross-tenant queueing. | Controlled multi-page HTTP fixtures, restart/crash and concurrent worker stress with assertions on every saved page; real-browser crawl controls; JS rendering is not implemented. Existing pipeline uses example.com and is not a deterministic full-site fixture. |
| 3 — Rule engine | `Phase3RuleEngineCoverageTests` (68): golden/empty/deterministic inputs, positive/negative boundaries; **fixture type set must equal the real DI registrations**. `GoldenPageRegressionTests` (33), other audit suites. Currently 36 `ISeoRule` registrations. | Every rule's malformed-evidence and boundary combinations, full rules→issues→recommendations HTTP/database lifecycle. The 300-rule target is not implemented. The duplicate-title reminder checks only selected rules and is not proof of a complete duplicate-title pipeline. |
| 4 — Scoring/dashboard and performance | `ScoringEngineTests` + `Phase4ScoringDeterminismTests` (14); API tests for missing score, partial history, and rejecting a stale score as current. Resource heuristics covered in rule tests. | Dashboard browser states and historical filtering; additional persistence scenarios. Core Web Vitals measurements/provider pipeline is missing; resource heuristics are not measured LCP/INP/CLS. |
| 5 — Content and link graph | Golden fixtures, similarity/graph suites, `Phase4ContentEngineTests`, `Phase3LinkGraphTests`; content API now returns null readability with no measured pages, with backend and frontend regressions. | Complete graph/content HTTP tests with seeded multi-page evidence, truncation and malformed input, multilingual browser journeys. A thin-content result for an observed 40-word text is not an unknown measurement. |
| 6 — Keyword intelligence | `Phase1To11ContractTests`: create with unknown metrics→import observed metrics/source→opportunity→delete; batch de-duplication and tenant isolation. `Phase5KeywordIntelligenceTests`. | Full trend/cannibalization segmentation, batch quota enforcement, import failure/replay and timezone boundaries, browser CRUD/import. |
| 7 — Search Console | `SearchConsoleServiceTests` (13): authentic Google snake_case token contract using fixtures, encrypted storage, invalid/expired OAuth state, tenant denial, sync/replay, no-data response, date validation, missing numeric evidence rejection, Google country/device filter shape. API disconnected status/sync failure. | Token refresh/rotation and revocation, pagination and 100k cap, partial-page failure semantics, real Google property permissions/OAuth/sync with configured account. Fixture transport tests are not live Google verification. |
| 8 — Competitors | `Phase6CompetitorIntelligenceTests`; API comparison retains null for unobserved metrics; active flag update/delete and foreign-tenant denial. | Deterministic bounded competitor crawl integration, comparison/gaps across real snapshots, failure/retry and browser workflows. |
| 9 — Backlinks | `Phase9BacklinkProviderTests` (15): provider/service/job/domain paths; API provider-disabled status, cross-tenant history/status and queue denial. | Full HTTP refresh→job→history/compare round-trip on PostgreSQL, concurrency, vendor contract/live acceptance and UI states. No vendor adapter is configured. |
| 10 — SERP | `Phase10SerpIntelligenceTests` (19): observed results, source/availability, comparisons/segmentation; API disabled status and access checks. | Full HTTP refresh→job→history/results/compare on PostgreSQL, wrong keyword/snapshot binding checks, concurrency, vendor/live/browser acceptance. Empty unavailable SERP must never mean observed absence of rank. |
| 11 — AEO/GEO | `Phase11AeoTests` (31) + `Phase11AeoServiceTests` (6): real analyzer/service; stored evidence isolation, failed robots→unknown, upsert/replay/read, viewer/outsider denial without fetch, wrong-project crawl denial, malformed heading shapes; missing-crawl and tenant HTTP tests. | AEO UI is absent; findings are not integrated into the issues pipeline. Positive HTTP/PG/browser flows, cancellation/concurrency and full malformed evidence coverage remain. Unit/service success does not close these product gaps. |

### Cross-cutting scopes (including the older plan's phases 9–11)

- Reports/PDF, monitoring/outbox, webhook signatures, billing/entitlements, retention, localization and observability already have named suites in the CI table. They are **not newly certified complete** by this report.
- No complete end-to-end browser suite or documented production-scale load/performance acceptance run has been executed here.
- Real SMTP, real Google authorization, live backlink/SERP vendors and production infrastructure are not validated by test doubles or an InMemory database.
- A zero count of observed rows is valid. A score/rank/metric that was not observed must be null or have explicit unavailable status, never a fabricated zero/perfect score.

## Regressions found in this pass

1. Content API reported average readability `0` for zero analyzed pages. Fixed to nullable output; frontend type and regression updated (UI already renders null as an em dash).
2. Google OAuth JSON uses `access_token`, `refresh_token`, `expires_in`; the previous DTO did not map those names. CI reproduced the failure. Explicit JSON names now round-trip encrypted tokens.
3. Google dimension filters require `operator`, not `operatorType`. Added outbound payload test and corrected serialization.
4. Absent Search Console measurements were converted to zero. Missing/non-numeric evidence is now rejected before keyword creation; tests remove each of clicks, impressions, ctr and position in turn.
5. AEO heading parsing threw on non-object elements or wrong property types. Valid object fixtures are used for positive storage tests; malformed-shape regressions separately verify no crash or invented heading signals.

## Commits and next gate

- `0cb1d41`: added API, Search Console and AEO orchestration tests; fixed empty readability. CI compiled successfully and exposed two failures.
- `6998f35`: fixes and additional regressions, plus DI rule coverage parity. CI fully green at 443 backend cases.

Next work stays within phases 1–11: close the remaining HTTP/PG/browser paths, then the missing AEO surfaces, then live external-service acceptance where configuration permits. Do not start phase 12 or label phases 1–11 fully tested while the above blockers remain.
