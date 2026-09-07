# DigiSEO AI Implementation Phase Prompts

Use these prompts sequentially with your coding AI. Do not send all of them at once.

---

## PROMPT 00 — Repository audit

Read the MASTER AI CODING PROMPT and the DigiSEO technical specification.

Do not write code yet.

Inspect the entire DigiStore repository and produce:

1. solution structure
2. project dependencies
3. .NET version
4. EF Core version
5. database provider
6. Identity architecture
7. ApplicationUser
8. DbContext
9. localization
10. authorization
11. background jobs
12. logging
13. subscription/payment
14. UI architecture
15. existing design system
16. migration state

Identify conflicts between the specification and the existing code.

Do not change existing code.

Output:
- Architecture Assessment
- Risks
- Required Decisions
- Recommended file structure
- Implementation order

---

## PROMPT 01 — SEO foundation

Implement only the DigiSEO Domain foundation.

Create:
- entities
- value objects
- enums
- domain events where justified

Do not implement controllers yet.

Create EF configurations separately.

Build and test.

Do not modify existing Identity.

---

## PROMPT 02 — Persistence

Implement EF Core configurations and migrations for DigiSEO.

Add indexes.

Check cascade-delete behavior carefully.

Ensure tenant/project ownership cannot accidentally be bypassed.

Run:
- build
- migration validation
- integration tests

---

## PROMPT 03 — Project management

Implement:
- create project
- update project
- delete/archive project
- list projects
- project settings
- ownership authorization

Do not implement crawler yet.

---

## PROMPT 04 — URL normalization

Implement the complete URL normalization library.

Create extensive unit tests.

Test:
- fragments
- query strings
- ports
- casing
- trailing slash
- percent encoding
- duplicate parameters
- tracking parameters
- Unicode domains

Do not over-normalize.

---

## PROMPT 05 — Robots engine

Implement:
- robots fetcher
- parser
- rule evaluator
- sitemap discovery
- caching
- tests

Add SSRF-safe HTTP handling.

---

## PROMPT 06 — Sitemap engine

Implement sitemap parser/validator.

Support:
- normal sitemap
- sitemap index
- gzip
- image/video/news where practical

Add coverage comparison.

---

## PROMPT 07 — Crawler

Implement the crawler.

Requirements:
- queue
- concurrency
- retries
- cancellation
- resume
- progress
- robots
- redirects
- SSRF protection
- URL normalization
- persistence

Do not implement every SEO rule in this phase.

First make crawling reliable.

---

## PROMPT 08 — HTML extraction

Implement extraction for:
- title
- description
- headings
- canonical
- robots
- links
- images
- alt
- hreflang
- JSON-LD
- OpenGraph
- Twitter
- text

Create fixtures and tests.

---

## PROMPT 09 — SEO rule engine

Implement the rule abstraction and first rule set.

Every rule must:
- have stable code
- return evidence
- be testable
- be versionable

Do not put rules in controllers.

---

## PROMPT 10 — Scoring

Implement deterministic scoring.

Store calculation version.

Add tests proving:
- same evidence => same score
- changing one issue changes only expected dimensions
- score is explainable

---

## PROMPT 11 — Internal links

Implement:
- graph
- orphan detection
- weak internal linking
- anchor analysis
- recommendations

---

## PROMPT 12 — Duplicate content

Implement:
- exact hashes
- normalized hashes
- similarity
- scalable clustering

Test Persian content.

---

## PROMPT 13 — Dashboard

Build the main SEO dashboard.

Use real backend data.

No fake/demo metrics in production mode.

---

## PROMPT 14 — Search Console

Implement OAuth and synchronization through an adapter.

Add:
- token encryption
- refresh
- incremental sync
- pagination
- failure recovery

Keep integration optional.

---

## PROMPT 15 — Keyword intelligence

Implement:
- query import
- tracked keywords
- page/query mapping
- cannibalization
- opportunity scoring

Do not invent search volume.

---

## PROMPT 16 — Competitors

Implement competitor projects and controlled crawling.

Build comparison reports from real crawl data.

---

## PROMPT 17 — AI foundation

Implement:
- IAiProvider
- local provider interface
- external provider interface
- AI evidence packet
- structured output validation
- prompt versioning
- audit logging

Do not build a generic chatbot.

---

## PROMPT 18 — AI SEO expert

Implement:
- diagnosis
- root-cause analysis
- recommendations
- content briefs
- title/meta suggestions
- internal-link suggestions
- report summaries

Every answer must reference evidence IDs.

---

## PROMPT 19 — Reports

Implement PDF/CSV/JSON reports.

Use the same scoring and data services as the dashboard.

---

## PROMPT 20 — Monitoring

Implement scheduled:
- crawls
- score comparisons
- issue changes
- robots changes
- sitemap changes
- noindex/canonical changes
- search anomalies

---

## PROMPT 21 — Quotas

Implement subscription limits.

Test server-side enforcement.

---

## PROMPT 22 — Security hardening

Perform a dedicated security review.

Focus on:
- SSRF
- tenant isolation
- OAuth
- secrets
- prompt injection
- path traversal
- report generation
- outbound requests
- rate limits

Fix findings.

---

## PROMPT 23 — Performance

Load test:
- 10k URLs
- 50k URLs
- multiple projects

Measure:
- queue depth
- memory
- DB writes
- crawl throughput
- analysis latency

Optimize only from measurements.

---

## PROMPT 24 — Release audit

Do not add features.

Review:
- architecture
- migrations
- tests
- security
- localization
- error handling
- observability
- deployment
- backup/recovery
- subscription enforcement

Produce a release checklist and list any blockers.
