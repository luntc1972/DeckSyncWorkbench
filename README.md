# DeckFlow

DeckFlow helps deck builders translate decks between Moxfield and Archidekt without manual editing. It provides a deterministic mana-base analyzer and a local bracket classifier; a Cut Lab workspace for trimming an oversized Commander pool down to exactly 100 cards; a Deck Modules compiler for building a fixed-command-zone deck out of a shared core plus 2-4 named strategy alternatives with linked mana support, each compiled configuration checked for legality and compared against the existing Mana Base/Bracket signals; a Deck Version Tracker (Deck History) tool for versioning a deck and generating an evolution prompt; AI prompt-building workflows for single-deck analysis, cEDH meta-gap analysis, head-to-head deck comparison, and deck-primer generation; Commander Spellbook combo lookup, Scryfall card and mechanic references, an Ask-a-Judge handoff flow, public feedback capture, and a cache-backed category suggestion engine.

## User help
End-user documentation is served by the running web app at `/help` (feature guides) and `/about` (version, source, credits). This README keeps the developer-facing material (build, publish, API, CLI, deployment).

**Repository description (≤350 characters):** DeckFlow unifies Moxfield/Archidekt decks with a Commander Mana Base Analyzer, Commander Bracket Checker, Moxfield–Archidekt Deck Sync, Deck Version Tracker, Cut Lab trimmer, and MTG Decklist Converter — plus paste-ready AI prompts (analysis, primer, cEDH meta-gap), card/mechanic lookup, and Ask-a-Judge. Live at deckflow.gg.


## Documentation

- [User feedback](docs/user-feedback.md) — feedback collection, moderation, and storage guidance.
- [Features](docs/features.md) — product highlights.
- [Getting started](docs/getting-started.md) — setup, development, and deployment guidance.
- [Deck analysis](docs/deck-analysis.md) — analysis workflows, question buckets, comparisons, and cEDH meta guidance.
- [Content knowledge base](docs/content-knowledge-base.md) — content knowledge-base guidance.
- [Integrations](docs/integrations.md) — integrations, APIs, command-line, and browser extension guidance.
- [Release notes](docs/release-notes.md) — release history.

## Form behavior conventions

These rules exist because breaking them produced silent, hard-to-report bugs. They apply to every deck tool.

- **Enter must run the current step's action.** Pressing Enter in a text field (or "Go" on a mobile keyboard) is *implicit submission*: the browser activates the first submit button in DOM order. Because the sticky "Download session (.zip)" bar renders before the workflow buttons, that used to mean Enter downloaded a session zip on Deck Analysis, Deck Comparison and cEDH Meta Gap — and ran "Load deck & detect costs" instead of "Analyze Mana Base" on Commander Mana Base Analyzer. Each step now marks its intended button with `data-default-action`, and `deck-sync.ts` routes Enter to it. The download button is demoted to `type="button"` at runtime; the markup keeps `type="submit"` so the `<noscript>` download still works natively.
- **Never hide a form control with CSS.** A `display:none` input is not submitted, so the value silently resets on post. Hiding the "Include card versions" checkbox behind `desktop-only` meant every mobile submission reset it to false and produced different prompt output with no explanation. Hide the explanatory copy instead, never the control.
- **Every deck-input form carries a `data-cache-key`.** It keys deck-sync.ts's per-form state store, while deck-input-store.ts separately carries one decklist under `deckflow.last-deck` by field-name convention. Bracket and Mana Base had the carry mechanism but lacked the per-form store.
- **Do not put working functionality inside `<noscript>` only.** The printing-conflict resolution form on Moxfield–Archidekt Deck Sync existed solely in the `<noscript>` block, so with JavaScript on (the normal case) `/resolve` was unreachable and the swap checklist could never be generated. The panel is now a real form on both paths.
- **Client-side caps are advisory; enforce them on the server too.** See the Card Lookup line cap below.

## Architecture
- Core logic is isolated in `DeckFlow.Core` (diff engine, export helpers, parsers, integration clients, knowledge store).
- Web and CLI layers orchestrate requests and rely on DI to resolve shared services.
- Importers for Archidekt and Moxfield implement typed interfaces (`IMoxfieldDeckImporter`, `IArchidektDeckImporter`) for easy test substitution.
- `DeckAnalysisPacketService` parallelizes independent fetches (banned-list, set-packet, Commander Spellbook) using `Task.WhenAll` to reduce total build time.
- `DeckComparisonService` parses two decklists, resolves cards via Scryfall, queries Commander Spellbook for both decks, derives comparison context (role counts, mana curves, combo gaps), and generates structured AI prompts with a JSON output schema.
- `CommanderSpellbookService` caches results for 30 minutes and degrades gracefully on API failure.
- `CategoryKnowledgeStore` persists observations through the configured relational provider. SQLite stores `artifacts/category-knowledge.db` by default; Postgres can be selected with `DECKFLOW_DATABASE_PROVIDER=Postgres`.

---

## UI Notes
- The floating back-to-top control uses inline SVG in the shared layout, not the old `chevron-up.png` bitmap.
- The back-to-top button stays hidden while the page is already near the top and appears only after the user scrolls down.

### Visual themes
A persistent theme picker in the shared layout lets users switch between visual themes. The selection is stored in `localStorage` and applied on page load. The shared layout now enhances that native select with an ARIA combobox button/listbox while preserving the original form control for form posts and keyboard fallback. Available themes:
- **Classic** — the base site stylesheet
- **Azorius (WU)**, **Dimir (UB)**, **Rakdos (BR)**, **Gruul (RG)**, **Selesnya (GW)**, **Orzhov (WB)**, **Izzet (UR)**, **Golgari (BG)**, **Boros (RW)**, **Simic (GU)** — the ten two-color guild palettes
- **Bant (GWU)**, **Abzan (WBG)**, **Sultai (BGU)**, **Mardu (RWB)**, **Temur (GUR)**, **Esper (WUB)**, **Grixis (UBR)**, **Jund (BRG)**, **Naya (RGW)**, **Jeskai (URW)** — the ten three-color shard/wedge palettes
- **Nyx** — enchantment-themed dark palette
- **Planeswalker Dark** — dark-mode palette
- **Commander Table** — warm tabletop-inspired palette

---

## License

DeckFlow is licensed under the [Apache License 2.0](LICENSE). Copyright 2026 Chris Lunt.

### Code vs. brand

The Apache 2.0 license covers the **source code only**. You are free to use,
modify, and self-host it — including commercially — provided you keep the
license and copyright notices and reproduce the [`NOTICE`](NOTICE) file in any
redistribution.

The license does **not** grant any right to the DeckFlow name, logo, or brand.
Under Apache 2.0 §6, trademarks are excluded from the grant. If you fork or
self-host, you must:

- not name your instance or derivative "DeckFlow";
- not use the DeckFlow logo or branding;
- not represent your deployment as the official DeckFlow, as originating from
  `deckflow.gg`, or as endorsed by or affiliated with DeckFlow.

"DeckFlow" is a trademark of Chris Lunt.
