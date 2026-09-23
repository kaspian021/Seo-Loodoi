# P0 — Repository Audit, Production Hardening, Billing/Entitlement, Auth/Email

Date: 2026-09-23 · Branch: `arena/01a0cf09-seo-loodoi` · Base: `82faf2b`

This covers the P0 group of the roadmap (Audit → Hardening → Tests → Billing → Auth/Email).
Later groups are listed at the end with what they need.

## 1. Audit summary (what exists)

The repo is a mature, layered .NET 10 solution (Domain / Application / Infrastructure / Api / Web).
Earlier hardening passes (FINDINGS F1–F15, all closed) and capability phases 1–11 already shipped.
The inventory:

| Area | State | Evidence |
|---|---|---|
| Identity (register, login, refresh, reset, TOTP 2FA, recovery codes) | REAL | `Program.cs` account endpoints, MapIdentityApi |
| Tenant scoping / RBAC (Owner/Admin/Editor/Viewer) | REAL | `ProjectAccessService`, every `/api/seo/projects/{id}` endpoint |
| Durable jobs, frontier (SKIP LOCKED), crawl pause/resume, recovery sweeper | REAL + PG tests | `DurableQueues.cs`, `DurableQueuesPostgresTests` |
| SSRF guard + DNS-pinned connect (F2) | REAL + tests | `SsrfPinnedHandler`, `OutboundUrlGuard` |
| robots / sitemap / extractor / normalizer | REAL + unit tests | `Crawling/*` |
| Rules, scoring v2, duplicate, link graph, AEO, SERP, backlink provider abstractions | REAL (provider data = NotConfigured by default, never fabricated) | `Application/Analysis`, `Aeo`, `Serp`, `Backlinks` |
| Signed alert webhooks + outbox w/ dead-letter | REAL + PG tests | `AlertDeliveryWorker` |
| Reports JSON/CSV/PDF with Persian shaping | REAL + tests | `ReportService` |
| Billing / entitlements | **PARTIAL — critical flaw, fixed here** | see §2 |
| Email delivery | REAL SMTP, **off by default with no production guard — fixed here** | `SmtpEmailSender` |
| CI | build `-warnaserror`, unit + API contract + PG integration + migrations check + frontend lint/test/build | `.github/workflows/ci.yml` |

## 2. P0 security findings (this pass)

| ID | Severity | Finding | Status |
|---|---|---|---|
| P0-1 | **Critical** | `POST /billing/checkout/return` applied the paid plan directly. With `EnableDevMock=true` **as the compiled default**, any logged-in user could call checkout then return and get Enterprise without paying. The frontend also did this client-side (`upgrade()` called `processReturn` immediately). | **FIXED**: production return only validates/consumes state and refreshes; plan changes only via signed webhook. Dev adapter off by default, allowed only in `appsettings.Development.json`, refused at startup elsewhere. |
| P0-2 | High | Checkout return token replayable for its 30 min lifetime (no server-side nonce). | **FIXED**: `BillingCheckoutSessions` single-use nonce, bound to user + plan. |
| P0-3 | High | Billing webhooks not idempotent: a replay within the timestamp window re-applied state (and could reset credits). | **FIXED**: `ProcessedBillingEvents` unique `EventId` ledger; replays acknowledged, not re-applied; concurrent duplicate insert handled. |
| P0-4 | High | Webhook accepted unknown status (defaulted to `Active`) and unknown plans (fell back to Starter). | **FIXED**: both rejected. |
| P0-5 | Medium | Webhook could re-bind an existing Loodoi account to a different `UserId`. | **FIXED**: account/user mismatch rejected; the stable `LoodoiAccountId` is linked explicitly. |
| P0-6 | Medium | `returnUrl` unvalidated → open redirect through checkout. | **FIXED**: relative paths or allow-listed origins only (`AllowedOrigins` + `Application:WebBaseUrl` + `Billing:AllowedReturnOrigins`). |
| P0-7 | Medium | Hard-coded dev HMAC secrets were the silent production default. | **FIXED**: startup validator rejects default/short/equal secrets. |
| P0-8 | Medium | AI credit consumption was read-modify-write (race could overspend). Inactive subscriptions could still spend. | **FIXED**: atomic conditional `UPDATE` on PostgreSQL; inactive → denied. |
| P0-9 | Medium | New tenants were silently granted the paid Starter plan. | **FIXED**: default `Billing:DefaultPlan=Free`. |
| P0-10 | Medium | Email verification/reset could be disabled in production with no warning. | **FIXED**: validator requires `Email:Enabled` + `RequireConfirmedEmail` + SSL + host/from, unless explicitly opted out with `Identity:AllowUnverifiedAccountsInProduction=true`. |
| P0-11 | Low | Webhook body read unbounded. | **FIXED**: 64 KB cap → 413. |
| P0-12 | Low | Checkout endpoint returned raw exception messages as 500. | **FIXED**: validation → 400. |

## 3. Billing architecture (as implemented now)

```
SEO Loodoi  --POST /billing/checkout-->  signed state (HMAC, single-use nonce, 30 min)
     |                                            |
     |  302 → Billing:CheckoutEndpoint?token&plan&account&returnUrl
     v
Loodoi Billing → payment provider → subscription
     |
     |  server-to-server  POST /api/billing/webhook
     |  X-Loodoi-Timestamp / X-Loodoi-Signature = sha256=HMAC(ts + "." + body)
     v
EntitlementService (idempotent by EventId) → TenantEntitlements  ← source of truth
     ^
     |  browser returns → POST /billing/checkout/return (consumes nonce, refreshes, NEVER grants)
```

Webhook contract (JSON, web casing): `eventId, eventType, loodoiAccountId, userId, plan, status
(Active|Trialing|PastDue|Canceled|Expired), periodStart, periodEnd`, optional limit overrides and `features`.
A new `periodStart` resets monthly AI credits.

**Not yet real:** the Loodoi billing service itself. `Billing:CheckoutEndpoint` and the webhook sender are an
external contract; nothing here pretends payment happened. The only payment-free path is the dev adapter.

## 4. Required production configuration

```
Billing__SecretKey, Billing__WebhookSecret   (≥32 chars, distinct, not the dev defaults)
Billing__CheckoutEndpoint                    (https)
Email__Enabled=true, Email__Host, Email__FromAddress, Email__UserName, Email__Password, Email__EnableSsl=true
Identity__RequireConfirmedEmail=true
Application__PublicBaseUrl, Application__WebBaseUrl  (https)
ConnectionStrings__Postgres                  (no CHANGE_ME)
```
Startup fails with a list of the offending **keys** (never values) if any are unsafe.

## 5. Changes

- **Migration:** `20260923120000_AddBillingCheckoutSessionsAndEvents` (2 tables, unique indexes). Model snapshot updated.
- **API:** no route changes. Behavior changes: `checkout/return` no longer grants plans outside dev (it returns
  the current entitlement), and replayed or foreign tokens now return 409. `checkout` returns 400 for a disallowed
  `returnUrl`. The webhook now rejects unknown plan/status (401) and oversize bodies (413).
- **Frontend:** upgrade button now redirects to `checkoutUrl`. The return page consumes the `session`/`token`
  parameter, strips it from the URL, opens Settings → Billing and shows "upgraded" or "pending confirmation"
  (new i18n key in all 11 languages).
- **Tests:** `BillingSecurityTests` (single-use, forgery, cross-user, open redirect, idempotency, cancellation,
  mismatch, validator matrix) + 3 new API contract tests.

## 6. Remaining limitations / next steps

- Seat enforcement (`MaxTeamMembers`) and project/keyword race-free quota consumption still use count-then-insert;
  they need a serializable transaction or a usage-counter row (P0 follow-up / Phase 20).
- The Loodoi Account ID is currently auto-generated (`loodoi_acc_{userId}`) until SSO with central Loodoi
  identity exists. It is replaced by the first webhook that carries the real id.
- `billing/entitlements` for team members resolves the owner via their first membership. Multi-organization
  users need an explicit organization context (Phase 18).
- P1–P4 (Crawler v2, 300+ rules, JS rendering, GSC production, AI expert, automated fixes…) are not started in this pass.
