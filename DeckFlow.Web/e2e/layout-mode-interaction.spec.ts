import { expect, test, type Page } from '@playwright/test';
import { resolveE2EPort } from './support/e2e-port';

// Phase 86 (86-05): interaction-outcome coverage for Bug D — the layout picker
// (Full/Compact/Advanced -> guided/focused/expert) was wired correctly but its CSS effect
// was imperceptible on the empty Step-1/2 landing (hidden elements were sparse/optional
// text), so the existing e2e suite (which only asserted the data-attribute flipped, not any
// visual/layout OUTCOME) could not catch it. This spec asserts the MEASURABLE delta plan
// 86-04 introduced, keyed to the always-rendered `.prompt-instructions` element, plus the
// guided/Full "positive style" (accent left-border) instead of a do-nothing default.
//
// Runs under both Playwright projects (chromium-desktop 1280, chromium-mobile 390) — the
// mobile media query (site-mobile.css:278) forces `.prompt-page-toolbar.desktop-only` back
// to visible on narrow viewports, so the layout-picker buttons are reachable on both.

const themes = [
  { name: 'Classic (site.css)', cookie: 'site.css' },
  { name: 'dimir (dark)', cookie: 'site-dimir.css' },
];

async function setTheme(page: Page, cookieFile: string, baseURL?: string): Promise<void> {
  await page.context().addCookies([
    { name: 'deckflow-theme', value: cookieFile, url: baseURL ?? `http://localhost:${resolveE2EPort()}` },
  ]);
}

async function gotoStep2(page: Page): Promise<void> {
  const response = await page.goto('/deck-analysis');
  expect(response?.ok()).toBeTruthy();
  await page.locator('[data-prompt-show-step="2"][role="tab"]').click();
}

// Resolve a CSS custom property to its computed color via a throwaway probe element.
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

for (const theme of themes) {
  test(`layout picker modes produce a measurable delta + guided keeps its positive style (${theme.name})`, async ({
    page,
    baseURL,
  }) => {
    await setTheme(page, theme.cookie, baseURL);
    await gotoStep2(page);

    // Step 2 and Step 4 both render a `.prompt-instructions` block; scope to Step 2's panel
    // (the one we just navigated to) so the locator is unambiguous.
    const instructions = page.locator('#prompt-step-panel-2 .prompt-instructions');
    await expect(instructions).toBeVisible();

    // guided (Full) is the server-rendered default, but the client JS overrides the INITIAL
    // mode to 'focused' on narrow/mobile viewports (mobile UI phase 1, unrelated to Bug D).
    // Click guided explicitly so this test measures the guided state deterministically on
    // both projects rather than relying on which mode happens to be the page's initial state.
    const guidedButton = page.locator('[data-prompt-ui-mode-button="guided"]');
    await expect(guidedButton).toBeVisible();
    await guidedButton.click();
    await expect(guidedButton).toHaveClass(/is-active/);

    // Bug D also requires guided to carry a POSITIVE accent marker, not a do-nothing default:
    // an accent-colored left border (site.css/fork `border-left: 4px solid var(--accent)`).
    const guidedBorderLeftWidth = await instructions.evaluate((el) => getComputedStyle(el).borderLeftWidth);
    const guidedBorderLeftColor = await instructions.evaluate((el) => getComputedStyle(el).borderLeftColor);
    const accentColor = await resolveCustomPropertyColor(page, '--accent');

    expect(parseFloat(guidedBorderLeftWidth), 'guided .prompt-instructions must show a visible left border').toBeGreaterThan(0);
    expect(guidedBorderLeftColor, 'guided left-border color must be the theme accent, not a neutral default').toBe(
      accentColor,
    );

    const guidedBox = await instructions.boundingBox();
    expect(guidedBox, 'guided .prompt-instructions must render with a real box').not.toBeNull();

    // focused (Compact): a measurable shrink vs guided (tighter padding), not just hidden text.
    const focusedButton = page.locator('[data-prompt-ui-mode-button="focused"]');
    await expect(focusedButton).toBeVisible();
    await focusedButton.click();
    await expect(focusedButton).toHaveClass(/is-active/);

    const focusedBox = await instructions.boundingBox();
    expect(focusedBox, 'focused .prompt-instructions must still be visible').not.toBeNull();
    expect(
      focusedBox!.height,
      'focused mode must measurably shrink .prompt-instructions vs guided',
    ).toBeLessThan(guidedBox!.height);

    // expert (Advanced): a guaranteed further delta — full collapse of the always-present
    // anchor element, so the mode is perceptible even on the empty Step-1/2 landing.
    const expertButton = page.locator('[data-prompt-ui-mode-button="expert"]');
    await expect(expertButton).toBeVisible();
    await expertButton.click();
    await expect(expertButton).toHaveClass(/is-active/);
    await expect(instructions).toBeHidden();
  });
}
