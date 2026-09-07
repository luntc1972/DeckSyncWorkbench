---
title: Deck Modules
summary: Compile one baseline Commander deck's fixed command zone against 2-4 named strategy alternatives with linked mana support, then compare each completed configuration's legality and mana-base impact.
order: 102
requires_flag: tool.deck-modules.enabled
---

# Deck Modules

The Deck Modules page (`/deck-modules`) is for a deck that already runs one strategy and is asking
"what if I swapped the whole game plan?" rather than "what if I cut one card?" — that comparison is
Cut Lab's job. Deck Modules keeps your commander's legal command zone fixed and lets you assign the
rest of the pool to a shared Core plus one or more named strategy modules, each with its own linked
mana support, then compiles exactly one 100-card configuration at a time so you can see whether it is
legal, what it changes, and how it plays.

Everything here is session-only: there is no saved project, no share link, and no collaboration
surface. Import, assign, compile, and export happen inside one browser session over data you hold —
nothing is written server-side.

## Import

Start from a public Archidekt/Moxfield URL or a pasted decklist. Deck Modules reads the same
commander-eligible card resolution as Mana Base: if the import already carries a clear command
zone, that becomes the fixed baseline. Maybeboard and sideboard entries are excluded from the
baseline — only commander-board and mainboard cards are pulled in.

## Guided first-run setup

The first import walks you through naming the imported baseline strategy and manually placing its
cards into three groups:

- **Core** — the shared cards every configuration keeps, including the fixed command zone.
- **Strategy (the first alternative)** — the cards specific to that named game plan.
- **Linked Mana Support** — the mana base tied to that specific strategy, since a different plan can
  need a different manabase.

From there you create additional alternatives (2-4 total, each a manually curated, equal-card-count
strategy) the same way, giving each a one-sentence play plan and a target profile — casual, bracket 4
/ high power, or cEDH.

## Compile one configuration

Pick exactly one alternative and Deck Modules compiles it immediately: Core plus that alternative's
cards plus its linked mana support, laid over the immutable command zone from the import. The
compiler runs entirely in-process against your held card data — no network call happens during
compile — and reports:

- Whether the completed 100-card configuration is **legal**, with any diagnostic naming the specific
  rule and cards involved (duplicate singleton copies, a broken or tampered command zone, a card that
  doesn't resolve, and the rest of the compiler's fixed rule set). When the card-legality facts the
  compiler was given are unverifiable, that is disclosed rather than assumed legal.
- The exact **swap checklist** for moving from the imported baseline to this configuration: what to
  add, what to cut, and what a full reset back to baseline looks like.

## Per-configuration analysis

Once a configuration compiles clean, run analysis to see what it actually changes, compared against
your existing Mana Base and Bracket/Game Changer signals rather than a new invented score:

- **Mana-base deltas** — how this configuration's linked mana support shifts land count and
  per-color source counts versus the baseline, reusing the same `ManabaseAnalysisService` math the
  full Mana Base page uses.
- **Interaction and signal deltas by module** — what each assigned module adds or removes, broken out
  so a strategy swap's actual mana/interaction cost is visible instead of buried in one final total.
- **Comparison table** — compare two compiled configurations side by side. Every metric is tracked as
  present, absent, or unchanged rather than defaulting a missing side to zero, so a configuration that
  hasn't been analyzed yet reads as "not analyzed," not as a false regression.

Deck Modules deliberately shows compact status and deltas here, then hands you to the existing full
Mana Base report for the deep read rather than duplicating its per-card castability table, tap
analysis, or opening-hand simulation.

## Hand off to Mana Base

From a compiled, analyzed configuration, one link opens the full Mana Base report already populated
with that configuration's decklist — no re-resolution, no manual copy/paste. That handoff link is
short-lived (minutes, not a saved or shareable link): once it expires, following it lands on the
ordinary empty Mana Base form with a notice instead of an error.

## Export

Copy or download the selected configuration's complete list alongside its IN/OUT/reset swap
checklist, ready to paste back into Moxfield or Archidekt.

Deck Modules is behind the `tool.deck-modules.enabled` feature flag and lives in the Analyze section
of the tools nav.
