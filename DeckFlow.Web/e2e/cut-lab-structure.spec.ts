import { expect, test, type Locator, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { acquireAdminLockForTest, releaseAdminLockForTest } from './support/admin-lock';
import { setToolEnabled } from './support/admin-tools';
import { clickManabasePillRadio } from './support/manabase-pill';

import { resolveE2EPort } from './support/e2e-port';

const baseUrl = `http://localhost:${resolveE2EPort()}`;
const screenshotDir = resolve(__dirname, '../../.planning/ui-design/cut-lab/screenshots');

const themes = [
  { name: 'classic', cookie: 'site.css' },
  { name: 'azorius', cookie: 'site-azorius.css' },
  { name: 'nyx', cookie: 'site-nyx.css' },
] as const;

const viewports = [
  { name: 'desktop', width: 1440, height: 1000 },
  { name: 'mobile', width: 430, height: 932 },
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
  await page.locator('#cut-lab-primary-plan').fill('Protect the control shell, then trim to the cleanest Zur line.');
  await page.locator('#cut-lab-secondary-plan').fill('Keep the fast mana package intact.');
  await clickManabasePillRadio(page, 'Bracket', '4');
  await clickManabasePillRadio(page, 'PlayExperience', 'Focused');
  await page.getByRole('button', { name: 'Import pool' }).click();

  await expect(page.getByRole('heading', { name: 'Lock your pool' })).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('tr[data-cut-lab-card="Zur the Enchanter"]')).toHaveAttribute('data-cut-lab-commander', 'true');
};

const waitForCutRounds = async (page: Page): Promise<void> => {
  await expect(page.getByRole('heading', { name: 'Cut rounds' })).toBeVisible();
  await expect(page.locator('.cutlab-round-banner .cutlab-finding__heading')).toBeVisible();
  await expect(page.locator('[data-cut-lab-sticky-remaining]')).toBeVisible();
  await expect(page.locator('.cutlab-proposal')).toBeVisible();
};

const getStickyRemainingCount = async (page: Page): Promise<number> => {
  const stickyText = await page.locator('[data-cut-lab-sticky-remaining]').textContent();
  const match = stickyText?.match(/^(\d+) to cut$/);
  return Number.parseInt(match?.[1] ?? '0', 10);
};

const getStickyAcceptedCount = async (page: Page): Promise<number> => {
  const stickyText = await page.locator('[data-cut-lab-sticky-accepted]').textContent();
  const match = stickyText?.match(/^(\d+) cuts? so far$/);
  return Number.parseInt(match?.[1] ?? '0', 10);
};

const formatAcceptedCountLabel = (count: number): string =>
  `${count} ${count === 1 ? 'cut' : 'cuts'} so far`;

const acceptCurrentProposal = async (page: Page): Promise<string> => {
  const proposal = page.locator('.cutlab-proposal');
  const heading = proposal.locator('.cutlab-proposal__heading');
  const proposalHeading = await heading.textContent();
  const cardName = proposalHeading?.replace(/^Proposed cut:\s*/, '').trim() ?? '';
  await proposal.locator('.cutlab-decision-btn--accept').click();
  return cardName;
};

const rejectCurrentProposal = async (page: Page): Promise<string> => {
  const proposal = page.locator('.cutlab-proposal');
  const heading = proposal.locator('.cutlab-proposal__heading');
  const proposalHeading = await heading.textContent();
  const cardName = proposalHeading?.replace(/^Proposed cut:\s*/, '').trim() ?? '';
  await proposal.locator('.cutlab-decision-btn--reject').click();
  return cardName;
};

const deferCurrentProposal = async (page: Page): Promise<string> => {
  const proposal = page.locator('.cutlab-proposal');
  const heading = proposal.locator('.cutlab-proposal__heading');
  const proposalHeading = await heading.textContent();
  const cardName = proposalHeading?.replace(/^Proposed cut:\s*/, '').trim() ?? '';
  await proposal.locator('.cutlab-decision-btn--defer').click();
  return cardName;
};

const getRoleFloorRow = (page: Page, roleKey: string): Locator =>
  page.locator(`tr[data-cut-lab-floor-row="${roleKey}"]`);

const getFloorValue = async (row: Locator): Promise<number> => {
  const rawValue = await row.locator('input[data-cut-lab-floor]').inputValue();
  return Number.parseInt(rawValue, 10);
};

const getFloorCount = async (row: Locator): Promise<number> => {
  const rawCount = await row.getAttribute('data-cut-lab-floor-count');
  return Number.parseInt(rawCount ?? '0', 10);
};

const getFloorDefault = async (row: Locator): Promise<number> => {
  const rawDefault = await row.getAttribute('data-cut-lab-floor-default');
  return Number.parseInt(rawDefault ?? '0', 10);
};

test.beforeEach(async ({ page }) => {
  heldLock = await acquireAdminLockForTest(page);
  await setToolEnabled(page, 'Cut Lab', true);
});

test.afterEach(async () => {
  await releaseAdminLockForTest(heldLock);
  heldLock = null;
});

test('renders the three structure sections with 10 collapsed role groups and 9 floor inputs', async ({ page }) => {
  await importPool(page);

  await expect(page.getByRole('heading', { name: 'How your pool competes' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Structural findings' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Role floors' })).toBeVisible();

  const roleGroups = page.locator('details.cutlab-role-group[data-cutlab-group-kind="role"]');
  await expect(roleGroups).toHaveCount(10);
  await expect(page.locator('details.cutlab-role-group[data-cutlab-group-kind="role"][open]')).toHaveCount(0);
  await expect(page.locator('input[data-cut-lab-floor]')).toHaveCount(9);
  await expect(page.locator('.cutlab-findings-count')).toBeVisible();
  await expect(page.locator('.cutlab-finding__heading').filter({ hasText: 'Weak floor cases' })).toHaveCount(1);
});

test('live-patches the structural findings section after a JS decide without a reload', async ({ page }) => {
  await importPool(page);
  await waitForCutRounds(page);

  const findingsSection = page.locator('[data-cut-lab-structural-findings]');
  const findingsBody = findingsSection.locator('[data-cut-lab-structural-findings-body]');
  const beforeBodyText = await findingsBody.textContent();
  const beforeSectionText = await findingsSection.textContent();
  const navigationCountBefore = await page.evaluate(() => performance.getEntriesByType('navigation').length);

  const responsePromise = page.waitForResponse(response =>
    response.url().includes('/api/cut-lab/decide') && response.request().method() === 'POST');

  await acceptCurrentProposal(page);

  const response = await responsePromise;
  expect(response.ok()).toBeTruthy();

  await expect(findingsBody).not.toHaveText(beforeBodyText ?? '');
  await expect(findingsSection).not.toHaveText(beforeSectionText ?? '');
  expect(await page.evaluate(() => performance.getEntriesByType('navigation').length)).toBe(navigationCountBefore);
  await expect(page).toHaveURL(`${baseUrl}/cut-lab`);
});

test('opens the Lands group, shows member chips, and toggles land rows from the group pill', async ({ page }) => {
  await importPool(page);

  const landsGroup = page.locator('details.cutlab-role-group').filter({ hasText: 'Lands' });
  await landsGroup.locator(':scope > summary').click();
  const lockAllButton = landsGroup.locator('[data-cut-lab-lock-role="lands"]');

  await expect(landsGroup.locator('[data-cut-lab-chip-card="Plains"]')).toBeVisible();
  await expect(landsGroup.locator('[data-cut-lab-chip-card="Island"]')).toBeVisible();
  await expect(lockAllButton).toContainText('Lock all lands');
  await expect(lockAllButton).toHaveAttribute('aria-pressed', 'false');

  await lockAllButton.click();
  await expect(page.locator('tr[data-cut-lab-card="Plains"] input[data-cut-lab-lock-card]')).toBeChecked();
  await expect(page.locator('tr[data-cut-lab-card="Island"] input[data-cut-lab-lock-card]')).toBeChecked();
  await expect(page.locator('tr[data-cut-lab-card="Command Tower"] input[data-cut-lab-lock-card]')).toBeChecked();
  await expect(lockAllButton).toHaveAttribute('aria-pressed', 'true');

  await lockAllButton.click();
  await expect(page.locator('tr[data-cut-lab-card="Plains"] input[data-cut-lab-lock-card]')).not.toBeChecked();
  await expect(page.locator('tr[data-cut-lab-card="Island"] input[data-cut-lab-lock-card]')).not.toBeChecked();
  await expect(page.locator('tr[data-cut-lab-card="Command Tower"] input[data-cut-lab-lock-card]')).not.toBeChecked();
  await expect(lockAllButton).toHaveAttribute('aria-pressed', 'false');
});

test('marks interaction-targeted as adjusted after floor edits and writes roleFloors into hidden state', async ({ page }) => {
  await importPool(page);

  const interactionRow = getRoleFloorRow(page, 'interaction-targeted');
  const interactionInput = interactionRow.locator('input[data-cut-lab-floor="interaction-targeted"]');
  const interactionCount = await getFloorCount(interactionRow);
  const interactionDefault = await getFloorDefault(interactionRow);
  const validHighValue = Math.max(interactionDefault + 1, interactionCount - 1);

  await interactionInput.fill('99');
  await interactionInput.blur();
  await interactionInput.fill(`${validHighValue}`);
  await interactionInput.blur();

  await expect(interactionRow.locator('[data-cut-lab-floor-adjusted-badge]')).toBeVisible();
  await expect(page.locator('input[name="CutLabStateJson"]').first()).toHaveValue(
    /"roleFloors":\[.*"role":"interaction-targeted".*"isUserSet":true/,
  );
});

test('preserves the adjusted interaction-targeted floor and badge across Recalculate', async ({ page }) => {
  await importPool(page);

  const interactionRow = getRoleFloorRow(page, 'interaction-targeted');
  const interactionInput = interactionRow.locator('input[data-cut-lab-floor="interaction-targeted"]');
  const interactionCount = await getFloorCount(interactionRow);
  const persistedValue = Math.max((await getFloorDefault(interactionRow)) + 1, interactionCount - 1);

  await interactionInput.fill(`${persistedValue}`);
  await interactionInput.blur();
  await page.locator('[data-cut-lab-recalculate]').click();

  await expect(page.getByRole('heading', { name: 'Lock your pool' })).toBeVisible({ timeout: 30_000 });
  await expect(getRoleFloorRow(page, 'interaction-targeted').locator('input[data-cut-lab-floor="interaction-targeted"]')).toHaveValue(`${persistedValue}`);
  await expect(getRoleFloorRow(page, 'interaction-targeted').locator('[data-cut-lab-floor-adjusted-badge]')).toBeVisible();
});

test('shows the at floor marker when a floor is raised to within 1 of the role count', async ({ page }) => {
  await importPool(page);

  const interactionRow = getRoleFloorRow(page, 'interaction-targeted');
  const interactionInput = interactionRow.locator('input[data-cut-lab-floor="interaction-targeted"]');
  const interactionCount = await getFloorCount(interactionRow);
  const atFloorValue = Math.max(0, interactionCount - 1);

  await interactionInput.fill(`${atFloorValue}`);
  await interactionInput.blur();

  await expect(interactionRow.locator('[data-cut-lab-floor-at-marker]')).toContainText('at floor');
});

test('resets an adjusted interaction-targeted floor back to its default value', async ({ page }) => {
  await importPool(page);

  const interactionRow = getRoleFloorRow(page, 'interaction-targeted');
  const interactionInput = interactionRow.locator('input[data-cut-lab-floor="interaction-targeted"]');
  const defaultValue = await getFloorDefault(interactionRow);
  const interactionCount = await getFloorCount(interactionRow);
  const adjustedValue = Math.max(defaultValue + 1, interactionCount - 1);

  await interactionInput.fill(`${adjustedValue}`);
  await interactionInput.blur();
  await expect(interactionRow.locator('[data-cut-lab-floor-adjusted-badge]')).toBeVisible();

  await interactionRow.locator('[data-cut-lab-floor-reset="interaction-targeted"]').click();

  await expect(interactionInput).toHaveValue(`${defaultValue}`);
  await expect(interactionRow.locator('[data-cut-lab-floor-adjusted-badge]')).toBeHidden();
});

test('accepts a proposal without a reload, keeps copy neutral, and shows a 7-row compare table', async ({ page }) => {
  await importPool(page);
  await expect(page.locator('.cutlab-sticky-bar[data-cut-lab-sticky-target]')).toBeVisible();
  await expect(page.locator('[data-cut-lab-sticky-locked]')).toContainText('1 locked');
  await expect(page.locator('[data-cut-lab-sticky-current]')).toContainText('106/100 cards');
  await waitForCutRounds(page);

  const startingRemaining = await getStickyRemainingCount(page);
  const startingAccepted = await getStickyAcceptedCount(page);
  const startingProposalHeading = await page.locator('.cutlab-proposal__heading').textContent();
  const mainFrameNavigations: string[] = [];
  const navigationListener = (frame: { parentFrame: () => object | null; url: () => string }): void => {
    if (frame.parentFrame() === null) {
      mainFrameNavigations.push(frame.url());
    }
  };

  page.on('framenavigated', navigationListener);

  const acceptedCardName = await acceptCurrentProposal(page);

  await expect(page.locator('[data-cut-lab-sticky-remaining]')).toContainText(`${startingRemaining - 1} to cut`);
  await expect(page.locator('[data-cut-lab-sticky-accepted]')).toContainText(formatAcceptedCountLabel(startingAccepted + 1));
  await expect(page.locator('.cutlab-cuts-made__row')).toContainText(acceptedCardName);
  await expect(page.locator('.cutlab-round-banner .cutlab-finding__heading')).toBeVisible();
  await expect(page.locator('.cutlab-proposal__heading')).not.toHaveText(startingProposalHeading ?? '');

  page.off('framenavigated', navigationListener);
  expect(mainFrameNavigations).toHaveLength(0);
  await expect(page.locator('.cutlab-proposal')).toContainText(/^(?!.*\b(?:worse|bad|better)\b).*/s);
  const deltaTexts = await page.locator('.cutlab-delta').allTextContents();
  expect(deltaTexts).not.toHaveLength(0);
  for (const deltaText of deltaTexts) {
    expect(deltaText).not.toMatch(/\b(?:worse|bad|better)\b/i);
  }

  const compareDetails = page.locator('details.cutlab-compare');
  await compareDetails.locator(':scope > summary').click();
  await expect(compareDetails.locator('table[data-prompt-cedh-reference-table]')).toBeVisible();
  await expect(compareDetails.locator('thead th')).toHaveText(['Metric', 'Baseline', 'Current', 'Delta']);
  // 7 metric families expand to per-kind rows: 5 single-kind families (EarlyInteraction
  // omitted for this non-cEDH fixture) + Flood/Screw/Curve (3) + category-by-turn caps (3) = 10.
  await expect(compareDetails.locator('tbody tr')).toHaveCount(10);
});

test('restores an accepted cut and reverts the working list counts', async ({ page }) => {
  await importPool(page);
  await waitForCutRounds(page);

  const startingRemaining = await getStickyRemainingCount(page);
  const startingAccepted = await getStickyAcceptedCount(page);
  const acceptedCardName = await page.locator('.cutlab-proposal__heading').textContent();
  const normalizedCardName = acceptedCardName?.replace(/^Proposed cut:\s*/, '').trim() ?? '';

  await acceptCurrentProposal(page);
  // 103-09 patches the proposal card, sticky bar, deltas, and cuts-made list in place; the
  // Phase 102 structural table refreshes only on a server render, so restore is proven via
  // the cuts-made list and sticky counts per the plan's HIGH-1/D-16 assertions.
  await expect(page.locator('.cutlab-cuts-made__row')).toContainText(normalizedCardName);

  await page.locator('.cutlab-cuts-made__row', { hasText: normalizedCardName }).locator('.cutlab-restore-btn').click();

  await expect(page.locator('[data-cut-lab-sticky-remaining]')).toContainText(`${startingRemaining} to cut`);
  await expect(page.locator('[data-cut-lab-sticky-accepted]')).toContainText(formatAcceptedCountLabel(startingAccepted));
  await expect(page.locator('.cutlab-cuts-made__row')).toHaveCount(0);
});

test('restarts rounds 1 and 2 without undoing accepted cuts or touching later-round decisions', async ({ page }) => {
  await importPool(page);
  await waitForCutRounds(page);

  const round1RejectedCard = 'Sol Ring';
  const acceptedCard = 'Exotic Orchard';
  const round2DeferredCard = 'Arcane Signet';
  await page.evaluate(({ round1RejectedCard, acceptedCard, round2DeferredCard }) => {
    const inputs = Array.from(document.querySelectorAll<HTMLInputElement>('input[name="CutLabStateJson"]'));
    for (const input of inputs) {
      const state = JSON.parse(input.value) as {
        decisions: Array<{ cardName: string; kind: number; round: string; ordinal: number }>;
      };
      state.decisions = [
        { cardName: round1RejectedCard, kind: 1, round: 'round-1', ordinal: 1 },
        { cardName: round2DeferredCard, kind: 2, round: 'round-2', ordinal: 2 },
        { cardName: acceptedCard, kind: 0, round: 'round-3', ordinal: 3 },
      ];
      input.value = JSON.stringify(state);
    }
  }, { round1RejectedCard, acceptedCard, round2DeferredCard });

  page.once('dialog', async dialog => {
    expect(dialog.message()).toContain('Round 1 & 2');
    await dialog.accept();
  });
  const restartResponse = page.waitForResponse(response =>
    response.url().includes('/api/cut-lab/restart-rounds') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Restart rounds 1 & 2' }).click();
  expect((await restartResponse).ok()).toBeTruthy();

  const state = JSON.parse(await page.locator('input[name="CutLabStateJson"]').first().inputValue()) as {
    decisions: Array<{ cardName: string; kind: number; round: string }>;
  };
  expect(state.decisions.some(decision => decision.cardName === acceptedCard && decision.kind === 0)).toBe(true);
  expect(state.decisions.some(decision => decision.cardName === round1RejectedCard)).toBe(false);
  expect(state.decisions.some(decision => decision.cardName === round2DeferredCard)).toBe(false);
  await expect(page.locator('[data-cut-lab-sticky-current]')).toContainText('105/100 cards');
  await expect(page.locator('[data-cut-lab-sticky-accepted]')).toContainText('1 cut so far');

  const resurfaced = new Set<string>();
  for (let guard = 0; guard < 10; guard += 1) {
    const proposalHeading = await page.locator('.cutlab-proposal__heading').textContent();
    const cardName = proposalHeading?.replace(/^Proposed cut:\s*/, '').trim() ?? '';
    expect(cardName).not.toBe(acceptedCard);
    if (cardName === round1RejectedCard || cardName === round2DeferredCard) {
      resurfaced.add(cardName);
    }

    if (resurfaced.has(round1RejectedCard) && resurfaced.has(round2DeferredCard)) {
      break;
    }

    await rejectCurrentProposal(page);
  }

  expect(resurfaced.has(round1RejectedCard)).toBe(true);
  expect(resurfaced.has(round2DeferredCard)).toBe(true);
});

test('submits the accept form through the no-JS fallback and re-renders with the cut applied', async ({ browser }) => {
  const context = await browser.newContext({
    javaScriptEnabled: false,
    viewport: { width: 1280, height: 900 },
    httpCredentials: {
      username: process.env.FEEDBACK_ADMIN_USER ?? 'admin',
      password: process.env.FEEDBACK_ADMIN_PASSWORD ?? 'changeme-local',
      send: 'always',
    },
  });
  const noJsPage = await context.newPage();

  try {
    // Intake's paste-panel toggle is JS-driven (server renders it hidden for the default Url
    // source), so arrange the form with an injected evaluate — Playwright evaluate still runs
    // when page scripts are disabled. The decision under test below remains a native form POST.
    await noJsPage.goto('/cut-lab');
    await expect(noJsPage.locator('h1')).toHaveText('Cut Lab');
    await noJsPage.locator('#cut-lab-input-source').selectOption('PasteText');
    await noJsPage.evaluate(() => {
      document.querySelector('[data-sync-panel="cut-lab-deck-text"]')?.classList.remove('hidden');
      document.querySelector('[data-sync-panel="cut-lab-deck-url"]')?.classList.add('hidden');
    });
    await noJsPage.locator('#cut-lab-deck-text').fill(oversizedPool);
    await noJsPage.locator('#cut-lab-primary-plan').fill('Protect the control shell, then trim to the cleanest Zur line.');
    await noJsPage.locator('#cut-lab-secondary-plan').fill('Keep the fast mana package intact.');
    await clickManabasePillRadio(noJsPage, 'Bracket', '4');
    await clickManabasePillRadio(noJsPage, 'PlayExperience', 'Focused');
    await noJsPage.getByRole('button', { name: 'Import pool' }).click();
    await expect(noJsPage.getByRole('heading', { name: 'Lock your pool' })).toBeVisible({ timeout: 60_000 });
    await waitForCutRounds(noJsPage);

    const startingProposalHeading = await noJsPage.locator('.cutlab-proposal__heading').textContent();
    const startingCardName = startingProposalHeading?.replace(/^Proposed cut:\s*/, '').trim() ?? '';
    const decideRequestPromise = noJsPage.waitForRequest(request =>
      request.isNavigationRequest()
      && request.method() === 'POST'
      && request.url().includes('/cut-lab/decide'),
    );
    const navigationPromise = noJsPage.waitForNavigation({ waitUntil: 'domcontentloaded' });

    await noJsPage.locator('.cutlab-proposal .cutlab-decision-btn--accept').click();

    const [decideRequest] = await Promise.all([decideRequestPromise, navigationPromise]);
    expect(decideRequest.url()).toContain('/cut-lab/decide');

    await expect(noJsPage.locator('.cutlab-cuts-made__row')).toContainText(startingCardName);
    await expect(noJsPage.locator('.cutlab-proposal__heading')).not.toHaveText(startingProposalHeading ?? '');
  } finally {
    await context.close();
  }
});

test('captures the structure screenshot matrix across themes and viewports', async ({ page }) => {
  mkdirSync(screenshotDir, { recursive: true });

  for (const viewport of viewports) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });

    for (const theme of themes) {
      await page.context().clearCookies();
      await page.context().addCookies([{ name: 'deckflow-theme', value: theme.cookie, url: baseUrl }]);
      await importPool(page);
      await page.locator('details.cutlab-role-group').filter({ hasText: 'Targeted removal' }).locator(':scope > summary').click();
      await page.locator('input[data-cut-lab-floor="interaction-targeted"]').scrollIntoViewIfNeeded();

      await page.screenshot({
        path: join(screenshotDir, `structure-${theme.name}-${viewport.name}.png`),
        fullPage: true,
      });

      await waitForCutRounds(page);
      await page.locator('.cutlab-sticky-bar[data-cut-lab-sticky-target]').scrollIntoViewIfNeeded();
      await page.locator('.cutlab-sticky-bar[data-cut-lab-sticky-target]').screenshot({
        path: join(screenshotDir, `rounds-${theme.name}-${viewport.name}.png`),
      });

      await acceptCurrentProposal(page);
      await page.locator('details.cutlab-cuts-made').scrollIntoViewIfNeeded();
      await page.locator('details.cutlab-cuts-made').screenshot({
        path: join(screenshotDir, `cuts-made-${theme.name}-${viewport.name}.png`),
      });

      const compareDetails = page.locator('details.cutlab-compare');
      await compareDetails.locator(':scope > summary').click();
      await compareDetails.scrollIntoViewIfNeeded();
      await compareDetails.screenshot({
        path: join(screenshotDir, `compare-${theme.name}-${viewport.name}.png`),
      });
    }
  }
});
