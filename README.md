# MTG Deck Studio

MTG Deck Studio helps deck builders translate decks between Moxfield and Archidekt without manual editing. It also provides a multi-step ChatGPT prompt-building workflow, Commander Spellbook combo lookup, Scryfall card and mechanic references, and a cache-backed category suggestion engine.

**Repository description (≤350 characters):** MTG Deck Studio unifies Moxfield and Archidekt decks, harvests Archidekt category data, and exposes CLI/web tools for diffs, printing conflict reports, card/mechanic lookup, ChatGPT deck-analysis prompts, Commander Spellbook combo integration, and cache-backed category suggestions.

## Highlights
- `MtgDeckStudio.Core` contains parsers, diffing logic, exporters, and the Archidekt/Moxfield integrations.
- `MtgDeckStudio.Web` provides an ASP.NET Core MVC UI for running syncs, ChatGPT prompt building, card lookup, commander category browsing, and category suggestions.
- `MtgDeckStudio.CLI` exposes deck comparison, category harvesting, and cache querying in a console tool.
- The ChatGPT Packets page is the primary analysis workflow: it produces a structured probe prompt, resolves unknown cards via Scryfall, looks up rules for mechanics via the WOTC rules page, queries Commander Spellbook for combos, and assembles a complete analysis prompt with reference data attached.
- The Commander Categories page shows which Archidekt tags appear most often on decks where a given card is listed as commander.
- The Moxfield Tag Exporter browser extension exports deck tags from moxfield.com into Archidekt or Moxfield bulk-edit format.

## Getting Started
1. Restore/build: `dotnet build MtgDeckStudio.sln`
2. Run the web app: `dotnet run --project MtgDeckStudio.Web`
3. Use the CLI to compare or harvest decks: `dotnet run --project MtgDeckStudio.CLI -- --help`

---

## ChatGPT Packets Workflow

The ChatGPT Packets page (`/Deck/ChatGptPackets`) guides you through a multi-step prompt-building flow. Each step produces text to send to ChatGPT and tells you what to paste back.

### Workflow layout modes
Three layouts are available via the toolbar: **Guided**, **Focused**, and **Expert**. They present the same underlying steps with different amounts of context and guidance text.

### Step 1 — Probe
Paste a public **Moxfield** or **Archidekt** deck URL, or paste deck export text directly. The page generates a probe prompt that:
- Includes the full decklist with set code, collector number, and DFC front-face names on every line.
- Annotates the commander line with `[Commander]`.
- Falls back to treating leading quantity-1 entries as the commander when no Commander section header is present (Moxfield plain-text exports), then validates the inferred commander against Scryfall before continuing.
- Rejects inferred commanders that are not legal by the workflow rules: legendary creature, legendary Vehicle, or a planeswalker whose oracle text says it can be your commander.
- Returns commander-validation fields in the probe JSON schema (`commander_status`, `commander_name`, `commander_reason`) alongside `unknown_cards`.
- Embeds a suggested ChatGPT conversation title so the chat is named before the analysis starts.
- Asks ChatGPT to identify any card names it is uncertain about and return them as JSON.

Paste the JSON response from ChatGPT into the **Probe response JSON** field.

### Step 2 — Deck Profile
Send the provided deck-profile schema to ChatGPT with the deck. ChatGPT fills in the schema (game plan, primary axes, speed, strengths, weaknesses, weak slots, synergy tags). Paste the completed `deck_profile` JSON back in.

### Step 3 — Analysis Packet
Configure the analysis:

| Setting | Purpose |
|---|---|
| **Target Commander Bracket** | Bracket 1–5. ChatGPT uses this when evaluating card quality, interaction density, and upgrade suggestions. |
| **Analysis questions** | Select one or more questions from the buckets below. |
| **Card name** | Required when card-specific questions are selected. |
| **Budget amount** | Required when the budget-upgrade question is selected. |
| **Decklist export format** | Moxfield or Archidekt — required when category questions are selected; optional for versioning questions. |
| **Include card versions** | When checked, the original deck's set code and collector number are sent so ChatGPT can preserve the exact printing for retained cards. |
| **Preferred category names** | Shown when **Update categories** is selected. One name per line; ChatGPT will prefer these over inventing new ones. |
| **Protected cards** | Cards that must appear in every generated deck version. |

Click **Generate Analysis Packet** to build the reference data and analysis prompt. The service:
- Resolves unknown cards from the probe response via Scryfall (`POST /cards/collection` in batches of 75).
- Fetches official mechanic rules text from the WOTC rules page for any keyword mechanics found on resolved cards.
- Fetches the Commander banned list.
- Queries the Commander Spellbook API if combo questions are selected.
- Fires the banned-list fetch, set-packet fetch, and Spellbook combo lookup concurrently to minimize wait time.

### Step 4 — Set Upgrade (optional)
Select one or more recent MTG sets. The page generates a set-upgrade prompt that references the deck profile and asks ChatGPT to evaluate new cards from each set as potential inclusions, with suggested cuts and traps called out per set.

### Artifact saving
Check **Save artifacts to disk** to write all generated prompts and reference files to:
```
Documents\MTG Deck Studio\ChatGPT Packets\<commander-name>\<timestamp>\
```
Files saved: `input-summary.txt`, `probe-prompt.txt`, `probe-schema.json`, `reference.txt`, `analysis.txt`, `deck-profile-schema.json`, `set-upgrade-prompt.txt` (when applicable).

---

## Analysis Question Buckets

Questions are grouped into collapsible buckets. Buckets with pre-selected questions open automatically on page load.

| Bucket | Notable questions |
|---|---|
| **Core Deck Analysis** | Strengths/weaknesses, win condition, consistency, power level, best meta |
| **Deck Construction & Balance** | Mana curve, lands and ramp, card draw, interaction count, underperformers |
| **Strategy & Synergy** | Key synergies, anti-synergies, commander support, protect-cards, game plan |
| **Optimization & Upgrades** | Cuts for strength, budget upgrades (requires amount), missing staples, faster/competitive, board-wipe resilience |
| **Meta & Matchups** | Performance vs. archetypes, pod weaknesses, tech options, hate pieces |
| **Play Pattern & Decision Making** | Ideal opening hand, tutor priorities, when to cast the commander, common misplays |
| **Specific Card-Level Questions** | Card worth including (requires card name), better alternatives (requires card name), weakest card, too many high-CMC cards |
| **Advanced / Expert-Level** | Turn clock, disruption vulnerability, keepable hand percentage, redundancy, mana-base optimization |
| **Combo Analysis (Commander Spellbook)** | Combos already in the deck, combos one card away within the color identity — both use live Commander Spellbook API data injected into the prompt |
| **Deck Versioning & Upgrade Paths** | Bracket 2/3/4/5 version, 3 named upgrade paths, assign categories, update categories |

### Deck Versioning output format
When any versioning or category question is selected, the analysis prompt instructs ChatGPT to:
- Output the **full, complete 100-card decklist** for each generated version — no truncation, no "fill with basics" shorthand.
- Count cards before responding to confirm the total reaches 100.
- Use the deck builder's inline format when an export format is chosen:
  - **Moxfield**: `1 CardName (SET) collectorNumber` — or with categories: `1 CardName (SET) collectorNumber #Category1 #Category2`
  - **Archidekt**: `1 CardName (SET) collectorNumber [Category1,Category2]` — commander line uses `[Commander]`
- Output a **Cards Added** and **Cards Cut** diff after each decklist, comparing against the original.
- Output a `deck_profile` JSON block for each generated deck version.
- When **Include card versions** is checked, preserve the original printing (set code + collector number) for every retained card.

### Category / tag questions
- **Assign categories** — ChatGPT assigns functional role categories to every card in the deck. Plain text export is not supported; Moxfield or Archidekt format is required.
- **Update categories** — ChatGPT updates or reassigns categories using the preferred category names you provide. Preferred names are injected into the prompt; ChatGPT may add new categories only when none of the preferred names fit.
- Basic card types (Creature, Instant, Sorcery, Enchantment, Artifact, Planeswalker, Battle) are excluded as categories. ChatGPT is instructed to use functional role labels instead (Ramp, Card Draw, Removal, Wipe, Tutor, Win Condition, Protection, etc.).
- For category questions, the prompt explicitly requires the final decklist to be returned only inside a fenced `text` code block so it can be pasted directly into Moxfield or Archidekt bulk edit.

### Commander Spellbook combo lookup
When either combo question is selected, the service calls the Commander Spellbook `find-my-combos` API before building the prompt:
- Returns up to 20 **included combos** (all pieces are in the deck) and up to 15 **almost-included combos** (exactly one card missing, within the deck's color identity).
- Each combo entry lists the card names, results, and up to 300 characters of instructions.
- Results are injected as a reference block in the prompt. ChatGPT is told to treat this data as authoritative.
- Results are cached for 30 minutes keyed by the sorted deck card list.
- API failures degrade gracefully — the analysis continues without combo data rather than failing.

---

## Deck Sync

The Deck Sync page (`/Deck/DeckSync`) compares two decks and generates the delta import needed to bring the target deck in line with the source.

Supported sync directions:

| Direction | Description |
|---|---|
| MoxfieldToArchidekt | Moxfield as source, Archidekt as target |
| ArchidektToMoxfield | Archidekt as source, Moxfield as target |
| MoxfieldToMoxfield | Compare two Moxfield decks |
| ArchidektToArchidekt | Compare two Archidekt decks |

For same-system comparisons, column labels update dynamically to reflect the source and target platform.

---

## Card Lookup

The Card Lookup page (`/Deck/CardLookup`) accepts up to 100 card names and returns printing details (set code, collector number, mana cost, type line) from Scryfall via `POST /cards/collection` in batches of 75. Unknown names are reported clearly. Cards can be edited inline before submitting.

---

## Commander Categories

The Commander Categories page shows the Archidekt tags that appear most often on decks where a given card is listed as the commander. It reports what observers assigned, not what the app infers.

---

## Archidekt category cache
- Run `dotnet run --project MtgDeckStudio.CLI -- archidekt-cache --minutes 5` to keep the local cache fed with the latest public decks.
- The CLI runs a dedicated cache session that respects rate limits via Polly, records skips for noisy decks, and persists card/category observations to `artifacts/category-knowledge.db`.
- The web cache service reuses the same session logic for on-demand refreshes from the MVC UI.
- Basic card type categories (Creature, Instant, Sorcery, Enchantment, Artifact, Planeswalker, Battle) are filtered out of cache suggestions.

---

## Web API
Swagger UI is available at `/swagger` when running in Development mode.

### Category suggestion
```
POST /api/suggestions/card
Content-Type: application/json

{
  "mode": "CachedData",
  "archidektInputSource": "PublicUrl",
  "archidektUrl": "",
  "archidektText": "",
  "cardName": "Guardian Project"
}
```

### Commander category lookup
```
POST /api/suggestions/commander
Content-Type: application/json

{
  "commanderName": "Bello, Bard of the Brambles"
}
```

### cURL examples
```bash
curl -X POST http://localhost:5000/api/suggestions/card \
  -H "Content-Type: application/json" \
  -d '{"mode":"CachedData","archidektInputSource":"PublicUrl","cardName":"Guardian Project"}'

curl -X POST http://localhost:5000/api/suggestions/commander \
  -H "Content-Type: application/json" \
  -d '{"commanderName":"Bello, Bard of the Brambles"}'
```

---

## Scryfall usage
- Scryfall is used for card-name autocomplete, commander autocomplete, the Card Lookup page, and unknown-card resolution in the ChatGPT Packets workflow.
- All Scryfall clients send a real `User-Agent`, an explicit `Accept` header, and use `https`.
- Card lookup uses `POST /cards/collection` in batches of 75 identifiers.
- The Card Lookup page is capped at 100 non-empty input lines per submission (at most two Scryfall requests).
- Unknown-card resolution in the ChatGPT workflow uses the same batch endpoint; results are cached per session.

---

## CLI usage examples
```bash
dotnet run --project MtgDeckStudio.CLI -- compare \
  --moxfield my.deck --archidekt other.deck --out diff.txt

dotnet run --project MtgDeckStudio.CLI -- archidekt-cache --minutes 10

dotnet run --project MtgDeckStudio.CLI -- category-find \
  --card "Guardian Project" --cache-seconds 20
```

---

## Browser Extension

The **Moxfield Tag Exporter** Chrome/Edge extension adds export buttons on `moxfield.com/decks/*` pages. It reads the deck's `authorTags` map and exports:
- **Archidekt-style text**: `1 CardName (SET) cn [Tag,Tag]`
- **Moxfield-style text**: `1 CardName (SET) cn #Tag #Tag`

See [`browser-extensions/moxfield-tag-exporter/README.md`](browser-extensions/moxfield-tag-exporter/README.md) for installation instructions.

---

## Architecture
- Core logic is isolated in `MtgDeckStudio.Core` (diff engine, export helpers, parsers, integration clients, knowledge store).
- Web and CLI layers orchestrate requests and rely on DI to resolve shared services.
- Importers for Archidekt and Moxfield implement typed interfaces (`IMoxfieldDeckImporter`, `IArchidektDeckImporter`) for easy test substitution.
- `ChatGptDeckPacketService` parallelizes independent fetches (banned-list, set-packet, Commander Spellbook) using `Task.WhenAll` to reduce total build time.
- `CommanderSpellbookService` caches results for 30 minutes and degrades gracefully on API failure.
- `CategoryKnowledgeStore` persists observations to SQLite (`artifacts/category-knowledge.db`) and is shared between the web app and CLI.

---

## UI Notes
- The floating back-to-top control uses inline SVG in the shared layout, not the old `chevron-up.png` bitmap.
- The back-to-top button stays hidden while the page is already near the top and appears only after the user scrolls down.
