# Footer Links Arrangement — Design

**Date:** 2026-04-24
**Scope:** `DeckFlow.Web` layout footer (About + Feedback links)
**Problem:** Current footer renders `<a>About</a><a>Feedback</a>` with no separator and no gap, so the two links visually fuse into a single run of text. Hard to tell them apart at a glance.

## Goal

Visually differentiate the two footer links so each is immediately scannable, while (a) keeping the muted, non-shouty footer aesthetic, (b) staying coherent across all 10 guild themes, and (c) signalling that `Feedback` is the actionable one.

## Chosen Approach — C1: Outlined Pill CTA

- `About` stays a plain link using the existing muted footer styling.
- `Feedback` gets promoted to an outlined pill "button" treatment — still a semantic `<a>` under the hood, just styled to read as a call-to-action.
- The two sit on a single right-aligned row with a meaningful gap between them, so the visual hierarchy is: quiet info link → clear CTA.

### Why C1 over siblings
- **C2 (solid pill):** too loud for the bottom of every page; competes with primary-page CTAs.
- **C3 (ghost link + arrow):** nice but not enough visual separation from the plain About link in themes where accent and ink sit close in hue.
- **C1 (outlined pill):** border gives spatial separation even when accent/ink are close; fill-on-hover gives a satisfying affordance without permanent noise.

## Visual Spec

```
                         About   [ Feedback ]
                           ^          ^
                       plain link   outlined pill
                       muted ink    accent-strong border + text
                                    hover/focus: fills accent-strong,
                                                 text flips to ink-on-accent
```

### Styling rules
- Footer row: right-aligned, flex, `gap: 1rem`.
- `About` link: unchanged from current `.page-footer__link` — inherits color, no decoration, underline on hover/focus.
- `Feedback` link (`.page-footer__link--cta`): 
  - padding `0.35rem 0.9rem`
  - border `1px solid var(--accent-strong)`
  - border-radius: pill (`999px`)
  - color `var(--accent-strong)`
  - background `transparent`
  - font-size inherits footer (`0.9rem`)
  - hover/focus: `background: var(--accent-strong)`, `color: var(--ink-on-accent, var(--panel))`
  - transition `background 120ms, color 120ms`
- Footer wrapper `opacity: 0.7` is **removed** so the CTA border isn't washed out. Muted feel is preserved via color choice, not opacity — About uses `currentColor` which is already subdued within the footer's typography, and the CTA is intentionally a touch bolder (that's the point).
- Minimum touch target: `Feedback` pill ≥ 40px tall including padding; `About` gets `padding: 0.35rem 0.25rem` so it hits target size too.

### Theme integration
- Uses existing `--accent-strong` token (already defined per guild theme).
- Introduces **no new tokens** unless `--ink-on-accent` is missing; if missing, fall back to `var(--panel)` which provides enough contrast against an accent fill in every current theme. Verify per theme during implementation; add `--ink-on-accent` to any theme where contrast fails.

### Accessibility
- Both elements stay `<a>` — no role change, no ARIA needed.
- Focus ring: keep browser default (or existing global focus style) on both; the pill's hover/focus fill also serves as a visible focus indicator.
- Contrast: outlined state must clear WCAG AA (4.5:1) against `--panel`/page bg in every theme. Solid hover state must clear AA for `--ink-on-accent` on `--accent-strong`.
- Decorative-only change; screen-reader output is unchanged.

### Responsive
- 2 links inline work at every breakpoint currently supported. No stacking needed.
- If page ever grows to 3+ footer links, revisit.

## Non-Goals

- No change to About or Feedback page content.
- No change to routes, controllers, or markup structure beyond adding one class to the Feedback anchor.
- No footer restructuring (columns, sections, etc.) — intentional YAGNI.
- No analytics/click-tracking addition.

## Files Touched

- `DeckFlow.Web/Views/Shared/_Layout.cshtml` — add `page-footer__link--cta` class to the Feedback anchor.
- `DeckFlow.Web/wwwroot/css/site-common.css` — add `.page-footer__link--cta` ruleset; add `gap` to `.page-footer`; remove `opacity: 0.7` from `.page-footer`; add small padding to `.page-footer__link` for touch target.
- Per-theme CSS (only if a theme lacks readable `--ink-on-accent` contrast) — minimal, theme-by-theme token add.

## Verification

- Visual check in browser across all 10 guild themes: outlined pill is readable in default and hover state.
- Tab key moves focus About → Feedback with a visible indicator on each.
- Contrast ratios ≥ 4.5:1 in both states, measured per theme.
- No regressions on narrow widths (mobile) or when footer lives on a themed panel page.
- Build: 0 errors, 0 warnings.

## Risks / Open Questions

- Some guild themes may need `--ink-on-accent` added if `--panel` fallback doesn't yield AA contrast against the accent fill. Resolve during implementation, per theme, with explicit contrast check.
- Removing `.page-footer opacity: 0.7` slightly brightens the About link too. Acceptable — it was the opacity that contributed to the "links mush together" feel by making both look equally secondary. If About now reads as too prominent, revisit by lowering its `color` mix instead of reintroducing opacity (opacity damages the CTA's border).
