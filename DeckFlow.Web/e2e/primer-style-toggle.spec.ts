import { expect, test, type Page } from '@playwright/test';
import { clickManabasePillRadio } from './support/manabase-pill';

import { resolveE2EPort } from './support/e2e-port';

const baseUrl = `http://localhost:${resolveE2EPort()}`;

async function setTheme(page: Page, theme: string): Promise<void> {
  await page.context().addCookies([{ name: 'deckflow-theme', value: theme, url: baseUrl }]);
}

test('deck primer primer-style radios render, default to Standard, and allow rich selection', async ({ page }) => {
  await setTheme(page, 'site-azorius.css');

  const response = await page.goto('/deck-primer');
  expect(response?.ok()).toBeTruthy();

  const standardRadio = page.getByRole('radio', { name: 'Standard' });
  const richRadio = page.getByRole('radio', { name: 'Moxfield-style rich' });
  const fullCedhRadio = page.getByRole('radio', { name: 'Full cEDH primer' });

  await expect(standardRadio).toBeVisible();
  await expect(richRadio).toBeVisible();
  await expect(fullCedhRadio).toBeHidden();
  await expect(standardRadio).toBeChecked();
  await expect(richRadio).not.toBeChecked();
  await expect(page.getByText('Moxfield-style adds a clickable table of contents')).toBeVisible();

  await clickManabasePillRadio(page, 'PrimerStyle', 'MoxfieldRich');

  await expect(richRadio).toBeChecked();
  await expect(standardRadio).not.toBeChecked();

  const hasNoOverflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
  expect(hasNoOverflow).toBeTruthy();
});

test('full cedh primer radio is bracket-gated and falls back when leaving cedh', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await setTheme(page, 'site-selesnya.css');

  const response = await page.goto('/deck-primer');
  expect(response?.ok()).toBeTruthy();

  const bracketSelect = page.locator('select[name="TargetCommanderBracket"]');
  const standardRadio = page.getByRole('radio', { name: 'Standard' });
  const richRadio = page.getByRole('radio', { name: 'Moxfield-style rich' });
  const fullCedhRadio = page.getByRole('radio', { name: 'Full cEDH primer' });
  const fullCedhInput = page.locator('input[name="PrimerStyle"][value="FullCedh"]');

  await expect(standardRadio).toBeVisible();
  await expect(richRadio).toBeVisible();
  await expect(fullCedhRadio).toBeHidden();

  await bracketSelect.selectOption('cEDH');
  await expect(fullCedhRadio).toBeVisible();

  await clickManabasePillRadio(page, 'PrimerStyle', 'FullCedh');
  await expect(fullCedhRadio).toBeChecked();

  await bracketSelect.selectOption('Optimized');
  await expect(fullCedhRadio).toBeHidden();
  await expect(fullCedhInput).not.toBeChecked();
  await expect(richRadio).toBeChecked();

  const hasNoOverflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
  expect(hasNoOverflow).toBeTruthy();
});
