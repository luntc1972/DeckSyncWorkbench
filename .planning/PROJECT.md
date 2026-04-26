# DeckFlow

## What This Is

DeckFlow is a Magic: The Gathering deck-builder workbench. It bridges Moxfield and Archidekt without manual editing, generates structured ChatGPT prompts for deck analysis / set-upgrade / cEDH meta-gap / deck comparison, and provides Scryfall-backed card and mechanic lookup, Ask-a-Judge handoff, Commander Spellbook combo references, an Archidekt category-knowledge cache, public feedback capture, and a companion Chrome/Edge browser extension. It serves both a web UI (ASP.NET Core MVC) and a CLI tool.

## Core Value

When a Commander player drops a deck into DeckFlow, the workflow they pick — sync, analysis, comparison, meta-gap, lookup — must complete end-to-end without surprise breakage; tools and data must stay trustworthy under load and as upstream APIs drift.

## Requirements

### Validated

<!-- Shipped capabilities currently relied upon by the running app. Locked unless a milestone explicitly retires them. -->

- ✓ Bidirectional Moxfield ⇄ Archidekt deck sync with diff/import generation — existing
- ✓ Plaintext deck input parsing for both platforms (DeckFlow.Core.Loading shared by Web + CLI) — existing
- ✓ ChatGPT Analysis 5-step workflow (deck setup → analysis prompt → JSON parse → set-upgrade prompt → set-upgrade JSON parse) — existing
- ✓ ChatGPT Deck Comparison workflow with `deck_comparison` JSON contract — existing
- ✓ ChatGPT cEDH Meta Gap workflow against EDHTop16 reference decks — existing
- ✓ Scryfall card resolution via `POST /cards/collection` (75-batch) with shared throttle (~9 req/s) — existing
- ✓ Card Lookup page (Single Card + Card List modes) with Scryfall rulings + keyword-mechanic rules — existing
- ✓ Mechanic Rules lookup against the official WOTC comprehensive rules — existing
- ✓ Ask-a-Judge handoff with chat.magicjudges.org link + secondary ChatGPT prompt fallback — existing
- ✓ Commander Categories report (Archidekt tag frequency for a given commander) — existing
- ✓ AI Category Suggestions (CachedData / ReferenceDeck / ScryfallTagger / All modes) — existing
- ✓ Archidekt category-knowledge cache (SQLite-backed) refreshable from CLI and background web job — existing
- ✓ Commander Spellbook combo lookup with 30-minute cache and graceful API-failure degrade — existing
- ✓ Commander ban list integration via mtgcommander.net scrape with 6-hour memory cache — existing
- ✓ Public Feedback form + admin queue (`/Admin/Feedback`) with Basic Auth, IP hashing, rate limiting — existing
- ✓ DeckFlow Bridge browser extension (Chrome/Edge) for Moxfield URL fetch via logged-in session — existing
- ✓ Same-origin Origin/Referer hardening on browser-facing JSON POST APIs — existing
- ✓ Theme picker with persistence (default + 3 specialty + 5 wedges + 5 shards already at full parity) — existing
- ✓ Mobile responsiveness sweep with cascade-safe `site-mobile.css` overrides — existing
- ✓ df-select ARIA 1.2 combobox foundation rolled across 23+ select elements — existing
- ✓ df-typeahead module powering all autocomplete inputs — existing
- ✓ Docker / Fly.io / Render deployment config + IIS publish profile — existing

### Active

<!-- Hardening Pass milestone scope. Hypotheses until shipped + verified. -->

**Critical infrastructure**
- [ ] Replace per-call `new HttpClient()` in CommanderBanListService, CommanderSpellbookService, ScryfallTaggerService with `IHttpClientFactory` named/typed clients
- [ ] Configure Markdig with `DisableHtml()` to neutralize the `@Html.Raw` Help-topic XSS surface
- [ ] Cache BasicAuth credentials at middleware construction (not per-request env var read); fail at startup if unconfigured
- [ ] Extend rate limiter coverage to Scryfall-backed API endpoints (`/api/cards/search`, `/api/suggestions/*`)
- [ ] Add Tagger CSRF-token/cookie short-window cache (eliminate two of three sequential round-trips per call)

**Refactor / structure**
- [ ] Extract `ICategoryKnowledgeRepository` interface; update 5 consumers to depend on the abstraction
- [ ] Split `ChatGptDeckPacketService` (1855 LOC) into PromptBuilder + DeckLoader + ArtifactPersistence collaborators
- [ ] Decompose `deck-sync.ts` (2426 LOC) into per-feature TypeScript modules (Sync / SuggestCategories / ChatGptPackets)

**Test coverage**
- [ ] Add `DeckSyncSupportTests` covering every sync-direction permutation
- [ ] Add `CommanderSpellbookServiceTests` (combo lookup, deserialization, cache, error paths)
- [ ] Add `ScryfallTaggerServiceTests` covering the live HTTP flow (Scryfall REST → CSRF → GraphQL)
- [ ] Add `ChatGptPacketArtifactStoreTests` (artifact directory, JSON serialization, path resolution)
- [ ] Set up vitest + jsdom for TypeScript; cover `df-select`, `df-typeahead`, and `deck-sync.ts` modules

**Resilience / scrapers**
- [ ] Add ban-list non-empty assertion + below-threshold warning (mtgcommander.net silent-empty guard)
- [ ] Add Commander Spellbook contract smoke test (top-level JSON shape validation)
- [ ] Audit and document Tagger graceful-degrade path (returns empty tag list rather than throwing)

**Performance**
- [ ] HelpContentService startup warmup (eliminate first-request `Lazy<T>` blocking)
- [ ] Audit `CategoryKnowledgeRepository` for sync `ExecuteReader` paths; convert to `ExecuteReaderAsync`

**UX**
- [ ] Build all 10 two-color guild themes (Azorius/Boros/Dimir/Golgari/Gruul/Izzet/Orzhov/Rakdos/Selesnya/Simic) up to three-color cascade parity (~1200 LOC each)

### Out of Scope

<!-- Explicit boundaries on what this milestone is NOT. -->

- New user-facing features — this milestone is correctness/durability only
- Migrating CommanderBanListService off mtgcommander.net (e.g., to Scryfall legality data) — considered, deferred to a future resilience milestone; non-empty-assertion guard is sufficient for v1
- Direct ChatGPT/Claude API integration (replacing copy-paste prompt flow) — saved for a later AI-integration milestone
- Embeddings / on-server LLM analysis / eval harness — same later AI milestone
- Mobile-native deck-builder rewrite — current mobile sweep is adequate
- Multi-deck collection management / playgroup features — out-of-scope feature scope
- New Magic format support beyond Commander — Commander is the focus
- Removing the two-color guild themes from the picker — user explicitly wants them present and working

## Context

**Codebase state.** Brownfield ASP.NET Core MVC 10 + .NET 10 Core libs + .NET CLI + TypeScript frontend, mapped 2026-04-26 (`.planning/codebase/`). Stack: C# 13, TypeScript 6.x, RestSharp, Polly, Markdig, Serilog, Microsoft.Data.Sqlite, xunit. Recent activity heavy on UX polish (mobile sweep, df-select ARIA combobox, df-typeahead extraction, themed Help/About pages) and feature breadth (multi-card picker, set-upgrade workflow, reprint filter, feedback system).

**Why hardening now.** Recent feature/UX velocity left a measurable backlog of CONCERNS.md items: socket-exhaustion risk in three external services, missing test coverage on critical helpers, a 1855-LOC service class, a 2426-LOC TypeScript file, stub guild themes broken in the picker, and brittle scraper integrations. None are blocking today, but every additional feature compounds the risk.

**Tech debt source.** All 28 Active items trace directly to `.planning/codebase/CONCERNS.md` (audit dated 2026-04-26).

**Deploy targets.** Production runs on Fly.io (Docker + persistent `/data` volume). IIS publish profile is also maintained. Tests must stay green on both.

## Constraints

- **Tech stack**: ASP.NET Core MVC 10 + .NET 10 + TypeScript 6.x + xunit — no framework swap during hardening
- **External APIs**: Scryfall (10 req/s soft cap), Commander Spellbook v2, Scryfall Tagger (unofficial), mtgcommander.net (HTML scrape), EDHTop16 — must keep cooperating with all of these or document the breakage explicitly
- **Storage**: SQLite via Microsoft.Data.Sqlite at `$MTG_DATA_DIR` — no DB engine change
- **Compatibility**: Existing public surface (web routes, JSON contracts for ChatGPT packets, CLI commands) must remain backward-compatible — refactors are internal-only
- **Dependency**: `IHttpClientFactory` registration goes in `Program.cs`; injected services keep current public APIs to limit blast radius
- **Test bar**: zero-warning build + all tests green is the milestone-done gate; no coverage threshold gate this milestone

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Hardening pass before any new features | Recent velocity outpaced quality work; CONCERNS.md backlog is concrete | — Pending |
| Keep all 10 two-color guild themes; build to parity rather than cut | User explicitly wants them present and working in the theme picker | — Pending |
| Keep mtgcommander.net scraper; add non-empty assertion instead of migrating to Scryfall legality | Migration is deferrable; assertion captures most of the safety benefit at low cost | — Pending |
| Single phase for all 10 guild themes (not split, not per-theme) | One review cycle, consistent quality bar across themes | — Pending |
| Set up vitest + jsdom in this milestone (df-select, df-typeahead, deck-sync.ts) | Two ARIA-heavy modules + the largest TS file are highest-leverage TS coverage | — Pending |
| Roll performance items into hardening rather than separate milestone | Tagger/HelpContent/SQLite touches overlap with same services as critical-infra fixes | — Pending |
| YOLO execution mode | Items are well-scoped from CONCERNS.md; auto-approve plans for cadence | — Pending |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd-transition`):
1. Requirements invalidated? → Move to Out of Scope with reason
2. Requirements validated? → Move to Validated with phase reference
3. New requirements emerged? → Add to Active
4. Decisions to log? → Add to Key Decisions
5. "What This Is" still accurate? → Update if drifted

**After each milestone** (via `/gsd-complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-04-26 after initialization*
