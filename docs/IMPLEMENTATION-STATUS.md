# SEO Loodoi implementation status

## Product decisions

- Standalone product; no DigiStore dependency.
- Brand: SEO Loodoi.
- Backend: .NET 10 / ASP.NET Core / Clean Architecture.
- Frontend: React + TypeScript.
- Primary database: PostgreSQL.
- Delivery strategy: usable MVP first, then all advanced phases.

## Completed foundation

- Solution and dependency boundaries
- Standalone Identity model
- Core project/crawl/page/link/issue/score/recommendation entities
- Extensible entities for keywords, competitors, AI, reports, integrations and alerts
- EF Core indexes, PostgreSQL migrations and project ownership scoping
- Authenticated project and crawl lifecycle APIs
- Durable database-backed SEO job queue with idempotency keys, leases, retries and exponential backoff
- Durable crawl frontier with PostgreSQL `FOR UPDATE SKIP LOCKED`, lease recovery and duplicate suppression
- Batch-based crawl worker supporting pause, resume, cancel, heartbeat and restart-safe continuation
- Real page/snapshot/link evidence persistence and bounded text retention
- Idempotent post-crawl rule analysis, issue persistence and score snapshots
- Host-level concurrency and request-spacing coordinator applied to every fetch and redirect hop
- Affected-page-ratio scoring model v1.1.0 (severity × scope, with category caps)
- Persian-aware content normalization, exact hashing, shingle similarity and duplicate clustering
- Directed internal-link graph with de-duplicated edges, in/out degree, orphan detection and DigiSEO internal authority
- Site-level duplicate/near-duplicate and orphan issues integrated into post-crawl analysis
- Expanded canonical, noindex, HTTPS, metadata-length and heading-structure rules
- URL normalization with conservative query handling
- RFC-oriented robots.txt parser, user-agent selection and allow/disallow evaluation
- Secure XML sitemap and sitemap-index parser with XXE/DTD protection
- SSRF-safe page fetcher with DNS validation on every redirect hop and strict response-size limits
- HTML evidence extraction for metadata, headings, canonical, links, images, JSON-LD, OpenGraph and Twitter cards
- Versioned deterministic scoring engine
- Initial rule set with structured evidence
- SSRF destination guard for IPv4/IPv6 and DNS results
- API rate limiting and CORS allowlist
- Responsive Persian authentication and dashboard UI connected to real APIs (no fabricated production metrics)
- Product-specific account registration with full name, optional company, email, password confirmation, explicit terms acceptance and preferred language
- Localized per-field server validation and Identity error mapping (no generic validation-only response)
- Confirmation-link dispatch when `Identity:RequireConfirmedEmail` is enabled, browser password-reset request/token flow, and localized recovery states
- Bearer login/refresh session flow with automatic token refresh, profile endpoint, TOTP 2FA setup/recovery codes and logout
- Default bare Identity registration route blocked so terms/profile requirements cannot be bypassed
- User registration profile migration with safe handling of existing rows
- Live project creation/selection, crawl start/pause/resume/cancel, progress polling, issues and score views
- Account/profile, TOTP security, crawl-policy, team-membership and alert-rule settings UI connected to scoped APIs
- Dedicated authentication rate limit plus authenticated API rate limit
- Development-only in-memory preview provider; production remains PostgreSQL-only by default
- Commercial-friendly AwesomeAssertions test dependency (no FluentAssertions license warning)
- Domain/security test project is present; API/PostgreSQL integration coverage and full backend execution are pending a .NET 10 SDK-enabled CI environment

## Delivered in the product-hardening slice

- Server-enforced Starter quota and usage endpoint; crawl starts cannot reserve more than the monthly page allowance.
- Project member roles with owner/editor/viewer authorization and cross-tenant resource scoping.
- Per-project crawl settings, schedule fields, robots opt-out, response/header/redirect evidence and scheduled crawl worker.
- Recommendation persistence and status workflow generated from every triggered rule.
- Keyword tracking, source-labelled metrics, opportunity scoring and Google Search Console OAuth/sync boundary. Tokens are protected with ASP.NET Data Protection and never returned by API.
- Competitor registry with explicit user action and no fabricated traffic metrics.
- AI evidence packets, structured provider output, deterministic fallback and cached analyses. Crawled text is data, never instructions.
- Snapshot reports in JSON, CSV and PDF plus score-drop/critical/crawl-failure alerts with a durable, leased email/webhook outbox, retry backoff and dead-letter state.
- Structured tenant audit log persistence and a project-scoped audit-log API for account, project, crawl, settings and membership actions.
- Expanded frontend navigation is connected to real API data and shows empty states rather than invented metrics. Search Console can start an evidence-backed 28-day synchronization from the keyword workspace.

## Remaining production release work

1. Testcontainers-based PostgreSQL integration suite and migration validation in CI.
2. Production SMTP/provider credentials, durable shared Data Protection key storage and branded email templates.
3. Full competitor comparison crawls, rank provider adapters and richer Search Console dimensions.
4. Full Persian/English resource localization and accessibility audit.
5. Structured observability, load/security suites and deployment automation.
6. Backup/recovery drills, retention cleanup, signed webhook payloads and PDF typography for Persian text.

## Security invariants

- Every resource query is owner/project scoped.
- Project IDs from clients never imply authorization.
- Every outbound hop must pass the SSRF guard, including redirects.
- Crawled content is untrusted data, never AI instructions.
- Provider credentials are encrypted and excluded from logs.
- AI is optional and can only consume evidence packets.
- No raw HTML retention by default.

## Not yet production-ready

This repository is an actively implemented MVP foundation, not a completed production release. Crawler execution, migrations, quotas, external adapters, reports, monitoring, localization resources, security/load suites and deployment automation remain open milestones.
