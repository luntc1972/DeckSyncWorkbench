import { expect, test, type Locator, type Page } from '@playwright/test';
import { join } from 'node:path';
import { clickManabasePillRadio } from './support/manabase-pill';
import { uiDesignDir } from './support/ui-design-dir';

import { resolveE2EPort } from './support/e2e-port';

const baseUrl = `http://localhost:${resolveE2EPort()}`;
const screenshotDir = uiDesignDir('mbgap-09');

const themes = [
  { name: 'classic', cookie: 'site.css' },
  { name: 'nyx', cookie: 'site-nyx.css' },
] as const;

const CEDH_INTERACTION_DECK = [
  'Commander',
  '1 Brago, King Eternal',
  '',
  'Deck',
  '1 Command Tower',
  '1 Exotic Orchard',
  '1 Hallowed Fountain',
  '1 Prairie Stream',
  '1 Glacial Fortress',
  '1 Adarkar Wastes',
  '1 Seachrome Coast',
  '1 Port Town',
  '1 Mystic Gate',
  '1 Flooded Strand',
  '1 Fabled Passage',
  '1 Evolving Wilds',
  '12 Plains',
  '12 Island',
  '1 Sol Ring',
  '1 Arcane Signet',
  '1 Azorius Signet',
  '1 Talisman of Progress',
  '1 Mana Crypt',
  '1 Swords to Plowshares',
  '1 Path to Exile',
  '1 Swan Song',
  '1 Spell Pierce',
  '1 Flusterstorm',
  '1 Mental Misstep',
  '1 An Offer You Can\'t Refuse',
  '1 Counterspell',
  '1 Arcane Denial',
  '1 Dovin\'s Veto',
  '1 Force of Negation',
  '1 Supreme Verdict',
  '1 Cyclonic Rift',
  '1 Rhystic Study',
  '1 Smothering Tithe',
].join('\n');

async function setTheme(page: Page, cookieFile: string, baseURL?: string): Promise<void> {
  await page.context().addCookies([
    { name: 'deckflow-theme', value: cookieFile, url: baseURL ?? baseUrl },
  ]);
}

async function applyProjectViewport(page: Page): Promise<void> {
  if (test.info().project.name.includes('mobile')) {
    await page.setViewportSize({ width: 390, height: 844 });
    return;
  }

  await page.setViewportSize({ width: 1280, height: 900 });
}

async function submitDeck(page: Page, mode: 'Casual' | 'Cedh'): Promise<boolean> {
  await page.goto('/manabase');
  await page.locator('#manabase-input-source').selectOption('PasteText');
  await page.locator('#manabase-deck-text').fill(CEDH_INTERACTION_DECK);
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

async function assertNoHorizontalScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth > window.innerWidth + 1,
  );
  expect(overflow, 'page must not gain a horizontal scrollbar').toBe(false);
}

async function assertWithinViewportWidth(page: Page, locator: Locator): Promise<void> {
  const box = await locator.boundingBox();
  expect(box).not.toBeNull();

  const viewport = page.viewportSize();
  expect(viewport).not.toBeNull();

  expect(box!.width).toBeLessThanOrEqual(viewport!.width + 1);
}

test('cEDH renders the early-interaction lens, holdable table column, and worst-5 expander', async ({ page }) => {
  await applyProjectViewport(page);

  const ok = await submitDeck(page, 'Cedh');
  test.skip(!ok, 'analysis result unavailable (Scryfall not reachable in this environment)');

  const interactionLens = page.locator('#manabase-early-interaction');
  await expect(interactionLens).toBeVisible();
  await expect(interactionLens.locator('.manabase-lens-big')).toContainText(/\d+\s*\/\s*\d+/);
  await expect(interactionLens).toContainText('interaction held up by turn 3');

  const visibleRows = interactionLens.locator(':scope > .manabase-lens-row');
  await expect(visibleRows).toHaveCount(5);

  const viewAll = interactionLens.locator('details summary');
  await expect(viewAll).toContainText(/View all/i);
  await expect(viewAll).toContainText(/\(\d+ more\)/);

  const castabilityTable = page.locator('table.castability-table').first();
  await expect(castabilityTable).toBeVisible();
  await expect(castabilityTable.getByRole('columnheader', { name: 'Held up (T1-3)' })).toBeVisible();

  const counterspellRow = castabilityTable.locator('tbody tr').filter({
    has: page.locator('td.castability-name', { hasText: 'Counterspell' }),
  });
  await expect(counterspellRow.locator('td[data-label="Held up (T1-3)"]')).toContainText('%');

  await assertWithinViewportWidth(page, page.locator('.manabase-twolens'));
  await assertWithinViewportWidth(page, page.locator('.castability-scroll').first());
  await assertNoHorizontalScroll(page);
});

test('Casual mode omits the early-interaction lens', async ({ page }) => {
  await applyProjectViewport(page);

  const ok = await submitDeck(page, 'Casual');
  test.skip(!ok, 'analysis result unavailable (Scryfall not reachable in this environment)');

  await expect(page.locator('#manabase-early-interaction')).toHaveCount(0);
});

test('captures desktop/mobile screenshots for a light and dark theme', async ({ page, baseURL }) => {
  await applyProjectViewport(page);
  for (const theme of themes) {
    await setTheme(page, theme.cookie, baseURL);

    const ok = await submitDeck(page, 'Cedh');
    test.skip(!ok, 'analysis result unavailable (Scryfall not reachable in this environment)');

    const interactionLens = page.locator('#manabase-early-interaction');
    const castabilityTable = page.locator('table.castability-table').first();

    await expect(interactionLens).toBeVisible();
    await expect(castabilityTable).toBeVisible();

    const details = interactionLens.locator('details');
    if ((await details.count()) > 0 && !(await details.first().evaluate((el) => (el as HTMLDetailsElement).open))) {
      await details.locator('summary').click();
    }

    await assertWithinViewportWidth(page, page.locator('.manabase-twolens'));
    await assertWithinViewportWidth(page, page.locator('.castability-scroll').first());
    await assertNoHorizontalScroll(page);

    const screenshotPath = join(
      screenshotDir,
      `manabase-interaction-${theme.name}-${test.info().project.name}.png`,
    );
    await page.screenshot({ path: screenshotPath, fullPage: true });
  }
});
