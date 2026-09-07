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
- Bearer login/refresh session flow with automatic token refresh, profile endpoint and logout
- Default bare Identity registration route blocked so terms/profile requirements cannot be bypassed
- User registration profile migration with safe handling of existing rows
- Live project creation/selection, crawl start/pause/resume/cancel, progress polling, issues and score views
- Dedicated authentication rate limit plus authenticated API rate limit
- Development-only in-memory preview provider; production remains PostgreSQL-only by default
- Commercial-friendly AwesomeAssertions test dependency (no FluentAssertions license warning)
- 45 passing unit/security tests plus end-to-end auth/project/crawl/analyze/score and cross-tenant smoke tests

## Next MVP slices

1. Testcontainers-based PostgreSQL integration suite and migration validation
2. Quota enforcement and usage metering
3. Keyword entities/metrics, opportunity scoring and Search Console adapter boundary
4. Competitor projects and controlled comparison crawls
5. AI evidence packets, provider abstraction and schema-validated outputs
6. Reports, monitoring and alert delivery
7. Full Persian/English resource localization and accessibility audit
8. Structured observability, load/security suites and deployment automation

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
