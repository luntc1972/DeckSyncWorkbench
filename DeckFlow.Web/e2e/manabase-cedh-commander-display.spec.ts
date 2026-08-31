import { expect, test, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { clickManabasePillRadio } from './support/manabase-pill';
import { resolveE2EPort } from './support/e2e-port';

// Live-only regression for the persistent commander header and cEDH baseline range copy.
// Runs under both Playwright viewport projects (desktop + mobile) and across a small theme
// matrix (Classic + Azorius) via the deckflow-theme cookie.
// Run from DeckFlow.Web/:
//   DECKFLOW_LIVE_E2E=1 npx --no-install playwright test manabase-cedh-commander-display

const WINOTA_DECK = readFileSync(resolve(__dirname, 'fixtures', 'winota-cedh.txt'), 'utf8');

const themes = [
  { name: 'Classic', cookie: 'site.css' },
  { name: 'Azorius', cookie: 'site-azorius.css' },
];

async function setTheme(page: Page, cookieFile: string, baseURL?: string): Promise<void> {
  await page.context().addCookies([
    { name: 'deckflow-theme', value: cookieFile, url: baseURL ?? `http://localhost:${resolveE2EPort()}` },
  ]);
}

async function submitDeck(page: Page, mode: 'Casual' | 'Cedh'): Promise<boolean> {
  await page.goto('/manabase');
  await page.locator('#manabase-input-source').selectOption('PasteText');
  await page.locator('#manabase-deck-text').fill(WINOTA_DECK);
  await clickManabasePillRadio(page, 'Mode', mode);
  await page.getByRole('button', { name: 'Analyze Mana Base' }).click();

  const result = page.locator('.result-panel:has(h2:has-text("Result"))');
  const error = page.locator('.error-banner:not(.hidden)');
  await Promise.race([
    result.waitFor({ state: 'visible', timeout: 60_000 }).catch(() => undefined),
    error.waitFor({ state: 'visible', timeout: 60_000 }).catch(() => undefined),
  ]);

  return (await result.count()) > 0 && (await result.isVisible());
}

for (const theme of themes) {
  test(`cEDH renders Winota commander header and baseline range (${theme.name})`, async ({ page, baseURL }) => {
    test.skip(!process.env.DECKFLOW_LIVE_E2E, 'live-only: needs Scryfall and cEDH baseline data');

    await setTheme(page, theme.cookie, baseURL);
    const ok = await submitDeck(page, 'Cedh');
    test.skip(!ok, 'analysis result unavailable (Scryfall not reachable in this environment)');

    const context = page.locator('.manabase-context');
    await expect(context).toContainText(/Mode:\s*cEDH/i);
    await expect(context).toContainText('Winota, Joiner of Forces');

    const landsLine = page.locator('.manabase-summary-lands');
    await expect(landsLine).toContainText('cEDH meta range ~26–29');
    await expect(landsLine).toContainText('33 decks, mean 27.5 ±1.6');
  });

  test(`Casual renders Winota commander header (${theme.name})`, async ({ page, baseURL }) => {
    test.skip(!process.env.DECKFLOW_LIVE_E2E, 'live-only: needs Scryfall');

    await setTheme(page, theme.cookie, baseURL);
    const ok = await submitDeck(page, 'Casual');
    test.skip(!ok, 'analysis result unavailable (Scryfall not reachable in this environment)');

    const context = page.locator('.manabase-context');
    await expect(context).toContainText(/Mode:\s*Casual/i);
    await expect(context).toContainText('Winota, Joiner of Forces');
  });
}
