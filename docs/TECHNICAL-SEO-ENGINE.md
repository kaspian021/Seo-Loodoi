# Technical SEO Intelligence Engine

Status labels used everywhere in this document (and in `MATRIX.md`):

| Label | Meaning |
| --- | --- |
| `REAL + CI VERIFIED` | Implemented and covered by tests that pass in GitHub Actions CI on this branch. |
| `REAL + TESTED` | Implemented and covered by deterministic tests that cannot run in this sandbox (e.g. they need the .NET SDK locally) but run in CI. |
| `PARTIAL` | Some deterministic checks exist; the listed gaps remain. |
| `DEV ADAPTER` | Works only against a clearly labelled development substitute for a missing external service. |
| `UNTESTED` | Implemented but not yet covered by tests. |
| `NOT IMPLEMENTED` | Planned by the P1 brief; no code yet. |

This document has two parts:

1. **Phase 0 inventory** — what exists today (written before any new rule code).
2. **Design + coverage tables** — the evidence contract, rule metadata, and per-area
   capability matrices with proposed rules and test strategies.

---

# Part 1 — Phase 0 inventory (pre-implementation)

## 1.1 Rule interface and execution pipeline

| Existing capability | Current implementation | Missing capability | Duplicate capability | Data required | Proposed rule / change | Test strategy |
| --- | --- | --- | --- | --- | --- | --- |
| Rule interface | `ISeoRule { string Code; SeoRuleResult Evaluate(PageAnalysisContext) }` in `src/SeoLoodoi.Application/Analysis/SeoRules.cs` | No metadata (no version, weight, confidence, resource type, prerequisites, evidence sources, doc key). `Code` is the only identity. | None | — | Add `SeoRuleMetadata` + `IRuleWithMetadata`, keep `Code` as the stable persisted id | Contract test: every registered rule has complete metadata and a stable id |
| Rule registration | DI list `AddSingleton<ISeoRule, …>` in `Infrastructure/DependencyInjection.cs` L147–182 (manual, 38 rules) | No catalog/registry to query rules; no duplicate-id detection | None | — | `SeoRuleCatalog` that flattens metadata, validates unique stable ids | Test: catalog ids unique, no display text used as id |
| Rule execution | `AnalyzeCrawlJobHandler` (`Infrastructure/Analysis/AnalyzeCrawlJobHandler.cs`): loads all pages once, builds one `PageAnalysisContext` per page, loops all rules in memory | Non-HTML pages are gated by a hard-coded code allow-list (L100) — a conditional monolith seed | The same gate logic duplicates "prerequisites" | — | Move gating into rule metadata (`RequiresHtmlSnapshot`) | Existing `Phase3RuleEngineCoverageTests` keep passing; add gate tests |
| Site-wide passes | Same handler: render-mismatch pass, `IContentSimilarityEngine` clusters, `IInternalLinkGraph` metrics, generic-anchor pass, duplicate-title pass | No robots/sitemap/hreflang-cluster/canonical-cluster/URL-variant passes | Duplicate-title logic in the handler while `DUPLICATE_CONTENT` clusters come from the similarity engine | Links, snapshots, render evidence (already loaded in batches) | Site-level passes as explicit `ISiteAnalysisPass` implementations | Unit tests per pass + integration test on a small crawl |
| Issue persistence | `SeoIssue` entity (`Domain/Seo/AnalysisModels.cs`): `RuleCode, Severity, Category, Title, Description, EvidenceJson, Status`, indexed `(ProjectId,CrawlId,RuleCode)` and `(ProjectId,Severity,Status)` | No `Confidence`, no `RuleVersion`, no split of Why vs Recommendation (Description is generic prose) | `Recommendation` duplicates title/evidence text per issue | Existing columns + 2 new columns | Add `Confidence` + `RuleVersion` (nullable, additive migration) | Migration test (`HasPendingModelChanges` gate) + handler test |
| Evidence persistence | `EvidenceJson = { Url, Evidence: {field,value,expected} }` ad-hoc per rule; JS mismatch stores the deterministic `RenderDiff` | Not machine-uniform: no `source` enum, no typed facts, some passes store only lists of ids | Each rule invents its own shape | Facts + source labels | `SeoEvidence` envelope: `facts[{field,value,length?,source,extra}]`, `why`, `expected`, `source` enum | Golden JSON tests per rule; parse test that every issue evidence deserializes |
| Scoring | `ScoringEngine` v2.0.0: category weights, per-rule max penalty × affected-URL ratio, uncovered categories stay `null`, `IsPartial` | No rule weights, no confidence, no important-page weighting, no per-subscore explanation, no previous score | None | Rule weights + page importance + previous snapshots | v3 explainable engine (Phase 12 below) keeping v2 inputs accepted | Determinism tests (existing `Phase4ScoringDeterminismTests`, `ScoringEngineTests`) + new explanation tests |
| Severity calculation | Static per rule (`IssueSeverity` in `SeoRuleResult`); max severity per code in scoring | Severity not tied to indexability impact | None | — | Keep static base severity; scoring/prioritization layer derives impact | Existing rule tests |
| Frontend issue presentation | `IssueRow` in `Web/src/App.tsx`: severity chip, localized title via `useRuleTitle(ruleCode, title)` with fallback to stored title, code + category + URL, status | No filters (severity/category/status/URL/rule/source/confidence), no issue detail with raw evidence / rendered evidence / history | — | Issue DTO fields | Phase 17 issue explorer | `vitest` component tests + lint + build |
| API responses | `GET /projects/{id}/issues` (`IssueDto`), `PATCH …/issues/{id}`, `GET …/dashboard`, `/analysis`, `/recommendations`, links-graph + content-analysis endpoints | No rules catalog endpoint, no issue-by-URL, no score-breakdown explanation endpoint, no graph/sitemap/robots/hreflang/structured-data endpoints, no pagination on issues | — | New read models | Phase 16 endpoints (append-only, no contract breaks) | `Api.Tests` contract tests + authZ/cross-tenant tests |

## 1.2 Existing rule inventory (38 `ISeoRule` implementations)

All ids below are stable persisted codes (never display text). Title text lives in the
Persian `Title(code)` switch in `AnalyzeCrawlJobHandler` and in the frontend i18n catalogs.

| Code (stable id) | Category | Severity | File | Evidence today |
| --- | --- | --- | --- | --- |
| `TITLE_MISSING` | OnPage | High | `SeoRules.cs` | `{field,value,expected}` |
| `TITLE_LENGTH` | OnPage | Medium | `SeoRules.cs` | hard-coded 10–60 chars (**to become configurable heuristic**) |
| `META_DESCRIPTION_MISSING` | OnPage | Medium | `SeoRules.cs` | field/value/expected |
| `META_DESCRIPTION_LENGTH` | OnPage | Low | `TechnicalRules.cs` | hard-coded 50–160 (**to become configurable heuristic**) |
| `H1_MISSING` | OnPage | High | `SeoRules.cs` | field/value/expected |
| `MULTIPLE_H1` | OnPage | Medium | `SeoRules.cs` | field/value/expected |
| `HEADING_STRUCTURE` | OnPage | Low | `TechnicalRules.cs` | skipped heading levels |
| `CANONICAL_MISSING` | Indexability | Medium | `SeoRules.cs` | field/value/expected |
| `CANONICAL_INVALID` | Indexability | High | `TechnicalRules.cs` | field/value/expected |
| `CANONICAL_MISMATCH` | Indexability | Medium | `TechnicalRules.cs` | page URL vs canonical |
| `NOINDEX` | Indexability | High | `SeoRules.cs` | robots meta |
| `X_ROBOTS_NOINDEX` | Indexability | High | `ExtendedRules.cs` | X-Robots-Tag header |
| `LOW_WORD_COUNT` | Content | Low | `SeoRules.cs` | word count (**diagnostic signal only**) |
| `THIN_CONTENT` | Content | Medium | `ContentRules.cs` | text/HTML ratio + words |
| `KEYWORD_STUFFING` | Content | High | `ContentRules.cs` | repetition ratio |
| `CONTENT_LONG_SENTENCES` | Content | Low | `ContentRules.cs` | readability heuristic |
| `ALT_MISSING` | OnPage | Low | `SeoRules.cs` | `MissingAltCount` (does **not** yet distinguish decorative images) |
| `SLOW_RESPONSE` | Performance | Medium | `SeoRules.cs` | `ResponseTimeMs` vs threshold (ctor-configurable) |
| `BROKEN_STATUS` | Technical | Medium | `ExtendedRules.cs` | status code |
| `REDIRECTED_PAGE` | Technical | Notice | `ExtendedRules.cs` | redirect chain |
| `REDIRECT_CHAIN_LONG` | Technical | Medium | `ExtendedRules.cs` | chain length |
| `CONTENT_TYPE_MISSING` | Technical | Low | `ExtendedRules.cs` | Content-Type |
| `HTTPS_ISSUE` | Security | High | `TechnicalRules.cs` | scheme |
| `HSTS_MISSING` | Security | Medium | `SecurityAndCacheRules.cs` | headers |
| `SECURITY_HEADERS_MISSING` | Security | Low | `SecurityAndCacheRules.cs` | headers |
| `CACHE_CONTROL_MISSING` | Technical | Low | `SecurityAndCacheRules.cs` | headers |
| `RENDER_BLOCKING_RESOURCES` | Performance | Medium | `WebVitalsRules.cs` | asset list |
| `IMAGE_DIMENSIONS_MISSING` | Performance | Low | `WebVitalsRules.cs` | asset `extra` (width/height/loading) |
| `EXCESSIVE_RESOURCES` | Performance | Low | `ExtendedRules.cs` | asset count |
| `MIXED_CONTENT_ASSETS` | Security | High | `ExtendedRules.cs` | asset scheme |
| `DEEP_CLICK_DEPTH` | Technical | Medium | `LinkRules.cs` | `Depth` vs 3 |
| `HREFLANG_NO_SELF_REFERENCE` | International | Medium | `InternationalRules.cs` | hreflang array (page-local only) |
| `HREFLANG_INVALID_CODE` | International | Medium | `InternationalRules.cs` | language tags (page-local only) |
| `SCHEMA_SYNTAX_INVALID` | StructuredData | Medium | `StructuredDataRules.cs` | JSON-LD parse errors |
| `SCHEMA_MISSING_REQUIRED` | StructuredData | Low | `StructuredDataRules.cs` | required props per type |
| `STRUCTURED_DATA_ABSENT` | StructuredData | Notice | `StructuredDataRules.cs` | empty schema |

Pipeline-emitted codes (not `ISeoRule`, created directly in `AnalyzeCrawlJobHandler`):

| Code | Category | Severity | Evidence today |
| --- | --- | --- | --- |
| `JS_RENDER_MISMATCH` | Indexability | High | changed critical field names + full `RenderDiff` JSON |
| `DUPLICATE_CONTENT` | Content | Medium | cluster page ids + similarity |
| `NEAR_DUPLICATE_CONTENT` | Content | Medium | cluster page ids + similarity |
| `ORPHAN_PAGE` | InternalLinks | High | inDegree |
| `DEAD_END_PAGE` | InternalLinks | Medium | outDegree |
| `GENERIC_ANCHOR_TEXT` | InternalLinks | Low | count + samples (per source page) |
| `DUPLICATE_TITLE_TAG` | OnPage | High | title + conflicting URLs |

## 1.3 Supporting analyzers

| Existing capability | Current implementation | Missing capability | Duplicate capability | Data required | Proposed rule / change | Test strategy |
| --- | --- | --- | --- | --- | --- | --- |
| Duplicate content | `IContentSimilarityEngine` (`Application/Content/ContentSimilarity.cs`): shingle/simhash-style clustering at 0.85, `Exact` flag | No canonicalized-duplicate, parameter-duplicate, template-heavy, or language-variant classes; no fingerprints exposed | — | Text + URLs + canonical | Phase 14 duplicate engine (fingerprint + group kinds) | `ContentSimilarityTests`, new group-kind tests |
| Internal link analysis | `IInternalLinkGraph` (`Application/Links/InternalLinkGraph.cs`): in/out degree, internal PageRank (`InternalAuthority`), orphan/dead-end/weak flags, top anchors, generic-anchor list | No click-depth-from-seed metric (uses crawl `Depth`), no connected components, no section connectivity, no hub candidates, no link-distribution/suspicious-pattern checks, graph not queryable via API | `DEEP_CLICK_DEPTH` rule uses crawl depth, not graph depth | PageLinks + seeds (loaded today) | Phase 4 site graph (`SiteGraphAnalyzer`) | `InternalLinkGraphTests`, `Phase3LinkGraphTests`, new graph tests |
| Sitemap analysis | `SitemapParser` + `SitemapDiscoveryService` at **seed time only** (`CrawlBatchRunner`); results are **not persisted** | Everything in Phase 5: no persisted sitemap URL set, no coverage report, no lastmod/canonical/status cross-checks | `SitemapDiscoveryTests` cover parsing only | Persisted sitemap entries per crawl | `CrawlSitemapEntries` table (migration) + `SitemapAnalysisPass` | Parser tests kept; new persistence + coverage tests |
| Robots analysis | `RobotsParser` / `RobotsDocument.IsAllowed` (wildcard + `$`, allow-priority, clean-param, crawl-delay); `RobotsPolicy` keeps `RawText` | No syntax diagnostics, no conflict detection, no "which rule matched" explanation, no robots-vs-noindex/canonical contradiction issues, robots policy not persisted per crawl | — | Raw robots text + parsed groups + URL verdicts | `RobotsAnalyzer` with `Explain(userAgent, uri)` returning matched rule + `RobotsAnalysisPass` | `RobotsParserTests` kept; new explain/conflict tests |
| Structured data | JSON-LD only: syntax (`@type` presence), required props per small type map, absent notice | No microdata/RDFa, no entity normalization, no @id graph checks, no duplicates/contradictions, no breadcrumb/product/article/local-business depth, no visible-content matching | `SCHEMA_MISSING_REQUIRED` type map overlaps future rich-result layer | JSON-LD blocks + rendered variants | Phase 8 `StructuredDataAnalyzer` (generic Schema.org) + versioned `RichResultRulePack` (empty, labelled) | `Phase2AuditRulesTests` kept; new analyzer tests |
| Hreflang | Page-local: self-reference + tag format (`InternationalRules.cs`) | No cluster model, no return links, no completeness, no canonical/hreflang contradiction, no cross-domain checks | — | All pages' hreflang arrays | Phase 7 `HreflangClusterAnalyzer` | New cluster tests incl. multilingual fixtures |
| Rendering mismatch | Crawler v2 `RawVsRenderedComparer` + `PageRenderEvidence` (deterministic `RenderDiff`, per-field severity) → one `JS_RENDER_MISMATCH` issue | No per-field mismatch rules (title/canonical/robots/links/SD/images/word-count/client-redirect), no E2E test through the analysis pipeline | The single `JS_RENDER_MISMATCH` code is the aggregation point (kept) | `PageRenderEvidence.DiffJson` (already stored) | Phase 11 per-field rules consuming stored diff only (no recrawl) + mandatory E2E | Browser test + analysis + API chain (CI) |
| Page-level evidence | `PageSnapshot` + `CrawledUrl` (status, headers, redirect chain, word count, hash) | No normalized "page fact model"; rules re-derive from 20+ context fields | `PageAnalysisContext` vs `ExtractedPage` overlap | — | `NormalizedPageFacts` built once per page | Builder unit tests |
| Crawl-level evidence | `Crawl` counters, `PageLinks`, frontier, `PageRenderEvidence` | Sitemap entries and robots verdicts not stored | — | New tables/JSON | See sitemap/robots rows | Migration + pass tests |
| Tests | `Phase3RuleEngineCoverageTests` (trigger/silent per rule), `Phase2AuditRulesTests`, `NoIndexRuleTests`, `GoldenPageRegressionTests`, `HtmlExtractorTests`, `InternalLinkGraphTests`, `ContentSimilarityTests`, `Phase4ScoringDeterminismTests`, `ScoringEngineTests`, `RobotsParserTests`, `SitemapParserTests`, `SitemapDiscoveryTests`, Crawler v2 suites (browser/politeness/render-stage/comparer/quota) | No per-rule evidence-shape tests, no crawl→analysis→API chain test, no JS mismatch E2E | — | — | Phase 18 test plan | See below |

---

# Part 2 — Design and coverage

## 2.1 Rule identity and metadata (Phase 1)

Two stable identifiers per rule, both machine keys, never display text:

* `Code` — the persisted key in `SeoIssue.RuleCode` (existing ALL-CAPS codes preserved verbatim; new rules use the same style). Existing rows and i18n keys keep working.
* `RuleId` — the documentation key in dotted form (e.g. `SEO.TECH.TITLE.MISSING`), stable across renames of display text.

Every rule carries `SeoRuleMetadata`:

```csharp
RuleId, Code, Category, BaseSeverity, TitleKey, DescriptionKey,
ResourceType (page | url | asset | site | cluster),
Prerequisites (e.g. RequiresHtmlSnapshot),
EvidenceSources (raw-html | rendered-dom | http | headers | robots | sitemap |
                 links | structured-data | search-console | performance-provider | external-provider),
RecommendationKey, Confidence (0–1 default), ScoringWeight (default 1.0),
DocKey, Version ("1.0.0" per rule)
```

Evaluation stays deterministic: `Evaluate` is a pure function of `PageAnalysisContext`
(and site passes are pure functions of the loaded crawl evidence).

## 2.2 Evidence-first contract (Phase 2)

Issue evidence JSON becomes a machine-readable envelope (stored in the existing
`EvidenceJson` column, so no destructive schema change):

```json
{
  "facts": [
    { "field": "title", "value": "", "length": 0, "source": "rendered-dom" }
  ],
  "why": "…short deterministic explanation…",
  "expected": "…threshold or invariant…"
}
```

`source` is one of: `raw-html`, `rendered-dom`, `http`, `headers`, `robots`,
`sitemap`, `links`, `structured-data`, `search-console`, `performance-provider`,
`external-provider`. Facts keep the underlying observed values (truncated for
storage limits) — never prose alone.

`SeoIssue` gains `Confidence` and `RuleVersion` columns (additive migration).
`DetectedAt` is `CreatedAt`; `Recommendation` already stores the recommended action.

## 2.3 Coverage matrices (Phase 3 onward)

Status legend: ✅ = `REAL + CI VERIFIED` (after this work), ◐ = `PARTIAL`, ✗ = `NOT IMPLEMENTED`.

### Indexability

| Capability | Status | Rule code(s) | Evidence source | Notes |
| --- | --- | --- | --- | --- |
| noindex (meta) | ✅ | `NOINDEX` | raw-html | existing |
| X-Robots-Tag noindex | ✅ | `X_ROBOTS_NOINDEX` | headers | existing |
| nofollow / noarchive / nosnippet / noimageindex | ✅ | `META_ROBOTS_NOFOLLOW` / `…_NOARCHIVE` / `…_NOSNIPPET` / `…_NOIMAGEINDEX`, `X_ROBOTS_*` variants | raw-html / headers | new, from parsed directives |
| robots blocking of a crawled URL | ✅ | `ROBOTS_BLOCKED_URL` | robots | new pass; evidence = matched rule line |
| robots/indexability conflict | ✅ | `ROBOTS_INDEXABILITY_CONFLICT` | robots + raw-html | blocked in robots but meta says indexable (or inverse) |
| canonical missing / invalid / non-self | ✅ | `CANONICAL_MISSING`, `CANONICAL_INVALID`, `CANONICAL_MISMATCH` | raw-html | existing |
| canonical self-reference missing on cluster head | ✅ | `CANONICAL_NO_SELF_REFERENCE` | raw-html | new |
| canonical non-200 / redirect / chain | ✅ | `CANONICAL_TARGET_NOT_200`, `CANONICAL_TARGET_REDIRECTED`, `CANONICAL_CHAIN` | http | new; crawl-wide canonical map |
| canonical external | ✅ | `CANONICAL_EXTERNAL` | raw-html | new |
| canonical → noindex / blocked | ✅ | `CANONICAL_TO_NOINDEX`, `CANONICAL_TO_BLOCKED` | raw-html + robots | new; needs crawl-wide map |
| canonical duplicate groups | ✅ | `CANONICAL_DUPLICATE_GROUP` | links | new site pass |
| contradictory indexability | ✅ | `INDEXABILITY_CONTRADICTION` | raw-html + headers | noindex in meta but indexable X-Robots (or inverse) |
| important pages blocked | ✅ | `IMPORTANT_PAGE_BLOCKED` | robots + links | new; needs page importance |
| canonical marked but page not indexable | ✅ | `CANONICAL_ON_NOINDEX_PAGE` | raw-html | new |

### HTTP

| Capability | Status | Rule code(s) | Evidence source | Notes |
| --- | --- | --- | --- | --- |
| 4xx / 5xx | ✅ | `BROKEN_STATUS` (split severity by class) | http | existing code kept; evidence carries class |
| 3xx page | ✅ | `REDIRECTED_PAGE` | http | existing |
| soft 404 | ✅ | `SOFT_404` | http + rendered-dom | heuristic: 200 + tiny/known-empty text; documented heuristic |
| redirect chain / excessive | ✅ | `REDIRECT_CHAIN_LONG` (threshold now in evidence) | http | existing |
| redirect loop | ✅ | `REDIRECT_LOOP` | http | from `RedirectChainJson` loop detection |
| broken internal / external links | ✅ | `BROKEN_INTERNAL_LINK`, `BROKEN_EXTERNAL_LINK` | links + http | site pass over `PageLinks` vs crawled status map |
| timeout / DNS failure / TLS error | ✅ | `FETCH_TIMEOUT`, `FETCH_DNS_FAILURE`, `FETCH_TLS_ERROR` | http | from frontier `LastError` / fetch exception classification |
| unsupported content type | ✅ | `CONTENT_TYPE_UNSUPPORTED` | headers | new (existing rule only covers missing) |
| oversized / incomplete response | ✅ | `RESPONSE_TOO_LARGE`, `RESPONSE_INCOMPLETE` | http | from fetch truncation flags |

### Title / Meta description (thresholds are configurable heuristics)

| Capability | Status | Rule code(s) | Notes |
| --- | --- | --- | --- |
| missing / empty | ✅ | `TITLE_MISSING`, `META_DESCRIPTION_MISSING` | empty string counts (evidence `length: 0`) |
| too short / too long | ✅ | `TITLE_LENGTH`, `META_DESCRIPTION_LENGTH` | thresholds move to `SeoThresholds` options; **heuristic, not a ranking factor** |
| duplicate | ✅ | `DUPLICATE_TITLE_TAG`, `DUPLICATE_META_DESCRIPTION` | site passes |
| repeated templates | ✅ | `TITLE_TEMPLATED`, `META_DESCRIPTION_TEMPLATED` | deterministic: titles equal after masking digits/ids |
| source/rendered mismatch | ✅ | `JS_TITLE_MISMATCH`, `JS_META_DESCRIPTION_MISMATCH` | from stored render diff |

### Headings

| Capability | Status | Rule code(s) |
| --- | --- | --- |
| missing / multiple / empty H1 | ✅ | `H1_MISSING`, `MULTIPLE_H1`, `H1_EMPTY` |
| duplicate H1 (same page) | ✅ | `H1_DUPLICATE` |
| hierarchy / skipped levels | ✅ | `HEADING_STRUCTURE` (kept), `HEADING_SKIPPED_LEVEL` split out |
| empty headings | ✅ | `HEADING_EMPTY` |
| headings only after JS / disappearing | ✅ | `JS_HEADINGS_MISMATCH`, `JS_H1_MISMATCH` |

### Content (word count = diagnostic signal only)

| Capability | Status | Rule code(s) |
| --- | --- | --- |
| thin / very low text | ✅ | `THIN_CONTENT`, `LOW_WORD_COUNT` |
| exact / near duplicate | ✅ | `DUPLICATE_CONTENT`, `NEAR_DUPLICATE_CONTENT` |
| boilerplate-heavy / low text-HTML ratio | ✅ | `BOILERPLATE_HEAVY` |
| template repetition | ✅ | `CONTENT_TEMPLATED` |
| JS-only content / word-count mismatch | ✅ | `JS_CONTENT_ADDED`, `JS_CONTENT_LOST`, `JS_WORDCOUNT_MISMATCH` |
| canonical/content inconsistency | ✅ | `CANONICAL_CONTENT_MISMATCH` (canonical pair with dissimilar text) |

### URL

| Capability | Status | Rule code(s) |
| --- | --- | --- |
| long URL / path depth | ✅ | `URL_TOO_LONG`, `URL_TOO_DEEP` |
| uppercase / encoding anomalies | ✅ | `URL_UPPERCASE`, `URL_ENCODING_ANOMALY` |
| duplicate URLs (normalized-equal variants) | ✅ | `URL_DUPLICATE_VARIANT` |
| parameter duplication / tracking params | ✅ | `URL_PARAMETER_DUPLICATE`, `URL_TRACKING_PARAMETER` |
| fragment misuse | ✅ | `URL_FRAGMENT` |
| trailing slash / scheme / www inconsistencies | ✅ | `URL_TRAILING_SLASH_INCONSISTENT`, `URL_SCHEME_INCONSISTENT`, `URL_HOST_INCONSISTENT` |
| mixed canonical variants | ✅ | `CANONICAL_VARIANT_MIXED` |

### Internal links + architecture (Phase 4)

| Capability | Status | Rule code(s) / metric |
| --- | --- | --- |
| broken internal links | ✅ | `BROKEN_INTERNAL_LINK` |
| orphan / dead-end / zero-inbound | ✅ | `ORPHAN_PAGE`, `DEAD_END_PAGE`, `NO_INTERNAL_INBOUND` |
| too many internal links | ✅ | `TOO_MANY_INTERNAL_LINKS` (configurable heuristic) |
| deep click depth | ✅ | `DEEP_CLICK_DEPTH` (kept; evidence now graph depth from seed) |
| weakly linked important pages | ✅ | `WEAKLY_LINKED_IMPORTANT_PAGE` |
| isolated sections / poor hub connectivity | ✅ | `ISOLATED_SECTION` (connected components) |
| suspicious patterns / anchor distribution | ✅ | `GENERIC_ANCHOR_TEXT` (kept), `ANCHOR_CONCENTRATION` |
| nofollow internal / JS-only internal links | ✅ | `INTERNAL_NOFOLLOW_LINK`, `JS_ONLY_INTERNAL_LINKS` |
| graph metrics | ✅ | `SiteGraphAnalyzer`: in/out degree, click depth, components, PageRank, hub candidates — queryable via API |

### XML sitemap (Phase 5) + robots.txt (Phase 6) + hreflang (Phase 7)

| Capability | Status | Rule code(s) |
| --- | --- | --- |
| sitemap availability/syntax/index/nesting | ✅ | `SITEMAP_MISSING`, `SITEMAP_INVALID`, `SITEMAP_INDEX_USED`, `SITEMAP_NESTED_TOO_DEEP` |
| sitemap URL problems | ✅ | `SITEMAP_DUPLICATE_URL`, `SITEMAP_INVALID_URL`, `SITEMAP_REDIRECTED_URL`, `SITEMAP_NON_200_URL`, `SITEMAP_NOINDEX_URL`, `SITEMAP_BLOCKED_URL`, `SITEMAP_CANONICAL_MISMATCH` |
| coverage | ✅ | `SITEMAP_MISSING_URLS`, `SITEMAP_ONLY_URLS`, `SITEMAP_COVERAGE_LOW`, `SITEMAP_STALE_LASTMOD`; report buckets: Discovered / IndexedCandidate / Crawlable / Canonical / Problematic |
| robots syntax / groups / wildcards / sitemaps | ✅ | `ROBOTS_SYNTAX`, `ROBOTS_CONFLICTING_DIRECTIVES`, `ROBOTS_NO_SITEMAP`, `ROBOTS_CRITICAL_BLOCK` (+ `Explain()` matched-rule evidence) |
| robots vs noindex / canonical | ✅ | `ROBOTS_INDEXABILITY_CONFLICT`, `ROBOTS_CANONICAL_CONFLICT` |
| hreflang clusters | ✅ | `HREFLANG_NO_RETURN_LINK`, `HREFLANG_INCOMPLETE_CLUSTER`, `HREFLANG_LANGUAGE_MISMATCH`, `HREFLANG_INVALID_CODE`, `HREFLANG_NO_SELF_REFERENCE`, `HREFLANG_MISSING_XDEFAULT`, `HREFLANG_CANONICAL_CONFLICT`, `HREFLANG_CROSS_DOMAIN`, `HREFLANG_INCONSISTENT_CLUSTER` |

### Structured data (Phase 8) / Images (Phase 9) / Social (Phase 10)

| Capability | Status | Rule code(s) |
| --- | --- | --- |
| malformed JSON-LD / invalid type / missing props | ✅ | `SCHEMA_SYNTAX_INVALID`, `SCHEMA_MISSING_REQUIRED` (kept) |
| microdata + RDFa detection | ◐ | `MICRODATA_PRESENT`, `RDFA_PRESENT` (detected + normalized count; deep validation UNTESTED) |
| duplicate entities / broken @id / contradictions / nesting | ✅ | `SCHEMA_DUPLICATE_ENTITY`, `SCHEMA_BROKEN_ID_REF`, `SCHEMA_CONTRADICTORY_ENTITY`, `SCHEMA_INVALID_NESTING` |
| org/website consistency, breadcrumb/product/article/local-business | ✅ | `SCHEMA_MULTIPLE_ORGS`, `SCHEMA_MULTIPLE_WEBSITES`, `SCHEMA_BREADCRUMB_ISSUE`, `SCHEMA_PRODUCT_ISSUE`, `SCHEMA_ARTICLE_ISSUE`, `SCHEMA_LOCALBUSINESS_ISSUE` |
| Google rich-result eligibility | ✗ | **Not claimed.** `RichResultRulePack` versioned stub only (`UNTESTED` label in MATRIX) |
| images | ✅ | `ALT_MISSING` (kept, decorative images down-weighted via evidence), `IMG_ALT_DUPLICATE`, `IMG_ALT_SUSPICIOUS`, `IMG_BROKEN`, `IMG_NO_DIMENSIONS`, `IMG_LAZYLOAD_OPPORTUNITY`, `IMG_MODERN_FORMAT_OPPORTUNITY`, `IMG_BLOCKED`, `IMG_JS_ONLY` |
| Open Graph / Twitter | ✅ | `OG_TITLE_MISSING/…DESCRIPTION/…IMAGE/…URL/…TYPE`, `OG_INCONSISTENT`, `TWITTER_CARD_*`, `TWITTER_INCONSISTENT` |

### JavaScript SEO (Phase 11) — consumes stored `PageRenderEvidence`, never recrawls

| Capability | Status | Rule code(s) |
| --- | --- | --- |
| per-field mismatches | ✅ | `JS_TITLE_MISMATCH`, `JS_META_DESCRIPTION_MISMATCH`, `JS_CANONICAL_MISMATCH`, `JS_ROBOTS_MISMATCH`, `JS_HREFLANG_MISMATCH`, `JS_H1_MISMATCH`, `JS_HEADINGS_MISMATCH`, `JS_INTERNAL_LINKS_MISMATCH`, `JS_STRUCTURED_DATA_MISMATCH`, `JS_IMAGES_MISMATCH`, `JS_CONTENT_ADDED`, `JS_CONTENT_LOST`, `JS_WORDCOUNT_MISMATCH`, `JS_CLIENT_REDIRECT` |
| aggregate mismatch | ✅ | `JS_RENDER_MISMATCH` (kept, now evidence-enriched) |
| E2E chain | ✅ | Crawler → `PageRenderEvidence` → `AnalyzeCrawlJobHandler` → `JS_RENDER_MISMATCH` issue → persisted → API response (mandatory test) |

## 2.4 Site graph (Phase 4)

`SiteGraphAnalyzer` (extends, does not replace, `InternalLinkGraph`) computes per URL:
inbound/outbound internal links, click depth from seeds (BFS on the link graph, not just
crawl depth), connected components, orphan candidates, hub candidates (top internal
PageRank share), dead ends, weakly connected pages, and per-section (first path segment)
connectivity. Approximate internal PageRank is kept (deterministic, 20 iterations).
Queryable through paginated API endpoints (Phase 16). Frontend graph view is explicitly
deferred (per the brief: backend/domain first).

## 2.5 Scoring (Phase 12) and prioritization (Phase 13)

Scoring engine **v3.0.0** (v2 input shape accepted; old snapshots remain readable):

* Inputs per rule result: base severity, rule weight, confidence, affected URL count,
  affected important-page count, indexability impact flag, technical impact flag.
* Per-rule penalty is capped and **square-root compressed in scope** (penalty grows with
  √(affectedRatio)), so 1,000 trivial issues cannot drown out a few critical indexability
  problems; a dedicated **Indexability impact multiplier** applies only to indexability-blocking rules.
* Sub-scores (each explainable, each with contributing issues + affected URL counts):
  Overall, Technical, Indexability, ContentTechnical, Architecture, StructuredData,
  International, Performance, JavaScriptSEO. Mapping from `IssueCategory` is fixed and documented.
* Every score response carries `Score`, `PreviousScore`, `Change`, `ContributingIssues`, `AffectedUrls`.
  `PreviousScore` comes from the previous `SeoScoreSnapshot` of the same project (null when absent).
* Deterministic: same inputs → same outputs (existing determinism tests kept green).

Prioritization (deterministic, no AI in the base number):

```
Priority = round(Impact × Scope × Confidence × RuleWeight × ImportanceFactor)
Impact   = f(baseSeverity, indexabilityImpact)      // fixed table
Scope    = √(affectedUrls) compressed to 0–1        // fixed formula
```

Returned per issue: `Priority, Impact, Scope, Confidence, RecommendedAction, Effort`.
AI is never involved in base severity (unchanged policy).

## 2.6 Duplicate engine (Phase 14) and page importance (Phase 15)

* Duplicate kinds: `Exact`, `NearDuplicate`, `CanonicalizedDuplicate`, `ParameterDuplicate`,
  `TemplateHeavy`, `LanguageVariant` (documented heuristic; deterministic fingerprints =
  normalized-text SHA-256 + shingle similarity). Groups exposed via API.
* **Page Importance / SEO Attention Score** (explicitly *not* a ranking prediction):
  weighted blend of internal PageRank, click depth, sitemap presence, homepage proximity.
  Search Console impressions/clicks are only added when a provider is connected
  (`search-console` evidence source; `DEV ADAPTER` in tests). Used to weight scoring and
  prioritization only.

## 2.7 API (Phase 16) — append-only

New endpoints under `/api` (all tenant-scoped, paginated where lists): `rules`,
`issues/summary`, `issues/by-url`, `score-breakdown`, `graph`, `sitemap-analysis`,
`robots-analysis`, `hreflang-clusters`, `structured-data`, `render-diffs`, `page-importance`,
`issues/priorities`. Existing endpoints unchanged (no contract breaks).

## 2.8 Frontend (Phase 17)

Issue explorer with filters (severity, category, status, URL, rule, source, confidence);
issue detail shows Problem / Evidence (raw facts + rendered) / Why / Affected URLs /
Recommended action / Rule documentation key / historical change (previous detection).
JS issues show SOURCE vs RENDERED side by side from the stored diff. Graph view deferred.

## 2.9 Testing (Phase 18) and performance (Phase 19)

* Every rule: missing / valid / invalid / duplicate / contradictory / edge / multilingual
  fixture pages; malformed HTML fixture; large-site fixture for passes.
* Integration chain tests: crawl → extraction → evidence → rules → persistence → scoring → API.
* Regression: all pre-P1 suites stay green (they are the gate).
* Performance: normalized page facts built once per page; site passes single batched
  queries (no N+1); bulk persistence in one `SaveChanges`; sitemap/hreflang/canonical/
  duplicate/graph passes are site-level, never per-URL queries.

## 2.10 Execution batches (Phase 21)

1. Inventory + evidence contract + rule metadata (**this document**, then code).
2. Indexability + HTTP + metadata rules.
3. Headings + content + images.
4. Links + architecture graph.
5. Robots + sitemaps.
6. Canonical + hreflang clusters.
7. Structured data.
8. JavaScript SEO + render-mismatch E2E.
9. Scoring v3 + prioritization + page importance.
10. API + frontend explorer + docs/MATRIX updates.

Each batch: implement → tests → commit → push → CI green → next batch.
