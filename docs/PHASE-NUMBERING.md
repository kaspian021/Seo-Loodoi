# Phase Numbering — Read This First

> **2026-09-20 verification warning:** this historical mapping disagrees with the
> actual Phase 0–12 list currently in `03-Master-AI-Coding-Prompt.md`. Use capability
> names and read [the phase 1–11 test gate](PHASE-1-11-TEST-GATE.md) before treating
> any row below as acceptance evidence. Phase 12 remains blocked. Counts below
> are historical, not the latest CI totals; 36 page rules are currently registered.

This repository has used **four different "phase" vocabularies** at different
times. They do not agree with each other, and two of them were never defined
against a written plan. That ambiguity has already caused confusion about what
has actually been delivered, so this document is the canonical key.

## The four vocabularies

| # | Vocabulary | Where it lives | Range | Meaning |
|---|---|---|---|---|
| **A** | **Hardening track** | `FINDINGS.md`, commits from 2026-09-13/14 | Phase 0–5 | Iterations of the *audit*: close findings **F1–F15**, add i18n/a11y, Testcontainers suite. Findings, not features. |
| **B** | **Product track (early)** | Commits from 2026-09-15, test files `Phase2…Phase8*Tests.cs` | Phase 2–8 | Capability pushes. Numbers were assigned session-by-session and **drift from the master plan**. |
| **C** | **Master plan** | `docs/03-Master-AI-Coding-Prompt.md` | PHASE 0–28 | The written roadmap. **This is the numbering used for all new work.** |
| **D** | **Legacy DigiSEO pack** | `docs/01-04`, `PROMPT 00–24` | 00–24 | Superseded historical DigiStore/DigiSEO plan. Do not use. |

## Mapping B → C

Track B's numbers were chosen before the master plan was applied to this repo,
so they are offset. Where a B phase maps cleanly it is listed; where it does
not, the closest master-plan phase is given.

| Track B commit | B label | What it built | Closest master-plan phase |
|---|---|---|---|
| `db951a0` | — | crawl engine hardening, redirect loops, asset extraction | PHASE 2 (Enterprise crawler) |
| `0d2c3da` | — | technical SEO engine, schema validation, hreflang | PHASE 3 (Technical SEO engine) |
| `d504910` | — | internal link graph authority, anchor intelligence | PHASE 3 (links) |
| `a3e9f46` | Phase 4 | content analysis engine, readability, thin content | PHASE 5 (Content intelligence) |
| `c574e8e` | Phase 5 | keyword intelligence, position delta, cannibalization | PHASE 6 (Keyword intelligence) |
| `df30722` | Phase 6 | competitor content gap analysis | PHASE 8 (Competitor intelligence) |
| `cc54338` | Phase 7 | AI evidence packets, expert diagnosis | PHASE 12 (AI SEO expert) |
| `df14dc0` | Phase 8 | enterprise alerting, webhook signatures | PHASE 15 (Monitoring) |
| `71d06c1` | — | Loodoi billing entitlement architecture | PHASE 19 (Billing) |
| `64cf6eb` | **PHASE 9** | backlink provider architecture | PHASE 9 (Backlink provider) ✅ aligned |
| `3c626a2` | **PHASE 10** | SERP intelligence | PHASE 10 (SERP intelligence) ✅ aligned |
| `2d95f29` | **PHASE 11** | AEO / GEO visibility | PHASE 11 (AEO/GEO) ✅ aligned |

**The drift stops at PHASE 9.** From `64cf6eb` onward, work is numbered
directly against the master plan, so commit label = master-plan phase.

## Rule for new work

1. Name commits, test files and matrix rows **after the master plan's PHASE
   number** (vocabulary C).
2. When a hardening/audit iteration ships, call it what it is — e.g.
   `audit: close F12` — rather than inventing a new "phase".
3. If a master-plan PHASE is only partly delivered, say so in the matrix
   (`PARTIAL`) instead of counting the commit as done.

## Honest snapshot of master-plan coverage

Measured against the code, not against commit messages:

| Master phase | Status | Evidence |
|---|---|---|
| PHASE 2 — Enterprise crawler | **PARTIAL** | Solid HTML crawling, robots, sitemaps, SSRF guard. **No JS rendering** (no `IPageRenderer`, no Chromium). |
| PHASE 3 — 300+ rules | **PARTIAL** | ~27 rule classes, ~43 distinct rule codes. Nowhere near 300. |
| PHASE 4 — Performance | **PARTIAL** | `WebVitalsRules.cs` exists; no Core Web Vitals measurement pipeline is wired to a provider. |
| PHASE 5 — Content intelligence | REAL (thin tests) | 5 tests |
| PHASE 6 — Keyword intelligence | REAL (thin tests) | 4 tests |
| PHASE 7 — Search Console | **PARTIAL** | OAuth + sync boundary exist; credentials empty by default → untested in prod. |
| PHASE 8 — Competitor intelligence | REAL (thin tests) | 5 tests |
| PHASE 9 — Backlink provider architecture | REAL | 15 tests, no vendor adapter |
| PHASE 10 — SERP intelligence | REAL | 19 tests, no vendor adapter |
| PHASE 11 — AEO / GEO | **PARTIAL** | AEO UI and evidence-backed issue integration implemented; HTTP and concurrent PostgreSQL tests pass. Full-browser/load/live acceptance remains open. See the test gate for current evidence. |
| PHASE 12 — AI SEO expert | REAL (thin tests) | 1 test |
| PHASE 13 — Automated fixes | **MISSING** | — |
| PHASE 14 — Historical analysis | **MISSING** | — |
| PHASE 15 — Monitoring | REAL (thin tests) | 7 tests |

Two things follow from this table and should not be lost:

* **Rule count is ~43, not 300.** PHASE 3 is the single largest remaining gap.
* **Coverage for the shipped capability phases (5–8, 12, 15) is very thin** —
  1 to 7 tests each. PHASE 22 of the master plan asks for substantially more.
