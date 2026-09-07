import { expect, test, type Page } from '@playwright/test';
import { join } from 'node:path';
import { acquireAdminLockForTest, releaseAdminLockForTest } from './support/admin-lock';
import { setToolEnabled } from './support/admin-tools';
import { expandCutLabSection, expandMobileCollapsibles } from './support/cut-lab-mobile-collapse';
import { clickManabasePillRadio } from './support/manabase-pill';
import { uiDesignDir } from './support/ui-design-dir';

import { resolveE2EPort } from './support/e2e-port';

const baseUrl = `http://localhost:${resolveE2EPort()}`;
const screenshotDir = uiDesignDir('cut-lab');

const themes = [
  { name: 'classic', cookie: 'site.css' },
  { name: 'azorius', cookie: 'site-azorius.css' },
  { name: 'nyx', cookie: 'site-nyx.css' },
] as const;

const viewports = [
  { name: 'desktop', width: 1440, height: 2200 },
  { name: 'mobile', width: 430, height: 2200 },
] as const;

const oversizedPool = [
  'Commander',
  '1 Zur the Enchanter',
  '',
  'Deck',
  '36 Plains',
  '36 Island',
  '20 Swamp',
  '1 Sol Ring',
  '1 Arcane Signet',
  '1 Fellwar Stone',
  '1 Mystic Remora',
  '1 Rhystic Study',
  '1 Swords to Plowshares',
  '1 Path to Exile',
  '1 Counterspell',
  '1 Dovin\'s Veto',
  '1 Demonic Tutor',
  '1 Enlightened Tutor',
  '1 Command Tower',
  '1 Exotic Orchard',
].join('\n');

type LockHandle = Awaited<ReturnType<typeof acquireAdminLockForTest>>;

let heldLock: LockHandle | null = null;

test.describe.configure({ mode: 'serial' });

const importPool = async (page: Page): Promise<void> => {
  await page.goto('/cut-lab');
  await expect(page.locator('h1')).toHaveText('Cut Lab');
  await page.locator('#cut-lab-input-source').selectOption('PasteText');
  await page.locator('#cut-lab-deck-text').fill(oversizedPool);
  await clickManabasePillRadio(page, 'Bracket', '4');
  await clickManabasePillRadio(page, 'PlayExperience', 'Focused');
  await page.getByRole('button', { name: 'Import pool' }).click();

  await expandCutLabSection(page, 'cut-lab-section-lock-pool');
  await expect(page.getByRole('heading', { name: 'Lock your pool' })).toBeVisible({ timeout: 30_000 });
  await expandCutLabSection(page, 'cut-lab-section-competes');
  await page.locator('details.cutlab-role-group').filter({ hasText: 'Lands' }).locator(':scope > summary').click();
  await expect(page.locator('[data-cut-lab-lock-role="lands"]')).toBeVisible();
  await expect(page.locator('tr[data-cut-lab-card="Zur the Enchanter"]')).toHaveAttribute('data-cut-lab-commander', 'true');
};

test.beforeEach(async ({ page }) => {
  heldLock = await acquireAdminLockForTest(page);
  await setToolEnabled(page, 'Cut Lab', true);
});

test.afterEach(async () => {
  await releaseAdminLockForTest(heldLock);
  heldLock = null;
});

test('/cut-lab renders the intake, intent controls, and hidden state field when the flag is ON', async ({ page }) => {
  const response = await page.goto('/cut-lab');
  expect(response?.ok(), '/cut-lab should return 200 with flag ON').toBeTruthy();

  const mainStateInput = page.locator('form[data-cache-key="cut-lab"] input[name="CutLabStateJson"]');
  await expect(page.locator('h1')).toHaveText('Cut Lab');
  await expect(page.locator('form[action="/cut-lab"]').first()).toHaveAttribute('data-cache-key', 'cut-lab');
  await expect(mainStateInput).toHaveCount(1);
  await expect(page.locator('#cut-lab-input-source')).toBeVisible();
  await expect(page.locator('#cut-lab-deck-url')).toBeVisible();
  await expect(page.locator('input[name="Bracket"][value="1"]')).toBeVisible();
  await expect(page.locator('input[name="PlayExperience"][value="Focused"]')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'No pool imported yet' })).toBeVisible();
});

test('imports a pool, locks lands and a package, then preserves those edits across a resubmit', async ({ page }) => {
  await importPool(page);
  await expandMobileCollapsibles(page);

  const landsLockButton = page.locator('[data-cut-lab-lock-role="lands"]');
  await expect(landsLockButton).toHaveAttribute('aria-pressed', 'false');
  await landsLockButton.click();
  await expect(page.locator('tr[data-cut-lab-card="Plains"] input[data-cut-lab-lock-card]')).toBeChecked();
  await expect(page.locator('tr[data-cut-lab-card="Island"] input[data-cut-lab-lock-card]')).toBeChecked();
  await expect(landsLockButton).toHaveAttribute('aria-pressed', 'true');

  const solRingPackageSelect = page.locator('select[data-cut-lab-package-card="Sol Ring"]');
  await solRingPackageSelect.selectOption('__new__');
  await page.locator('[data-cut-lab-new-package-input]').fill('Fast mana');
  await page.locator('[data-cut-lab-new-package-save]').click();
  await page.locator('select[data-cut-lab-package-card="Arcane Signet"]').selectOption({ label: 'Fast mana' });
  await page.locator('[data-cut-lab-package-id]').filter({ hasText: 'Fast mana' }).locator('input[data-cut-lab-package-toggle]').check();

  const hiddenState = page.locator('form[data-cache-key="cut-lab"] input[name="CutLabStateJson"]');
  await expect(hiddenState).toHaveValue(/"commander":"Zur the Enchanter"/);
  await expect(hiddenState).toHaveValue(/"name":"Plains".*"isLocked":true/);
  await expect(hiddenState).toHaveValue(/"name":"Fast mana"/);

  await page.locator('details.cutlab-intake > summary').click();
  await page.getByRole('button', { name: 'Import pool' }).click();
  await expandCutLabSection(page, 'cut-lab-section-lock-pool');
  await expect(page.getByRole('heading', { name: 'Lock your pool' })).toBeVisible({ timeout: 30_000 });
  await expandMobileCollapsibles(page);

  await expect(page.locator('[data-cut-lab-package-id]').filter({ hasText: 'Fast mana' })).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-cut-lab-package-id]').filter({ hasText: 'Fast mana' }).locator('input[data-cut-lab-package-toggle]')).toBeChecked();
  await expect(page.locator('tr[data-cut-lab-card="Plains"] input[data-cut-lab-lock-card]')).toBeChecked();
  await expect(page.locator('tr[data-cut-lab-card="Island"] input[data-cut-lab-lock-card]')).toBeChecked();
  await expect(page.locator('tr[data-cut-lab-card="Zur the Enchanter"] .cutlab-lock-badge--commander')).toContainText('Commander · Always locked');
  await expect(page.locator('tr[data-cut-lab-card="Zur the Enchanter"] input[data-cut-lab-lock-card]')).toBeChecked();
  await expect(page.locator('tr[data-cut-lab-card="Zur the Enchanter"] input[data-cut-lab-lock-card]')).toBeDisabled();
  await expect(page.locator('[data-cut-lab-lock-count]')).toHaveText(/^\d+ cards in pool · \d+ locked$/);
  await expect(page.getByText('No banned cards found')).toBeVisible();
});

test('captures imported Cut Lab screenshots across themes and viewports', async ({ page }) => {
  for (const viewport of viewports) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });

    for (const theme of themes) {
      await page.context().clearCookies();
      await page.context().addCookies([{ name: 'deckflow-theme', value: theme.cookie, url: baseUrl }]);
      await importPool(page);

      const screenshotPath = join(
        screenshotDir,
        `cut-lab-${theme.name}-${viewport.name}-${test.info().project.name}.png`,
      );
      await page.screenshot({ path: screenshotPath, fullPage: true });
    }
  }
});

test('with tool.cut-lab.enabled OFF, /cut-lab returns 404 and the Home tile is absent', async ({ page }) => {
  await setToolEnabled(page, 'Cut Lab', false);

  const response = await page.goto('/cut-lab');
  expect(response?.status(), '/cut-lab should be 404 with flag OFF').toBe(404);

  await page.goto('/');
  await expect(page.locator('.hub-card[href$="/cut-lab"]')).toHaveCount(0);
});
