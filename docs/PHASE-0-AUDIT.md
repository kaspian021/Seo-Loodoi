# PHASE 0 — FULL REPOSITORY AUDIT & CAPABILITY MATRIX
**SEO Loodoi Intelligence & Optimization Platform**  
*Audit Date: September 2026 | Auditor: Principal Software Architect & Lead Security/SEO Engineer*

---

## 1. Executive Summary & Architecture Assessment

SEO Loodoi is a modern, high-standard .NET 10 / React SaaS platform built on Clean Architecture principles. It distinguishes itself from generic SEO wrappers by enforcing a strict separation between **deterministic crawl facts** and **probabilistic AI interpretation**.

### Core Architecture Review
- **Clean Architecture Hierarchy**:
  - `SeoLoodoi.Domain`: Pure domain models, value objects, and invariants. Zero infrastructure dependencies. Entities encapsulate business rules (e.g. `Crawl.Start`, `CrawlFrontierItem.TryLease`, `SeoProject.ScheduleNext`, `AlertDelivery.MarkFailed`).
  - `SeoLoodoi.Application`: Use cases, interfaces, contracts, deterministic rule evaluation, scoring engine v2.0.0, internal link graph analysis, content shingling/similarity, and URL normalization.
  - `SeoLoodoi.Infrastructure`: Concrete adapters for PostgreSQL 16 (EF Core 10, raw SQL `FOR UPDATE SKIP LOCKED`), ASP.NET Core Identity, durable background workers, AngleSharp HTML parser, embedded Vazirmatn Persian TrueType PDF generator, SSRF-pinned network fetcher, and Google Search Console / AI provider adapters.
  - `SeoLoodoi.Api`: Minimal API boundary, rate limiting, security middleware, JWT bearer tokens, TOTP 2FA, structured JSON logging.
  - `SeoLoodoi.Web`: React 18, TypeScript, Vite, responsive 11-language internationalization (canonical Persian RTL + LTR languages), accessible components, live crawl polling, and multi-workspace UI.

### Test & CI/CD Status
- **Backend Quality**: 75 Domain unit tests, 7 API contract tests, 10 PostgreSQL integration tests (running on real PostgreSQL 16 via Testcontainers in GitHub Actions).
- **Frontend Quality**: 22 Vitest tests (i18n, statusText, api client, a11y smoke tests), zero oxlint errors, clean production bundle build.
- **CI Pipeline**: GitHub Actions (`.github/workflows/ci.yml`) runs on every push to `main` and `arena/**`, building with `-warnaserror` across backend and frontend, validating idempotent EF Core migrations against PostgreSQL 16.

---

## 2. Comprehensive Capability Matrix

| # | Capability | Existing | Partial | Missing | Risk | Priority | Proposed Implementation |
|---|---|---|---|---|---|---|---|
| 1 | **Clean Architecture & Domain Model** | ✅ Real | | | Low | P0 | Maintain strict 0-dep Domain, extend entities for AEO, Backlinks, SERP, Fixes, Entitlements. |
| 2 | **PostgreSQL Persistence & Migrations** | ✅ Real | | | Low | P0 | Keep idempotent migrations, aligned model snapshot, and real PostgreSQL verification. |
| 3 | **Identity, 2FA, Session Management** | ✅ Real | | | Low | P0 | Hardened TOTP 2FA, recovery codes, secure bearer tokens, registration validation. Configure production email. |
| 4 | **RBAC & Project Tenancy Scoping** | ✅ Real | | | Low | P0 | Every endpoint validated with `IProjectAccessService` (Owner/Admin/Editor/Viewer). Maintain cross-tenant invariants. |
| 5 | **Central Loodoi Billing & Entitlements** | | ⚠️ Partial | | High | P0 | Replace static `QuotaOptions` with `IEntitlementService`, signed return state validation, Loodoi Account ID integration. |
| 6 | **Durable Background Job Queue** | ✅ Real | | | Low | P0 | PostgreSQL `FOR UPDATE SKIP LOCKED`, lease management, exponential backoff, dead-letter state. |
| 7 | **Resumable Crawl Frontier** | ✅ Real | | | Medium | P1 | Frontier items leasable with crash recovery (`StalledCrawlRecoveryService`), host-level rate coordination. |
| 8 | **SSRF & DNS-Rebinding Guard** | ✅ Real | | | Low | P0 | `OutboundUrlGuard` + `SsrfPinnedHandler` connecting directly to pre-validated socket IP. Protects all outbound requests. |
| 9 | **Robots.txt & Sitemap Discovery** | ✅ Real | | | Low | P1 | RFC-compliant parser, crawl-delay, sitemap-index support, XXE-safe XML parsing. |
| 10 | **Deterministic SEO Rule Engine** | | ⚠️ Partial (20 rules) | | Medium | P1 | Expand rule framework to 300+ deterministic checks covering 12 standard SEO categories. |
| 11 | **Deterministic Scoring Engine v2.0** | ✅ Real | | | Low | P1 | Weighted category penalties, affected-page-ratio model, partial evaluation safety (no fabricated 100s). |
| 12 | **Internal Link Graph & PageRank** | ✅ Real | | | Low | P1 | Directed graph, in/out degree, orphan detection, internal authority calculation. |
| 13 | **Duplicate Content Detection** | ✅ Real | | | Low | P1 | Persian-normalized FormKC, exact SHA256 hashing, MinHash/Jaccard shingle similarity clustering. |
| 14 | **JavaScript Rendering & DOM Diffing** | | | ❌ Missing | High | P1 | Chromium / Playwright headless rendering pipeline; Raw HTML vs Rendered DOM comparison engine. |
| 15 | **Core Web Vitals & Performance** | | ⚠️ Partial (TTFB only) | | Medium | P1 | Performance diagnostic engine: LCP, INP, CLS, FCP, TTFB, resource weight analysis (JS/CSS/Image). |
| 16 | **Content Intelligence Engine** | | ⚠️ Partial (Word count) | | Medium | P2 | Content scoring, topical relevance, semantic coverage, search intent, content depth, readability, entity coverage. |
| 17 | **Keyword Tracking & Opportunities** | ✅ Real | | | Medium | P2 | Tracked keywords, opportunity score (`impr >= 10 && pos 4-15`), keyword cannibalization detection. |
| 18 | **Google Search Console Integration** | | ⚠️ Partial (OAuth + 28d sync) | | Medium | P2 | Multi-property selection, automated scheduled sync, device/country slicing, historical trend tracking. |
| 19 | **Competitor Intelligence** | ✅ Real | | | Low | P2 | Bounded crawl (25 pages, depth 1), comparative metrics based strictly on observed evidence. |
| 20 | **Backlink Provider Architecture** | | | ❌ Missing | High | P2 | `IBacklinkProvider` vendor-neutral abstraction (referring domains, anchor text, dofollow/nofollow, new/lost links). |
| 21 | **SERP Intelligence Engine** | | | ❌ Missing | High | P2 | `ISerpProvider` vendor-neutral abstraction (organic positions, SERP features: snippets, PAA, maps, video, shopping). |
| 22 | **AEO / GEO / AI Search Visibility** | | | ❌ Missing | Medium | P3 | AI bot accessibility rules (GPTBot, ClaudeBot, etc.), Answer Readiness Score, Citation Readiness Score, AI Visibility Score. |
| 23 | **AI SEO Expert & Prompt Safety** | ✅ Real | | | Low | P3 | Evidence-packet architecture, prompt injection isolation, deterministic fallback, cached structured output. |
| 24 | **Automated SEO Fixes Workflow** | | | ❌ Missing | High | P4 | Safe workflow: Issue → Proposed Fix → Diff → User Approval → Apply → Recrawl Verification → Confirm Resolution. |
| 25 | **Historical Audit Comparison** | | ⚠️ Partial (Score snapshots) | | Medium | P1 | Full audit snapshot comparison: resolved issues, new issues, regressed issues, page delta, category score trends. |
| 26 | **Monitoring & Alert Outbox** | ✅ Real | | | Low | P1 | Leased outbox delivery, retry backoff, dead-letter threshold, signed HMAC-SHA256 webhooks, email alerts. |
| 27 | **Multi-format Reporting** | ✅ Real | | | Low | P1 | JSON, CSV, and Persian-shaped PDF reports with embedded Vazirmatn font. |
| 28 | **Frontend UX & Accessibility** | ✅ Real | | | Medium | P1 | 11 languages, RTL/LTR synchronization, responsive design, dark/light theme, accessible controls. |

---

## 3. Product Gap Analysis Against Benchmark Platforms

### 1. Versus Semrush / SE Ranking
- **Strengths in SEO Loodoi**: Native Persian text shaping and RTL support, zero-knowledge deterministic evidence packets for AI, strict SSRF and tenant isolation, open architecture.
- **Current Gaps**: Lacks commercial backlink database provider adapter, SERP keyword difficulty provider, and multi-channel domain overview.
- **Strategy**: Implement `IBacklinkProvider` and `ISerpProvider` abstractions so data feeds from external APIs or internal crawlers can plug in seamlessly without hardcoding dependencies.

### 2. Versus Sitebulb / Screaming Frog
- **Strengths in SEO Loodoi**: Cloud-native SaaS architecture, durable database-backed crawl frontier, background worker persistence, automated continuous monitoring and scheduled audits.
- **Current Gaps**: Screaming Frog & Sitebulb have 300+ technical diagnostic rules, JavaScript DOM rendering, and raw vs rendered HTML diffing.
- **Strategy**: Phase 1 & 2 expand deterministic rules to 300+ checks and introduce Chromium-compatible rendering for raw vs rendered DOM analysis.

### 3. Versus Modern AEO / GEO Platforms (e.g. Profound, Peec AI)
- **Strengths in SEO Loodoi**: Native content shingling, Persian semantic normalization, evidence-based AI validation.
- **Current Gaps**: Needs dedicated AI Search Visibility scores: AI Crawler Accessibility, Citation Readiness, Answer Readiness, and structured factual density analysis.

---

## 4. Security Findings & Technical Debt

1. **SSRF Guard & DNS Rebinding**: Fixed and verified via `SsrfPinnedHandler`. Sockets connect to the pre-validated IP address directly.
2. **Alert Webhook Security**: Webhook secrets use HMAC-SHA256 (`X-Loodoi-Signature` and `X-Loodoi-Timestamp`). Replay protection and tamper rejection verified by tests.
3. **Data Protection**: Google Search Console access and refresh tokens are encrypted at rest using ASP.NET Core Data Protection.
4. **AI Prompt Injection Isolation**: Crawled text is never concatenated into system instructions; it is encapsulated inside an untrusted JSON `EVIDENCE_PACKET` with deterministic fallbacks.
5. **Technical Debt**:
   - `src/SeoLoodoi.Web/src/App.tsx` contains 61 KB in a single file; modularization into feature-based view components will improve maintainability.
   - Rule engine currently contains 19 rules; need to scale to a modular 300+ rule framework with category-specific visitors.
   - Billing system currently relies on hardcoded `QuotaOptions`; requires migration to central Loodoi entitlement integration.

---

## 5. Prioritized Implementation Roadmap

### P0 — Core Hardening & Central Entitlements (Immediate Foundation)
1. **Central Loodoi Billing & Entitlements Architecture**:
   - Implement `IEntitlementService` and Loodoi shared identity/account model.
   - Plan tier definitions (Free, Starter, Pro, Enterprise) with dynamic quotas for projects, crawl pages, keywords, competitors, and AI credits.
   - Secure signed return state, anti-CSRF token validation, and entitlement refresh endpoint.
2. **Production Email & Identity Configuration**:
   - Production SMTP options and verifiable delivery templates.
   - Complete 2FA and password reset flows with localized error handling.

### P1 — Scalable Crawler & Comprehensive Technical SEO Engine
1. **Crawler v2 & JavaScript Rendering Architecture**:
   - Headless rendering interface (`IPageRenderer`) and Raw vs Rendered DOM diffing (Title, Meta, Canonical, Headings, Links, Schema).
   - Advanced crawl parameters: Custom User-Agent (Mobile/Desktop), authentication headers for staging sites, custom URL lists, crawl depth/limits.
2. **300+ Deterministic SEO Rules Framework**:
   - Indexability: Canonical conflicts, canonical chains/loops, canonical non-200, indexability contradictions.
   - HTTP & Transport: Soft 404, redirect chains/loops, SSL certificate issues, mixed content.
   - Metadata & Headings: Pixel width approximations, missing/duplicate social metadata, comprehensive heading hierarchies.
   - Structured Data: Schema.org JSON-LD validator for Organization, Product, Article, BreadcrumbList, FAQPage, LocalBusiness.
   - Hreflang: Return-tag validation, ISO language/country code validation, x-default checks.
   - Images & Resources: Oversized images, missing dimensions, modern formats (WebP/AVIF opportunities).
3. **Historical Audit Comparison Engine**:
   - Full snapshot comparison: issue lifecycle (New, Resolved, Regressed, Persisted), score deltas, crawl stat changes.

### P2 — Search Intelligence & Provider Abstractions
1. **Production Google Search Console**:
   - Multi-property selection, automated recurring sync, multidimensional aggregation.
2. **Keyword & Content Intelligence**:
   - Keyword clustering, keyword cannibalization detection, content depth, topical authority signals, readability scores.
3. **Provider Abstractions**:
   - `IBacklinkProvider` (referring domains, backlink profiles, lost/gained links).
   - `ISerpProvider` (SERP snapshots, rank tracking, SERP features).

### P3 — AEO / GEO & Advanced AI SEO Expert
1. **AI Search & Engine Optimization (AEO/GEO)**:
   - Configurable AI crawler definitions (GPTBot, ClaudeBot, PerplexityBot, Applebot-Extended, Bytespider).
   - AI Crawlability Score, Answer Readiness Score, Citation Readiness Score, AI Visibility Score.
2. **AI SEO Expert v2**:
   - Evidence-grounded root cause analysis, actionable code examples, impact-effort prioritization.

### P4 — Automated SEO Fixes, Continuous Monitoring & Enterprise Polish
1. **Safe Automated Fix Workflow**:
   - Proposed Fix → Unified Diff → User Approval → Verification Recrawl → Confirm Resolution.
2. **Enterprise Monitoring & Reporting**:
   - Metric anomaly detection (traffic drop, ranking drop, sudden 404 spikes).
   - Multi-format executive reports with tenant white-label branding.
