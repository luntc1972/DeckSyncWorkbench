import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { withToolEnabled } from './support/admin-tools';
import { uiDesignDir } from './support/ui-design-dir';
import { assignWithKeyboard } from './support/deck-modules-assign';

const winotaDeck = readFileSync(join(__dirname, 'fixtures', 'winota-cedh.txt'), 'utf8').trim();

test.describe.configure({ mode: 'serial' });
test.setTimeout(120_000);
withToolEnabled('Deck Modules');

test('analyzes a compiled configuration', async ({ page }, testInfo) => {
  const response = await page.goto('/deck-modules');
  expect(response?.ok(), '/deck-modules should return 200 with flag ON').toBeTruthy();

  await page.locator('[data-deck-modules-source]').fill(winotaDeck);
  const importResponse = page.waitForResponse('/deck-modules/import');
  await page.getByRole('button', { name: 'Import deck' }).click();
  expect((await importResponse).status()).toBe(200);

  await page.locator('[data-deck-modules-name]').fill('Winota Combat');
  await page.locator('[data-deck-modules-profile]').selectOption('Cedh');
  await page.locator('[data-deck-modules-plan]').fill('Trigger Winota early and pressure every combat.');
  await page.locator('[data-deck-modules-add-alternative]').click();
  await assignWithKeyboard(page, 'unassigned', 'core');
  await assignWithKeyboard(page, 'unassigned', 'strategy');
  await assignWithKeyboard(page, 'unassigned', 'mana');

  await page.locator('[data-deck-modules-name]').fill('Winota Stax');
  await page.locator('[data-deck-modules-profile]').selectOption('Cedh');
  await page.locator('[data-deck-modules-plan]').fill('Lock opponents while Winota supplies pressure.');
  await page.locator('[data-deck-modules-add-alternative]').click();
  await assignWithKeyboard(page, 'unassigned', 'strategy');
  await assignWithKeyboard(page, 'unassigned', 'mana');

  await page.locator('[data-deck-modules-compile]').click();
  await expect(page.locator('.deck-modules__report')).toBeVisible();

  const analysisResponse = page.waitForResponse('/deck-modules/analyze');
  await page.locator('[data-deck-modules-analyze]').click();
  expect((await analysisResponse).status()).toBe(200);
  await expect(page.locator('[data-deck-modules-analysis]')).toBeVisible();
  await expect(page.locator('[data-deck-modules-analysis-health]')).not.toBeEmpty();
  await expect(page.locator('[data-deck-modules-bracket]')).not.toBeEmpty();
  const handoff = page.locator('[data-deck-modules-manabase-handoff]');
  await expect(handoff).toBeVisible();
  await handoff.click();
  await expect(page).toHaveURL(/\/manabase\?handoff=/);
  await expect(page.getByRole('heading', { name: /Mana Base/ })).toBeVisible();
  await page.goBack();
  await expect(page.locator('[data-deck-modules-analysis]')).toBeVisible();
  // "Winota Stax" is the last alternative added, so it is the selected/active one at compile time.
  await expect(page.locator('[data-deck-modules-declared-plan]')).toHaveText('Lock opponents while Winota supplies pressure.');

  await page.getByRole('button', { name: /Winota Combat/ }).click();
  const secondAnalysisResponse = page.waitForResponse('/deck-modules/analyze');
  await page.locator('[data-deck-modules-analyze]').click();
  expect((await secondAnalysisResponse).status()).toBe(200);

  await page.locator('[data-deck-modules-compare-reference]').selectOption({ label: 'Winota Combat' });
  await page.locator('[data-deck-modules-compare-other]').selectOption({ label: 'Winota Stax' });
  const comparisonResponse = page.waitForResponse(response => response.url().endsWith('/deck-modules/compare') && response.status() === 200);
  await page.locator('[data-deck-modules-compare]').click();
  expect((await comparisonResponse).status()).toBe(200);
  const comparison = page.locator('[data-deck-modules-comparison-table]');
  await expect(comparison).toBeVisible();
  await expect(comparison.locator('thead th')).toHaveCount(3);
  await expect(comparison.locator('tbody tr').filter({ hasText: 'Land count' }).locator('td').first()).not.toBeEmpty();

  await page.screenshot({ path: join(uiDesignDir('deck-modules-analysis'), `${testInfo.title}.png`), fullPage: true });
});
