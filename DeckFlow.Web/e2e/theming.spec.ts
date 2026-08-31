import { expect, test, type Page } from '@playwright/test';

// These guard the theme-aware custom checkbox/radio + textarea scrollbar fixes.

import { resolveE2EPort } from './support/e2e-port';

const baseUrl = `http://localhost:${resolveE2EPort()}`;
const themeFiles = [
  'site.css',
  'site-azorius.css',
  'site-dimir.css',
  'site-rakdos.css',
  'site-gruul.css',
  'site-selesnya.css',
  'site-orzhov.css',
  'site-izzet.css',
  'site-golgari.css',
  'site-boros.css',
  'site-simic.css',
  'site-bant.css',
  'site-abzan.css',
  'site-sultai.css',
  'site-mardu.css',
  'site-temur.css',
  'site-esper.css',
  'site-grixis.css',
  'site-jund.css',
  'site-naya.css',
  'site-jeskai.css',
  'site-nyx.css',
  'site-planeswalker-dark.css',
  'site-commander-table.css',
] as const;

// Mechanism (layout/size/appearance) is theme-INDEPENDENT — the custom control
// rules live in site-theme-overrides.css and render identically regardless of
// which guild theme's tokens are active. So structural assertions only need a
// few representative themes (a light default + a dark fork), not all 24. The
// full themeFiles list is reserved for the token-application tier, which is the
// only thing that genuinely varies per theme.
const representativeThemes = ['site.css', 'site-azorius.css', 'site-nyx.css'] as const;

type ThemeSnapshot = {
  rootAccent: string;
  rootDanger: string;
  rootLink: string;
  rootFocus: string;
  rootCtaBorder: string;
  checkboxAppearance: string | null;
  checkboxWebkitAppearance: string | null;
  checkboxBackground: string | null;
  checkboxBorderColor: string | null;
  checkboxRenderWidth: number | null;
  checkboxRenderHeight: number | null;
  checkboxPadding: string | null;
  textareaFound: boolean;
  textareaScrollbar: string;
  textareaScrollbarProperty: string;
  textareaScrollbarWidth: string;
};

async function readThemeSnapshot(page: Page, themeFile: string): Promise<ThemeSnapshot> {
  const context = page.context();

  await context.addCookies([
    {
      name: 'deckflow-theme',
      value: themeFile,
      url: baseUrl,
    },
  ]);

  const response = await page.goto('/deck-analysis');
  expect(response?.ok()).toBeTruthy();

  return page.evaluate(() => {
    const rootStyle = getComputedStyle(document.documentElement);
    const checkbox = document.querySelector<HTMLInputElement>('input[type="checkbox"]');
    const checkboxStyle = checkbox ? getComputedStyle(checkbox) : null;
    const textarea = document.querySelector<HTMLTextAreaElement>('textarea');
    const textareaStyle = textarea ? getComputedStyle(textarea) : null;

    return {
      rootAccent: rootStyle.getPropertyValue('--accent').trim(),
      rootDanger: rootStyle.getPropertyValue('--danger').trim(),
      rootLink: rootStyle.getPropertyValue('--link').trim(),
      rootFocus: rootStyle.getPropertyValue('--focus').trim(),
      rootCtaBorder: rootStyle.getPropertyValue('--cta-border').trim(),
      checkboxAppearance: checkboxStyle ? checkboxStyle.getPropertyValue('appearance').trim() : null,
      checkboxWebkitAppearance: checkboxStyle ? checkboxStyle.getPropertyValue('-webkit-appearance').trim() : null,
      checkboxBackground: checkboxStyle?.backgroundColor ?? null,
      checkboxBorderColor: checkboxStyle?.borderColor ?? null,
      // Computed width/height (not getBoundingClientRect — the first checkbox
      // lives in a collapsed bucket, so its rect is 0 while its box size is
      // still resolved correctly by the cascade).
      checkboxRenderWidth: checkboxStyle ? parseFloat(checkboxStyle.width) : null,
      checkboxRenderHeight: checkboxStyle ? parseFloat(checkboxStyle.height) : null,
      checkboxPadding: checkboxStyle ? checkboxStyle.padding : null,
      textareaFound: textarea !== null,
      textareaScrollbar: textareaStyle?.scrollbarColor ?? '',
      textareaScrollbarProperty: textareaStyle?.getPropertyValue('scrollbar-color') ?? '',
      textareaScrollbarWidth: textareaStyle?.getPropertyValue('scrollbar-width').trim() ?? '',
    };
  });
}

async function setThemedViewport(page: Page): Promise<void> {
  const isMobile = test.info().project.name.includes('mobile');

  if (isMobile) {
    await page.setViewportSize({ width: 390, height: 844 });
    return;
  }

  await page.setViewportSize({ width: 1280, height: 900 });
}

function normalizeColor(value: string | null): string {
  return value?.trim().toLowerCase() ?? '';
}

function isRealColor(value: string | null): boolean {
  const normalized = normalizeColor(value);

  return normalized !== '' && normalized !== 'auto' && normalized !== 'none' && normalized !== 'transparent';
}

function pickScrollbarValue(snapshot: ThemeSnapshot): string {
  return normalizeColor(snapshot.textareaScrollbar) || normalizeColor(snapshot.textareaScrollbarProperty);
}

// ── Tier 1: MECHANISM ──────────────────────────────────────────────────────
// Structural render of the custom controls — theme-independent, so only a few
// representative themes, but BOTH color schemes (the original bug was native
// chrome going OS-black under dark `color-scheme`). Catches: native chrome not
// disabled, box inflated by inherited input padding, non-square, offset.
test('custom checkbox renders compact, square, and themed (representative themes, light + dark)', async ({ page }) => {
  await setThemedViewport(page);

  for (const colorScheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme });

    for (const themeFile of representativeThemes) {
      const snapshot = await readThemeSnapshot(page, themeFile);

      if (snapshot.checkboxAppearance === null) {
        test.skip(true, `No checkbox found on /deck-analysis for ${themeFile}.`);
      }

      // Native chrome disabled (else the empty box follows OS color-scheme).
      expect(snapshot.checkboxAppearance, `${themeFile} should disable native checkbox rendering in ${colorScheme} mode`).toBe('none');
      expect(snapshot.checkboxWebkitAppearance, `${themeFile} should disable WebKit native checkbox rendering in ${colorScheme} mode`).toBe('none');

      // Themed surfaces (not OS default).
      expect(isRealColor(snapshot.checkboxBackground), `${themeFile} should theme the checkbox background in ${colorScheme} mode`).toBeTruthy();
      expect(isRealColor(snapshot.checkboxBorderColor), `${themeFile} should theme the checkbox border in ${colorScheme} mode`).toBeTruthy();

      // Size guard (regression: the generic `input` padding inflated the
      // appearance:none box to ~29x26 and offset the checkmark). Must stay
      // small (~1.05rem), square, with no inherited padding.
      const w = snapshot.checkboxRenderWidth ?? 0;
      const h = snapshot.checkboxRenderHeight ?? 0;
      expect(w, `${themeFile} checkbox width should stay compact (not inflated by inherited input padding) in ${colorScheme} mode`).toBeGreaterThan(10);
      expect(w, `${themeFile} checkbox width should stay compact in ${colorScheme} mode`).toBeLessThanOrEqual(24);
      expect(h, `${themeFile} checkbox height should stay compact in ${colorScheme} mode`).toBeLessThanOrEqual(24);
      expect(Math.abs(w - h), `${themeFile} checkbox should render square in ${colorScheme} mode`).toBeLessThanOrEqual(2);
      expect(snapshot.checkboxPadding, `${themeFile} checkbox should not inherit text-input padding in ${colorScheme} mode`).toBe('0px');
    }
  }
});

// ── Tier 2: TOKENS ─────────────────────────────────────────────────────────
// The only thing that genuinely varies per theme: each of the 24 themes must
// expose its tokens AND apply them to the control (so a theme missing --line or
// --accent, or not applying it, fails). Cheap computed-style reads, one scheme.
// The cross-theme "border colors differ" check proves tokens are actually
// flowing through per theme, not hardcoded.
test('every theme exposes tokens and applies them to the checkbox', async ({ page }) => {
  await setThemedViewport(page);
  await page.emulateMedia({ colorScheme: 'light' });

  const borderColorsByTheme = new Map<string, string>();

  for (const themeFile of themeFiles) {
    const snapshot = await readThemeSnapshot(page, themeFile);

    expect(snapshot.rootAccent, `${themeFile} should expose a theme accent`).not.toBe('');

    if (snapshot.checkboxAppearance === null) {
      test.skip(true, `No checkbox found on /deck-analysis for ${themeFile}.`);
    }

    expect(isRealColor(snapshot.checkboxBackground), `${themeFile} should apply a themed checkbox background`).toBeTruthy();
    expect(isRealColor(snapshot.checkboxBorderColor), `${themeFile} should apply a themed checkbox border`).toBeTruthy();

    borderColorsByTheme.set(themeFile, normalizeColor(snapshot.checkboxBorderColor));
  }

  // Distinct border colors across themes prove the tokens actually differ per
  // theme (catches a regression that hardcodes the box to one color).
  expect(new Set(borderColorsByTheme.values()).size, 'themes should resolve to distinct checkbox border colors').toBeGreaterThanOrEqual(2);
});

// ── Tier 1 (mechanism): textarea scrollbar follows theme, not OS ────────────
test('textarea scrollbar-color is themed (not OS default)', async ({ page }) => {
  await setThemedViewport(page);

  const snapshots = new Map<string, ThemeSnapshot>();

  for (const themeFile of representativeThemes) {
    const snapshot = await readThemeSnapshot(page, themeFile);

    expect(snapshot.rootAccent, `${themeFile} should expose a theme accent`).not.toBe('');

    if (!snapshot.textareaFound) {
      test.skip(true, `No textarea found on /deck-analysis for ${themeFile}.`);
    }

    snapshots.set(themeFile, snapshot);
  }

  const classic = snapshots.get('site.css');
  const dark = snapshots.get('site-nyx.css');

  expect(classic).toBeTruthy();
  expect(dark).toBeTruthy();

  const classicScrollbar = pickScrollbarValue(classic!);
  const darkScrollbar = pickScrollbarValue(dark!);
  const scrollbarExposed = classicScrollbar !== '' && darkScrollbar !== '';

  if (scrollbarExposed) {
    expect(isRealColor(classicScrollbar), 'site.css should compute a non-default textarea scrollbar color').toBeTruthy();
    expect(isRealColor(darkScrollbar), 'site-nyx.css should compute a non-default textarea scrollbar color').toBeTruthy();
    expect(classicScrollbar).not.toBe(darkScrollbar);
    return;
  }

  // Some engines do not expose computed scrollbar-color; in that case, assert
  // theme accents differ and scrollbar-width is still computed as thin so the
  // themed rule is at least being applied.
  expect(normalizeColor(classic!.rootAccent)).not.toBe(normalizeColor(dark!.rootAccent));
  expect(normalizeColor(classic!.textareaScrollbarWidth)).toBe('thin');
  expect(normalizeColor(dark!.textareaScrollbarWidth)).toBe('thin');
});

// ── THEME-02 regression guard: --danger must never equal --link ────────────
// Phase 84 decoupled the brand-emphasis alias tokens (--link/--focus/
// --cta-border, now re-pointed to var(--accent-strong)) from the fixed
// --danger token, so a guild's brand color can never coincide with its
// error/danger color. rakdos is the strongest case: its --link:#ff9ea4
// override (site-rakdos.css, UI-VS-02) is deliberately distinct from BOTH its
// red --accent-strong (#a92434) and the fixed --danger (#c53030) — a
// regression that dropped the override, or re-aliased --danger onto
// --accent-strong, would collapse rakdos's danger and link colors together.
// getComputedStyle(...).getPropertyValue resolves nested var() references
// (verified: site-commander-table.css's `--link: var(--accent-strong)`
// resolves to its hex value, not the literal `var(...)` text), so a plain
// string-inequality check is a valid, permanent structural guard. Runs over
// the full themeFiles array (not a sample) because any fork could silently
// reintroduce the collision; desktop + mobile are both covered because this
// spec runs under both Playwright projects (chromium-desktop/chromium-mobile).
test('computed --danger never equals computed --link, in every theme', async ({ page }) => {
  await setThemedViewport(page);
  await page.emulateMedia({ colorScheme: 'light' });

  for (const themeFile of themeFiles) {
    const snapshot = await readThemeSnapshot(page, themeFile);

    const danger = normalizeColor(snapshot.rootDanger);
    const link = normalizeColor(snapshot.rootLink);

    expect(danger, `${themeFile} should expose a --danger color`).not.toBe('');
    expect(link, `${themeFile} should expose a --link color`).not.toBe('');
    expect(danger, `${themeFile}: --danger must never equal --link (THEME-02 structural guard)`).not.toBe(link);
  }
});

// ── THEME-01 regression guard: semantic tokens must resolve to real colors ──
// Catches a future regression that reintroduces an orphaned raw
// --accent-strong call site under a fork missing the semantic token block
// entirely (the site-commander-table.css D4 gap Phase 84 fixed).
// Custom properties are untyped, so reading getPropertyValue('--link') and
// checking it is a non-empty string is too weak — a bogus `--link: banana`
// declaration would satisfy a string check yet fail as a color. Instead,
// resolve each token THROUGH a real `color` property: a valid color yields an
// rgb value, while a missing or non-color token makes the declaration invalid
// at computed-value time so `color` falls back to the inherited value. We
// capture that fallback via an intentionally-undefined control token and assert
// each semantic token resolves to a real rgb color distinct from the fallback.
test('every theme resolves --link, --focus, and --cta-border to a real color', async ({ page }) => {
  await setThemedViewport(page);
  await page.emulateMedia({ colorScheme: 'light' });

  for (const themeFile of themeFiles) {
    await readThemeSnapshot(page, themeFile); // establishes the themed /deck-analysis render

    const resolved = await page.evaluate(() => {
      const probe = document.createElement('span');
      document.body.appendChild(probe);
      const resolveViaColor = (expr: string): string => {
        probe.style.setProperty('color', expr);
        return getComputedStyle(probe).color;
      };
      // Control: an undefined token makes `color: var(...)` invalid, so the probe
      // inherits document.body's color — the tell-tale "token did not resolve" value.
      const invalidControl = resolveViaColor('var(--deckflow-nonexistent-token-xyz)');
      const link = resolveViaColor('var(--link)');
      const focus = resolveViaColor('var(--focus)');
      const ctaBorder = resolveViaColor('var(--cta-border)');
      probe.remove();
      return { invalidControl, link, focus, ctaBorder };
    });

    const rgbPattern = /^rgba?\(/;
    const tokens: ReadonlyArray<readonly [string, string]> = [
      ['--link', resolved.link],
      ['--focus', resolved.focus],
      ['--cta-border', resolved.ctaBorder],
    ];
    for (const [token, value] of tokens) {
      expect(value, `${themeFile}: ${token} should resolve to an rgb color`).toMatch(rgbPattern);
      expect(isRealColor(value), `${themeFile}: ${token} should not resolve to transparent`).toBeTruthy();
      expect(
        value,
        `${themeFile}: ${token} must resolve to a real color, not the invalid-var inherited fallback (orphaned/non-color token regression)`,
      ).not.toBe(resolved.invalidControl);
    }
  }
});
