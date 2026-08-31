import { expect, test, type Page } from '@playwright/test';
import { resolveE2EPort } from './support/e2e-port';

// Phase 86 (86-05): visual-regression + a11y coverage that the CURRENT e2e suite could not
// provide — it asserts DOM/selectors exist, not visual STATE (which tab is filled, contrast)
// or accent-leak. That gap is exactly why Bugs A/B/C shipped green. This spec closes it.
//
// Covers:
//   Bug A — .prompt-step-tab.is-active must be a FILLED var(--accent) pill, distinct from
//           the inactive tab AND from var(--panel-soft-bg), with WCAG >=4.5:1 active-tab text
//           on the previously-failing dark themes.
//   Bug B — the layout-segment hover/active, ui-mode-button active, and clear-cache-button
//           hover states must tint from the THEME'S OWN --accent, never the old hardcoded
//           Jeskai-blue rgb(43, 108, 176) literal.
//   Bug C — the analysis-questions bucket toggle must have a non-empty accessible name and
//           be borderless (no stray grey pill).
//
// Runs under BOTH Playwright projects (chromium-desktop 1280, chromium-mobile 390) so desktop
// and mobile are both covered by this single spec file.
//
// Why an EXPLICIT deckflow-theme cookie for every theme (incl. Classic/site.css): a null or
// absent cookie is brittle — it silently inherits whatever theme cookie a PRIOR test in the
// same worker/context left set. Every test below sets the cookie explicitly before navigating.

const BLUE_LEAK_RE = /rgba?\(\s*43,\s*108,\s*176\b/;

async function setTheme(page: Page, cookieFile: string, baseURL?: string): Promise<void> {
  await page.context().addCookies([
    { name: 'deckflow-theme', value: cookieFile, url: baseURL ?? `http://localhost:${resolveE2EPort()}` },
  ]);
}

async function gotoStep2(page: Page): Promise<void> {
  const response = await page.goto('/deck-analysis');
  expect(response?.ok()).toBeTruthy();
  await page.locator('[data-prompt-show-step="2"][role="tab"]').click();
  await expect(page.locator('[data-prompt-show-step="2"][role="tab"]')).toHaveClass(/is-active/);
}

// Resolve a CSS custom property to its computed color via a throwaway probe element —
// independent of any specific selector's cascade quirks (duplicate-fork rules, load order).
async function resolveCustomPropertyColor(page: Page, token: string): Promise<string> {
  return page.evaluate((cssToken) => {
    const probe = document.createElement('div');
    probe.style.color = `var(${cssToken})`;
    document.body.appendChild(probe);
    const value = getComputedStyle(probe).color;
    probe.remove();
    return value;
  }, token);
}

function parseRgb(value: string): [number, number, number] {
  const match = value.match(/rgba?\(\s*([\d.]+),\s*([\d.]+),\s*([\d.]+)/);
  if (!match) {
    throw new Error(`Could not parse computed color: ${value}`);
  }
  return [Number(match[1]), Number(match[2]), Number(match[3])];
}

function relativeLuminance([r, g, b]: [number, number, number]): number {
  const toLinear = (channel: number): number => {
    const srgb = channel / 255;
    return srgb <= 0.03928 ? srgb / 12.92 : Math.pow((srgb + 0.055) / 1.055, 2.4);
  };
  const [rl, gl, bl] = [toLinear(r), toLinear(g), toLinear(b)];
  return 0.2126 * rl + 0.7152 * gl + 0.0722 * bl;
}

function contrastRatio(a: string, b: string): number {
  const luminanceA = relativeLuminance(parseRgb(a));
  const luminanceB = relativeLuminance(parseRgb(b));
  const lighter = Math.max(luminanceA, luminanceB);
  const darker = Math.min(luminanceA, luminanceB);
  return (lighter + 0.05) / (darker + 0.05);
}

// ── Bug A: filled-accent-pill active step-tab ───────────────────────────────────────────────

// Representative set: >=1 light @import (azorius), a dark fork (jund), a dark @import that
// needs --accent-contrast (dimir), a dark fork that needs --accent-contrast
// (planeswalker-dark), and Classic with an EXPLICIT site.css cookie (never a null cookie).
const pillThemes = [
  { name: 'azorius (light @import)', cookie: 'site-azorius.css' },
  { name: 'jund (dark fork)', cookie: 'site-jund.css' },
  { name: 'dimir (dark @import, --accent-contrast)', cookie: 'site-dimir.css' },
  { name: 'planeswalker-dark (dark fork, --accent-contrast)', cookie: 'site-planeswalker-dark.css' },
  { name: 'Classic (explicit site.css)', cookie: 'site.css' },
];

for (const theme of pillThemes) {
  test(`active step-tab is a filled accent pill, distinct from inactive and --panel-soft-bg (${theme.name})`, async ({
    page,
    baseURL,
  }) => {
    await setTheme(page, theme.cookie, baseURL);
    await gotoStep2(page);

    const activeTab = page.locator('.prompt-step-tab.is-active');
    const inactiveTab = page.locator('.prompt-step-tab:not(.is-active)').first();
    await expect(activeTab).toBeVisible();
    await expect(inactiveTab).toBeVisible();

    const activeBg = await activeTab.evaluate((el) => getComputedStyle(el).backgroundColor);
    const inactiveBg = await inactiveTab.evaluate((el) => getComputedStyle(el).backgroundColor);
    const panelSoftBg = await resolveCustomPropertyColor(page, '--panel-soft-bg');
    const accent = await resolveCustomPropertyColor(page, '--accent');

    expect(activeBg, 'active tab bg must differ from inactive tab bg').not.toBe(inactiveBg);
    expect(activeBg, 'active tab bg must differ from --panel-soft-bg').not.toBe(panelSoftBg);
    expect(activeBg, 'active tab bg must equal the resolved --accent (proves the filled pill)').toBe(accent);
  });
}

// ── Bug A: WCAG >=4.5:1 active-tab text on the previously-failing dark themes ───────────────

// dimir/golgari/planeswalker-dark/nyx were the confirmed WCAG fails; jund/sultai are guards
// (Codex sweep found they already pass white-on-accent, but we assert it here too so a
// regression on those two is caught as well).
const contrastThemes = [
  { name: 'dimir', cookie: 'site-dimir.css' },
  { name: 'golgari', cookie: 'site-golgari.css' },
  { name: 'planeswalker-dark', cookie: 'site-planeswalker-dark.css' },
  { name: 'nyx', cookie: 'site-nyx.css' },
  { name: 'jund', cookie: 'site-jund.css' },
  { name: 'sultai', cookie: 'site-sultai.css' },
];

for (const theme of contrastThemes) {
  test(`active step-tab text meets WCAG >=4.5:1 (${theme.name})`, async ({ page, baseURL }) => {
    await setTheme(page, theme.cookie, baseURL);
    await gotoStep2(page);

    const activeTab = page.locator('.prompt-step-tab.is-active');
    const bg = await activeTab.evaluate((el) => getComputedStyle(el).backgroundColor);
    const fg = await activeTab.evaluate((el) => getComputedStyle(el).color);
    const ratio = contrastRatio(bg, fg);
    expect(ratio, `${theme.name} active-tab contrast measured ${ratio.toFixed(2)}:1, must be >= 4.5:1`).toBeGreaterThanOrEqual(4.5);
  });
}

// ── Bug B: no hardcoded Jeskai-blue leak on a non-Jeskai theme ──────────────────────────────

test('no hardcoded Jeskai-blue leak on layout-segment / ui-mode-button / clear-cache-button (jund)', async ({
  page,
  baseURL,
}) => {
  await setTheme(page, 'site-jund.css', baseURL);
  const response = await page.goto('/deck-analysis');
  expect(response?.ok()).toBeTruthy();

  const focusedSegment = page.locator('[data-prompt-ui-mode-button="focused"]');
  await expect(focusedSegment).toBeVisible();

  // (1) hover a .prompt-layout-segment — exercises site-common.css's global :hover rule
  //     (site-common.css:792, shared by ALL 24 themes — the widest leak surface).
  await focusedSegment.hover();
  const hoverBg = await focusedSegment.evaluate((el) => getComputedStyle(el).backgroundColor);
  expect(hoverBg, 'layout-segment hover bg must not be the hardcoded Jeskai blue').not.toMatch(BLUE_LEAK_RE);

  // (2) activate the [data-prompt-ui-mode-button] — exercises site.css/fork's `.is-active` rule
  //     (site.css:308, mirrored into bant/mardu/naya per 86-01).
  await focusedSegment.click();
  await expect(focusedSegment).toHaveClass(/is-active/);
  const activeBg = await focusedSegment.evaluate((el) => getComputedStyle(el).backgroundColor);
  expect(activeBg, 'ui-mode-button active bg must not be the hardcoded Jeskai blue').not.toMatch(BLUE_LEAK_RE);

  // (3) hover .clear-cache-button — exercises site.css/fork's `:hover` rule (site.css:623).
  const clearCache = page.locator('.clear-cache-button').first();
  await expect(clearCache).toBeVisible();
  await clearCache.hover();
  const clearCacheHoverBg = await clearCache.evaluate((el) => getComputedStyle(el).backgroundColor);
  expect(clearCacheHoverBg, 'clear-cache-button hover bg must not be the hardcoded Jeskai blue').not.toMatch(
    BLUE_LEAK_RE,
  );
});

// ── Bug C: bucket-toggle accessible name + borderless chevron ──────────────────────────────

test('analysis-questions bucket toggle has an accessible name and no bordered pill', async ({ page, baseURL }) => {
  await setTheme(page, 'site.css', baseURL);
  await gotoStep2(page);

  const toggle = page.locator('.prompt-question-bucket__toggle').first();
  await expect(toggle).toBeVisible();

  const ariaLabel = await toggle.getAttribute('aria-label');
  expect(ariaLabel, 'bucket toggle must have a non-empty aria-label').toBeTruthy();
  expect((ariaLabel ?? '').trim().length).toBeGreaterThan(0);

  const borderWidth = await toggle.evaluate((el) => getComputedStyle(el).borderWidth);
  expect(borderWidth, 'bucket toggle must be borderless (no standalone pill)').toBe('0px');
});

// ── Back-to-top icon: chevron stroke must contrast its button background ────────────────────
//
// Azorius flips .back-to-top-button to a near-white background but the base icon stroke is
// var(--on-accent)=#fff — a white chevron on a white button is invisible (shipped bug). Any
// theme that repaints the button background must keep the chevron readable. This guards a
// representative set: the fixed bug (azorius), two dark forks that also repaint the bg
// (jund, sultai), and the base dark path (Classic/site.css).
const backToTopThemes = [
  { name: 'azorius (light @import — the shipped bug)', cookie: 'site-azorius.css' },
  { name: 'jund (dark fork bg)', cookie: 'site-jund.css' },
  { name: 'sultai (dark fork bg)', cookie: 'site-sultai.css' },
  { name: 'Classic (explicit site.css — base dark button)', cookie: 'site.css' },
];

for (const theme of backToTopThemes) {
  test(`back-to-top chevron contrasts its button background (${theme.name})`, async ({ page, baseURL }) => {
    await setTheme(page, theme.cookie, baseURL);
    const response = await page.goto('/deck-analysis');
    expect(response?.ok()).toBeTruthy();

    // The button is fixed + hidden until scroll, but its computed colors resolve regardless.
    // The icon has three <path>s (all share the stroke) — take the first to stay single-match.
    const buttonBg = await page
      .locator('#back-to-top-button')
      .evaluate((el) => getComputedStyle(el).backgroundColor);
    const iconStroke = await page
      .locator('.back-to-top-icon path')
      .first()
      .evaluate((el) => getComputedStyle(el).stroke);

    // >=3:1 is the WCAG non-text (UI component/graphical object) minimum. White-on-white is
    // ~1.0, so this fails loudly on the pre-fix Azorius regression.
    const ratio = contrastRatio(buttonBg, iconStroke);
    expect(
      ratio,
      `chevron stroke ${iconStroke} vs button bg ${buttonBg} must meet >=3:1`,
    ).toBeGreaterThanOrEqual(3);
  });
}
