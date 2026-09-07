# Protection-Vocabulary Blast Radius — 2026-09

Card-by-card measurement of what Phase 9.1's widened `DeckStatClassifier.ProtectionOracleNeedles`
(plan `09.1-02`, 5 → 17 needles) actually changed across all three production consumers of
`IsProtectionCard` — Cut Lab's `protection` role (`CutLabRoleAssigner`), the plan-presence
`PlanRole.Interaction` grant (`PlanRoleClassifier`), and the deck-analysis interaction audit's
protection/recursion bucket (`InteractionAuditAggregator`) — over the nine real committed deck
fixtures at `DeckFlow.Web.Tests/Manabase/fixtures/.manabase-*-facts.json`. Produced by, and pinned
as exact-set assertions in, `DeckFlow.Web.Tests/ProtectionVocabularyBlastRadiusTests.cs` (plan
`09.1-03` Task 1). **No fixture, golden, or shipped data file was regenerated to produce this
measurement or to make the pinned test pass.**

## Fixtures measured

Nine dot-prefixed real-deck fact fixtures, 535 distinct cards after deduplication by name:

| Fixture | Deck |
|---|---|
| `.manabase-brago-facts.json` | Brago, King Eternal (WU control) |
| `.manabase-brago-promote-facts.json` | Brago promote variant |
| `.manabase-5c-facts.json` | Kenrith 5-color rocks |
| `.manabase-golgari-facts.json` | Meren Golgari ramp/ritual |
| `.manabase-arch-23563520-facts.json` | Archidekt 23563520 — Marchesa |
| `.manabase-arch-23753514-facts.json` | Archidekt 23753514 — graveyard fungus |
| `.manabase-arch-23638601-facts.json` | Archidekt 23638601 — Townos |
| `.manabase-arch-8066726-facts.json` | Archidekt 8066726 — The Necrobloom |
| `.manabase-arch-7084567-facts.json` | Archidekt 7084567 — army now |

## Method

For each of the 535 distinct cards, the "after" (shipped, widened) sets are produced by calling
the real production consumers directly — `CutLabRoleAssigner.AssignRoles`,
`PlanRoleClassifier.Classify`, `InteractionAuditAggregator.Compute` — with empty crowd categories
and `isComboPiece: false` so each falls through to its oracle-text heuristic path rather than a
category short-circuit. The "before" (narrow, pre-Phase-9.1) sets are produced by a test-local
reproduction of the four needles the classifier carried before this phase (`gains hexproof`,
`gains indestructible`, `gain protection from`, `phases out`, each an `OrdinalIgnoreCase` substring,
plus the curated `StaxProtectionCatalog` list) run for real over the same nine fixtures — these four
needles are frozen historical fact, not a moving target, so reproducing them locally does not
reintroduce the guessed-vocabulary problem Success Criterion 1 forbids for the *shipped* table.

For `PlanRoleClassifier`, "earns `PlanRole.Interaction` via protection" means the card is
protection-flagged AND the real `Classify` call (after its permanent-only-roles gate, which strips
`Interaction` from one-shot instants/sorceries) still carries the `Interaction` flag. For
`InteractionAuditAggregator`, whose protection/recursion bucket is `IsRecursionCard(...) ||
IsProtectionCard(...)`, "attributable to protection rather than recursion" means the card is
protection-flagged AND NOT also a recursion card — a card that is both would already sit in the
bucket via recursion regardless of the protection widening, so counting it as protection movement
would overstate the change.

## Before set (narrow, pre-Phase-9.1) — 10 distinct cards

Identical at Cut Lab and the interaction audit (none of these ten are also recursion cards);
narrower at `PlanRoleClassifier` because six of the ten are non-permanent (instant) cards stripped
by the permanent-only-roles gate.

**Cut Lab / interaction audit (10):** Brave the Elements; Deflecting Swat; Flawless Maneuver;
Heroic Intervention; Kytheon, Hero of Akros // Gideon, Battle-Forged; Loran's Escape; Plaza of
Heroes; Revitalizing Repast // Old-Growth Grove; Teferi's Protection; The One Ring.

**`PlanRoleClassifier` (3, permanent-front subset of the above):** Kytheon, Hero of Akros //
Gideon, Battle-Forged; Plaza of Heroes; The One Ring.

This matches the plan's own planning-time sanity check exactly: seven by oracle needle (Brave the
Elements; Kytheon, Hero of Akros // Gideon, Battle-Forged; Loran's Escape; Plaza of Heroes;
Revitalizing Repast // Old-Growth Grove; Teferi's Protection; The One Ring) and four by the curated
`StaxProtectionCatalog` list (Deflecting Swat, Flawless Maneuver, Heroic Intervention, Teferi's
Protection — counted once, since it is in both lists) = 10 distinct total.

## After set (shipped, widened) — 20 distinct cards

**Cut Lab / interaction audit (20, identical — no recursion collision in this fixture set):** Amalia
Benavides Aguirre; Boromir, Warden of the Tower; Brave the Elements; Deflecting Swat; Flare of
Fortitude; Flawless Maneuver; Giver of Runes; Heroic Intervention; Kytheon, Hero of Akros // Gideon,
Battle-Forged; Lightning Greaves; Loran's Escape; Mother of Runes; Plaza of Heroes; Revitalizing
Repast // Old-Growth Grove; Seasoned Dungeoneer; Swiftfoot Boots; Sylvan Safekeeper; Teferi's
Protection; The One Ring; Whispersilk Cloak.

**`PlanRoleClassifier` (12):** Amalia Benavides Aguirre; Boromir, Warden of the Tower; Giver of
Runes; Kytheon, Hero of Akros // Gideon, Battle-Forged; Lightning Greaves; Mother of Runes; Plaza of
Heroes; Seasoned Dungeoneer; Swiftfoot Boots; Sylvan Safekeeper; The One Ring; Whispersilk Cloak.

This lands inside the roughly-20-to-22-distinct-card range the plan flagged as the expected order of
magnitude for a correct measurement.

## Added and removed cards, named individually with the needle that moved each

**Removed: none.** Every one of the ten before-set cards is still protection-flagged after the
widening, at both Cut Lab/audit and (where already a member) `PlanRoleClassifier`.

**Added at Cut Lab and the interaction audit (10 cards):**

| Card | Oracle text (excerpt) | Needle that moved it |
|---|---|---|
| Amalia Benavides Aguirre | "Ward—Pay 3 life." | `ward—pay` |
| Boromir, Warden of the Tower | "Creatures you control gain indestructible until end of turn." | `gain indestructible` (plural) |
| Flare of Fortitude | "permanents you control gain hexproof and indestructible until end of turn." | `gain hexproof` (plural) |
| Giver of Runes | "Another target creature you control gains protection from colorless or from the color of your choice..." | `gains protection from` (singular) |
| Lightning Greaves | "Equipped creature has haste and shroud." | `haste and shroud` |
| Mother of Runes | "Target creature you control gains protection from the color of your choice..." | `gains protection from` (singular) |
| Seasoned Dungeoneer | "target attacking Cleric, Rogue, Warrior, or Wizard gains protection from creatures until end of turn." | `gains protection from` (singular) |
| Swiftfoot Boots | "Equipped creature has hexproof and haste." | `has hexproof` |
| Sylvan Safekeeper | "Target creature you control gains shroud until end of turn." | `gains shroud` |
| Whispersilk Cloak | "Equipped creature can't be blocked and has shroud." | `has shroud` |

**Added at `PlanRoleClassifier` (9 of the above 10 — all except Flare of Fortitude, an instant
stripped by the permanent-only-roles gate):** Amalia Benavides Aguirre; Boromir, Warden of the
Tower; Giver of Runes; Lightning Greaves; Mother of Runes; Seasoned Dungeoneer; Swiftfoot Boots;
Sylvan Safekeeper; Whispersilk Cloak.

Three of the ten added cards (Brave the Elements, Deflecting Swat, Flawless Maneuver, Heroic
Intervention, Loran's Escape, Revitalizing Repast // Old-Growth Grove, Teferi's Protection — the
before-set's seven instants) stay absent from `PlanRoleClassifier`'s after-set for the same
permanent-front reason they were absent before: the gate strips `Interaction` from one-shot
instants regardless of which needle grants them protection.

## Per-consumer totals

| Consumer | Before | After | Added | Removed |
|---|---:|---:|---:|---:|
| Cut Lab `protection` role | 10 | 20 | 10 | 0 |
| `PlanRoleClassifier` `PlanRole.Interaction` via protection | 3 | 12 | 9 | 0 |
| `InteractionAuditAggregator` protection/recursion bucket (attributable to protection) | 10 | 20 | 10 | 0 |

## Standing invariant: the cannot-be-regenerated removal family

The five real "can't be regenerated" removal spells present in these fixtures — Artifact Mutation,
Damn, Damnation, Putrefy, Terminate — are asserted absent from all three after-sets, independent of
the pinned expected arrays: `ProtectionVocabularyBlastRadiusTests` asserts directly that
`DeckStatClassifier.IsProtectionCard` returns `false` for each of the five, which structurally
guards all three consumers at their shared root (every consumer requires `IsProtectionCard` true
before it can add a card to a protection-adjacent set). None of the five appear in either the
before or after set above.

## What this means for the shipped role-floor snapshot

**Nothing.** `protection` is not one of `RoleFloorBaseline.AdoptedRoleKeys`'s six commander-aware
floor roles (`ramp`, `draw`, `interaction-targeted`, `engines`, `payoffs`, `wincons` —
`DeckFlow.Core/Research/RoleFloorBaseline.cs:13-21`), and `CutLabFloorDefaults.cs:116-119`
documents why: "protection are out of scope for insufficient breadth." The shipped commander
role-floor snapshot `DeckFlow.Web/Data/role-floor-baseline/latest.json` (678 commanders, 1,463
adopted floors) carries no protection floor for this phase's widened live count to out-run, and
widening the classifier does not change that file. **`DeckFlow.Web/Data/role-floor-baseline/latest.json`
stays as-is** — there is no floor-vs-live asymmetry here to accept, narrow, or defer, because there
is no floor for `protection` in the first place. What plan `09.1-03` Task 2 accepts is simply the
measured card movement above: that it is correct and intended, exactly what Success Criterion 3
asks a human to sign off on.

No fixture under `DeckFlow.Web.Tests/Manabase/fixtures/` was edited to produce this measurement.
