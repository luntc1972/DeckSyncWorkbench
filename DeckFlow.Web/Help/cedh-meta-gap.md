---
title: cEDH Meta Gap
summary: Compare your deck against recent EDH Top 16 lists for the same commander.
order: 30
---

# ChatGPT cEDH Meta Gap

The cEDH Meta Gap page (`/chatgpt-cedh-meta-gap`) generates a structured ChatGPT workflow for comparing your deck against recent EDH Top 16 lists for the same commander.

## Step 1 — Load Deck And Fetch References

Paste a public Moxfield or Archidekt URL, or paste deck export text directly. You can optionally override the commander name. The page then queries EDH Top 16 using:

- Time period
- Sort by (`TOP` or `NEW`)
- Minimum event size
- Maximum standing

The service parses the submitted deck, removes sideboard and maybeboard cards, resolves the commander, fetches matching EDH Top 16 entries, and sorts them newest-first before display.

## Step 2 — Generate Meta-Gap Prompt

Select 1 to 3 EDH Top 16 reference decks and generate the prompt. While building the prompt, the service:

- Resolves submitted-deck and reference-deck card names through Scryfall so alternate print names and reskins are converted to canonical Oracle names where possible.
- Normalizes split and multi-face names to the base/front name for prompt display.
- Queries Commander Spellbook for your deck and for each selected reference deck, then injects combo summaries into the prompt.
- Caps the reference-deck count at 3 to keep the prompt size reasonable once decklists and combo references are included.

ChatGPT is instructed to:

- Write a concise human-readable meta-gap summary first.
- Then return a fenced `json` block whose top-level object is `meta_gap`.
- Prefer the supplied Commander Spellbook combo evidence over weaker inferred combo reads when they conflict.
- Fill every field, using empty strings, zero values, `false`, or empty arrays when evidence is missing.

## Step 3 — Paste Returned JSON

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

## Artifact saving

Check **Save artifacts to disk** to write generated files to:

```
Documents\DeckFlow\ChatGPT cEDH Meta Gap\<commander-name>\<timestamp>\
```
