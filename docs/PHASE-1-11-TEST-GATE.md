# Phase 1–11 verification gate — NOT COMPLETE

Updated: 2026-09-21. **Phase 12 (AI SEO expert) Stages 1–3 are delivered** — see the Phase 12 closure slice below. A green CI run is necessary, not sufficient, for full capability sign-off.

## Phase 12 closure slice — 2026-09-21 (AI expert: credit ordering, provider resilience, UI)

Verified code commits: `4b95744` (Stage 1 — credit ordering, one correction retry, deterministic fallback, contract tests), `b6ffdbe` (test-only unicode-escape fix), `7161591` (Stage 2 — UI, i18n, vitest, E2E). All on branch `arena/01a0c397-seo-loodoi`, based on `82faf2b`; not merged to main.
CI: https://github.com/kaspian021/Seo-Loodoi/actions/runs/35593693243 — **success**: build (warnings as errors), backend tests, migration/model checks, frontend lint/unit/build, pre-flight host readiness and the Playwright suite (including the new AI journey) all green.

**Real measured numbers** (backend from `backend-test-summary`, frontend from the local vitest run mirrored by the green CI step):
- Backend: **482 passed · 0 failed · 482 total** (462 baseline + 20 new cases: 12 `AiSeoExpertTests` — 10 Facts + 1 Theory with 2 cases —, 5 `AiAnalysisServiceTests`, 1 big `AiExpertContractTests`, 2 new facts in `AiAnalysisAccessTests`).
- Frontend: **47 passed** (44 baseline + 3 `AiView.test.tsx`). Lint: 0 errors, 22 pre-existing warnings. Production build green (pre-existing chunk-size warning remains).
- E2E: 4 serial browser journeys green (3 pre-existing + 1 new AI journey).
- No new EF migration was required (`AiAnalyses` already existed).

What this slice implements and pins by test:
- **Credit ordering**: the AI credit is consumed only inside `AiAnalysisService`, after the `CanEdit` guard and after a complete crawl is found. Rejected (viewer) and crawl-less requests consume **zero** credits and touch no entitlement. An exhausted monthly allowance raises `AiCreditsExhaustedException` → HTTP 429 whose problem **title** is the Persian message «اعتبار تحلیل هوش مصنوعی شما برای دوره جاری به پایان رسیده است. لطفاً پلن خود را ارتقا دهید.» (previously the endpoint charged the credit before any guard).
- **Locked metering decision** (pinned by `AiAnalysisServiceTests` and `AiExpertContractTests`): every *accepted* analyze request is metered exactly one credit, including cache replays. Changing this policy requires updating those tests and the E2E credit expectation together.
- **Provider resilience**: invalid JSON, HTTP errors and network failures get exactly **one correction retry** (a user-turn correction message; the system prompt stays a fixed constant), then fall back to the deterministic template. The fallback appends a transparency note to `missingEvidence`, and the `provider` badge (`deterministic-expert-engine` versus `openai-compatible`) is the disclosure channel.
- **Injection boundary**: the system message is a fixed constant on every attempt; crawl text crosses into the prompt only as serialized JSON data in the user turn (tests assert the wire shape, including a hostile-title escalation attempt).
- **Caching**: every result — including deterministic fallback output with its badge and note — is cached under (evidence hash, `promptVersion`) and replayed intact.
- **Stage 2 UI**: `AiView` renders `rootCauses` and `recommendations` (previously hidden) and shows the provider badge with `promptVersion`. Three new i18n keys ship in all 11 catalogs: real Persian/English, the other nine explicitly fall back to English per the established convention until translations are reviewed.
- **E2E AI journey** (`workspace.spec.ts`, no route/API mocks): register → one project on the fixture host (a second project for the same owner must use a distinct host — the duplicate-host 500 gap below still stands) → real crawl + durable analysis → `analysis-status` Succeeded → AI analyze returns the structured output (`provider: deterministic-expert-engine`, `promptVersion: 1.0.0`, non-empty summary/observations/rootCauses/missingEvidence) → the UI renders the new sections and badge → entitlements show plan Starter and **`aiCreditsUsed` exactly 1**.

Still open (not claimed solved): live `openai-compatible` provider acceptance with a real endpoint/key (CI only exercises the deterministic engine and a scripted fake handler); the nine-locale translation review for the new keys; and every pre-existing gap (duplicate-host 500→4xx, load testing, live Google/vendor providers, real third-party crawls, JS rendering, measured CWV).

## Real-browser E2E closure slice — 2026-09-20

A Playwright/Chromium suite now runs in CI against the **unmodified API on real Kestrel and migrated PostgreSQL** (isolated `seoloodoi_e2e_browser` database, created and applied per run). The only substitution is the outbound page transport: `tests/SeoLoodoi.E2E.Host` registers a deterministic `IPageFetcher` fixture serving a controlled two-page `https://example.com` site (robots.txt with a blocked GPTBot group, sitemap, a titled home page and an untitled guide page). Identity, authorization, quotas, rate limiting, durable jobs, the crawler, HTML extraction, the rule engine, scoring and the AEO pipeline all run for real. The host refuses to start without explicit opt-in and a database name prefix, and the fixture transport fails closed for any non-fixture host.

Verified code commit: `6f49aaf`. CI: https://github.com/kaspian021/Seo-Loodoi/actions/runs/35510029004 — **success**: backend 462 passed / 0 failed, migration/model checks, frontend lint/unit/build, pre-flight host readiness, Playwright install and the E2E suite all green.

Executed browser journeys (`src/SeoLoodoi.Web/e2e/workspace.spec.ts`, no route/API mocks):
1. Register → create project → AEO correctly inert before any crawl (no invented score, disabled action) → real crawl + durable analysis through the running job workers → `analysis-status` Succeeded with a real score and issues → explicit AEO run returns the saved report (2 analyzed pages, GPTBot observed blocked) → audit view shows the observed `TITLE_MISSING` page issue and the `AEO_CRAWLERS_BLOCKED` advisory with evidence containing the crawl → user marks it Resolved → AEO re-run preserves the decision → reload → sign out (token cleared) → sign back in.
2. Second project (different host, per the unique `(Owner, host)` schema constraint) has no borrowed AEO data; project switching never shows another project's report; Persian language switch renders RTL with the translated empty state.
3. A different real account sees no trace of the owner project: AEO read and analyze both 404, issue list is an empty 200.

Constraints honored by the suite: the production 120-requests/minute per-user API limit is respected by aligning the second test with the next fixed window (the full cycle in test 1 can nearly exhaust it); the 10/minute auth limit is naturally below threshold. Test artifacts are gitignored (they can contain fixture-only session tokens).

**E2E-discovered product gap (open):** creating a second project for the same owner with an already-claimed host returns **HTTP 500** (`DbUpdateException` from the unique index) instead of a 4xx validation/409 response. The E2E suite works around it with a distinct host; the API should translate this constraint violation into an explicit client error. Not fixed in this stage to keep it pure test/infrastructure work.

Still not covered: load/stress acceptance, live Google/vendor/provider credentials, real third-party site crawls, JS-rendered content, CWV measurements, and the nine-locale translation review. jsdom component tests remain in addition to this suite.

## AEO closure slice — 2026-09-20

The two previously missing AEO surfaces are now implemented, **not a sign-off of all phases 1–11**:

- A dedicated AEO/GEO navigation item and assessment screen. Explicit run/re-run, loading/error/retry/empty states, null-versus-zero scores, crawler policy, sampled evidence and a clear readiness-not-citations disclaimer. Switching project/crawl discards stale responses; successful assessment refreshes the issues list.
- Two site-level `Notice` advisories (`AEO_CRAWLERS_BLOCKED`, `AEO_ANSWER_STRUCTURE_MISSING`) are projected into existing `SeoIssues` with project/crawl scope, evidence and assessment time. They do not alter the per-page SEO scoring formula. Missing/invalid evidence does not trigger an absence rule. A later unavailable check retains the earlier issue's evidence; an observed pass removes only stale open advisories. User Ignored/Resolved decisions are preserved.
- AEO writes are atomic and serialized per crawl on PostgreSQL. Normal SEO analysis retries exclude AEO-owned issue codes from deletion. Cancellation is propagated instead of persisting a misleading successful assessment.
- Real extractor serialization is now tested: PascalCase heading records and JSON-LD script strings are both understood. Root-access evaluation reuses the crawler's robots precedence rather than an approximate duplicate matcher.

Verified code commit: `3951dca` (preceded by `43f279a`, `32291a7`, `7a55c9a`).
CI: https://github.com/kaspian021/Seo-Loodoi/actions/runs/35508327963

**462 backend tests passed, 0 failed, 462 total**. Frontend: **44 tests passed** locally; frontend test/build/lint steps also passed CI. This adds 19 backend cases and 11 frontend cases relative to the previous gate. Frontend lint still reports 22 pre-existing warnings; the production bundle-size warning remains. No new migration was required.

New/extended evidence:
- `AeoWorkflowContractTests`: real HTTP authentication → analyze → saved report → issues → ignore → reanalyze, with a fixture only for outbound robots.
- `AeoIssuesPostgresTests`: real migrated PostgreSQL, concurrent assessment and survival across SEO analysis retries.
- `AeoIssueRulesTests`, extended `Phase11AeoServiceTests`/`Phase11AeoTests`: unknown/malformed evidence, real extraction format, cancellation, tenant scoping, repeat execution, user status and robots precedence.
- `AeoView.test.tsx` (8 component cases) and AEO API client cases: unknown versus zero, errors/retry, empty states, duplicate submissions, project switch and refresh on success.

Limits: these frontend tests use jsdom, not a real-browser full-stack E2E harness. Outbound robots in integration/HTTP tests is controlled test data, not live acceptance. This UI runs AEO explicitly; it does not add an automatic post-crawl AEO scheduler or provider-derived citation tracking. New AEO copy is Persian/English; the other nine UI locales explicitly use English fallback for these keys until translations are reviewed. Backend advisory text remains Persian. Full load testing, vendor credentials and the other phase acceptance gaps below remain open.

## Earlier verified execution

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
| 1 — Projects, database, account/access foundation | `Phase1To11ContractTests`: invalid/unsafe creation; scoped reads/collections; persisted settings; invalid updates preserve settings; viewer→editor→removed permissions. API fixture executes real registration/login. Existing PostgreSQL migrations and tenant quota query tests. Real-browser registration, login, sign-out, project creation and cross-account isolation now execute in the E2E suite (see the E2E slice above). | Dedicated registration validation, refresh/reset/confirmation/TOTP flows; complete role×endpoint mutation matrix; quota boundaries for every create/import route; browser member/team journeys. Duplicate-host project creation still returns 500 (documented above). |
| 2 — Crawler | `CrawlPipelinePostgresTests`, `DurableQueuesPostgresTests`, lifecycle/frontier/concurrency, robots/sitemap, URL normalization, SSRF/pinned-handler and fetcher suites. New API tests deny cross-tenant queueing. The E2E suite now runs the real durable crawl→analysis pipeline in a browser journey against a controlled two-page fixture site on real PostgreSQL. | Restart/crash and concurrent worker stress with assertions on every saved page; crawl pause/resume/cancel in the browser; JS rendering is not implemented. |
| 3 — Rule engine | `Phase3RuleEngineCoverageTests` (68): golden/empty/deterministic inputs, positive/negative boundaries; **fixture type set must equal the real DI registrations**. `GoldenPageRegressionTests` (33), other audit suites. Currently 36 `ISeoRule` registrations. | Every rule's malformed-evidence and boundary combinations, full rules→issues→recommendations HTTP/database lifecycle. The 300-rule target is not implemented. The duplicate-title reminder checks only selected rules and is not proof of a complete duplicate-title pipeline. |
| 4 — Scoring/dashboard and performance | `ScoringEngineTests` + `Phase4ScoringDeterminismTests` (14); API tests for missing score, partial history, and rejecting a stale score as current. Resource heuristics covered in rule tests. | Dashboard browser states and historical filtering; additional persistence scenarios. Core Web Vitals measurements/provider pipeline is missing; resource heuristics are not measured LCP/INP/CLS. |
| 5 — Content and link graph | Golden fixtures, similarity/graph suites, `Phase4ContentEngineTests`, `Phase3LinkGraphTests`; content API now returns null readability with no measured pages, with backend and frontend regressions. | Complete graph/content HTTP tests with seeded multi-page evidence, truncation and malformed input, multilingual browser journeys. A thin-content result for an observed 40-word text is not an unknown measurement. |
| 6 — Keyword intelligence | `Phase1To11ContractTests`: create with unknown metrics→import observed metrics/source→opportunity→delete; batch de-duplication and tenant isolation. `Phase5KeywordIntelligenceTests`. | Full trend/cannibalization segmentation, batch quota enforcement, import failure/replay and timezone boundaries, browser CRUD/import. |
| 7 — Search Console | `SearchConsoleServiceTests` (13): authentic Google snake_case token contract using fixtures, encrypted storage, invalid/expired OAuth state, tenant denial, sync/replay, no-data response, date validation, missing numeric evidence rejection, Google country/device filter shape. API disconnected status/sync failure. | Token refresh/rotation and revocation, pagination and 100k cap, partial-page failure semantics, real Google property permissions/OAuth/sync with configured account. Fixture transport tests are not live Google verification. |
| 8 — Competitors | `Phase6CompetitorIntelligenceTests`; API comparison retains null for unobserved metrics; active flag update/delete and foreign-tenant denial. | Deterministic bounded competitor crawl integration, comparison/gaps across real snapshots, failure/retry and browser workflows. |
| 9 — Backlinks | `Phase9BacklinkProviderTests` (15): provider/service/job/domain paths; API provider-disabled status, cross-tenant history/status and queue denial. | Full HTTP refresh→job→history/compare round-trip on PostgreSQL, concurrency, vendor contract/live acceptance and UI states. No vendor adapter is configured. |
| 10 — SERP | `Phase10SerpIntelligenceTests` (19): observed results, source/availability, comparisons/segmentation; API disabled status and access checks. | Full HTTP refresh→job→history/results/compare on PostgreSQL, wrong keyword/snapshot binding checks, concurrency, vendor/live/browser acceptance. Empty unavailable SERP must never mean observed absence of rank. |
| 11 — AEO/GEO | Assessment UI and evidence-backed issue projection implemented. Unit/service, positive HTTP workflow and concurrent PostgreSQL/retry tests pass; see the closure slice above. Real-browser full-stack AEO journey (inert before crawl, explicit run, evidence, advisory lifecycle, user decision, project isolation) now passes in CI. | Complete malformed-input matrix and load acceptance; reviewed translations for nine fallback locales; automatic post-crawl AEO scheduling is out of scope. Live robots/provider acceptance is not implied by fixtures. |

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

Next work stays within phases 1–11: close the remaining HTTP/PG/browser paths outside the AEO slice, full-stack AEO browser acceptance, then live external-service acceptance where configuration permits. Do not start phase 12 or label phases 1–11 fully tested while the above blockers remain.
