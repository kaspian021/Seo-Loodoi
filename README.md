# SEO Loodoi

Independent SEO intelligence and optimization SaaS. The deterministic engine owns crawl facts, rules, scoring and evidence; external search/AI providers are optional adapters.

## Current milestone — MVP foundation

- .NET 10 clean four-project backend
- ASP.NET Core Identity bearer authentication
- PostgreSQL / EF Core persistence model
- owner-scoped project endpoints
- deterministic URL normalization and scoring
- first independently testable SEO rules
- robots.txt and secure Sitemap parsers
- SSRF-safe fetcher with redirect revalidation and response limits
- HTML SEO evidence extractor (metadata, links, images, JSON-LD and social cards)
- durable leased background jobs and resumable PostgreSQL crawl frontier
- end-to-end batched crawl, evidence persistence, deterministic analysis and score snapshots
- host-level concurrency/rate coordination and redirect-hop throttling
- weighted scoring v2.0.0, Persian-aware duplicate clustering and internal-link graph analysis
- authenticated start/pause/resume/cancel/progress/issues/scores APIs
- real product registration (profile, company, password confirmation, terms and localized field errors), confirmation-link delivery, password reset and TOTP 2FA
- React bearer authentication with refresh flow, project management, crawl polling, issues, scores and account/team/project settings
- evidence-backed workspace views for crawl history, audit details, internal-link evidence, content, keywords, competitors, AI explanation and reports
- quota enforcement and usage metering for projects, crawl pages, keywords and competitors
- member roles (viewer/editor/admin) with owner/member scoped resource queries
- keyword tracking with source-labelled metrics and Search Console OAuth, encrypted tokens, pagination and opportunity scoring
- provider-safe AI evidence packets with deterministic fallback, cached structured output and prompt-injection isolation
- JSON, CSV and snapshot PDF reports plus dashboard alerts, scheduled checks and durable SSRF-revalidated email/webhook delivery with retry state
- tenant-scoped audit log persistence/API for account, project, crawl, settings and membership activity
- development-only in-memory preview mode; PostgreSQL remains the production default
- domain/security coverage for scheduling, evidence rules, redirects and audit invariants; frontend build/lint and a GitHub Actions backend quality pipeline are configured

## Run locally

Requirements: .NET 10 SDK, Node 20+, PostgreSQL 16+.

```bash
cp .env.example .env
# provide ConnectionStrings__Postgres through environment or user-secrets
dotnet restore SeoLoodoi.slnx
dotnet run --project src/SeoLoodoi.Api
cd src/SeoLoodoi.Web && npm ci && npm run dev
```

Never commit production passwords or provider tokens. The password in `appsettings.json` is a non-production placeholder and must be overridden.

## Architecture

- `SeoLoodoi.Domain`: entities, value objects and invariants; no infrastructure dependencies.
- `SeoLoodoi.Application`: use-case contracts, deterministic rules, scoring and URL normalization.
- `SeoLoodoi.Infrastructure`: PostgreSQL, Identity and secured outbound adapters.
- `SeoLoodoi.Api`: authenticated HTTP boundary and rate limiting.
- `SeoLoodoi.Web`: React + TypeScript responsive RTL/LTR UI.

See `docs/IMPLEMENTATION-STATUS.md` for phased delivery status and security decisions.
