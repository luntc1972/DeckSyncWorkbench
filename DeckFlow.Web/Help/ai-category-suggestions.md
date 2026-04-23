---
title: AI Category Suggestions
summary: Suggest functional categories for a card using multiple data sources.
order: 80
---

# AI Category Suggestions

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

The page also exposes a background Archidekt harvest button so the local category store can be refreshed while the rest of the UI remains usable. Only one harvest runs at a time, and a local browser notification appears when the job completes.

Basic card type categories (Creature, Instant, Sorcery, Enchantment, Artifact, Planeswalker, Battle) are filtered out of cache suggestions.
