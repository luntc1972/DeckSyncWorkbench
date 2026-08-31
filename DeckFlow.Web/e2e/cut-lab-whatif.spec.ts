import { expect, test, type Browser, type Locator, type Page } from '@playwright/test';
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

const fillImportFormNoJs = async (page: Page): Promise<void> => {
  await page.locator('#cut-lab-input-source').selectOption('PasteText');
  await page.locator('#cut-lab-deck-text').evaluate((element, value) => {
    (element as HTMLTextAreaElement).value = value;
  }, oversizedPool);
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

const acceptCurrentProposal = async (page: Page): Promise<string> => {
  const proposalHeading = await page.locator('.cutlab-proposal__heading').textContent();
  const cardName = proposalHeading?.replace(/^Proposed cut:\s*/, '').trim() ?? '';
  await page.locator('.cutlab-proposal .cutlab-decision-btn--accept').click();
  return cardName;
};

const getMainStateInput = (page: Page): Locator =>
  page.locator('form[data-cache-key="cut-lab"] input[name="CutLabStateJson"]');

const getSelectOptions = async (page: Page, selector: string): Promise<string[]> =>
  page.locator(`${selector} option`).evaluateAll((options) =>
    options.map((option) => (option as HTMLOptionElement).value));

const chooseCardOut = async (page: Page, excluded: ReadonlySet<string>): Promise<string> => {
  const options = await getSelectOptions(page, 'select[data-cut-lab-whatif-card-out]');
  const cardOut = options.find((value) => value !== '' && !excluded.has(value));
  if (!cardOut) {
    throw new Error('Expected at least one eligible what-if card-out option.');
  }

  return cardOut;
};

const expectCutsMadeRow = async (page: Page, cardName: string, roundLabel: string): Promise<void> => {
  const row = page.locator('.cutlab-cuts-made__row').filter({ hasText: cardName });
  await expect(row).toContainText(cardName, { timeout: 30_000 });
  await expect(row).toContainText(`cut in ${roundLabel}`, { timeout: 30_000 });
};

const buildNoJsPage = async (browser: Browser): Promise<{ page: Page; close: () => Promise<void> }> => {
  const context = await browser.newContext({
    javaScriptEnabled: false,
    viewport: { width: 1440, height: 1000 },
  });
  const page = await context.newPage();
  return {
    page,
    close: () => context.close(),
  };
};

test.beforeEach(async ({ page }) => {
  heldLock = await acquireAdminLockForTest(page);
  await setToolEnabled(page, 'Cut Lab', true);
});

test.afterEach(async () => {
  await releaseAdminLockForTest(heldLock);
  heldLock = null;
});

test('previews, discards, and keeps a what-if swap without mutating state until Keep', async ({ page }) => {
  await importPool(page);
  await waitForCutRounds(page);
  await expandMobileCollapsibles(page);

  await page.locator('tr[data-cut-lab-card="Plains"] input[data-cut-lab-lock-card]').check();
  const cutPileCard = await acceptCurrentProposal(page);
  await expect(page.locator('[data-cut-lab-sticky-accepted]')).toContainText('1 cut so far');

  const cardOutSelect = 'select[data-cut-lab-whatif-card-out]';
  const cardInSelect = 'select[data-cut-lab-whatif-card-in]';
  const cardOutOptionsBefore = await getSelectOptions(page, cardOutSelect);
  const cardInOptionsBefore = await getSelectOptions(page, cardInSelect);

  expect(cardOutOptionsBefore).not.toContain('Zur the Enchanter');
  expect(cardInOptionsBefore).toContain(cutPileCard);

  const cardOut = await chooseCardOut(page, new Set(['', cutPileCard]));
  const stateBeforePreview = await getMainStateInput(page).inputValue();
  await page.locator(cardOutSelect).selectOption(cardOut);
  await page.locator(cardInSelect).selectOption(cutPileCard);
  await page.locator('[data-cut-lab-whatif-preview-submit]').click();

  await expect(page.locator('[data-cut-lab-whatif-selection]')).toContainText(`Previewing: cut ${cardOut}, restore ${cutPileCard}.`);
  await expect(page.locator('[data-cut-lab-whatif-delta-body] tr').first()).toBeVisible();
  await expect(page.locator('[data-cut-lab-sticky-accepted]')).toContainText('1 cut so far');
  expect(await getSelectOptions(page, cardOutSelect)).toEqual(cardOutOptionsBefore);
  expect(await getSelectOptions(page, cardInSelect)).toEqual(cardInOptionsBefore);
  await expect(page.locator('.cutlab-cuts-made__row').filter({ hasText: cardOut })).toHaveCount(0);

  await page.locator('[data-cut-lab-whatif-discard]').click();

  await expect(page.locator('[data-cut-lab-whatif-selection]')).toHaveClass(/hidden/);
  await expect(page.locator('[data-cut-lab-whatif-delta-body] tr')).toHaveCount(0);
  await expect(page.locator('[data-cut-lab-sticky-accepted]')).toContainText('1 cut so far');
  expect(await getSelectOptions(page, cardOutSelect)).toEqual(cardOutOptionsBefore);
  expect(await getSelectOptions(page, cardInSelect)).toEqual(cardInOptionsBefore);

  await page.locator(cardOutSelect).selectOption(cardOut);
  await page.locator(cardInSelect).selectOption(cutPileCard);
  await page.locator('[data-cut-lab-whatif-preview-submit]').click();
  await expect(page.locator('[data-cut-lab-whatif-delta-body] tr').first()).toBeVisible();

  await page.locator('[data-cut-lab-whatif-keep-submit]').click();

  await expectCutsMadeRow(page, cardOut, 'What-if swap');
  await expect(getMainStateInput(page)).not.toHaveValue(stateBeforePreview);
  expect(await getSelectOptions(page, cardOutSelect)).toContain(cutPileCard);
  expect(await getSelectOptions(page, cardOutSelect)).not.toContain(cardOut);
  expect(await getSelectOptions(page, cardInSelect)).toContain(cardOut);
  expect(await getSelectOptions(page, cardInSelect)).not.toContain(cutPileCard);
});

test('supports the no-JS what-if preview and keep fallback via full-page re-render', async ({ browser }) => {
  const noJs = await buildNoJsPage(browser);

  try {
    await noJs.page.goto(`${baseUrl}/cut-lab`);
    await expect(noJs.page.locator('h1')).toHaveText('Cut Lab');
    await fillImportFormNoJs(noJs.page);
    await noJs.page.getByRole('button', { name: 'Import pool' }).click();
    await expect(noJs.page.getByRole('heading', { name: 'Lock your pool' })).toBeVisible({ timeout: 30_000 });
    await expect(noJs.page.locator('tr[data-cut-lab-card="Zur the Enchanter"]')).toHaveAttribute('data-cut-lab-commander', 'true');
    await waitForCutRounds(noJs.page);

    const cutPileCard = await acceptCurrentProposal(noJs.page);
    await expect(noJs.page.locator('[data-cut-lab-sticky-accepted]')).toContainText('1 cut so far');

    const cardOut = await chooseCardOut(noJs.page, new Set(['', cutPileCard]));
    await noJs.page.locator('select[name="cardOut"]').selectOption(cardOut);
    await noJs.page.locator('select[name="cardIn"]').selectOption(cutPileCard);
    await noJs.page.locator('form[action$="/cut-lab/whatif"] button[name="intent"][value="preview"]').click();

    await expect(noJs.page.locator('[data-cut-lab-whatif-selection]')).toContainText(`Previewing: cut ${cardOut}, restore ${cutPileCard}.`);
    expect(await noJs.page.locator('[data-cut-lab-whatif-delta-body] tr').count()).toBeGreaterThan(0);
    await expect(noJs.page.locator('.cutlab-cuts-made__row').filter({ hasText: cardOut })).toHaveCount(0);

    await noJs.page.locator('form[action$="/cut-lab/whatif"] button[name="intent"][value="keep"]').click();

    await expectCutsMadeRow(noJs.page, cardOut, 'What-if swap');
    expect(await getSelectOptions(noJs.page, 'select[data-cut-lab-whatif-card-out]')).toContain(cutPileCard);
    expect(await getSelectOptions(noJs.page, 'select[data-cut-lab-whatif-card-in]')).toContain(cardOut);
  } finally {
    await noJs.close();
  }
});
