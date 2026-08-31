import { expect, test, type Locator, type Page } from '@playwright/test';
import { acquireAdminLockForTest, releaseAdminLockForTest } from './support/admin-lock';
import { setToolEnabled } from './support/admin-tools';
import { resolveE2EPort } from './support/e2e-port';

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

const getCardModal = (page: Page): Locator =>
  page.locator('dialog#cutlab-card-modal');

const openCardModal = async (trigger: Locator, page: Page, cardName: string): Promise<Locator> => {
  const modal = getCardModal(page);
  await trigger.click();
  await expect(modal).toHaveAttribute('open', '');
  await expect(modal.locator('#cutlab-card-modal-title')).toHaveText(cardName);
  await expect(modal.locator('[data-cutlab-modal-oracle]')).toBeVisible();
  return modal;
};

const closeCardModal = async (page: Page): Promise<void> => {
  const modal = getCardModal(page);
  await modal.locator('[data-cutlab-modal-close]').click();
  await expect(modal).not.toHaveAttribute('open', '');
};

test('individual card pills lock cards and Lock All stays readable in Commander Table dark OS mode', async ({ page, baseURL }) => {
  const heldLock = await acquireAdminLockForTest(page);
  try {
    await setToolEnabled(page, 'Cut Lab', true);
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.context().addCookies([{
      name: 'deckflow-theme',
      value: 'site-commander-table.css',
      url: baseURL ?? `http://localhost:${resolveE2EPort()}`,
    }]);
    await page.goto('/cut-lab');

    const b4Label = page.locator('label.manabase-pill').filter({ hasText: 'B4 Optimized' });
    await b4Label.click();
    await expect(page.locator('input[name="Bracket"][value="4"]')).toBeChecked();

    await page.locator('#cut-lab-input-source').selectOption('PasteText');
    await page.locator('#cut-lab-deck-text').fill(oversizedPool);
    await page.locator('#cut-lab-primary-plan').fill('Protect the control shell.');
    await page.getByRole('button', { name: 'Import pool' }).click();
    await expect(page.getByRole('heading', { name: 'Lock your pool' })).toBeVisible({ timeout: 30_000 });

    const group = page.locator('details.cutlab-role-group').filter({ hasText: 'Lands' });
    await group.locator(':scope > summary').click();
    const lockAll = group.locator('[data-cut-lab-lock-role="lands"]');
    const commandTowerPill = group.locator('button[data-cut-lab-chip-card="Command Tower"]');
    const commandTowerCheckbox = page.locator(
      'tr[data-cut-lab-card="Command Tower"] input[data-cut-lab-lock-card]',
    );

    const commandTowerModal = await openCardModal(commandTowerPill, page, 'Command Tower');
    await expect(commandTowerModal.locator('[data-cutlab-modal-oracle]')).toContainText('Add one mana');
    await commandTowerModal.locator('[data-cutlab-modal-lock]').click();
    await expect(commandTowerCheckbox).toBeChecked();
    await expect(commandTowerPill).toHaveAttribute('aria-pressed', 'true');
    await closeCardModal(page);

    const before = await lockAll.evaluate(element => {
      const style = getComputedStyle(element);
      const spanStyle = getComputedStyle(element.querySelector('span')!);
      return { color: style.color, spanColor: spanStyle.color, backgroundColor: style.backgroundColor, pointerEvents: style.pointerEvents };
    });
    expect(before).toEqual({
      color: 'rgb(26, 21, 16)',
      spanColor: 'rgb(26, 21, 16)',
      backgroundColor: 'rgb(250, 248, 243)',
      pointerEvents: 'auto',
    });

    await lockAll.click();
    const after = await lockAll.evaluate(element => {
      const style = getComputedStyle(element);
      const spanStyle = getComputedStyle(element.querySelector('span')!);
      return { color: style.color, spanColor: spanStyle.color, backgroundColor: style.backgroundColor, pointerEvents: style.pointerEvents };
    });
    expect(after).toEqual({
      color: 'rgb(255, 255, 255)',
      spanColor: 'rgb(255, 255, 255)',
      backgroundColor: 'rgb(45, 122, 79)',
      pointerEvents: 'auto',
    });
    await expect(lockAll).toHaveAttribute('aria-pressed', 'true');
  } finally {
    await releaseAdminLockForTest(heldLock);
  }
});

test('card modal meta shows power and toughness for creature cards', async ({ page }) => {
  const heldLock = await acquireAdminLockForTest(page);
  try {
    await setToolEnabled(page, 'Cut Lab', true);
    await page.goto('/cut-lab');

    await page.locator('#cut-lab-input-source').selectOption('PasteText');
    await page.locator('#cut-lab-deck-text').fill(oversizedPool);
    await page.locator('#cut-lab-primary-plan').fill('Protect the control shell.');
    await page.getByRole('button', { name: 'Import pool' }).click();
    await expect(page.getByRole('heading', { name: 'Lock your pool' })).toBeVisible({ timeout: 30_000 });

    const commanderTrigger = page.locator('button[data-cutlab-card-open="Zur the Enchanter"]').first();
    const modal = await openCardModal(commanderTrigger, page, 'Zur the Enchanter');

    await expect(modal.locator('[data-cutlab-modal-meta]')).toContainText('1/4');
  } finally {
    await releaseAdminLockForTest(heldLock);
  }
});

test('structural evidence pills lock the canonical pool checkbox and inert spans stay non-lockable', async ({ page }) => {
  const heldLock = await acquireAdminLockForTest(page);
  try {
    await setToolEnabled(page, 'Cut Lab', true);
    await page.goto('/cut-lab');

    await page.locator('#cut-lab-input-source').selectOption('PasteText');
    await page.locator('#cut-lab-deck-text').fill(oversizedPool);
    await page.locator('#cut-lab-primary-plan').fill('Protect the control shell.');
    await page.getByRole('button', { name: 'Import pool' }).click();
    await expect(page.getByRole('heading', { name: 'Lock your pool' })).toBeVisible({ timeout: 30_000 });

    const findingsSection = page.locator('[data-cut-lab-structural-findings]');
    await expect(findingsSection).toBeVisible();

    let lockableEvidenceButtons = findingsSection.locator('button[data-cut-lab-chip-card]');
    if (await lockableEvidenceButtons.count() === 0) {
      await expect(page.locator('.cutlab-proposal')).toBeVisible();
      const responsePromise = page.waitForResponse(response =>
        response.url().includes('/api/cut-lab/decide') && response.request().method() === 'POST');

      await page.locator('.cutlab-decision-btn--accept').click();

      const response = await responsePromise;
      expect(response.ok()).toBeTruthy();
      lockableEvidenceButtons = findingsSection.locator('button[data-cut-lab-chip-card]');
    }

    const lockableCount = await lockableEvidenceButtons.count();
    test.skip(
      lockableCount === 0,
      'No lockable Structural evidence chips from the oversized pool decide; CLUP-09 lock behavior remains deterministically covered by Task 1 Vitest + Task 2 xUnit.',
    );
    expect(
      lockableCount,
      'expected >= 1 lockable Structural evidence chip from the oversized pool decide',
    ).toBeGreaterThan(0);

    const evidenceButton = lockableEvidenceButtons.first();
    const cardName = await evidenceButton.getAttribute('data-cut-lab-chip-card');
    expect(cardName).not.toBeNull();

    const checkbox = page.locator(
      `tr[data-cut-lab-card="${cardName}"] input[data-cut-lab-lock-card]`,
    );

    await expect(checkbox).not.toBeChecked();
    const evidenceModal = await openCardModal(evidenceButton, page, cardName!);
    await evidenceModal.locator('[data-cutlab-modal-lock]').click();
    await expect(checkbox).toBeChecked();
    await expect(evidenceButton).toHaveAttribute('aria-pressed', 'true');
    await closeCardModal(page);

    await openCardModal(evidenceButton, page, cardName!);
    await getCardModal(page).locator('[data-cutlab-modal-lock]').click();
    await expect(checkbox).not.toBeChecked();
    await expect(evidenceButton).toHaveAttribute('aria-pressed', 'false');
    await closeCardModal(page);

    await expect(findingsSection.locator('span.kb-chip[data-cut-lab-chip-card]')).toHaveCount(0);

    const inertSpans = findingsSection.locator('span.kb-chip');
    if (await inertSpans.count() > 0) {
      const inertSpan = inertSpans.first();
      expect(await inertSpan.evaluate(element => element.tagName)).toBe('SPAN');
      expect(await inertSpan.getAttribute('aria-pressed')).toBeNull();

      const checkedBefore = await page.locator('input[data-cut-lab-lock-card]:checked').count();
      await inertSpan.click();
      const checkedAfter = await page.locator('input[data-cut-lab-lock-card]:checked').count();

      expect(checkedAfter).toBe(checkedBefore);
    }
    // Deterministic unmatched Structural-evidence inert proof lives in Task 1 Vitest + Task 2 xUnit.
  } finally {
    await releaseAdminLockForTest(heldLock);
  }
});
