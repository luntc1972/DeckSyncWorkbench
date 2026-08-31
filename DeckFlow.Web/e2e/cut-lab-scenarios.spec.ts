import { expect, test, type Locator, type Page } from '@playwright/test';
import { acquireAdminLockForTest, releaseAdminLockForTest } from './support/admin-lock';
import { setToolEnabled } from './support/admin-tools';
import { expandMobileCollapsibles } from './support/cut-lab-mobile-collapse';
import { clickManabasePillRadio } from './support/manabase-pill';

import { resolveE2EPort } from './support/e2e-port';

const baseUrl = `http://localhost:${resolveE2EPort()}`;

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

const importPool = async (page: Page, primaryPlan: string): Promise<void> => {
  await page.goto(`${baseUrl}/cut-lab`);
  await expect(page.locator('h1')).toHaveText('Cut Lab');
  await fillImportForm(page, primaryPlan);
  await page.getByRole('button', { name: 'Import pool' }).click();

  await expect(page.getByRole('heading', { name: 'Lock your pool' })).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('tr[data-cut-lab-card="Zur the Enchanter"]')).toHaveAttribute('data-cut-lab-commander', 'true');
};

const fillImportForm = async (page: Page, primaryPlan: string): Promise<void> => {
  await page.locator('#cut-lab-input-source').selectOption('PasteText');
  await page.locator('#cut-lab-deck-text').fill(oversizedPool);
  await page.locator('#cut-lab-primary-plan').fill(primaryPlan);
  await page.locator('#cut-lab-secondary-plan').fill('Keep the fast mana package intact.');
  await clickManabasePillRadio(page, 'Bracket', '4');
  await clickManabasePillRadio(page, 'PlayExperience', 'Focused');
};

const waitForCutRounds = async (page: Page): Promise<void> => {
  await expect(page.getByRole('heading', { name: 'Cut rounds' })).toBeVisible();
  await expect(page.locator('.cutlab-round-banner .cutlab-finding__heading')).toBeVisible();
  await expect(page.locator('.cutlab-proposal')).toBeVisible();
};

const acceptCurrentProposal = async (page: Page): Promise<string> => {
  const proposalHeading = await page.locator('.cutlab-proposal__heading').textContent();
  const cardName = proposalHeading?.replace(/^Proposed cut:\s*/, '').trim() ?? '';
  await page.locator('.cutlab-proposal .cutlab-decision-btn--accept').click();
  return cardName;
};

const getMainStateInput = (page: Page): Locator =>
  page.locator('form[data-cache-key="cut-lab"] input[name="CutLabStateJson"]');

const getScenarioRow = (page: Page, scenarioName: string): Locator =>
  page.locator('.cutlab-scenarios__item').filter({
    has: page.locator('strong.cutlab-scenarios__name', { hasText: scenarioName }),
  });

const clearScenarioStorage = async (page: Page): Promise<void> => {
  if (page.isClosed()) {
    return;
  }

  try {
    if (!page.url().startsWith(baseUrl)) {
      await page.goto('/cut-lab');
    }

    await page.evaluate(() => {
      for (const key of Object.keys(window.localStorage)) {
        if (key === 'deckflow.cutlab.scenario-index' || key.startsWith('deckflow.cutlab.scenario.')) {
          window.localStorage.removeItem(key);
        }
      }
    });
  } catch {
    // Best-effort cleanup only; the test body performs the real assertions.
  }
};

const clearCutLabSessionCache = async (page: Page): Promise<void> => {
  await page.evaluate(() => {
    window.sessionStorage.removeItem('decksync-form-state-cut-lab');
    window.sessionStorage.removeItem('decksync-form-state-cut-lab:savedAt');
    window.sessionStorage.removeItem('deckflow.last-deck');
  });
};

test.beforeEach(async ({ page }) => {
  heldLock = await acquireAdminLockForTest(page);
  await setToolEnabled(page, 'Cut Lab', true);
});

test.afterEach(async ({ page }) => {
  try {
    await clearScenarioStorage(page);
  } finally {
    await releaseAdminLockForTest(heldLock);
    heldLock = null;
  }
});

test('saves a named scenario, then restores the saved session after a fresh import', async ({ page }) => {
  const savedPrimaryPlan = 'Protect the control shell, then trim to the cleanest Zur line.';
  const freshPrimaryPlan = 'Fresh import only: favor mana density before trimming.';
  const scenarioName = 'Locked tutor line';

  await importPool(page, savedPrimaryPlan);
  await waitForCutRounds(page);
  await expandMobileCollapsibles(page);

  await page.locator('tr[data-cut-lab-card="Rhystic Study"] input[data-cut-lab-lock-card]').check();
  await page.locator('input[data-cut-lab-goal="commander"]').fill('5');
  const acceptedCard = await acceptCurrentProposal(page);

  await expect(page.locator('[data-cut-lab-sticky-accepted]')).toContainText('1 cut so far');
  await expect(page.locator('.cutlab-cuts-made__row')).toContainText(acceptedCard);

  await page.locator('input[data-cut-lab-scenario-name]').fill(scenarioName);
  await page.locator('[data-cut-lab-scenario-save]').click();

  await expect(page.locator('[data-cut-lab-scenario-status]')).toHaveText('Scenario saved.');
  await expect(getScenarioRow(page, scenarioName)).toBeVisible();
  await expect(getMainStateInput(page)).toHaveValue(new RegExp(`"cardName":"${acceptedCard.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}"`));
  await expect(getMainStateInput(page)).toHaveValue(/"name":"Rhystic Study".*"isLocked":true/);
  await expect(getMainStateInput(page)).toHaveValue(/"goals":\{"commanderByTurn":5/);

  await page.locator('[data-clear-cache]').click();
  await expect(page.getByRole('heading', { name: 'No pool imported yet' })).toBeVisible({ timeout: 30_000 });
  await clearCutLabSessionCache(page);
  await page.evaluate(() => {
    const stateInput = document.querySelector<HTMLInputElement>('form[data-cache-key="cut-lab"] input[name="CutLabStateJson"]');
    if (stateInput) {
      stateInput.value = '';
    }
  });
  await fillImportForm(page, freshPrimaryPlan);
  await page.getByRole('button', { name: 'Import pool' }).click();
  await waitForCutRounds(page);
  await expandMobileCollapsibles(page);

  await expect(page.locator('[data-cut-lab-sticky-accepted]')).toContainText('0 cuts so far');
  await expect(page.locator('.cutlab-cuts-made__row')).toHaveCount(0);
  await expect(page.locator('tr[data-cut-lab-card="Rhystic Study"] input[data-cut-lab-lock-card]')).not.toBeChecked();
  await expect(page.locator('input[data-cut-lab-goal="commander"]')).toHaveValue('3');
  await expect(page.locator('#cut-lab-primary-plan')).toHaveValue(freshPrimaryPlan);

  await getScenarioRow(page, scenarioName).getByRole('button', { name: 'Load' }).click();

  await expect(page.getByRole('heading', { name: 'Lock your pool' })).toBeVisible({ timeout: 30_000 });
  await expandMobileCollapsibles(page);
  await expect(page.locator('#cut-lab-primary-plan')).toHaveValue(savedPrimaryPlan);
  await expect(page.locator('input[data-cut-lab-goal="commander"]')).toHaveValue('5');
  await expect(page.locator('tr[data-cut-lab-card="Rhystic Study"] input[data-cut-lab-lock-card]')).toBeChecked();
  await expect(page.locator('[data-cut-lab-sticky-accepted]')).toContainText('1 cut so far');
  await expect(page.locator('.cutlab-cuts-made__row').filter({ hasText: acceptedCard })).toContainText(acceptedCard);
  expect((await page.locator('select[data-cut-lab-whatif-card-in] option').allTextContents()).map(text => text.trim())).toContain(acceptedCard);
  await expect(getScenarioRow(page, scenarioName)).toBeVisible();
});

test('blocks the 21st saved scenario with the documented cap message', async ({ page }) => {
  await importPool(page, 'Save-cap coverage for local scenario storage.');
  await waitForCutRounds(page);
  await expandMobileCollapsibles(page);

  const stateJson = await getMainStateInput(page).inputValue();
  await page.evaluate((savedState) => {
    const index = Array.from({ length: 20 }, (_, slot) => ({
      id: `seed-${slot + 1}`,
      name: `Seed ${slot + 1}`,
      savedAt: new Date(Date.UTC(2026, 6, 20, 12, slot, 0)).toISOString(),
    }));

    window.localStorage.setItem('deckflow.cutlab.scenario-index', JSON.stringify(index));
    for (const entry of index) {
      window.localStorage.setItem(`deckflow.cutlab.scenario.${entry.id}`, savedState);
    }
  }, stateJson);

  await page.locator('input[data-cut-lab-scenario-name]').fill('Overflow slot');
  await page.locator('[data-cut-lab-scenario-save]').click();

  await expect(page.locator('[data-cut-lab-scenario-status]')).toHaveText('Delete a scenario first (max 20).');
});
