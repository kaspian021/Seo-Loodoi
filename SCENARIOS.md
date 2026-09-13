# SCENARIOS — Phase 1 Execution Matrix (SEO Loodoi @ arena/01a09bd2-seo-loodoi)

## Environment bootstrap — what was achieved, what was blocked (stated explicitly)

| Requirement (RUNNING.md) | Result | Evidence |
|---|---|---|
| Node 20+ | ✅ **v22.22.3** (meets ≥20) | `node --version` |
| Frontend deps | ✅ `npm ci` clean | 0 audit findings, 0 lint findings |
| PostgreSQL 16 | ✅ **PostgreSQL 16.14 live on 127.0.0.1:5432** (db `seo_loodoi`, user `seo_loodoi`, TCP verified) | npm-embedded `@embedded-postgres/linux-x64` binaries + `initdb`/`pg_ctl`; **not** Docker — Docker is not installed in this sandbox |
| .NET 10 SDK | ❌ **BLOCKED by sandbox network policy** — no SDK source reachable (see below) | probe table below |
| NuGet restore | ❌ **BLOCKED** — every nuget CDN cut | probe table below |
| Docker Postgres 16 | ❌ Docker binary absent (`command not found`) | `docker --version` |
| Backend build/test/run locally | ❌ impossible without SDK+NuGet | — |
| **Executable verification fallback** | ✅ **GitHub Actions CI** (`quality` workflow: dotnet 10.0.x + Node 20 + PG16 service + `-warnaserror` + tests + migrations script + FE lint/build/audit) | run **34772536539** on commit `0a1d264`: **all 13 steps green** |

### Network probe evidence (TLS connections reset at the sandbox edge; `000` = connect/TLS failure)

| Host (purpose) | Result |
|---|---|
| builds.dotnet.microsoft.com (dotnet-install CDN) | 000 |
| aka.ms/dotnet/10.0/dotnet-sdk-linux-x64.tar.gz | 000 |
| dotnetcli.azureedge.net / dotnetcli.blob.core.windows.net / dotnetbuilds.azureedge.net | 000 |
| api.nuget.org, nuget.org, azuresearch-usnc.nuget.org, globalcdn.nuget.org, registry.nuget.org | 000 (`SSL_ERROR_SYSCALL`) |
| nuget.pkg.github.com, ghcr.io | 000 |
| deb.debian.org, apt.postgresql.org, ftp.postgresql.org, nodejs.org, repo.maven.apache.org | 000 |
| objects.githubusercontent.com (GitHub release asset binaries) | 000 — and `dotnet/sdk` + `dotnet/installer` + `dotnet/dotnet` releases ship **no binary assets** (only `.sig`/`release.json`) |
| registry.npmjs.org, github.com, api.github.com, codeload.github.com, pypi.org | ✅ 200 |

**Consequence:** the .NET half of the product cannot be compiled, run, or test-executed *inside this sandbox*. GitHub Actions is the only available real .NET+Postgres executor for this branch, so Phase 1/2 execution evidence comes from CI runs plus exhaustive static verification. This is an environment gap, not a code verdict.

## Scenario matrix

Legend: `EXEC-BLOCKED` = cannot run in this sandbox (reason above), static verification performed instead · `EXECUTED` = actually ran here · verdicts cite file:line.

| # | Scenario | Status | Evidence / static verdict |
|---|---|---|---|
| 1 | register→confirm-link→login→TOTP setup+recovery→logout | EXEC-BLOCKED | Code path verified end-to-end statically: `Program.cs:84-116` (register, 503 when email-confirmation on w/o SMTP), `SmtpEmailSender.cs:30-37` (confirm + reset-code deep link), MapIdentityApi login/refresh, `Program.cs:204-236` (2FA enable w/ code verify, recovery-code reset). **Risk noted:** confirm-link delivery requires `Email.Enabled` (off by default) → with `RequireConfirmedEmail=false` (default) login is immediate. |
| 2 | create project→real-site crawl→poll→pause/resume/cancel→completion | EXEC-BLOCKED | `CrawlCommandService.cs:14-73` (serializable start, FOR UPDATE state changes), `CrawlJobHandlers.cs:17-214` (batching, reload-per-page, unique continuation keys line 168), pause/resume/cancel race note (F4). Polling: UI 3 s interval (`App.tsx`). |
| 3 | analysis success + retry-after-failure + partial score | EXEC-BLOCKED | `AnalyzeCrawlJobHandler.cs:20-175` (idempotent purge/rebuild, NoData branch), `AnalysisStatusService.cs:56-86` (retryable matrix, retry enqueue), partial-score semantics `ScoringEngine.cs:21-50`. Unit coverage exists for status machine only (`AnalysisLifecycleTests`). Retry-key collision noted (F7). |
| 4 | issues/recommendations workflows incl. status transitions | EXEC-BLOCKED | List/patch endpoints scoped (`Program.cs:158-167,199-213`); `RecommendationQueryService.cs:25-36` (CanEdit + audit); issue PATCH audit `Program.cs:214-220`. Enum validation present. |
| 5 | three report formats + downloads (PDF bytes) | EXEC-BLOCKED | `ReportService.cs:19-64`. **Bug F1 confirmed statically:** no-completed-crawl → `InvalidOperationException` uncaught → HTTP 500 (`Program.cs:341-344`). PDF bytes hand-rolled (`ToPdf`), Persian→`?` (F11). |
| 6 | manual keywords + opportunities | EXEC-BLOCKED | `KeywordService.cs` — quota check, dup metric → 409 (`Program.cs:183-187`), opportunity math (`impr≥10 && pos 4–15`). |
| 7 | GSC without creds (clean failure) + with creds | EXEC-BLOCKED | No-creds: `connect` → `GetAuthorizationUrlAsync` returns null → **404** (`SearchConsoleService.cs:32-38`); `sync` → `InvalidOperationException` → **409** with Persian message (`Program.cs:232-238`). With-creds path UNTESTED (no credentials available; integrations OFF by default — register #4). |
| 8 | competitor add→crawl→compare | EXEC-BLOCKED | `CompetitorService.cs` + `CompetitorCrawlRunner.cs` (≤25 pages, depth≤1, SSRF-guarded per hop, host-scope enforced). |
| 9 | AI disabled (template) + enabled (mock provider asserting packet-only) | EXEC-BLOCKED | Template path deterministic (`AiSeoExpert.cs:42-54`); packet built only from issues+one page snapshot (`AiSeoExpert.cs:69-80`); cached by evidence hash. Real provider path unexecuted (AI disabled default). **Mock-provider packet-only assertion test is a Phase 2/3 candidate.** |
| 10 | all 3 alert types × 3 channels + outbox retry/dead-letter | EXEC-BLOCKED | Rule validation + SSRF webhook check (`AlertService.cs:20-50`), check logic (`AlertService.cs:58-96`), leased outbox w/ backoff + dead-letter@10 (`AlertDeliveryWorker.cs:44-108`, `AlertDelivery.cs:47-56`), SMTP off by default → email deliveries fail-clean into retry (`SmtpEmailSender.cs:47-51`). |
| 11 | members invite/roles/remove + cross-tenant access-denied | EXEC-BLOCKED | Access matrix verified statically for **every** endpoint (§C of FINDINGS): owner⇒Admin; Editor=edit; Viewer=read-only; all data queries filter `ProjectId` + membership. No unscoped endpoint found. Invite requires pre-existing account (`Program.cs:331-344`). |
| 12 | quota ceilings (projects/pages/keywords/competitors) | EXEC-BLOCKED | `QuotaService.cs:39-68` — Starter 3/500/25/3; crawl-start reserves `MaxPages` (`CrawlCommandService.cs:19`); 429 mapping (`Program.cs`). Perf hazard F6 noted. |
| 13 | scheduled crawl firing | EXEC-BLOCKED | `ScheduledCrawlWorker.cs:14-46` (1-min tick, delegates to quota-checked StartAsync, quota-fail reschedules +10 min — F8), `SeoProject.ScheduleNext` (`SeoProject.cs:39-56`). |
| 14 | STALL REPRO: force stuck crawl, prove recovery sweeper revives | EXEC-BLOCKED | `StalledCrawlRecoveryService.cs:23-61` — 30 s initial delay, 5-min sweep, revives Queued/Running crawls with no live `*:{crawlId}*` job key under `continue-crawl:{id}:recovery:{guid}`. Logic reads correct; **never executed**. |
| 15 | restart-resume: kill API mid-crawl, restart, prove continuation | EXEC-BLOCKED | By design: durable frontier rows (Leased w/ expiry) + recovery sweeper + `NotBefore` recheck jobs. **Never executed**; the PR#3 regression class (register #6) is exactly this. |

## What WAS executed (green evidence)

| Check | Result | Where |
|---|---|---|
| `npm ci` | ✅ clean | local sandbox |
| `npm run lint` (oxlint) | ✅ 0 warnings / 0 errors (6 files, 116 rules) | local sandbox |
| `npm run build` (tsc -b + vite) | ✅ 1837 modules, 270.11 kB JS (80.14 kB gzip) | local sandbox |
| `npm test` (vitest) | ✅ 6/6 passed (`statusText`) | local sandbox |
| `npm audit --audit-level=high --omit=dev` | ✅ 0 vulnerabilities | local sandbox |
| CI `quality` — backend restore | ✅ | GitHub run 34772536539 |
| CI — backend build Release `-warnaserror` | ✅ | same run |
| CI — `dotnet test` (56 tests) | ✅ | same run |
| CI — EF migrations script vs live Postgres 16 | ✅ | same run |
| CI — FE lint/build/audit | ✅ | same run |
| PostgreSQL 16.14 bootstrap + TCP reachability | ✅ | local sandbox (embedded binaries) |

## Phase 1 addendum (post-Phases 2–3)

The Testcontainers suite added in Phases 2–3 (`tests/SeoLoodoi.Integration.Tests`, executed on every push in CI against a real PostgreSQL 16 container) converted parts of the EXEC-BLOCKED matrix into real execution evidence:

| Scenario part | Now executed? | Evidence |
|---|---|---|
| (2) start crawl → poll → completion | ✅ start + batch crawl of live example.com + completion under row lock | `CrawlPipelinePostgresTests.StartCrawl_BatchRun_Analysis_SucceedOnRealPostgres` |
| (2) pause/resume/cancel variants | ✅ incl. queued-pause rejection, cross-user denial, audit entries | `CrawlPipelinePostgresTests.PauseAndCancel_AreEnforcedByTheStateMachine_OnRealPostgres` |
| (3) analysis success + idempotent re-run | ✅ issues/score snapshot rebuilt, no duplication | same pipeline test |
| (12)/(frontier/jobs raw SQL) | ✅ ON CONFLICT dedupe, SKIP LOCKED exclusivity, expiry reclaim, EnqueueOnce, NotBefore, retry backoff | `DurableQueuesPostgresTests` (5 tests) |
| migrations vs live PG16 | ✅ applied from scratch per run — caught F15 | `PostgresFixture.InitializeAsync` |
| (5) report-no-crawl contract | ✅ API-level 409 | `ReportsContractTests` (InMemory pipeline) |

Remaining EXEC-BLOCKED in this sandbox (no .NET process possible locally): recovery-sweeper revival (14), restart-resume (15), scheduled firing (13), alert outbox loops (10), GSC live sync (7b), TOTP interactive login (1b), report byte downloads (5b), quota-ceiling hammering (12b). These require a long-lived process harness and remain honestly unexecuted.

## Phase 1 verdict

**Scenario execution against a running API: 0/15 — all EXEC-BLOCKED by sandbox egress policy (no .NET SDK/NuGet/Docker reachable).** No failure was observed because no execution was possible; this is an environment gap stated explicitly, per mission rules. All 15 scenarios received line-level static verification (above), and the build/test/migration layers were executed green in CI. The dominant unproven surface remains the runtime behavior class from register item 6 — that is what Phase 2 regression tests and the Phase 3 Testcontainers suite must close.
