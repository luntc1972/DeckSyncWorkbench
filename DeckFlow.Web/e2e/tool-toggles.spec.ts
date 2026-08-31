import { expect, test, type Page } from '@playwright/test';
import { acquireAdminLockForTest, releaseAdminLockForTest } from './support/admin-lock';
import { gotoAdminTools, setToolEnabled } from './support/admin-tools';

import { resolveE2EPort } from './support/e2e-port';

const baseUrl = `http://localhost:${resolveE2EPort()}`;
const adminUser = process.env.FEEDBACK_ADMIN_USER ?? 'admin';
const adminPassword = process.env.FEEDBACK_ADMIN_PASSWORD ?? 'changeme-local';
const basicAuthHeader = `Basic ${Buffer.from(`${adminUser}:${adminPassword}`).toString('base64')}`;
const representativeThemes = ['site.css', 'site-nyx.css'] as const;
const deckAnalysisDownloadDeck = `Commander
1 Atraxa, Praetors' Voice

1 Sol Ring
1 Arcane Signet`;

type LockHandle = Awaited<ReturnType<typeof acquireAdminLockForTest>>;

const toolLabelsToReset = [
  'Card Lookup',
  'Category Suggestions',
  'Category Reference',
  'Deck Analysis',
] as const;

let heldLock: LockHandle | null = null;

test.describe.configure({ mode: 'serial' });

test.beforeEach(async ({ page }) => {
  // Share the same lock used by the other /Admin/ specs so no admin page or
  // admin-side mutation overlaps with another worker or viewport project.
  heldLock = await acquireAdminLockForTest(page);
  await restoreAllTogglesOn(page);
});

test.afterEach(async ({ page }) => {
  try {
    await restoreAllTogglesOn(page);
  } finally {
    await releaseAdminLockForTest(heldLock);
    heldLock = null;
  }
});

test('admin smoke renders sections, card-lookup toggle, and no horizontal overflow across themes', async ({ page }) => {
  for (const theme of representativeThemes) {
    await setTheme(page, theme);

    const response = await page.goto('/Admin/Tools');
    expect(response?.ok(), `/Admin/Tools should render for theme ${theme}`).toBeTruthy();

    for (const sectionHeading of ['Analyze', 'Build', 'Reference', 'Categories']) {
      await expect(page.getByRole('heading', { level: 2, name: sectionHeading, exact: true })).toBeVisible();
    }

    const cardLookupRow = await gotoAdminTools(page).then(() => getAdminToolRow(page, 'Card Lookup'));
    await expect(cardLookupRow.locator('button')).toBeVisible();

    await assertNoHorizontalOverflow(page, `/Admin/Tools should not overflow for theme ${theme}`);
  }
});

test('hide flow removes card lookup everywhere and disabled routes return 404', async ({ page }) => {
  await setTheme(page, representativeThemes[0]);

  await setToolEnabled(page, 'Card Lookup', false);
  await setToolEnabled(page, 'Category Suggestions', false);

  await page.goto('/');
  await expect(page.locator('.hub-grid a[href="/card-lookup"]')).toHaveCount(0);
  await expect(page.locator('#deck-tool-nav a[href="/card-lookup"]')).toHaveCount(0);
  await assertNoHorizontalOverflow(page, 'home should not overflow after hiding Card Lookup');

  await page.goto('/help');
  await expect(page.locator('.help-index__list a[href="/help/card-lookup"]')).toHaveCount(0);

  const cardLookupRoute = await page.goto('/card-lookup');
  expect(cardLookupRoute?.status()).toBe(404);

  const cardLookupHelp = await page.request.get('/help/card-lookup', {
    headers: { Authorization: basicAuthHeader, 'CF-Connecting-IP': '203.0.113.21' },
  });
  expect(cardLookupHelp.status()).toBe(404);

  const cardSearch = await page.request.get('/suggest-categories/card-search?query=sol', {
    headers: { Authorization: basicAuthHeader, 'CF-Connecting-IP': '203.0.113.21' },
  });
  expect(cardSearch.status()).toBe(404);
});

test('deck-analysis download POST is gated with a valid antiforgery token after the tool is disabled', async ({ page }) => {
  await setTheme(page, representativeThemes[0]);

  const deckAnalysisResponse = await page.goto('/deck-analysis');
  expect(deckAnalysisResponse?.ok()).toBeTruthy();

  const antiForgeryToken = await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  expect(antiForgeryToken).not.toBe('');

  await setToolEnabled(page, 'Deck Analysis', false);

  const gatedDownload = await page.request.post('/deck-analysis/download', {
    form: {
      __RequestVerificationToken: antiForgeryToken,
      WorkflowStep: '1',
      DeckText: deckAnalysisDownloadDeck,
    },
    headers: {
      Authorization: basicAuthHeader,
      'CF-Connecting-IP': '203.0.113.22',
    },
  });

  expect(gatedDownload.status()).toBe(404);
});

test('show flow restores card lookup everywhere and the route returns 200', async ({ page }) => {
  await setTheme(page, representativeThemes[0]);

  await setToolEnabled(page, 'Card Lookup', false);
  await setToolEnabled(page, 'Card Lookup', true);

  await page.goto('/');
  await expect(page.locator('.hub-grid a[href="/card-lookup"]')).toHaveCount(1);
  await expect(page.locator('#deck-tool-nav a[href="/card-lookup"]')).toHaveCount(1);
  await assertNoHorizontalOverflow(page, 'home should not overflow after restoring Card Lookup');

  await page.goto('/help');
  await expect(page.locator('.help-index__list a[href="/help/card-lookup"]')).toHaveCount(1);

  const cardLookupRoute = await page.goto('/card-lookup');
  expect(cardLookupRoute?.ok()).toBeTruthy();
});

test('disabling every Categories tool collapses the section and restoring them brings it back', async ({ page }) => {
  await setTheme(page, representativeThemes[0]);

  await setToolEnabled(page, 'Category Suggestions', false);
  await setToolEnabled(page, 'Category Reference', false);

  await page.goto('/');
  await expect(page.locator('#deck-tool-nav [data-tool-nav-trigger]', { hasText: 'Categories' })).toHaveCount(0);
  await expect(page.locator('#hub-group-categories')).toHaveCount(0);

  await setToolEnabled(page, 'Category Suggestions', true);
  await setToolEnabled(page, 'Category Reference', true);

  await page.goto('/');
  await expect(page.locator('#deck-tool-nav [data-tool-nav-trigger]', { hasText: 'Categories' })).toHaveCount(1);
  await expect(page.locator('#hub-group-categories')).toHaveCount(1);
});

test('disabling a core Analyze tool shows the inline warning banner and removes its public surfaces', async ({ page }) => {
  await setTheme(page, representativeThemes[0]);

  await setToolEnabled(page, 'Deck Analysis', false);

  await expect(page.locator('.admin-banner--warn')).toContainText('Deck Analysis');
  await expect(page.locator('.admin-banner--warn')).toContainText('core Analyze workflow');

  await page.goto('/');
  await expect(page.locator('.hub-grid a[href="/deck-analysis"]')).toHaveCount(0);
  await expect(page.locator('#deck-tool-nav a[href="/deck-analysis"]')).toHaveCount(0);
});


async function restoreAllTogglesOn(page: Page): Promise<void> {
  for (const label of toolLabelsToReset) {
    await setToolEnabled(page, label, true);
  }
}

function getAdminToolRow(page: Page, label: string) {
  return page.locator('tbody tr').filter({
    has: page.locator('td[data-label="Tool"] span', { hasText: label }),
  });
}

async function setTheme(page: Page, theme: (typeof representativeThemes)[number]): Promise<void> {
  await page.context().addCookies([
    {
      name: 'deckflow-theme',
      value: theme,
      url: baseUrl,
    },
  ]);
}

async function assertNoHorizontalOverflow(page: Page, message: string): Promise<void> {
  const hasNoOverflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
  expect(hasNoOverflow, message).toBeTruthy();
}
