# Manabase Analysis — Rule Reference

Every rule the DeckFlow manabase analyzer applies, per stage, with exact
thresholds and `file:line` citations. Source of truth is the code, not this
doc — if they disagree, the code wins and this file is stale. Paths are relative
to the repo root; `MA` = `DeckFlow.Core/Manabase/ManabaseAnalyzer.cs`,
`CS` = `DeckFlow.Core/Manabase/CastabilitySimulator.cs`,
`Cls` = `DeckFlow.Core/Manabase/ManabaseClassifier.cs`,
`Kar` = `DeckFlow.Core/Manabase/KarstenManabase.cs`,
`Mdl` = `DeckFlow.Core/Manabase/ManabaseModels.cs`,
`RDB` = `DeckFlow.Core/Manabase/ManabaseRampDrawBudget.cs`,
`VS` = `DeckFlow.Core/Manabase/ManabaseVerdictSynthesizer.cs`,
`PRC` = `DeckFlow.Web/Services/Manabase/PlanRoleClassifier.cs`,
`MAS` = `DeckFlow.Web/Services/Manabase/ManabaseAnalysisService.cs`.

## Pipeline

```
decklist
  → 1. Classification            (Cls, CardTypeLine)      → ManabaseDeck (sources, spells, budget pieces)
  → 2. Land target               (Kar, MA)                → recommended land count
  → 3. Per-color source req       (Kar + MA sim clamp)    → ColorFindings, demanding cards
  → 4. Castability Monte-Carlo    (CS)                    → per-spell cast %, mulligan/tap counters
  → 5. Derived reads             (RDB, MA, PRC, CS)       → ramp/draw budget, tap %, mulligan eval, plan-presence
  → 6. Verdict synthesis         (Mdl health, VS, PrimaryFix) → four-tier health, biggest fix, plain-language
```

Nearly every accuracy knob is a **feature flag**, snapshotted once per analysis
and threaded down as a plain argument; see the [Feature Flag Catalog](#feature-flag-catalog).
`IsFlagOn` is **fail-safe OFF** — a missing key reads `false` (`MAS:384-387`),
deliberately unlike the generic cache default.

---

## 1. Classification — decklist → analyzer inputs

Entry: `ManabaseClassifier.Classify(cards, isSingleton=true, rampCreditV2=false, landRampSim=false, payLifeUntapped=false, checkLandUntapped=false, restrictedLands=false)` (`Cls:95`). The four accuracy params (`rampCreditV2`, `landRampSim`, `payLifeUntapped`, `checkLandUntapped`) are bundled under `analysis.manabase.accuracy` (default **ON**). `restrictedLands` is the default-OFF classifier gate that plan 04 wires to `analysis.manabase.restricted-lands`. MDFC land-backs are modeled as real lands **unconditionally** — no flag (§1.4).

### 1.1 Lands
- A card is a **land** iff its **front face** (before `//`) type line contains "Land" (case-insensitive) — `Cls:664-668`, `CardTypeLine.FrontFace`. So a spell//land MDFC (`Instant // Land`) is **not** a front-face land. Its land back is counted as a **real land source** (§1.4), unconditionally.
- A front-face land skips all spell processing (`continue`) — `Cls:125-130`.

### 1.2 Mana sources (color set, amount, land vs non-land)
- **Land sources** (one `ManaSource` per copy, `IsLand=true`) — `Cls:334-364`. Colors = `MapColors(produced_mana)` (W/U/B/R/G → color; C, S(snow) → Colorless) — `Cls:649-662`, `ManaCost.cs:141-151`.
- **Additive source capabilities** (`analysis.manabase.colorless-snow`, default **ON**) — every classified `ManaSource` also carries two independent capability flags: `ProducesColorless` (oracle / production can make true `{C}`) and `IsSnow` (type line contains `Snow`). These do NOT change the legacy `Produces` mapping: `{C}` / `{S}` still fold into `ManaColor.Colorless` there for flag-off parity; the new flags feed only the gated `{C}` / `{S}` requirement lane and sim mask (§3.2, §4.2).
- **EntersUntapped** = `!EntersTapped`; tapped iff oracle contains "enters tapped" or "enters the battlefield tapped" — `TextEntersTapped`. **Pay-life override** (bundled under `analysis.manabase.accuracy`, default **ON**): a land whose oracle offers "you may pay N life" to avoid the tapped clause (shocklands — Steam Vents, Godless Shrine) is treated as **untapped** — `TextPayLifeUntapped`. The regex is anchored to "you may pay" so always-tapped lands with a life-payment *activated* ability (Boseiju Who Shelters All, Hall of the Bandit Lord, Untaidake) stay tapped. Scope is pay-life only: plain taplands stay tapped; board/hand-conditional lands are handled by the conditional-untapped override below. **Single-faced and MDFC land-back paths share one primitive**: single-faced lands read `OracleText`; MDFC backs read the isolated land-face text (`LandFaceOracleText ?? OracleText`) via the `LandFaceEntersTapped` / `LandFacePayLifeUntapped` wrappers, so a pay-life MDFC back (Agadeem, the Undercrypt) enters untapped too (§1.4).
- **Conditional-untapped / conditional-color land handling** (`checkLandUntapped`, bundled under `analysis.manabase.accuracy`, default **ON**) — `IsConditionallyUntapped` plus the MBGAP-02 classifier helpers:
  - **Bond / crowd lands** (Sea of Clouds, Training Center, … — `BondLandRegex` "tapped unless you have two or more opponents") → **always untapped**. DeckFlow models Commander (always multiplayer), so the condition always holds; no census.
  - **Check lands** (Glacial Fortress cycle — `CheckLandRegex` "tapped unless you control a Plains or an Island") and **Snarls** (Strixhaven — `SnarlRevealRegex` "reveal an Island or Mountain card from your hand … enters tapped") → **untapped iff the deck runs ≥ `CheckLandMatchTypeThreshold` (6) lands bearing a named basic type** (`CountLandsBearingAnyType`, union count over basics/duals/shocks/triomes). Named types are pulled only from the matched trigger clause, so a stray type word elsewhere in the oracle can't misfire.
  - **Fast lands**, **slow lands**, and **ELD threshold lands** (Mystic Sanctuary class) no longer collapse to a fixed untapped boolean when the existing `analysis.manabase.accuracy` bundle is ON. The classifier emits `ManaSource.CountCondition` metadata (`FastLand`, `SlowLand`, `EldThreshold`) plus `CountThreshold` (`2`, `2`, `3`) and, for ELD lands, `CountTypeFilter` (for example `Island`) only on that accuracy-ON path. With the bundle OFF, classification stays on the historic fixed tapped/untapped behavior.
  - **Verge lands** (Floodfarm Verge cycle) are **always untapped**. Their first printed color is unconditional; their second printed color is added only when the deck runs ≥ `CheckLandMatchTypeThreshold` (6) lands bearing either named basic type from the oracle clause ("a Plains or an Island"). This is a **static type census** like check lands, but it gates color availability, not tapped state.
  - **Training Compound** and its MSH allied-cycle siblings are **always untapped** and always produce `{C}`. Their allied-color ability is enabled only when the deck runs ≥ `CheckLandMatchTypeThreshold` (6) **true basic lands** counted by `Basic` supertype (`CountBasicLands`), not merely typed nonbasics. This is another **static** color gate.
  - **Vivid lands** are modeled as **ETB tapped** base-color lands plus one reduced-weight any-color source (`IsConditional=true`, `Weight=0.25`, `Produces = deck colors`) to approximate the two charge counters. This is intentionally shallow: the simulator does **not** track "charges remaining" per game.
  - **Restricted source lands** (`restrictedLands`, default **OFF**; plan 04 wires this to `analysis.manabase.restricted-lands`) use a deck-composition approximation instead of spell-by-spell spend masks:
    - **Cavern of Souls / Unclaimed Territory** match `"Spend this mana only to cast a creature spell of the chosen type"` and scale to `Clamp(dominantTypeShare, 0.25, 1.0)`, where `dominantTypeShare = max(subtypeHistogram.Values) / totalCreatureCount`.
    - **Ancient Ziggurat** matches `"Spend this mana only to cast a creature spell."` and scales to `creatureShare = totalCreatureCount / nonlandCount`.
    - **Nykthos, Shrine to Nyx** matches `"devotion to that color"` and adds one `IsConditional=true`, `Weight=0.25` any-color source; the simulator reuses the existing Bernoulli conditional-source path for that speculative devotion burst.
    - The subtype histogram is Quantity-weighted and built from `TypeLine.Split('—')` on creature cards only. With the gate OFF, these lands stay on the historic classifier path and `RestrictedSourceLandNames` remains empty.
- **ManaAmount** carried from `CardFact.ManaAmount` (Ancient Tomb = 2) — `Cls:361`. **Color-source counts never scale with ManaAmount** — a 2-mana rock is one source of its color; amount only feeds the castability sim — `ManaProductionAmount.cs:12-16`.
- `AddWeighted` **drops a source that produces no colors** — `Cls:635-647` (a colorless-only rock adds no *color* source here; it can still be a fast-mana land-target credit, §1.4).
- **Conditional Mox post-pass** (always on): after the classifier builds `Sources`, `Spells`, and the generic `FastMana` bucket, `ConditionalMoxHeuristics.Apply(...)` rewrites exactly five source rows by canonical name — **Mox Amber**, **Mox Opal**, **Chrome Mox**, **Mox Tantalite**, **Mox Diamond** — and may subtract their `+1` fast-mana credit. Inputs are: `L` = quantity-weighted legendary creatures + legendary planeswalkers (including commanders), `A` = quantity-weighted artifact cards, `Tk` = quantity-weighted oracle-text artifact-token creators (`create ... Treasure|Clue|Food|Blood|Gold|Powerstone|Map|artifact token`), and `Ae = A + 0.5·Tk`. Amber/Chrome colors are capped to the commander's pip-derived color identity (fallback WUBRG if the commander mask is empty/colorless-only). Tier table: **Amber** `L >= 12` = WUBRG-capped untapped fast mana at `0.75`; `6..11` = tapped/no-fast-mana at `0.60`; `<6` = tapped/no-fast-mana at `0.40`. **Opal** `Ae >= 15` = untapped fast mana at `0.75`; `8..14` = tapped/no-fast-mana at `0.60`; `<8` = tapped/no-fast-mana at `0.40`. **Chrome Mox** = commander-capped, untapped, no fast mana, `0.50`. **Mox Tantalite** = WUBRG, tapped, no fast mana, `0.50`. **Mox Diamond** = WUBRG, untapped, keeps fast mana, `0.75`. The sources always remain in `ManabaseDeck.Sources`; only their color set / untapped state / weight / fast-mana credit change.

### 1.3 Ramp — rock (0.75) vs dork (0.5), and what is excluded
- **`IsRockOrDork`** (canonical test) requires ALL: `!HasLandFace`, `ProducedMana.Count > 0`, and `HasRepeatableManaAbility`; then rock/dork iff type contains "Creature" or "Artifact" — `Cls:435-443`.
- **Weight**: creature (dork) = **0.5**, artifact (rock) = **0.75** (creature checked first) — `Cls:619-632`.
- **`HasRepeatableManaAbility`** (why one-shot mana is excluded) — `Cls:486-536`: strips parenthesized reminder text (`\([^)]*\)`), then requires an activated `<cost>: Add …` line whose cost does **not** contain "Sacrifice". This drops rituals, Lotus Petal / Lion's Eye Diamond (sac-to-add), Ashnod's/Phyrexian Altar (sac-outlet), and triggered Treasure makers (Dockside, Goldspan).
- **Self-grant vs other-grant**: a quoted `"…{T}: Add…"` counts as the card's own ability only when the granting clause names the card itself — a self pronoun (`it / this creature…` + has|gains, `Cls:462-464`) or a collective naming one of the card's own types ("All Slivers have", "Creatures you control have") — `Cls:543-575`. Other-grants (Chromatic Lantern, Paradise Mantle) are handled by §1.5, not here.

### 1.4 MDFC land-backs, fast mana, basic-fetch
- **MDFC land back** (`HasLandFace`, spell front) — **always** a real land (`AddPartialSources`, `Cls:670`): adds a `ManaSource` with `IsLand=true`, colors from the land face, **color-supply weight `1.0`** (a real land supplies its color fully; its tapped-or-pay-life timing is the only penalty, carried by the sim — a sub-1 color discount on top would double-count the downside), and `EntersUntapped` read from the isolated land-face text (§1.3, so a tapped or pay-life back is modeled correctly). It counts toward the actual land total (by copy) and the castability sim like any other land. Returns before the rock/dork branch, so land-backs are never rocks. (The pre-2026-07 legacy path — a non-land partial source plus a Karsten land-target credit — has been removed.)
- **Fast-mana tally** (per copy) — `Cls:233-238`: `!HasLandFace && ManaValue==0 && Artifact && ProducesMana` → **FastMana** bucket (Lotus Petal, Mana Crypt). MDFC land-backs are excluded (they already raise the real land count).
- **Basic-fetch**: oracle contains "Search your library for a" + "basic land" — `Cls:677-683`; weight **0.67** in a 3+ color deck else 1.0 — `Cls:347-349`. Fetch colors are derived from the basics/duals the deck actually runs, never speculatively — `Cls:695-789`.

### 1.5 Granted / conditional sources (weight 0.25)
- `DetectGranter` finds mana-ability granters (Cryptolith Rite → AllCreatures, Relic of Legends → LegendaryCreatures, equipment/aura → single-creature) — `Cls:1173-1209`.
- `AddGrantedSources` adds one `ManaSource` per eligible creature named `"<name> (granted)"`, `Produces = deck colors`, **Weight 0.25**, **`IsConditional=true`** — `Cls:1211-1276`. Only the broadest scope counts; existing rocks/dorks and non-creatures are skipped. The sim gates these with a per-trial Bernoulli roll (§4.9).

### 1.6 Land-ramp-to-battlefield (`landRampSim` flag)
- `IsLandRampToBattlefield`: oracle has "Search your library for" + "land" + "onto the battlefield" (Cultivate, Rampant Growth) — `Cls:919-925`. Land-search-to-**hand** does not qualify.
- When `landRampSim` (default **ON** in prod): add a colorless (`Produces = empty`), non-land ramp `ManaSource`, `Weight 1.0`, `DeployCost = max(1, round(ManaValue))` — `Cls:236-250`. Never changes land/color counts.
- Known approximation: the fetched land is modeled as a delayed source once the ramp resolves, but the simulator does **not** thin that land out of the library. That slightly overstates later draw density while slightly understating the resolved ramp's deck-compression benefit; over the short castability window those errors partially offset.

### 1.7 Ramp/draw budget piece counting (`rampCreditV2` flag)
- **Land-target credit** `RampAndDrawUnderThree`: `+Quantity` when `ManaValue <= 2` and the ramp/draw predicate matches — `Cls:191-195`.
  - `rampCreditV2` **off** → broad `IsRampOrDraw` (search-land, "Add ", "create a Treasure", draw-a/two) — `Cls:808-818`.
  - `rampCreditV2` **on** (prod) → narrowed `IsRepeatableRampOrDraw` = you-draw OR land-ramp-to-battlefield OR (permanent AND non-one-shot front "Add"); "permanent" here tests the **whole** type line (any spell face disqualifies) — `Cls:847-873`. Drops one-shot rituals/Treasure from the land credit.
- **Budget piece counts** (independent of the ≤2 gate): `IsRampPieceForBudget` (search-land, "Add ", Treasure, rock/dork, land-ramp) and `IsDrawPieceForBudget` (you-draw) — `Cls:820-832`. Final: `RampPieceCount = rampPieces − 0.5·both`, `DrawPieceCount = drawPieces − 0.5·both` (a dual-purpose card splits half/half) — `Cls:301-303`.

### 1.8 Cost reducers & alt/reduced-cost
- **`DetectCostReducer`** (always-on static generic reducers): needs `(<scope>) spells you cast cost {N} less`; excluded if oracle has "for each / less for / affinity / improvise / convoke / delve / opponent(s)"; amount > 0 — `Cls:938-987`. `ReductionScope` (whole-word): empty→All, instant/sorcery→InstantSorcery, creature→Creature, artifact→Artifact, anything else → dropped — `Cls:1120-1154`.
- **`DetectSelfCost`** (below-printed cost, most-specific first) — `Cls:994-1062`: free/pitch → "0"; greatest-power reducer (Skullspore Nexus); board-scaling ("costs {1} less for each …") → "0"; evoke cost; suspend cost.
- **Auto-applied to the analysis**: greatest-power reduction and free/alt-cost (override to "0") — `Cls:149,161-171`. **Suggestion-only** (pre-fills the override box): evoke / suspend / board-scaling — `Cls:157-159`.
- `EffectiveTurn`/`GenericReduction` (`MA:411-449`): reduction floors at `max(1, colored pips)`, total generic reduction capped at **2**, reducer applies only when its own MV < the spell's MV and scope matches.
- **Cost overrides** change a spell's effective MV/pips before analysis but deliberately do **not** touch deck aggregates (land target, avg MV) — "an alt cost changes castability, not the curve" — `MA:213-296`.

### 1.9 Unsupported interactions (dropped from castability)
- One `UnsupportedInteraction` per distinct name — `Cls:181-189`: `HasVariableCost` (X/Y/Z) → "Variable (X) cost — castability not simulated" (X spells excluded from the sim, `Cls:370-373`); else `ManaCost` contains `/` → "Flexible split pips (hybrid / Phyrexian / twobrid) — color requirement approximated". Hybrid pips add no hard single-color pip; twobrid `{2/U}` adds +2 MV, other hybrids +1 — `ManaCost.cs:47-54`.

### 1.10 Curve / singleton / commander
- Curve uses **non-land, non-commander** cards only: `AverageManaValue = round(mvSum / nonlandCount, 2)` — `Cls:132-137, 290, 298`.
- `TotalCards` = Σ quantity of all cards; `CommanderCount` = Σ quantity of `IsCommander`; `IsSingleton` passed through (true = Commander/singleton, false = 60-card) — `Cls:119-123, 307`.
- `SpellRequirement.ManaValue` = reduced value if overridden else `max(0, round(ManaValue))`; `IsGold` = `DistinctColors >= 2` — `Cls:384-386`.
- `RestrictedSourceLandNames` / `HasRestrictedSourceApproximation` are deck-level disclosure surfaces for the restricted-land approximation. In this phase the classifier populates the deck-level name list only when `restrictedLands` is on; plan 04 copies it onto the report/UI and marks the matching land rows.
- `ScrySourceCreditCopies` is a deck-level analyzer input only: count cheap non-land spells with mana value ≤ 2 whose reminder-stripped oracle text contains a real `scry N` effect (`N ≥ 1`, case-insensitive). Lands are excluded entirely, including scry lands (already full sources), and reminder-text-only matches are excluded.

---

## 2. Land target (Karsten regression)

Shared constants (`Kar:22-24`): `LandIntercept 19.59`, `LandMvSlope 1.90`,
`RampDrawCredit 0.28`. (MDFCs no longer earn a fractional land-target credit — they
count as real lands, §1.4 — so the old `MdfcCommonCredit`/`MdfcMythicCredit` are gone.)

- **Singleton / Commander** (`Kar:38-55`):
  `scale = (totalCards − commanderCount)/60`,
  `interior = 19.59 + 1.90·avgMV + 0.27·commanderCount`,
  `target = scale·interior − 0.28·rampDrawUnder3 − fastMana − 1.35`.
- **60-card constructed** (`Kar:92-105`): `19.59 + 1.90·avgMV − 0.28·ramp − fastMana` (no scale, no commander term). (The old H5 bug that pre-multiplied the interior by 5/3 is fixed.)
- **cEDH** (`Kar:63-108`): flag OFF = historic `max(28.0, SingletonLandTarget − 3.5)`. Flag ON (`analysis.manabase.cedh-land-target`, ships OFF by default and is enabled on deckflow.gg) swaps to a hybrid target: `curveTarget = singleton − 3.5`; when the commander's committed baseline has `n ≥ 10` (and its mean is finite, in `[10,60]`), blend halfway toward it — `target = curveTarget − 0.5·(curveTarget − mean)` (`CedhBaselineBlendWeight = 0.5`); then clamp to **[22, 45]** (`CedhSafetyFloor` / `CedhTargetCeiling`). A second dark flag, `analysis.manabase.ritual-land-credit`, also ships OFF by default and is enabled on deckflow.gg; it can then subtract `min(3.0, 0.5 × netPositiveRitualCount)` from that enabled cEDH target before the final clamp, and the Web breakdown now names that ritual credit on its own line. It is cEDH-only, separate from the ritual burst sim, and byte-identical when OFF. The committed baseline is a **6-month** cEDH sample (EDHTop16 size-tiered, cEDH-gated at avgMV ≤ 2.7 & 95–101 cards), currently **mean 26.5 lands across N=3281** decks, 54 commanders usable at `n ≥ 10` (plus commander-search exceptions like Plagon for low-play commanders). Calibration on that set cut under-target flagging from **76.5% → 21.8%** for the hybrid target, then **21.8% → 11.1%** when the ritual credit column was applied; mean target moved **25.4 → 24.7** (average `-0.7` lands), with **351** additional decks un-flagged vs hybrid and **0** newly flagged under. The Web layer resolves the commander→baseline mean; Core stays name-agnostic (`CedhLandContext(mean?, n, enabled)`).
- **Routing** (`MA:301-340`): non-singleton → 60-card; singleton+Casual → singleton; singleton+cEDH → cEDH. `commanderCount = max(1, CommanderCount)`, `librarySize = TotalCards − commanderCount`.
- **CommanderImportance does NOT change the land target** (explicitly orthogonal) — `MA:86-90`. Floor/ceiling: none beyond the flag-OFF cEDH 28 floor; flag ON clamps the hybrid target to [22, 45] (above).
- `actualLands` counts only `IsLand` sources — partial sources never fill a land slot — `MA:145`.

---

## 3. Per-color source requirements + findings

### 3.1 Karsten mulligan-blind "ceiling"
- **Consistency threshold** = `clamp((89 + max(1, MV))/100, 0, 0.99)` — 90% at MV1 rising to 96% at MV7, cap 99% — `Kar:111-115`. MBGAP-04 re-verified this against the existing headless-browser primary-source capture of Frank Karsten's 2022 TCGplayer article; see `.planning/phases/manabase-research-gap-closure/MBGAP-04-threshold-decision.md`.
- **Cards seen by turn** = `7 + (onPlay ? turn−1 : turn)` — `Kar:121-122`.
- **CastConsistency** = P(≥pips colored sources AND ≥M lands by turn M) ÷ P(≥M lands by turn M), triple-category hypergeometric; `pips ≤ 0` → 1.0 — `Kar:135-189`, `Hypergeometric.cs`.
- **SourcesNeeded** = smallest `sources ≥ pips` meeting the threshold (the table figure) — `Kar:196-213`.

### 3.2 Mulligan-aware requirement (what findings actually use)
- **`SimRequiredSources`** (`MA:686-744`): binary-searches the on-color land count whose **isolation-probe** sim cast % (`SimColorCast`, a synthetic deck measuring color access only, ramp-free) meets the threshold — `SourceSearchTrials 5000` during search, confirmed at 20k.
- **Clamp-down**: if even an all-on-color base can't hit the bar, return `pips` (mana/curve-limited, not color) — `MA:718-722`.
- **Karsten ceiling clamp**: `min(sim result, SourcesNeeded)` — the sim may only *lower* the requirement below Karsten, never inflate it — `MA:742-743`.
- **Gold-contention bump** (`MA:544-551`): `need = min(totalLands, simNeed + otherColorsNeeded)` — one extra headroom source per other color the same gold card demands.
- **Cheap scry source credit** (`analysis.manabase.scry-credit`, `MA:195-204, 648-649, 1065-1078`): when the flag is ON, add `0.2 × ScrySourceCreditCopies` as an analyzer-only any-color source bonus inside `EffectiveSources` before each color's requirement comparison. This is NOT a `ManaSource`, so it never leaks into castability/tap/ramp outputs, and it never changes the land target. Draw+scry stacking with the `−0.28·rampDrawUnder3` land-target term is intentional: the two credits model different lanes (land count vs color-source count), so a cheap cantrip that both draws and scries can earn both.
- **True colorless / snow requirement rows** (`analysis.manabase.colorless-snow`, `MA:651-1041`): when enabled, the analyzer runs the same mulligan-aware requirement search for true `{C}` and `{S}` pips as separate categories. Actual supply counts only qualifying sources (`ProducesColorless` for `{C}`, `IsSnow` for `{S}`), and the probe ceiling stays Karsten-clamped; for example the underlying 60-card basis matches Karsten's `10` colorless sources for a turn-4 `{C}` spell (Thought-Knot Seer class) and `14` snow sources for a turn-1 `{S}` spell (Arcum's Astrolabe class), then scales through the same existing deck-size/singleton path as normal colors.
- Measured at the spell's **effective on-curve turn** (post cost-reduction), not printed MV — `MA:526-529`.

### 3.3 Commander weighting (`CommanderImportance`)
- Enum: Central / Standard(default) / Low — `Mdl:544-558`.
- **Support threshold** = mode base (Casual **80**, Focused **85**, cEDH **88**), raised to **88** for a commander color when importance is Central — `MA:831-846`.
- Commander is a mandatory worst-driver candidate unless importance is Low — `MA:556-562`; a Central commander color below threshold is promoted in the weakest-color ranking — `MA:813-829`.

### 3.4 Color findings
- One `ColorSourceFinding` per used color — `MA:463-673`.
- `EffectiveSources` = weighted sum of source weights for the color; `untappedOnly` drops tapped sources. Turn-1 casts may use **untapped only** (`onCurveTurn <= 1`), else all — `MA:530, 889-908`.
- `ActualSources = round(all, 1)`; `RequiredSources` = worst-driver sim figure (+gold bump); `Deficit = Required − Actual`; `IsAdequate = Deficit <= 0` — `Mdl:515-518`.
- **UnderSupportedCount** = spells of this color with `castPercent < threshold` (mana- or color-limited) — `MA:575-577`.
- **ColorLimitedUnderSupportedCount** = subset where `colorLimited && deficit > 0`; `colorLimited` = LimitingFactor is `"both"` or `"color:<thisColor>"` (a different color's `"color:X"` does NOT count this color starved) — `MA:591-603, 751-764`. **This is the only count that pushes health toward "Needs work"** — a card the base already supports color-wise is a curve problem, not a manabase one.
- **KarstenMet** (left-lens display): `Met = ActualSources >= RequiredSources`; `Deficit = Met ? 0 : max(1, ceil(Required − Actual))` — `ManabaseDisplay.cs:155-161`.

### 3.5 Demanding cards (the recent keep-band / deficit fix)
- **Record** any spell with `castPercent < threshold` in a needed color, keeping its **lowest** cast% across demanded colors — `MA:607-610`.
- **Turn-aware deficit**: `deficit = need − available` where `available` is untapped-only for a turn-1 cast (§3.4). A color supplied for the turn shows `deficit <= 0` → cheap turn-1 misses are *structural* (a land drop fixes them, not more sources).
- **Cheap-miss prune** (`MA:618-621, 664-670`): a spell that is `colorLimited && deficit <= 0` in its single limiting color is "structural"; it is pruned **only** if it is source-fixable (`deficit > 0`) in **no** demanded color.
- **"both"-color safety**: a `"both"`-limited card short in one color but supplied in another lands in `sourceFixableNames` and survives the prune — `MA:479-483, 663`.
- **Mana-limited bombs** (LimitingFactor `"mana"`) are never `colorLimited`, so never structural-pruned — kept as demanding (curve problem) — `MA:617`.
- Ordered ascending by `CastPercent` then name — `MA:162-166`.

---

## 4. Castability Monte-Carlo simulation (`CS`)

### 4.1 Trials, seed, determinism
- **`DefaultTrials = 20,000`** (`CS:34`); Monte-Carlo error < ~0.5 pt.
- Per-spell seed = `Random(StableSeed(spell.Name))`, **FNV-1a** over UTF-16 (offset `2166136261`, prime `16777619`) — reproducible across runs/machines, explicitly not `GetHashCode` — `CS:230, 1988-2003`. Plan-presence uses fixed seed `"__deckflow_plan_presence__"` — `CS:456`.
- Per-trial RNG order fixed: roll partials → shuffle prefix → mulligan — `CS:1670-1677`. Observation counters (TAP-02, keep sizes) add no draws.

### 4.2 The joint event & effective cost
- Answers: "by effective on-curve turn T, can I make ≥T mana **including** the spell's colored pips?" — one shuffled library, one land sequence, single joint test — `CS:8-9`. Replaces the old `P_mana × P_color` product (understated ~30 pts).
- `effectiveCost = max(max(1, totalPips), effectiveGeneric + totalPips)`, `effectiveGeneric = max(0, printedGeneric − reduction)` — cost never dips below colored pip count or 1 — `CS:214-219`. `turn = max(1, effectiveTurn)`; `castPercent = clamp(round(100·successes/trials), 0, 100)` — `CS:227, 375`.
- **Mask extensions** (`analysis.manabase.colorless-snow`): the historic source mask stays W/U/B/R/G = bits `0..4`. When the flag is ON only, bit `5` means "this source can pay true `{C}`" (`ProducesColorless`) and bit `6` means "this source is snow" (`IsSnow`). Snow permanents keep their normal color bits AND add bit `6`. The flag-off path keeps the old behavior byte-for-byte, including dropping colorless-folded pips from the pip requirement array entirely.

### 4.3 London mulligan
- **`hiCap = avgMV >= 3.0 ? 5 : 3`** — high-curve keeps up to 5 lands, normal-curve 2–3; sweet spot 3 — `CS:1688-1692`.
- Schedule `(Keep, Bottom, Lo, Hi, RampGate)`:
  - Singleton (`CS:1701-1708`): depth0 `(7,0,2,hiCap,true)`; **depth1 Commander free mulligan `(7,0,2,hiCap,true)`** (keeps 7, bottoms 0); depth2 `(6,1,2,hiCap,false)`; depth3 forced `(5,2,1,4,false)`.
  - Non-singleton (`CS:1709-1714`): depth0 `(7,…)`; depth1 `(6,1,…)`; depth2 forced `(5,2,1,4)`.
- **RampGate**: a 2-land (low-bound) fresh-7 keep needs ≥1 ramp piece in the opening 7 — `CS:1731-1735`. Loosens once at 6.
- **Color-aware keep** (MQ-05, `colorAware`): non-forced keep also needs opening **lands** to show `min(deckColorCount, lands, ColorKeepCap=2)` distinct colors; mono decks no-op — `CS:40, 1737-1742, 1848-1850`.
- **Bottoming**: non-lands first, highest cost first (filler cost treated as 3); free-mull depths bottom 0 — `CS:1864-1940`. **M1**: a bottomed card is relocated to the never-drawn tail of the prefix so turn-1 doesn't redraw it — `CS:1902-1916`.

### 4.4 Draw model
- **Draws every turn including turn 1** (multiplayer — CR 103.8a doesn't apply); by turn N a card has seen 7+N — `CS:988-995`.
- Prefix window `min(library, 7 + turn + grace + 2)`; only the prefix is shuffled (Fisher-Yates), covering the mulligan look and every draw plus a +2 never-drawn margin — `CS:273, 1762-1771`.

### 4.5 Land play priority (`PlayOneLand`, `CS:1144-1247`)
- Order: untapped-that-adds-a-missing-color → any untapped → tapped. Missing colors judged only against lands already **online**.
- **M2**: on a slack turn before the cast turn, a tapped fixer that adds a missing color beats a color-useless untapped land (it comes online next turn, still ≤ cast turn) — `CS:1225-1228`.
- Tapped land is online **next** turn (`onlineTurn = currentTurn+1`); untapped same turn — `CS:1244`.
- **ConditionalCountLand** (gated by `analysis.manabase.accuracy`, ON in prod): fast / slow / ELD threshold lands are resolved at the moment the land is played, using the trial's own `landsOnBoard` state. The simulator keys this path off the classifier-emitted metadata: when the accuracy bundle is OFF the metadata is absent, so no card enters the `ConditionalCountLand` path.
  - **Fast**: untapped when the trial already has **≤ 2** other lands in play.
  - **Slow**: untapped when the trial already has **≥ 2** other lands in play.
  - **ELD threshold**: untapped when the trial already has **≥ 3** other lands whose `BasicTypeMask` matches the land's `CountTypeFilter` (for example `Island` for Mystic Sanctuary). This is explicitly **not** a static census fallback; the simulator carries a per-land basic-type bitmask on every played land entry in `landsOnBoard` and counts only the matching already-played lands in that trial.

### 4.6 Ramp deploy (`TryDeployRamp`, `CS:1252-1308`)
- Only when `availableNow < effectiveCost` (stops over-ramping). Cheapest affordable piece; 0-cost fast mana online same turn, else next turn.
- **Deploy-friction reserve** (`gateRampOnCastable`): the mana spent playing the rock is reserved out of this turn's sources, least-color-flexible first — `CS:1314-1353`. Flag-off = no reserve, byte-identical.
- **Gated own-colored-cost**: with the gate on, a ramp piece is deployed only if the board can also pay the ramp's **own** colored cost (`RampPips`) — mirrors 17Lands — `CS:1287-1291`.

### 4.7 Mana quantity (`useManaQuantity`, MQ-02)
- Source pays `ManaAmount` mana of **one locked color** (a multi-color source can't pay two different pips) — `CS:118-122, 843`. Off → every source = 1.
- `ColorsCoverable`: colorless → total-mana check; unit **greedy fast path** when no source makes >1 (byte-identical to history); else exact **DFS** backtracking — `CS:1453-1642`.

### 4.7a Ritual / one-shot burst mana (`ritual-burst-mana`)
- Behind `analysis.manabase.ritual-burst-mana` (**default OFF**), the sim may credit a drawn
  **instant/sorcery ritual** as a one-turn burst source on the tracked spell's cast attempt —
  examples: Dark Ritual, Rite of Flame, Cabal Ritual. Qualification is conservative: front face
  Instant/Sorcery, unconditional `Add {…}`, and **net-positive** mana over the spell's own mana cost.
- The ritual must be **payable from the pre-burst board** first — its own colored cost is checked
  before any burst mana is added. A red ritual cannot cover a missing blue pip, and one ritual
  cannot pay for another in v1.
- The credit is **non-persistent**: it helps only that cast attempt, never becomes a permanent source,
  and never changes the land target, effective color-source counts, or Karsten requirements.
- The Web flag is **cEDH-only**: `ritualBurst=true` is hard-gated inside `ManabaseAnalyzer.Analyze`
  to `mode == Cedh`. Casual stays byte-identical even when the flag is on. Flag OFF is byte-identical
  in both modes.
- `analysis.manabase.ritual-land-credit` is a **separate** cEDH-only flag on the land-target side. It
  lowers the recommended land count for decks whose already-classified `OneShots` contain net-positive
  rituals, but it does **not** disable those rituals in the burst-mana sim. That double-credit is
  intentional: the land target is a strategic deck-construction heuristic, while the burst-mana credit is
  a tactical per-cast simulation of whether a drawn ritual helps make a specific spell on time.

### 4.8 Grace window & delay
- **`GraceWindow = 1`** uniform — "on its turn or one turn late" (17Lands convention; replaced the old 3/2/1) — `CS:1142`. `firstCastableTurn` bounded at `lastTurn+1`; `AverageDelay = round(Σ max(0, first−turn)/trials, 1)` — `CS:962, 377`.

### 4.9 Partial / conditional sources
- Only `IsConditional` granted sources (the 0.25 Cryptolith/Relic any-color sources) get a per-trial Bernoulli roll at their weight — `CS:128-131, 873-914`. Deployable ramp/discounted lands enter at **full value 1.0** (friction already modeled by deploy cost + online turn; re-applying analytic weights would double-discount ~5-7 pts).

### 4.10 TAP-02 & LimitingFactor
- **TAP-02**: on turn 1, record whether any online turn-1 source can make a **needed** color (colorless spells accept any) — a 1-bit observation, no RNG — `CS:1407-1437`.
- **LimitingFactor** (`CS:1944-1967`): colorless → "mana"; no failures → "mana"; `manaShort > 2·colorShort` → "mana"; `colorShort > 2·manaShort` → "color:<most-pips color>"; else "both".

---

## 5. Derived reads (layered on the sim)

### 5.1 Ramp/draw budget (advisory — never touches target/color/health)
- **Threshold** (`RDB:126-153`): highest commander MV if a commander exists, else the 75th-percentile MV of non-mana spells (`min(count−1, ceil(count·0.75))`), else 4.0.
- **TargetRamp** (`RDB:113-124`): `≤2→8`; `≤4→8+2(t−2)`; `≤6→12+(t−4)`; `>6→14`. **TargetDraw = 24 − TargetRamp**.
- **IsBalanced** = `|rampDelta| ≤ 2 AND |drawDelta| ≤ 2` — both axes (`RDB:87`). `IsRampLight/Heavy` = `rampDelta </> ±2`; `IsDrawLight` = `drawDelta < −2` (no draw-heavy). `RampShort/DrawShort = ceil(−delta)` when light — `RDB:88-93`.

### 5.2 Tap analysis (`ComputeTapAnalysis`, `MA:916-962`) — flag `tap-analyzer`
- Per-color untapped % = `round(100·rawUntapped/rawTotal)` (raw un-rounded weights). Overall = same over sums. Turn1UntappedPercent = mean `Turn1UntappedTrials` over non-commander rows ÷ trials.
- "Untapped" = a source online turn 1 whose color matches a needed pip (colorless spells accept any) — `CS:1407-1437`.

### 5.3 Mulligan evaluation (`ComputeMulliganEvaluation`, `MA:971-1044`) — flag `mulligan-eval`
- Keep-size counters bucket by the **returned** keep value (7/6/5); `keepable == kept7 + to6` by construction — `CS:290-310`.
- `KeepableHandPercent = Kept7% + MulliganTo6%`; `MulliganTo5% = max(0, 100 − keepable)` — `MA:986-989`.
- **KeepableBand**: `≥85 high / ≥70 medium / else low` — `MA:991-996`.
- Openers drawn from non-commander rows with **ManaValue ≥ 1** (free spells carry no signal) — `MA:1006-1007`.
- **Openers source**: if the plan-presence pass ran, use its plan-preferred openers verbatim (one per depth 7/6/5); else fall back to per-row samples grouped by decision, max 3 — `MA:1021-1030`.

### 5.4 Plan-presence (`SimulatePlanPresence`, `CS:408-659`) — flags `plan-presence` AND `mulligan-eval`
- A **plan card** = `PlanRoles != None`; placed as identifiable, mana-inert filler. Pass runs only when the deck carries plan tags — `MA:173-176`.
- **Role classification** (`PRC:33-70`), first-hit-wins: crowd categories → Commander Spellbook combo piece (TutorCombo) → oracle heuristic.
- **Permanents-only gate** (`PRC:55-75`): `PermanentOnlyRoles = Payoff | Interaction`; for a non-permanent **front face** (`CardTypeLine.IsNonPermanentFront`), `roles &= ~PermanentOnlyRoles`. So a one-shot instant/sorcery **payoff** (Torment of Hailfire) or **interaction** (Swords, Counterspell) is stripped, while **Tutors (TutorCombo) and card draw (Engine) still count even as instants/sorceries**. Front face judged before `//` (Adventure creature = permanent; `Instant // Land` MDFC = not).
- **Per-hand test**: a plan card counts only when **drawn by its on-curve turn** AND **castable by it** (`SimulateGame` on its own cost) — `CS:527-563`.
- **Denominator = keepable-only** (`keptSize ≥ 6`); mull-to-5 is sampled for the opener display but not the percent — `CS:501-505, 565-568`.
- **Bands**: PayoffPercent headline `≥20 high / ≥10 medium / else low` (`CS:706-711`); composite PlanPresence `≥65 high / ≥40 medium / else low` (`CS:715-720`).
- **Representative openers**: one per depth (7/6/5), preferring a plan-holding hand; a depth locks once a plan hand is stored; mull-5 sampling capped at **200** attempts to bound plan-less decks; no-plan hand renders "no castable plan by its curve turn" — `CS:476-608`.

### 5.5 Opener `HasPlan` has two producers
- **Per-spell opener**: `HasPlan = ≥2 lands AND ≥ planColorTarget colors AND the tracked spell castable on curve` — a resource/keepability proxy — `CS:333-349`.
- **Plan-presence opener**: `HasPlan = the hand holds a castable-on-curve permanent plan card` (the win-plan read), naming the card — `CS:634-659`.
- When the plan-presence pass ran, its openers replace the per-row ones in the mulligan block, so the surfaced "with a plan" opener uses the win-plan meaning — `MA:1021-1022`.

### 5.6 cEDH keep shapes + casual curve coverage — flag `keep-shapes` (`SimulatePlanPresence` extension, `SimulateCurveCoverage`, `IsCommanderCentral`)
Gated `keepShapes = IsFlagOn(keep-shapes) && showMulliganEval` (`MAS`) so nothing runs when the opening-hand block is hidden; `keepShapes` also widens `classifyPlanRoles` (`|| keepShapes && mode==Cedh`) so the shape gate has role data. Calibration constants in `CedhMulliganCalibration`: `TurnCapExplosive=3`, `TurnCapEngine=2`, `BridgeInteractionMin=2`, `BridgeDevelopmentMin=2`, `RepresentativeLineTurnCap=4`.

- **Three-shape keep gate** (inside `SimulatePlanPresence`, only when `keepShapes && mode==Cedh`): for each **keepable** hand (`keptSize ≥ 6`), a mana-keepable hand becomes **plan-keepable** iff it passes one of —
  - **Shape A — explosive**: a `Payoff`/`TutorCombo` plan card (or the commander via the command-zone premium path) whose `SimulateGame` first-castable turn `≤ TurnCapExplosive(3)`. Target turn is the **cap, not the printed MV**, so in-hand rocks/rituals credit acceleration (a Sol-Ring line casting a 4-MV payoff on turn 3 qualifies — Acceptance #5).
  - **Shape B — early engine**: an `Engine` plan card first-castable `≤ TurnCapEngine(2)`.
  - **Shape C — interaction bridge**: `≥ BridgeInteractionMin(2)` distinct cards with `SpellRequirement.IsInteractionSpell` (the **pre-gate** truth — NOT the permanent-gate-stripped `PlanRole.Interaction`, so non-permanent counterspells count) held/drawn within `RepresentativeLineTurnCap`, AND `(lands + rampPieces) ≥ BridgeDevelopmentMin(2)`.
- **Commander library membership** (`BuildLibrary`): commander spells are **excluded from the drawable library** (a commander is never "drawn"), so commander keep-credit comes only from the Shape-A command-zone premium path — no double-count. `LibraryCard` carries `IsInteractionSpell` from the source `SpellRequirement` for Shape C.
- **Percents**: `PlanKeepablePercent = round(100 × planKeepable / trials)` — denominator is **trials**, numerator a **subset of keepable hands**, so it is **≤ mana-keepable % by construction** (pinned by `PlanKeepable_NeverExceedsManaKeepable`). Band reuses the KeepableBand switch (`≥85 high / ≥70 medium / else low`); per-shape percents (`ShapeExplosive/Engine/Bridge`) are over keepable. Surfaced as `ManabaseMulliganEvaluation.PlanKeepablePercent/Band`.
- **Representative openers** (`ComputeMulliganEvaluation` selection, cEDH keep-shapes): (a) **turn cap** — a candidate whose on-curve turn is `≥ RepresentativeLineTurnCap+1 (≥5)` is never surfaced as workable; a no-shape hand renders `no plan by turn 4 — mulligan`. (b) each opener carries a **ShapeLabel** (`explosive/engine/bridge keep`) from the per-hand `KeepShape` verdict (precedence Explosive > Engine > Bridge), threaded via `BuildPlanOpenerSample` (no gate recompute). (c) **commander-central gate** (`IsCommanderCentral`): commander rows are excluded from the opener pool UNLESS `mode==Cedh AND importance != Low AND commander command-zone CastPercent ≥ CedhSupportThreshold(88) AND commander carries a win-directed PlanRole (Payoff/Engine/TutorCombo)`; when central the commander is preferred as the representative line if deployable ahead of curve. Bare `commanderDriver` (IsCommander && importance != Low) is **not** a sufficient fallback (MED-3).
- **Casual curve coverage** (`SimulateCurveCoverage` — role-independent, a **standalone** pass since casual decks carry no PlanRoles, NOT folded into plan-presence): per trial, for turns 1..5 a turn is **covered** iff ≥1 eligible non-commander/non-source spell with `MV ≤ T` is drawn-by and castable that turn (short-circuit on first hit; each turn counted at most once). `CurveCoverageTurns` = average covered turns across trials, a double in `[0,5]`; surfaced only when `keepShapes` is on (runs in both modes but is the **casual-facing** headline), else `0.0`.
- **Byte-identity**: when `!keepShapes || mode!=Cedh` the shape gate + its fields stay at defaults and no extra `SimulateGame` calls run; `SimulateCurveCoverage` runs only when `keepShapes` on. The view and `ManabaseReportTextBuilder` (`includeCedhKeepShapes`) append zero bytes off — proven by the OFF-excision render test and verbatim prompt pins (Acceptance #7).

---

## 6. Verdict synthesis

### 6.1 Four-tier health (`ManabaseReport.Health`, `Mdl:750-794`)
Labels (`ManabaseLabels.cs:14-17`): Healthy→**Excellent**, Functional→**Solid**, Workable→**Workable**, NeedsWork→**Needs work**. Evaluated in order:
1. **Needs work** if `AnySevereColorDeficit` (any `Deficit > 2`) OR `ColorsWithIssue ≥ 2` — `Mdl:770-773, 905-908`.
2. **Land-short escalation**: `landShort = LandDelta ≤ −2`. If land-short AND (a color issue OR broad under-support) → **Workable** only when `simFunctions && ColorsWithIssue == 1`, else **Needs work** — `Mdl:775-780`. (A raw land shortfall alone never forces Needs work — the land regression under-credits cheap ramp — so the sim must corroborate.)
3. **Workable** if `ColorsWithIssue == 1` — `Mdl:783-786`.
4. **Excellent** if `LandDelta ≥ −1 AND EveryColorClear` — `Mdl:788-791`.
5. **Solid** otherwise — `Mdl:793`.

Per-color "issue" (`ComputeColorSignals`, `Mdl:855-889`), tolerance `max(1, ceil(colorCards·0.15))`: `sourceShort = Deficit > 1`, `colorStarved = ColorLimitedUnderSupportedCount > tolerance`, `simWeakestProblem` (health-band path). **Mana-limited curve bombs are excluded from color issues** — only color-limited misses move the tier.

`simFunctions` (headline floor) = `UseHealthBandHeadlineFloor && AvgOnCurvePercent ≥ 85 && WorstColorCastPercent ≥ 50 && !AnySevereColorDeficit && !BroadColorUnderSupport` — `Mdl:763-768`.

### 6.2 Keep band's contribution
The opening-keep affects the verdict **only** through the sim cast % it produces (which feeds `AvgOnCurvePercent`/`WorstColorCastPercent`), not as a separate input. The recent "tighten keep band + gate color-starved on real deficit" work is the RampGate/hiCap tuning (§4.3) plus the `colorStarved`/`simWeakestProblem` gating (§6.1), so a structural cheap-spell miss on a well-supplied color no longer trips the verdict.

### 6.3 Biggest fix (`ManabaseReport.PrimaryFix`, `Mdl:1088-1146`)
Strict priority: **ColorSources** (largest `Deficit > 1`) → **Lands** (only if `LandDelta < −1 && !LandShortfallCoveredByRamp`) → **DemandingCards** (if the weakest color has `UnderSupportedCount > 0`, `DemandingCount = that count`) → **None**. All user-facing manabase wording now shares one advisory-count helper (`ManabaseWording.ApproximateCount` / `Pluralize`): `PrimaryFix`, the summary string, the verdict, the `.txt` artifact, the swap prompt, and the left-lens deficit marker all use the same rounded display count, so a `1.05` shortfall reads `~1`, not `~2`, everywhere.

### 6.4 Plain-language verdict (`plain-language-verdict` flag; Casual only)
`VS.Synthesize` builds up to **3** issue lines, then appends **`…plus N more`** when additional issues were collected instead of silently truncating them — `VS:19-100`:
1. Per-color issues from `ColorIssueFindings` (the exact set the health band uses, so the verdict can't say "no changes needed" beside a Workable/Needs-work chip) — `VS:49-57`.
2. Land line if `LandDelta < −1 && !LandShortfallCoveredByRamp` — `VS:59-64`.
3. Ramp-light / draw-light budget lines, each tagged "(community heuristic, not Karsten math)" — `VS:66-92`.
Per-color "add N sources" wording is now explicitly tagged as **heuristic guidance** on the page, in the `.txt` artifact, and in the swap prompt; the shortfall count is advisory wording only, not a new math path. The same shared helper also removed the last user-visible `(s)` artifacts from the summary / biggest-fix / lens surfaces. No-issue path: on the balanced budget → "in balance"; on a surplus/heavy side → "leans off the community split" (deliberately not "close enough") — `VS:157-165`. Verdict + budget are computed **only in Casual**; cEDH uses the flag as a UI-gloss gate — `MAS:328-331`.

---

## Short-lived inbound handoff

`/manabase` accepts an optional `handoff` query parameter produced by another tool. It names a cached analysis and renders that report without resolving the deck again. The cached result is available for five minutes; after it expires, `/manabase` renders the empty form with a notice inviting the user to run the source analysis again. This expiry is a normal property of the handoff.

## Feature Flag Catalog

Keys read via `MAS.IsFlagOn` (fail-safe OFF). Seed defaults in `FeatureFlagStore.cs`.

| Flag | Default | Changes |
|---|---|---|
| `analysis.manabase.accuracy` | **ON** | Bundled sim-accuracy knobs: mana quantity, repeatable-ramp credit, color-aware mulligan, land-ramp sim, health-band headline floor, pay-life untapped, conditional-untapped lands (bond always; check/Snarl on matching-type census). (MDFC land-backs are modeled as real lands **unconditionally** — not part of this bundle.) |
| `analysis.manabase.health-band-castability` | **ON** | Composite-weakest color's worst-spell cast % can tip Solid→Workable (`simWeakestProblem`). |
| `analysis.manabase.plain-language-verdict` | **ON** | Casual: plain-language verdict + ramp/draw budget advisory. |
| `analysis.manabase.commander-castability` | **ON** | Command-zone castability callout + companion modeling (+3 "to hand" tax). |
| `analysis.manabase.tap-analyzer` | **ON** | "Untapped Sources" block + tap card. |
| `analysis.manabase.mulligan-eval` | **ON** | Opening-hand / mulligan-evaluator block. Renamed from `analysis.mulligan-eval` (state carried by the store's idempotent rename migration). |
| `analysis.manabase.plan-presence` | **ON** | "With a plan" opener stat. Gated **also** on `mulligan-eval`; its category + Spellbook I/O only fire when both are on (fail-open). |
| `analysis.manabase.keep-shapes` | **OFF** | Gated `&& mulligan-eval`; also widens plan-role classification in cEDH. **cEDH**: three-shape opening-hand keep gate (explosive ≤T3 / early-engine ≤T2 / interaction-bridge ≥2+≥2) → second **plan-keepable %** headline (≤ mana-keepable by construction), shape-labeled + turn-capped (on-curve ≥5 never workable) representative openers, commander surfaced when commander-central (`IsCommanderCentral`: importance≠Low & cmd cast%≥88 & win-directed role). **Casual / Focused**: role-independent **curve-coverage** line (avg of turns 1–5 with a castable play). Seed OFF, flip after UAT; off (and non-cEDH off) = byte-identical on page, `.txt`, and swap prompt. |
| `analysis.manabase.focused-tier` | **OFF** | Show the Focused mid-power mode between Casual and cEDH. Focused keeps the Casual land target and display surfaces, but raises the color-support threshold to **85%**. Seed OFF is mandatory because missing flag keys default ON in the generic cache. |
| `analysis.manabase.source-list` | **OFF** | Display-only. Shows two nested disclosures inside the Untapped Sources lens: a full physical mana-source list (lands, rocks, dorks) with compact pip letters, and a tapped-sources subset (`EntersUntapped == false`). The Core report always carries the slim `ManaSourceListing` projection; the flag gates page HTML only, so it is intentionally NOT part of `PromptMutatingAnalysisFlags`. |
| `analysis.manabase.ritual-burst-mana` | **OFF** | Credit instant/sorcery rituals as one-shot burst mana in the castability sim. cEDH mode only; land count and color counts unchanged. |
| `analysis.manabase.ritual-land-credit` | **OFF** | cEDH only. Ships OFF by default and is enabled on deckflow.gg. Subtract `min(3.0, 0.5 × net-positive rituals)` from the enabled hybrid cEDH land target before the final `[22,45]` clamp. Separate from `ritual-burst-mana`; off = byte-identical. The Web breakdown also names the ritual land credit on its own line when applied. Calibration on the current 3281-deck harness moved the cEDH under-target rate from `21.8%` to `11.1%` and lowered the mean target by `0.7` lands. |
| `analysis.manabase.scry-credit` | **ON** | Add `0.2 ×` each qualifying cheap non-land `scry N` spell as analyzer-only any-color effective sources in the Karsten per-color requirement lane. Reminder-text-only matches and lands are excluded. Separate from the `≤2 MV` ramp/draw land-target credit, so draw+scry cards can count in both places; castability and land target stay unchanged. Off = byte-identical. |
| `analysis.manabase.colorless-snow` | **ON** | Track true `{C}` and snow `{S}` costs as separate source categories. The analyzer adds dedicated requirement rows; the sim requires real colorless producers for `{C}` and snow permanents for `{S}`. Off keeps the historic "drop colorless-folded pips" path byte-identical. |
| `analysis.manabase.restricted-lands` | **OFF** | Ships OFF by default and is enabled on deckflow.gg. Enable the restricted-land approximation for Cavern of Souls, Unclaimed Territory, Ancient Ziggurat, and Nykthos, Shrine to Nyx. Off = byte-identical; on also surfaces name-matched land-row disclosure markers, a gated footnote, and one unsupported-interactions entry naming the affected lands. |
| `analysis.manabase.cedh-land-target` | **OFF** | cEDH only. Replaces the flat 28-floor target with a curve-anchored target that can blend halfway toward a committed commander baseline when the baseline has `n ≥ 10`. Off = byte-identical. |

**Hardcoded, no flag**: `gateRampOnCastable = true` — P4 gated-ramp is always on (`MAS:301-305`); before crediting a ramp piece the sim verifies the ramp's own colored cost is payable.

---

## Mode differences (Casual vs Focused vs cEDH)

| Rule | Casual | Focused | cEDH |
|---|---|---|---|
| Land target | singleton figure | same as Casual | Flag OFF: `max(28, singleton − 3.5)`; flag ON: hybrid `singleton − 3.5`, optional commander-baseline blend, clamp `[22,45]` |
| Color support threshold | 80 | 85 | 88 |
| Central-commander color bar | raised to 88 if Central | raised to 88 if Central | already 88 |
| Plain-language verdict + budget | computed | computed | flag is UI-gloss only |
| Castability table | shown | shown | hidden unless cEDH interaction lens is on |
| Keep shapes (`keep-shapes` flag) | curve-coverage line only (avg turns 1–5 with a castable play) | same as Casual | three-shape keep gate → plan-keepable % headline + shape-labeled/turn-capped openers + commander-central opener |

Mode does **not** change the confirmed `(89+M)%` Karsten consistency threshold,
the per-color source math, or the 60-card path. MBGAP-04 explicitly reviewed and
rejected a separate casual-multiplayer `(85+M)%` relaxation because DeckFlow
already models Commander with a more generous every-turn draw pattern in the
simulator, so lowering the threshold as well would risk double-counting the
multiplayer benefit.

---

## Notes & caveats

- **Nearly nothing here is flag-free.** The prod-live accuracy bundle is
  `analysis.manabase.accuracy` (ON) plus the always-on gated-ramp. The opening-hand
  (`mulligan-eval`), tap, plan-presence, plain-language, commander-castability, and
  health-band-castability reads now also **default ON** — every current manabase
  display/verdict flag ships enabled. The dark-launched sim flag
  `analysis.manabase.ritual-burst-mana` is the exception and stays **default OFF**;
  an admin can still hide or flip any one from `/Admin/Flags`. Flipping a seed default
  does not retroactively flip an existing DB's stored row (operator toggles those),
  except `mulligan-eval`, whose prior state is carried across its rename.
- **Flag-off = byte-identical** is a maintained invariant for the sim accuracy
  flags (MQ-02 / MQ-05 / gated-ramp) — see the guards cited in §4.
- `analysis.manabase.accuracy` ships **ON** — seed and catalog both enable it.
