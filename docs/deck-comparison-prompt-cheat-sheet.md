# Deck Comparison Prompt Cheat Sheet

Use these prompts outside the app when you want a fast comparison between two MTG decks. Replace the bracketed placeholders before sending them to ChatGPT.

## Best-Practice Header

Add this block at the top of any comparison prompt when you want cleaner, more disciplined answers.

```text
Use the decklists as the primary evidence base.
Do not invent card text, archetype details, or matchup claims.
When a conclusion is directly supported by the list, treat it as evidence.
When a conclusion depends on pattern recognition or likely play patterns, label it as inference.
If the list is insufficient to support a confident claim, say so clearly.
```

## Quick Verdict

```text
Compare these two Magic: The Gathering decks in [FORMAT] at [POWER LEVEL].

For each deck, identify:
- core game plan
- main win conditions
- biggest strengths
- biggest weaknesses

Then give:
- a short side-by-side comparison
- which deck is stronger overall
- why

Be explicit about what is direct evidence from the decklists versus inference.

Deck A ([DECK A NAME]):
[DECK A LIST]

Deck B ([DECK B NAME]):
[DECK B LIST]
```

## Matchup

```text
Analyze the matchup between these two Magic: The Gathering decks in [FORMAT] at [POWER LEVEL].

Explain:
- how Deck A vs Deck B usually plays out
- which deck is favored
- why that deck is favored
- what early turns matter most
- what cards, engines, or interactions swing the matchup
- what each deck must do to win

End with:
- matchup verdict
- 5 cards or patterns that matter most

Separate direct evidence from inference.

Deck A ([DECK A NAME]):
[DECK A LIST]

Deck B ([DECK B NAME]):
[DECK B LIST]
```

## Competitive / Meta

```text
Compare these two Magic: The Gathering decks for a [CASUAL / HIGH-POWER / COMPETITIVE] [FORMAT] environment.

Evaluate:
- consistency
- speed
- resilience
- interaction density
- mana efficiency
- closing power
- expected performance into a typical field

Answer:
- which deck is better for a competitive meta
- which is more resilient
- which is more explosive
- which is easier to pilot well

End with:
- final ranking
- confidence notes

Do not overstate claims that are only inferred from the decklists.

Deck A ([DECK A NAME]):
[DECK A LIST]

Deck B ([DECK B NAME]):
[DECK B LIST]
```

## Best All-In-One

```text
Compare these two Magic: The Gathering decks in detail for [FORMAT] at [POWER LEVEL].

For each deck, explain:
- game plan
- commander or focal card role
- setup speed
- win conditions
- strengths
- weaknesses
- consistency
- resilience
- interaction
- closing power

Then compare:
- how the matchup plays out head-to-head
- what cards or packages create the biggest gap
- which deck is stronger in a vacuum
- which is better into a typical meta
- which is more explosive
- which is more resilient

Finish with:
- a readable side-by-side summary
- a final verdict
- 5 concrete card or package differences that best explain the result

Be explicit about direct evidence versus inference.

Deck A ([DECK A NAME]):
[DECK A LIST]

Deck B ([DECK B NAME]):
[DECK B LIST]
```

## JSON Return Prompt

Use this when you want structured output compatible with the app's Deck Comparison workflow.

```text
Compare these two Magic: The Gathering decks in [FORMAT] at [POWER LEVEL].

Analyze:
- deck identity and overall game plan for each deck
- commander role and deck thesis
- speed and setup tempo
- ramp, draw, interaction, board wipes, recursion, and finishing power
- consistency and resilience
- strengths and weaknesses of each deck
- major overlap and major differences
- which deck is stronger in a vacuum
- which deck is more resilient
- which deck is more explosive
- which deck is likely better at a typical [FORMAT] table
- recommended audience or playgroup fit for each deck
- 5 concrete cards or packages that best explain the gap

Output requirements:
- start with a readable comparison summary
- then give a side-by-side comparison
- then give a final verdict
- then return a fenced ```json block
- inside that block, return a top-level object named "deck_comparison"
- the JSON must be valid and must match the schema exactly
- if evidence is limited, say so in confidence_notes

Schema:
{
  "deck_comparison": {
    "deck_a_name": "[DECK A NAME]",
    "deck_b_name": "[DECK B NAME]",
    "deck_a_commander": "",
    "deck_b_commander": "",
    "shared_themes": [],
    "major_differences": [],
    "deck_a_strengths": [],
    "deck_b_strengths": [],
    "deck_a_weaknesses": [],
    "deck_b_weaknesses": [],
    "speed_comparison": "",
    "resilience_comparison": "",
    "interaction_comparison": "",
    "closing_power_comparison": "",
    "overall_verdict": "",
    "key_gap_cards_or_packages": [],
    "recommended_for": {
      "deck_a": [],
      "deck_b": []
    },
    "confidence_notes": []
  }
}

Separate direct evidence from inference.

Deck A ([DECK A NAME]):
[DECK A LIST]

Deck B ([DECK B NAME]):
[DECK B LIST]
```

## Always Include

- `Format: [Commander / Modern / Standard / etc.]`
- `Power level: [casual / mid-power / high-power / cEDH / tournament]`
- `Deck A label: [name]`
- `Deck B label: [name]`
- Full decklists whenever possible
