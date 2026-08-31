import { expect, test, type Page } from '@playwright/test';

// Theme + responsive guards for the three quick UI changes:
//   - deck-primer workflow step tabs (now click-to-scroll)
//   - manabase hero (short lede + "How it works" disclosure)
//   - manabase "Load deck & detect costs" action sitting beside Analyze
//
// Layout CSS lives in site-common.css (theme-token-independent), so a few
// representative themes — a light default, a dark fork, a guild fork — catch
// overflow/layout regressions without running all 24. Each test also runs under
// both the desktop and mobile Playwright projects, so this is the desktop+mobile
// ×themes "no horizontal overflow" coverage the project rule requires.

import { resolveE2EPort } from './support/e2e-port';

const baseUrl = `http://localhost:${resolveE2EPort()}`;
const themes = ['site.css', 'site-nyx.css', 'site-azorius.css'] as const;

async function setTheme(page: Page, theme: string): Promise<void> {
  await page.context().addCookies([{ name: 'deckflow-theme', value: theme, url: baseUrl }]);
}

async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const { scrollWidth, clientWidth } = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
  }));
  // 1px slack for sub-pixel rounding.
  expect(
    scrollWidth,
    `horizontal overflow: page scrollWidth ${scrollWidth} exceeds viewport ${clientWidth}`,
  ).toBeLessThanOrEqual(clientWidth + 1);
}

for (const theme of themes) {
  test(`deck primer step tabs render without overflow [${theme}]`, async ({ page }) => {
    await setTheme(page, theme);
    const response = await page.goto('/deck-primer');
    expect(response?.ok()).toBeTruthy();

    await expect(page.locator('[data-primer-show-step]')).toHaveCount(3);
    await expectNoHorizontalOverflow(page);
  });

  test(`manabase hero + load/analyze actions render without overflow [${theme}]`, async ({ page }) => {
    await setTheme(page, theme);
    const response = await page.goto('/manabase');
    expect(response?.ok()).toBeTruthy();

    // Short hero with the collapsed "How it works" disclosure.
    await expect(page.locator('.hero-detail')).toBeVisible();
    // Both workflow actions present (Load step before Analyze).
    await expect(page.locator('.manabase-load-button')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Analyze Mana Base' })).toBeVisible();

    await expectNoHorizontalOverflow(page);
  });
}
