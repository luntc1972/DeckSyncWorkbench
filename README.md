# DeckFlow

DeckFlow helps deck builders translate decks between Moxfield and Archidekt without manual editing. It also provides ChatGPT prompt-building workflows for single-deck analysis, cEDH meta-gap analysis, and head-to-head deck comparison, Commander Spellbook combo lookup, Scryfall card and mechanic references, and a cache-backed category suggestion engine.

**Repository description (≤350 characters):** DeckFlow unifies Moxfield and Archidekt decks, harvests Archidekt category data, and exposes CLI/web tools for diffs, printing conflict reports, card/mechanic lookup, ChatGPT deck-analysis, cEDH meta-gap, and deck-comparison prompt generation with Scryfall references, Commander Spellbook combos, and cache-backed category suggestions.

## Highlights
- `DeckFlow.Core` contains parsers, diffing logic, exporters, and the Archidekt/Moxfield integrations.
- `DeckFlow.Core.Loading` centralizes deck input loading and Commander deck-size validation so the web app and CLI share the same parsing/import rules.
- `DeckFlow.Web` provides an ASP.NET Core MVC UI for running syncs, ChatGPT prompt building, cEDH meta-gap analysis, deck comparison prompt building, card lookup, commander category browsing, and category suggestions.
- `DeckFlow.CLI` exposes deck comparison, category harvesting, and cache querying in a console tool.
- The ChatGPT Analysis page is the primary single-deck analysis workflow: it resolves card text via Scryfall, looks up rules for mechanics via the WOTC rules page, queries Commander Spellbook for combos, and assembles a complete analysis prompt with reference data attached.
- The cEDH Meta Gap page compares a submitted deck against 1 to 3 EDH Top 16 reference lists for the same commander, resolves canonical card names through Scryfall, injects Commander Spellbook combo references, generates a structured `meta_gap` ChatGPT prompt, and renders the returned JSON as a readable upgrade path.
- The Commander Categories page shows which Archidekt tags appear most often on decks where a given card is listed as commander.
- The Moxfield Tag Exporter browser extension exports deck tags from moxfield.com into Archidekt or Moxfield bulk-edit format.
- DeckFlow can optionally prompt users to install the included Moxfield browser extension when a Moxfield URL import would otherwise be blocked from the server.

## Getting Started
1. Restore/build: `dotnet build DeckFlow.sln`
2. Run the web app: `dotnet run --project DeckFlow.Web`
3. Use the CLI to compare or harvest decks: `dotnet run --project DeckFlow.CLI -- --help`

### Helper scripts
- `scripts/run-web.sh` — bash wrapper that rebuilds `DeckFlow.Web` and launches it on `http://localhost:5173` with no browser auto-launch.
- `scripts/run-web.ps1` — PowerShell equivalent for Windows terminals.

### UI styling
- `DeckFlow.Web/wwwroot/css/site-common.css` contains shared shell and view-level styles that apply regardless of the selected color theme.
- `DeckFlow.Web/wwwroot/css/site*.css` files remain responsible for theme palettes and component styling.
- Keep long-lived CSS out of Razor views; prefer shared stylesheets so caching and theme behavior stay predictable.

### IIS publish
- Publish the web app with `dotnet publish DeckFlow.Web/DeckFlow.Web.csproj /p:PublishProfile=IIS-LocalFolder`
- The publish output goes to `DeckFlow.Web/bin/Release/net10.0/publish/iis-local/`
- The .NET SDK generates `web.config` during publish; there is no checked-in `web.config`
- In IIS, create an application such as `/deckflow` that points at that publish folder
- Install the ASP.NET Core Hosting Bundle on the IIS machine
- The checked-in views and scripts are path-base safe, so links and API calls stay under the IIS application path instead of jumping to `/`

### Deploying to cloud hosts (Render, Fly, etc.)
- A `Dockerfile`, `fly.toml`, and `render.yaml` ship at the repo root for one-command builds on Fly.io or Render.
- Set `MTG_DATA_DIR=/data` and mount a persistent volume there. Both the SQLite category-knowledge DB and the ChatGPT Analysis artifact folder get redirected under that single directory.
- The Dockerfile's entrypoint resolves `$PORT` at container start so platforms that inject a dynamic port (Render) work without changes.
- **Moxfield URL caveat.** Moxfield's Cloudflare edge blocks requests from datacenter IP ranges with HTTP 403/5xx. When that happens, DeckFlow automatically falls back to Commander Spellbook's public `card-list-from-url` endpoint (which accepts the same Moxfield URL) and loads the deck from there instead. The UI surfaces a warning banner noting that card printings, set codes, collector numbers, author tags/categories, and sideboard/maybeboard entries are not available through the fallback. For full metadata, users should copy the Moxfield deck export text and paste it into the deck input directly — that path continues to work from anywhere.
- **Optional browser-extension path.** The web UI now detects Moxfield deck URLs before submit. If the optional DeckFlow browser extension is installed, the browser fetches the Moxfield deck directly, converts it to Moxfield bulk-edit text, and submits that text through the existing form flow. If the extension is not installed, DeckFlow can prompt the user with an install page (`/extension-install.html`). Browsers do not allow the site to silently install the extension. Mobile browsers are left on the normal server/fallback path and are not prompted for the extension.

### Browser extension install
- Extension folder: `browser-extensions/moxfield-tag-exporter`
- Current install mode: unpacked extension via `chrome://extensions` or `edge://extensions`
- The extension contains:
  - `content.js` for exporting directly on `moxfield.com/decks/*`
  - `deckflow-bridge.js` for the optional DeckFlow web-app bridge
  - `background.js` for cross-origin Moxfield API requests

---

## ChatGPT Analysis Workflow

The ChatGPT Analysis page (`/Deck/ChatGptPackets`) guides you through a 5-step workflow. Step 2 generates the analysis prompt, Step 3 parses and renders the returned `deck_profile` JSON, Step 4 optionally generates a set-upgrade prompt using that parsed profile, and Step 5 parses and renders the returned `set_upgrade_report` JSON.

### Workflow layout modes
Three layouts are available via the toolbar: **Guided**, **Focused**, and **Expert**. They present the same underlying steps with different amounts of context and guidance text.

### Step 1 — Deck Setup
Choose an **Input method** (paste text or public deck URL) and provide either a **Moxfield**/**Archidekt** deck URL or pasted deck export text. The chosen mode round-trips with the form so it survives refreshes and workflow-step navigation. The service:
- Falls back to treating leading quantity-1 entries as the commander when no Commander section header is present (Moxfield plain-text exports), then validates the inferred commander against Scryfall before continuing.
- Rejects inferred commanders that are not legal by the workflow rules: legendary creature, legendary Vehicle, or a planeswalker whose oracle text says it can be your commander.

### Step 2 — Analysis
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
- Resolves all deck cards via Scryfall (`POST /cards/collection` in batches of 75) to supply authoritative Oracle text.
- Fetches official mechanic rules text from the WOTC rules page for any keyword mechanics found on resolved cards.
- Fetches the Commander banned list.
- Queries the Commander Spellbook API if combo questions are selected.
- Fires the banned-list fetch, set-packet fetch, and Spellbook combo lookup concurrently to minimize wait time.
- Generates a suggested ChatGPT conversation title displayed in the UI with a copy button.

The generated prompt uses `##` section headings (TASK, EVIDENCE RULES, BRACKET GUIDANCE, ANALYSIS QUESTIONS, OUTPUT FORMAT, REFERENCE DATA, DECKLIST) to keep long prompts structured.

### Step 3 — Analysis Results
Paste the fenced `deck_profile` JSON block or raw JSON payload returned from ChatGPT. You can also paste a saved `deck_profile` JSON file here directly without filling out Steps 1 and 2 again. The page validates the payload, parses it into a strongly typed model, and renders a readable summary of:
- Format and commander
- Game plan, speed, primary axes, and synergy tags
- Strengths, weaknesses, deck needs, and weak slots
- Per-question answers with basis notes
- Full deck versions when versioning questions were requested

This step is local to the returned JSON. It does not regenerate the analysis packet or call upstream services again.

### Step 4 — Set Upgrade (optional)
Select one or more recent MTG sets, or paste a condensed set packet override. The page generates a set-upgrade prompt that references the parsed deck profile and asks ChatGPT to evaluate new cards from each set as potential inclusions, with suggested cuts, bracket-fit notes, speculative tests, and traps called out per set. The set dropdown loads asynchronously from `/api/set-options` so the page renders immediately. A deck in Step 1 is required; the parsed Step 3 deck profile is optional but strongly recommended — without it ChatGPT gets an empty schema and produces generic recommendations.

### Step 5 — Set Upgrade Results (optional)
Paste the fenced `set_upgrade_report` JSON block or raw JSON payload returned from ChatGPT. The page validates the payload, parses it into a strongly typed model, and renders a readable summary of:
- Per-set panels: top adds with suggested cuts and reasoning, traps, and speculative tests
- Final shortlist broken into must-test, optional, and skip columns

Like Step 3, this step is local to the returned JSON. You can paste a saved `set_upgrade_report` JSON file here directly without re-running the earlier steps — Step 5 runs standalone when no deck source is present.

### Prompt output-format rules
All ChatGPT prompts generated by this app (analysis, set-upgrade, deck comparison, meta-gap) explicitly instruct ChatGPT to return JSON inside a fenced ```` ```json ```` code block. Raw JSON outside a code block is rejected by the wording.

### Artifact saving
Check **Save artifacts to disk** to write all generated prompts and reference files to:
```
Documents\DeckFlow\ChatGPT Analysis\<commander-name>\<timestamp>\
```
Files saved: `01-request-context.txt`, `00-input-summary.txt`, `30-reference.txt`, `31-analysis-prompt.txt`, `41-deck-profile-schema.json`, `50-set-upgrade-prompt.txt` (when applicable), plus `40-deck-profile.json` and `51-set-upgrade-response.json` capturing the pasted Step 3 and Step 5 JSON responses. The standalone Step 3 and Step 5 paste flows also save their pasted JSON when **Save artifacts to disk** is checked.

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

## ChatGPT Deck Comparison

The Deck Comparison page (`/Deck/ChatGptDeckComparison`) generates structured ChatGPT prompts for comparing two Commander decklists side by side. It lives under the **ChatGPT** dropdown alongside the Analysis page.

### Step 1 — Deck Setup
Paste two decklists (Moxfield/Archidekt URL or plain-text export) and select a Commander Bracket for each deck. Optionally name each deck — the service falls back to the commander name if left blank.

### Step 2 — Generate Comparison Packet
The service:
- Parses both decklists, resolving cards via Scryfall `POST /cards/collection` in batches of 75.
- Falls back to per-card Scryfall search when a submitted name is an alternate-art or Universes Beyond printing that does not round-trip through the collection endpoint cleanly, then labels rendered decklists as `resolved name [printed as: submitted name]`.
- Queries Commander Spellbook for combos in each deck.
- Builds a comparison context document with bracket definitions, role counts (ramp, draw, interaction, wipes, recursion, closing power), mana curves, color identity, category overlap, and combo gaps.
- Generates a structured comparison prompt with `## TASK`, `## RULES`, `## COMPARISON AXES`, `## OUTPUT FORMAT`, deck sections, and comparison context. The prompt instructs ChatGPT to produce both a human-readable comparison and a fenced `json` block matching a `deck_comparison` schema.
- Generates a follow-up prompt for iterative refinement of the comparison.

Comparison axes include: commander role and game plan, speed and setup tempo, ramp, draw, spot interaction, sweepers, recursion, closing power (including combos), resilience, consistency, mana stability, commander dependence, table fit, major overlap/differences, and five concrete cards or packages that best explain the gap.

### Step 3 — Review Results
Paste ChatGPT's JSON response back into the form. The page parses the `deck_comparison` JSON and renders a formatted view with:
- Game plans and bracket labels for each deck
- Strengths and weaknesses per deck
- Key combos per deck
- Verdict panel: speed, resilience, interaction, mana consistency, closing power, and combo comparisons
- Shared themes and major differences
- Key gap cards or packages
- Recommended-for notes per deck
- Confidence notes (when ChatGPT flags uncertainty)

If you continue asking follow-up questions in the same ChatGPT thread, use `32-comparison-follow-up-prompt.txt` to have ChatGPT revise the readable comparison and regenerate the full `deck_comparison` JSON block.

### Artifact saving
Check **Save artifacts to disk** to write generated files to:
```
Documents\DeckFlow\ChatGPT Deck Comparison\<timestamp>\
```
Files saved: `00-comparison-input-summary.txt`, `10-deck-a-list.txt`, `11-deck-b-list.txt`, `20-comparison-context.txt`, `30-comparison-prompt.txt`, `32-comparison-follow-up-prompt.txt`.

### Prompt templates
The `prompt-templates/deck-comparison/` directory contains reference templates for compact and JSON-structured comparison prompts: all-in-one, competitive meta, matchup, quick verdict, JSON matchup, JSON strict return, and JSON tuning variants. See `docs/deck-comparison-prompt-cheat-sheet.md` for usage guidance.

---

## ChatGPT cEDH Meta Gap

The cEDH Meta Gap page (`/chatgpt-cedh-meta-gap`) generates a structured ChatGPT workflow for comparing your deck against recent EDH Top 16 lists for the same commander.

### Step 1 — Load Deck And Fetch References
Paste a public Moxfield or Archidekt URL, or paste deck export text directly. You can optionally override the commander name. The page then queries EDH Top 16 using:

- Time period
- Sort by (`TOP` or `NEW`)
- Minimum event size
- Maximum standing

The service parses the submitted deck, removes sideboard and maybeboard cards, resolves the commander, fetches matching EDH Top 16 entries, and sorts them newest-first before display.

### Step 2 — Generate Meta-Gap Prompt
Select 1 to 3 EDH Top 16 reference decks and generate the prompt. The service builds:

- `30-meta-gap-prompt.txt`
- `31-meta-gap-schema.json`

While building the prompt, the service also:

- Resolves submitted-deck and reference-deck card names through Scryfall so alternate print names and reskins are converted to canonical Oracle names where possible.
- Normalizes split and multi-face names to the base/front name for prompt display.
- Queries Commander Spellbook for your deck and for each selected reference deck, then injects combo summaries into the prompt.
- Caps the reference-deck count at 3 to keep the prompt size reasonable once decklists and combo references are included.

The prompt is structured with clear sections:

- `ROLE`
- `EVIDENCE PRIORITY`
- `RULES`
- `INPUT DATA`
- `ANALYSIS TASK`
- `OUTPUT CONTRACT`
- `JSON SHAPE`

ChatGPT is instructed to:

- Write a concise human-readable meta-gap summary first.
- Then return a fenced `json` block whose top-level object is `meta_gap`.
- Prefer the supplied Commander Spellbook combo evidence over weaker inferred combo reads when they conflict.
- Fill every field, using empty strings, zero values, `false`, or empty arrays when evidence is missing.

### Step 3 — Paste Returned JSON
Paste the raw JSON or fenced `json` block back into the page. The shared JSON extractor accepts fenced responses and ignores surrounding prose or extra trailing fence noise before parsing the payload. The page renders:

- Overview and readiness score
- Win lines
- Interaction
- Speed
- Mana efficiency
- Core convergence
- Missing staples
- Potential cuts
- Top 10 adds and cuts

### Artifact saving
Check **Save artifacts to disk** to write generated files to:
```
Documents\DeckFlow\ChatGPT cEDH Meta Gap\<commander-name>\<timestamp>\
```
Files saved: `00-input-summary.txt`, `30-meta-gap-prompt.txt`, `31-meta-gap-schema.json`, `40-meta-gap-response.json` (when a JSON response is pasted).

---

## Deck Sync

The Deck Sync page (`/sync`) compares two decks and generates the delta import needed to bring the target deck in line with the source.

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

The Card Lookup page (`/card-lookup`) has two modes:

- **Single Card** (default; the only mode visible on mobile) — type a card name, get live Scryfall suggestions once you've entered 4+ characters, and picking a suggestion (or pressing Look Up) renders that card's Oracle text plus WOTC rulings inline via `GET /card-lookup/single`.
- **Card List** (desktop-only) — paste up to 100 card names and download the full Scryfall output as `.txt` (`POST /card-lookup/download`) or structured `.json` (`POST /card-lookup/download-json`). The inline line editor with per-row autocomplete is still available for editing before downloading.

Under the hood all modes use the same `ICardLookupService`: the card collection is fetched via `POST /cards/collection` in batches of 75, and rulings are fetched per-card via `GET /cards/{id}/rulings`.

The Single Card result panel includes an "Ask a rules question about this card →" link that deep-links into `/judge-questions?card=<name>`.

---

## Ask a Judge

The Ask a Judge page (`/judge-questions`) leads with a prominent link to the live community judge chat at [`chat.magicjudges.org/mtgrules`](https://chat.magicjudges.org/mtgrules/) — a 24/7 IRC channel (`#magicjudges-rules` on Libera.Chat) staffed by certified judges and rules experts. This is the authoritative path. When the page is opened with a `?card=<name>` query parameter (e.g. from Card Lookup), it pre-formats a `!CardName — ` opening message ready to copy into the chat.

A clearly labeled **secondary** ChatGPT prompt generator is provided below for casual play and quick second opinions. It carries a prominent disclaimer ("ChatGPT can be confidently wrong about MTG rules") and, if a reference card is supplied, fetches that card's Oracle text and rulings via `GET /card-lookup/single` and embeds them in the generated prompt. The prompt itself starts with the same warning so ChatGPT cannot bury it.

---

## Commander Categories

The Commander Categories page shows the Archidekt tags that appear most often on decks where a given card is listed as the commander. It reports what observers assigned, not what the app infers.

---

## AI Category Suggestions

The AI Category Suggestions page supports multiple lookup modes:

- `CachedData`
- `ReferenceDeck`
- `ScryfallTagger`
- `All`

Current behavior:

- `ReferenceDeck` reads exact categories from a supplied Archidekt deck URL or pasted Archidekt text.
- `CachedData` runs a short local cache sweep, then reads category hits from the local Archidekt-backed store.
- `ScryfallTagger` returns oracle-tag style suggestions from Scryfall Tagger.
- `All` combines the cached-store path and tagger path, with EDHREC as a fallback when no other source returns anything.

The page also exposes a background Archidekt harvest button so the local category store can be refreshed while the rest of the UI remains usable.

---

## Archidekt category cache
- Run `dotnet run --project DeckFlow.CLI -- archidekt-cache --minutes 5` to keep the local cache fed with the latest public decks.
- The CLI runs a dedicated cache session that respects rate limits via Polly, records skips for noisy decks, and persists card/category observations to `artifacts/category-knowledge.db`.
- The web cache service reuses the same session logic for on-demand refreshes from the MVC UI.
- The AI Category Suggestions page can start a 5-minute Archidekt harvest as a background job. The rest of the site stays usable while it runs, only one harvest is allowed at a time, and a local browser notification/banner appears when the job completes.
- Background harvest state is polled from the web app, and the start button stays disabled while the job is queued or running.
- The cache session now stays alive for the requested harvest window even when the queue runs dry, and it retries transient recent-page fetch failures instead of ending the whole job early.
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

### Archidekt cache background jobs
Start a background harvest:
```
POST /api/archidekt-cache-jobs
Content-Type: application/json

{
  "durationSeconds": 300
}
```

Poll a specific job:
```
GET /api/archidekt-cache-jobs/{jobId}
```

Get the currently active job, if any:
```
GET /api/archidekt-cache-jobs/active
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
- Scryfall is used for card-name autocomplete, commander autocomplete, the Card Lookup page, card reference resolution in the ChatGPT Analysis workflow, and async set catalog loading.
- All Scryfall clients send a real `User-Agent`, an explicit `Accept` header, and use `https`.
- Card lookup uses `POST /cards/collection` in batches of 75 identifiers.
- The Card Lookup page is capped at 100 non-empty input lines per submission (at most two `cards/collection` requests plus one `cards/{id}/rulings` request per unique resolved card, all throttled).
- The ChatGPT workflow uses the same batch endpoint to resolve authoritative Oracle text for all deck cards.
- The set catalog is fetched via `GET /sets` and cached in memory for 6 hours; the web UI loads it asynchronously via `/api/set-options`.

### Rate limiting
- Scryfall enforces a soft cap of 10 requests per second at the Cloudflare edge (no proactive `X-RateLimit-*` headers on 200 responses; only `Retry-After` on 429).
- `ChatGptDeckPacketService` throttles all Scryfall calls to ~110ms apart (≈9 req/s) via a process-wide semaphore so batched collection lookups plus per-card fallback searches stay under the cap.
- On a 429 the wrapper reads `Retry-After` and retries once if the cooldown is ≤5 seconds; longer cooldowns surface as a friendly "Scryfall returned HTTP 429. Try again shortly." error instead of being misattributed to card/commander validation.
- The CLI ships a diagnostic `scryfall-probe` command that calls Scryfall and dumps status, headers, and body — useful for reproducing rate-limit responses. Example: `dotnet run --project DeckFlow.CLI -- scryfall-probe --endpoint random --repeat 25` (intentionally triggers 429).

---

## CLI usage examples
```bash
dotnet run --project DeckFlow.CLI -- compare \
  --moxfield my.deck --archidekt other.deck --out diff.txt

dotnet run --project DeckFlow.CLI -- archidekt-cache --minutes 10

dotnet run --project DeckFlow.CLI -- category-find \
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
- Core logic is isolated in `DeckFlow.Core` (diff engine, export helpers, parsers, integration clients, knowledge store).
- Web and CLI layers orchestrate requests and rely on DI to resolve shared services.
- Importers for Archidekt and Moxfield implement typed interfaces (`IMoxfieldDeckImporter`, `IArchidektDeckImporter`) for easy test substitution.
- `ChatGptDeckPacketService` parallelizes independent fetches (banned-list, set-packet, Commander Spellbook) using `Task.WhenAll` to reduce total build time.
- `ChatGptDeckComparisonService` parses two decklists, resolves cards via Scryfall, queries Commander Spellbook for both decks, derives comparison context (role counts, mana curves, combo gaps), and generates structured ChatGPT prompts with a JSON output schema.
- `CommanderSpellbookService` caches results for 30 minutes and degrades gracefully on API failure.
- `CategoryKnowledgeStore` persists observations to SQLite (`artifacts/category-knowledge.db`) and is shared between the web app and CLI.

---

## UI Notes
- The floating back-to-top control uses inline SVG in the shared layout, not the old `chevron-up.png` bitmap.
- The back-to-top button stays hidden while the page is already near the top and appears only after the user scrolls down.

### Visual themes
A persistent theme picker in the shared layout lets users switch between visual themes. The selection is stored in `localStorage` and applied on page load. Available themes:
- **Default** — the base site stylesheet
- **Abzan**, **Bant**, **Esper**, **Grixis**, **Jeskai**, **Jund**, **Mardu**, **Naya**, **Sultai**, **Temur** — color-shard/wedge-inspired palettes
- **Nyx** — enchantment-themed dark palette
- **Planeswalker Dark** — dark-mode palette
- **Commander Table** — warm tabletop-inspired palette
