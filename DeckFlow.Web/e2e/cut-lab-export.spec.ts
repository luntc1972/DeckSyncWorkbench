import { expect, test, type Locator, type Page } from '@playwright/test';
import { acquireAdminLockForTest, releaseAdminLockForTest } from './support/admin-lock';
import { setToolEnabled } from './support/admin-tools';
import { clickManabasePillRadio } from './support/manabase-pill';

import { resolveE2EPort } from './support/e2e-port';

const baseUrl = `http://localhost:${resolveE2EPort()}`;

const oversizedPool = [
  'Commander',
  '1 Zur the Enchanter',
  '',
  'Deck',
  '35 Plains',
  '35 Island',
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
  await page.goto(`${baseUrl}/cut-lab`);
  await expect(page.locator('h1')).toHaveText('Cut Lab');
  await fillImportForm(page);
  await page.getByRole('button', { name: 'Import pool' }).click();

  await expect(page.getByRole('heading', { name: 'Lock your pool' })).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('tr[data-cut-lab-card="Zur the Enchanter"]')).toHaveAttribute('data-cut-lab-commander', 'true');
};

const fillImportForm = async (page: Page): Promise<void> => {
  await page.locator('#cut-lab-input-source').selectOption('PasteText');
  await page.locator('#cut-lab-deck-text').fill(oversizedPool);
  await page.locator('#cut-lab-primary-plan').fill('Protect the control shell, then trim to the cleanest Zur line.');
  await page.locator('#cut-lab-secondary-plan').fill('Keep the fast mana package intact.');
  await clickManabasePillRadio(page, 'Bracket', '4');
  await clickManabasePillRadio(page, 'PlayExperience', 'Focused');
};

const waitForCutRounds = async (page: Page): Promise<void> => {
  await expect(page.getByRole('heading', { name: 'Cut rounds' })).toBeVisible();
  await expect(page.locator('.cutlab-round-banner .cutlab-finding__heading')).toBeVisible();
  await expect(page.locator('.cutlab-proposal')).toBeVisible();
};

const getExportTab = (page: Page): Locator => page.locator('#cut-lab-step-tab-4');

const getExportPanel = (page: Page): Locator => page.locator('#cut-lab-step-panel-4');

const cutToTarget = async (page: Page): Promise<void> => {
  // At exactly 100 the sticky remaining counter is removed and the proposal shows the
  // terminal "You're at 100 cards" heading, so drive off that single heading element.
  // Each accepted cut removes that card from the working list, so the proposal heading
  // always advances to a distinct card (or the terminal heading) — wait on that change
  // to sync each accept, avoiding races with the async re-render.
  const headingLocator = page.locator('.cutlab-proposal__heading');
  for (let guard = 0; guard < 12; guard += 1) {
    // Let the previous decision's async re-render fully settle before clicking, so a
    // click never races an in-flight decide response (which drops or double-fires it).
    await page.waitForLoadState('networkidle');
    const heading = (await headingLocator.textContent())?.trim() ?? '';
    if (heading.includes('at 100 cards')) {
      return;
    }

    const accept = page.locator('.cutlab-proposal .cutlab-decision-btn--accept');
    if (await accept.count() === 0) {
      throw new Error(`No accept button and not at target (heading: "${heading}").`);
    }

    await accept.first().click();
    // The first decide pays cold JIT + cold sim-cache cost; allow headroom on slow CI.
    await expect(headingLocator).not.toHaveText(heading, { timeout: 30_000 });
  }

  throw new Error('Expected to reach exactly 100 cards within 12 accepted cuts.');
};

test.beforeEach(async ({ page }) => {
  heldLock = await acquireAdminLockForTest(page);
  await setToolEnabled(page, 'Cut Lab', true);
});

test.afterEach(async () => {
  await releaseAdminLockForTest(heldLock);
  heldLock = null;
});

test('keeps export disabled until the working list reaches exactly 100 cards', async ({ page }) => {
  await importPool(page);
  await waitForCutRounds(page);

  const exportTab = getExportTab(page);
  const exportPanel = getExportPanel(page);

  await expect(page.locator('[data-cut-lab-sticky-remaining]')).toContainText('4 to cut');
  await expect(exportTab).toBeDisabled();
  await expect(exportTab).toHaveAttribute('aria-disabled', 'true');
  await expect(exportPanel.locator('.cutlab-export__hint')).toContainText('Reach exactly 100 cards before copying the finished-list export.');
  await expect(exportPanel.locator('.cutlab-export__status').first()).toContainText('Card count = 104');
  await expect(exportPanel.locator('#cut-lab-export-moxfield-full')).toHaveCount(0);
  await expect(exportPanel.locator('#cut-lab-export-archidekt-full')).toHaveCount(0);
  await expect(exportPanel.locator('#cut-lab-export-moxfield-patch')).toHaveValue('');
  await expect(exportPanel.locator('#cut-lab-export-archidekt-patch')).toHaveValue('');
});

test('live-updates the export panel card count after an AJAX cut decision without building the export', async ({ page }) => {
  await importPool(page);
  await waitForCutRounds(page);

  const exportPanel = getExportPanel(page);
  const headingLocator = page.locator('.cutlab-proposal__heading');
  const initialHeading = (await headingLocator.textContent())?.trim() ?? '';

  await expect(exportPanel.locator('.cutlab-export__status').first()).toContainText('Card count = 104');
  await page.locator('.cutlab-proposal .cutlab-decision-btn--accept').first().click();

  await expect(headingLocator).not.toHaveText(initialHeading, { timeout: 30_000 });
  await expect(exportPanel.locator('.cutlab-export__status').first()).toContainText('Card count = 103');
  await expect(exportPanel.locator('.cutlab-export__status').first()).toContainText('Reach 100 cards to unlock the finished-list export.');
  await expect(exportPanel.locator('#cut-lab-export-moxfield-full')).toHaveCount(0);
  await expect(exportPanel.locator('#cut-lab-export-archidekt-full')).toHaveCount(0);
});

test('builds the export once accepted cuts reach the target count and shows the validation summary', async ({ page }) => {
  await importPool(page);
  await waitForCutRounds(page);
  await cutToTarget(page);

  const exportTab = getExportTab(page);
  const exportPanel = getExportPanel(page);

  await expect(page.locator('.cutlab-proposal__heading')).toHaveText('You\'re at 100 cards');
  await expect(exportTab).toBeEnabled();
  await expect(exportTab).toHaveAttribute('aria-disabled', 'false');
  await expect(exportPanel.locator('.cutlab-export__status').first()).toContainText('Card count = 100');
  // Before the export POST, only the card-count line is live-patched by JS. The
  // finished-list textareas and legality/verification status lines still require
  // the real export submission, so the patch text remains empty here.
  await expect(exportPanel.locator('#cut-lab-export-moxfield-full')).toHaveCount(0);
  await expect(exportPanel.locator('#cut-lab-export-archidekt-full')).toHaveCount(0);
  await expect(exportPanel.locator('#cut-lab-export-moxfield-patch')).toHaveValue('');
  await expect(exportPanel.locator('#cut-lab-export-archidekt-patch')).toHaveValue('');

  // Activating the Export tab submits cut-lab-export-form (server POST) and re-renders.
  await exportTab.click();

  const moxfieldFull = exportPanel.locator('#cut-lab-export-moxfield-full');
  const archidektFull = exportPanel.locator('#cut-lab-export-archidekt-full');
  const moxfieldPatch = exportPanel.locator('#cut-lab-export-moxfield-patch');
  const archidektPatch = exportPanel.locator('#cut-lab-export-archidekt-patch');

  await expect(moxfieldFull).toBeVisible();
  await expect(archidektFull).toBeVisible();
  await expect(moxfieldFull).not.toHaveValue('');
  await expect(archidektFull).not.toHaveValue('');
  await expect(moxfieldPatch).toContainText('CUT');
  await expect(archidektPatch).toContainText('CUT');
  await expect(moxfieldPatch).toContainText('ADD');
  await expect(archidektPatch).toContainText('ADD');

  // After the POST re-render the "reach 100" hint is gone and the count check is green.
  await expect(exportPanel.locator('.cutlab-export__hint')).toHaveCount(0);

  const exportStatuses = exportPanel.locator('.cutlab-export__status');
  await expect(exportStatuses.filter({ hasText: 'Card count = 100' })).toHaveCount(1);
  await expect(exportStatuses.filter({ hasText: 'Color-identity' })).toHaveCount(2);
  await expect(exportStatuses.filter({ hasText: 'Banlist' })).toHaveCount(1);
});
