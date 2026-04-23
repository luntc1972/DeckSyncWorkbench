---
title: ChatGPT Analysis
summary: Five-step workflow that builds an analysis prompt and renders the returned JSON.
order: 10
---

# ChatGPT Analysis

The ChatGPT Analysis page (`/Deck/ChatGptPackets`) guides you through a 5-step workflow. Step 2 generates the analysis prompt, Step 3 parses and renders the returned `deck_profile` JSON, Step 4 optionally generates a set-upgrade prompt using that parsed profile, and Step 5 parses and renders the returned `set_upgrade_report` JSON.

## Workflow layout modes

Three layouts are available via the toolbar: **Guided**, **Focused**, and **Expert**. They present the same underlying steps with different amounts of context and guidance text.

## Step 1 — Deck Setup

Choose an **Input method** (paste text or public deck URL) and provide either a **Moxfield** / **Archidekt** deck URL or pasted deck export text. The chosen mode round-trips with the form so it survives refreshes and step navigation. The service:

- Falls back to treating leading quantity-1 entries as the commander when no Commander section header is present (Moxfield plain-text exports), then validates the inferred commander against Scryfall before continuing.
- Rejects inferred commanders that are not legal by the workflow rules: legendary creature, legendary Vehicle, or a planeswalker whose oracle text says it can be your commander.

## Step 2 — Analysis

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

Click **Generate Analysis Packet** to build the reference data and analysis prompt. The generated prompt uses `##` section headings (TASK, EVIDENCE RULES, BRACKET GUIDANCE, ANALYSIS QUESTIONS, OUTPUT FORMAT, REFERENCE DATA, DECKLIST) to keep long prompts structured.

## Step 3 — Analysis Results

Paste the fenced `deck_profile` JSON block or raw JSON payload returned from ChatGPT. You can also paste a saved `deck_profile` JSON file here directly without filling out Steps 1 and 2 again. The page validates the payload, parses it into a strongly typed model, and renders a readable summary of:

- Format and commander
- Game plan, speed, primary axes, and synergy tags
- Strengths, weaknesses, deck needs, and weak slots
- Per-question answers with basis notes
- Full deck versions when versioning questions were requested

This step is local to the returned JSON. It does not regenerate the analysis packet or call upstream services again.

## Step 4 — Set Upgrade (optional)

Select one or more recent MTG sets, or paste a condensed set packet override. The page generates a set-upgrade prompt that references the parsed deck profile and asks ChatGPT to evaluate new cards from each set as potential inclusions, with suggested cuts, bracket-fit notes, speculative tests, and traps called out per set. A deck in Step 1 is required; the parsed Step 3 deck profile is optional but strongly recommended — without it ChatGPT gets an empty schema and produces generic recommendations.

## Step 5 — Set Upgrade Results (optional)

Paste the fenced `set_upgrade_report` JSON block or raw JSON payload returned from ChatGPT. The page validates the payload, parses it into a strongly typed model, and renders a readable summary of:

- Per-set panels: top adds with suggested cuts and reasoning, traps, and speculative tests
- Final shortlist broken into must-test, optional, and skip columns

Like Step 3, this step is local to the returned JSON. You can paste a saved `set_upgrade_report` JSON file here directly without re-running the earlier steps — Step 5 runs standalone when no deck source is present.

## Analysis Question Buckets

Questions are grouped into collapsible buckets. Buckets with pre-selected questions open automatically on page load.

| Bucket | Notable questions |
|---|---|
| **Core Deck Analysis** | Strengths/weaknesses, win condition, consistency, power level, best meta |
| **Deck Construction & Balance** | Mana curve, lands and ramp, card draw, interaction count, underperformers |
| **Strategy & Synergy** | Key synergies, anti-synergies, commander support, protect-cards, game plan |
| **Optimization & Upgrades** | Cuts for strength, budget upgrades, missing staples, faster/competitive, board-wipe resilience |
| **Meta & Matchups** | Performance vs. archetypes, pod weaknesses, tech options, hate pieces |
| **Play Pattern & Decision Making** | Ideal opening hand, tutor priorities, when to cast the commander, common misplays |
| **Specific Card-Level Questions** | Card worth including and better alternatives can each target multiple card names, and every `[card]` question is emitted once per card you add; also includes weakest card and too many high-CMC cards |
| **Advanced / Expert-Level** | Turn clock, disruption vulnerability, keepable hand percentage, redundancy, mana-base optimization |
| **Combo Analysis (Commander Spellbook)** | Combos already in the deck, combos one card away within the color identity — both use live Commander Spellbook API data injected into the prompt |
| **Deck Versioning & Upgrade Paths** | Bracket 2/3/4/5 version, 3 named upgrade paths, assign categories, update categories |

### Deck Versioning output format

When any versioning or category question is selected, the analysis prompt instructs ChatGPT to:

- Output the **full, complete 100-card decklist** for each generated version — no truncation, no "fill with basics" shorthand.
- Count cards before responding to confirm the total reaches 100.
- Use the deck builder's inline format when an export format is chosen (Moxfield or Archidekt inline format, with categories when requested).
- Output a **Cards Added** and **Cards Cut** diff after each decklist, comparing against the original.
- Output a `deck_profile` JSON block for each generated deck version.
- When **Include card versions** is checked, preserve the original printing (set code + collector number) for every retained card.

### Category / tag questions

- **Assign categories** — ChatGPT assigns functional role categories to every card in the deck. Plain-text export is not supported; Moxfield or Archidekt format is required.
- **Update categories** — ChatGPT updates or reassigns categories using the preferred category names you provide. Preferred names are injected into the prompt; ChatGPT may add new categories only when none of the preferred names fit.
- Basic card types (Creature, Instant, Sorcery, Enchantment, Artifact, Planeswalker, Battle) are excluded as categories. ChatGPT is instructed to use functional role labels instead (Ramp, Card Draw, Removal, Wipe, Tutor, Win Condition, Protection, etc.).

### Commander Spellbook combo lookup

When either combo question is selected, the service queries the Commander Spellbook `find-my-combos` API before building the prompt:

- Returns up to 20 **included combos** (all pieces are in the deck) and up to 15 **almost-included combos** (exactly one card missing, within the deck's color identity).
- Each combo entry lists the card names, results, and up to 300 characters of instructions.
- Results are injected as a reference block in the prompt. ChatGPT is told to treat this data as authoritative.
- API failures degrade gracefully — the analysis continues without combo data rather than failing.

## Artifact saving

Check **Save artifacts to disk** to write all generated prompts and reference files to:

```
Documents\DeckFlow\ChatGPT Analysis\<commander-name>\<timestamp>\
```

Files saved: `01-request-context.txt`, `00-input-summary.txt`, `30-reference.txt`, `31-analysis-prompt.txt`, `41-deck-profile-schema.json`, `50-set-upgrade-prompt.txt` (when applicable), plus `40-deck-profile.json` and `51-set-upgrade-response.json` capturing the pasted Step 3 and Step 5 JSON responses.
