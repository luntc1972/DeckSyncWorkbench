import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { withToolEnabled } from './support/admin-tools';
import { assignWithKeyboard } from './support/deck-modules-assign';

const winotaDeck = readFileSync(join(__dirname, 'fixtures', 'winota-cedh.txt'), 'utf8').trim();

test.describe.configure({ mode: 'serial' });
test.setTimeout(120_000);
withToolEnabled('Deck Modules');

test('imports, assigns, compiles, and remains usable at the current viewport', async ({ page }, testInfo) => {
  const response = await page.goto('/deck-modules');
  expect(response?.ok(), '/deck-modules should return 200 with flag ON').toBeTruthy();

  await expect(page.getByRole('heading', { name: 'Deck Modules' })).toBeVisible();
  await expect(page.locator('.tool-nav__link.is-active')).toHaveText('Deck Modules');
  await expect(page.locator('.deck-modules__report')).toBeHidden();
  await expect(page.locator('[data-deck-modules-copy]')).toBeDisabled();
  await expect(page.locator('[data-deck-modules-export]')).toBeDisabled();
  await expect(page.getByRole('button', { name: /share|save|project/i })).toHaveCount(0);

  await page.locator('[data-deck-modules-source]').fill(winotaDeck);
  const importResponse = page.waitForResponse('/deck-modules/import');
  await page.getByRole('button', { name: 'Import deck' }).click();
  const imported = await importResponse;
  expect(imported.status()).toBe(200);
  await expect(page.locator('[data-deck-modules-entries="unassigned"] [data-deck-modules-select]')).not.toHaveCount(0);

  await page.locator('[data-deck-modules-name]').fill('Winota Combat');
  await page.locator('[data-deck-modules-profile]').selectOption('Cedh');
  await page.locator('[data-deck-modules-plan]').fill('Trigger Winota early and pressure every combat.');
  await page.locator('[data-deck-modules-add-alternative]').click();
  await expect(page.locator('[data-deck-modules-alternative]')).toHaveCount(1);
  await expect(page.locator('[data-deck-modules-summary-profile]')).toHaveText('cEDH');
  await expect(page.locator('[data-deck-modules-summary-plan]')).toContainText('Trigger Winota early');

  await assignWithKeyboard(page, 'unassigned', 'core');
  await assignWithKeyboard(page, 'unassigned', 'strategy');
  await assignWithKeyboard(page, 'unassigned', 'mana');
  await expect(page.locator('[data-deck-modules-live]')).toContainText('Moved 1 card entries');

  await page.locator('[data-deck-modules-name]').fill('Winota Stax');
  await page.locator('[data-deck-modules-profile]').selectOption('Cedh');
  await page.locator('[data-deck-modules-plan]').fill('Lock opponents while Winota supplies pressure.');
  await page.locator('[data-deck-modules-add-alternative]').click();
  await expect(page.locator('[data-deck-modules-alternative]')).toHaveCount(2);
  await expect(page.locator('[data-deck-modules-active-name]')).toHaveText('Winota Stax');

  await assignWithKeyboard(page, 'unassigned', 'strategy');
  await assignWithKeyboard(page, 'unassigned', 'mana');
  await expect(page.locator('[data-deck-modules-balance]')).toHaveText('Alternatives are balanced.');
  await expect(page.locator('[data-deck-modules-compile]')).toBeEnabled();

  const outline = await page.locator('[data-deck-modules-move="unassigned:core"]').evaluate(element => {
    const style = getComputedStyle(element);
    return `${style.outlineStyle}:${style.outlineWidth}`;
  });
  expect(outline).not.toBe('none:0px');

  await page.locator('[data-deck-modules-compile]').click();
  await expect(page.locator('.deck-modules__report')).toBeVisible();
  await expect(page.locator('[data-deck-modules-report-total]')).toHaveText('3');
  await expect(page.locator('[data-deck-modules-diagnostics] li')).toContainText(['The imported command zone is empty.', 'Compiled deck has 3 cards; a Commander deck needs exactly 100.']);
  await expect(page.locator('[data-deck-modules-diagnostics]')).not.toContainText('[object Object]');
  await expect(page.locator('[data-deck-modules-report-strategy]')).toHaveText('Winota Stax');
  await expect(page.locator('[data-deck-modules-report-mana]')).toHaveText('Winota Stax Mana Support');
  await expect(page.locator('[data-deck-modules-swap="add"]')).toBeAttached();
  await expect(page.locator('[data-deck-modules-swap="reset"]')).toBeAttached();
  await expect(page.locator('[data-deck-modules-swap="remove"] li')).not.toHaveCount(0);
  await expect(page.locator('[data-deck-modules-copy]')).toBeEnabled();
  await expect(page.locator('[data-deck-modules-export]')).toBeEnabled();

  await page.screenshot({ path: testInfo.outputPath(`deck-modules-${testInfo.project.name}.png`), fullPage: true });
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);
  expect(overflow).toBeTruthy();

  if (testInfo.project.name === 'chromium-mobile') {
    await expect(page.locator('.deck-modules__assignment').first()).toBeVisible();
    await expect(page.locator('[data-deck-modules-active-name]')).toBeVisible();
    await expect(page.locator('[data-deck-modules-compile]')).toBeVisible();
  }
});
