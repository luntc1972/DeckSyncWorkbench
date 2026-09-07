import { expect, test } from '@playwright/test';
import { mkdtempSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { acquireAdminLockForTest, releaseAdminLockForTest } from './support/admin-lock';
import { setToolEnabled } from './support/admin-tools';
import { uiDesignDir } from './support/ui-design-dir';

import { resolveE2EPort } from './support/e2e-port';

const baseUrl = `http://localhost:${resolveE2EPort()}`;
const screenshotDir = uiDesignDir('deck-history');

const DECK_V1 = [
  'Commander',
  '1 Zur the Enchanter',
  '',
  'Deck',
  '1 Sol Ring',
  '1 Arcane Signet',
  '1 Brainstorm',
  '10 Plains',
  '10 Island',
  '10 Swamp',
].join('\n');

const DECK_V2 = [
  'Commander',
  '1 Zur the Enchanter',
  '',
  'Deck',
  '1 Sol Ring',
  '1 Arcane Signet',
  '1 Mystic Remora',
  '10 Plains',
  '10 Island',
  '10 Swamp',
].join('\n');

const themes = [
  { name: 'classic', cookie: 'site.css' },
  { name: 'azorius', cookie: 'site-azorius.css' },
  { name: 'nyx', cookie: 'site-nyx.css' },
] as const;

type LockHandle = Awaited<ReturnType<typeof acquireAdminLockForTest>>;

let heldLock: LockHandle | null = null;

test.describe.configure({ mode: 'serial' });

test.beforeEach(async ({ page }) => {
  heldLock = await acquireAdminLockForTest(page);
  await setToolEnabled(page, 'Deck History', true);
});

test.afterEach(async ({ page }) => {
  try {
    await setToolEnabled(page, 'Deck History', false);
  } finally {
    await releaseAdminLockForTest(heldLock);
    heldLock = null;
  }
});

test('/deck-history renders the form when the flag is ON', async ({ page }) => {
  const response = await page.goto('/deck-history');
  expect(response?.ok(), '/deck-history should return 200 with flag ON').toBeTruthy();

  await expect(page.locator('h1')).toHaveText('Deck History');
  await expect(page.locator('input[type="file"][name="historyFile"]')).toBeVisible();
  await expect(page.locator('#deck-history-input-source')).toBeVisible();
  await expect(page.locator('#deck-history-notes')).toBeVisible();
  const mainForm = page.locator('form[action="/deck-history"]').first();
  await expect(mainForm).toBeVisible();
  await expect(mainForm).toHaveAttribute('data-cache-key', 'deck-history');
  await page.locator('#deck-history-input-source').selectOption('PublicUrl');
  const bridgeHint = page.locator('details.deckflow-bridge-hint');
  await expect(bridgeHint).toBeAttached();
  await expect(bridgeHint.locator('summary')).toContainText('DeckFlow Bridge extension');
  await expect(page.locator('.history-timeline')).toHaveCount(0);
});

test('creates history, intercepts download, appends a second version, and captures screenshots across themes', async ({
  page,
}) => {
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);

  const projectName = test.info().project.name;
  const downloadDir = mkdtempSync(join(tmpdir(), 'deck-history-smoke-'));
  const uploadJsonPath = join(downloadDir, `${projectName}-download.json`);
  const finalHistoryJsonPath = join(downloadDir, `${projectName}-final-history.json`);

  await page.goto('/deck-history');
  await expect(page.locator('h1')).toHaveText('Deck History');
  await page.locator('#deck-history-input-source').selectOption('PasteText');
  await page.locator('#deck-history-deck-text').fill(DECK_V1);
  await page.locator('#deck-history-deck-name').fill('Zur Logbook');
  await page.locator('#deck-history-notes').fill('Initial list.');
  await page.getByRole('button', { name: 'Update history' }).click();

  await expect(page.locator('.history-timeline')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('.success-banner')).toContainText('Started a new history — version 1 saved.');
  await expect(page.locator('.success-banner')).toContainText(
    'To add the next version: update your deck, import it above, and press Update history again — your history carries forward on this page.',
  );
  await expect(page.locator('.warning-banner')).toContainText(
    'Deck has 34 cards — Commander decks run 100. Snapshot saved anyway.',
  );
  await expect(page.locator('.result-panel h2')).toHaveText([
    'Timeline',
    'Save your history',
    'Compare versions',
    'AI prompt — "How has this deck evolved?"',
  ]);
  await expect(page.locator('.history-timeline tbody tr').first()).toContainText('Initial list.');
  const promptPanel = page.locator('.result-panel').filter({
    has: page.getByRole('heading', { name: 'AI prompt — "How has this deck evolved?"' }),
  });
  await expect(promptPanel).toContainText(
    'Add a second version to generate the evolution prompt.',
  );

  const promptTextarea = page.locator('#deck-history-prompt');
  await expect(promptTextarea).toHaveCount(0);

  const downloadButton = page.locator('[data-prompt-download-submit]');
  // deck-sync.ts demotes every download button to type="button" so it can never be a
  // form's implicit default submitter. Asserted here rather than in batch-G's G1 loop
  // because this button only renders once a history exists, so a bare GET never sees it.
  await expect(downloadButton).toHaveAttribute('type', 'button');

  const downloadResponsePromise = page.waitForResponse((response) =>
    response.url().includes('/deck-history/download') && response.request().method() === 'POST',
  );
  // Why a real click, not a hand-rolled fetch: this spec previously replayed the request
  // itself inside page.evaluate using submitter.form, which never ran deck-sync.ts's
  // registered click handler. That is how a completely dead download button shipped to
  // prod green (debug session deck-history-download-noop) — the test resolved the form
  // with the correct API while the handler under test used the wrong one.
  const downloadPromise = page.waitForEvent('download');
  await downloadButton.click();
  const downloadResponse = await downloadResponsePromise;
  expect(downloadResponse.ok(), 'download response should succeed').toBeTruthy();
  expect(downloadResponse.headers()['x-deckflow-filename']).toMatch(
    /^deck-history-zur-logbook-\d{8}\.json$/,
  );
  // The blob save is the user-visible outcome; without it the button is a no-op even
  // when the POST succeeds.
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toMatch(/^deck-history-zur-logbook-\d{8}\.json$/);

  // Round-trip the file the browser actually wrote, not the response body: Playwright
  // cannot serve a body back once Chromium diverts it into a download, and the saved
  // bytes are the stronger assertion anyway.
  await download.saveAs(uploadJsonPath);

  await page.goto('/deck-history');
  await page.locator('#deck-history-input-source').selectOption('PasteText');
  await page.locator('input[type="file"][name="historyFile"]').setInputFiles(uploadJsonPath);
  await page.locator('#deck-history-deck-text').fill(DECK_V2);
  await page.locator('#deck-history-deck-name').fill('Zur Logbook');
  await page.locator('#deck-history-notes').fill('Swapped Brainstorm for Mystic Remora.');
  await page.getByRole('button', { name: 'Update history' }).click();

  await expect(page.locator('.history-timeline tbody tr')).toHaveCount(2, { timeout: 30_000 });
  await expect(page.locator('.success-banner')).toContainText('Version 2 added.');
  await expect(page.locator('.success-banner')).toContainText(
    'To add the next version: update your deck, import it above, and press Update history again — your history carries forward on this page.',
  );
  await expect(page.locator('.history-diff')).toBeVisible();
  await expect(promptPanel).not.toContainText(
    'Add a second version to generate the evolution prompt.',
  );

  const addsPanel = page.locator('.history-diff__panel').filter({
    has: page.getByRole('heading', { name: 'Adds' }),
  });
  const cutsPanel = page.locator('.history-diff__panel').filter({
    has: page.getByRole('heading', { name: 'Cuts' }),
  });
  await expect(addsPanel).toContainText('Mystic Remora');
  await expect(cutsPanel).toContainText('Brainstorm');
  await expect(promptTextarea).toBeVisible();
  const promptText = await promptTextarea.inputValue();
  expect(promptText.trim()).not.toBe('');

  const copyButton = promptPanel.locator('[data-copy-target="deck-history-prompt"]');
  await copyButton.click();
  await expect(copyButton).toHaveText('Copied');
  const clipboardText = await page.evaluate(() => navigator.clipboard.readText());
  expect(clipboardText.startsWith('You are an expert Magic')).toBeTruthy();
  expect(clipboardText.trimEnd()).toBe(promptText.trimEnd());

  const finalDownloadResponsePromise = page.waitForResponse((response) =>
    response.url().includes('/deck-history/download') && response.request().method() === 'POST',
  );
  const finalDownloadPromise = page.waitForEvent('download');
  await page.locator('[data-prompt-download-submit]').click();
  const finalDownloadResponse = await finalDownloadResponsePromise;
  expect(finalDownloadResponse.ok(), 'final download response should succeed').toBeTruthy();
  await (await finalDownloadPromise).saveAs(finalHistoryJsonPath);

  for (const theme of themes) {
    await page.context().clearCookies();
    await page.context().addCookies([{ name: 'deckflow-theme', value: theme.cookie, url: baseUrl }]);

    await page.goto('/deck-history');
    await page.evaluate(() => {
      window.sessionStorage.removeItem('decksync-form-state-deck-history');
      window.sessionStorage.removeItem('decksync-form-state-deck-history:savedAt');
      window.sessionStorage.removeItem('deckflow.last-deck');
    });
    await page.goto('/deck-history');
    await expect(page.locator('h1')).toHaveText('Deck History');

    const formScreenshotPath = join(
      screenshotDir,
      `deck-history-form-${theme.name}-${projectName}.png`,
    );
    await page.screenshot({ path: formScreenshotPath, fullPage: true });

    await page.locator('input[type="file"][name="historyFile"]').setInputFiles(finalHistoryJsonPath);
    await page.getByRole('button', { name: 'Update history' }).click();
    await expect(page.locator('.history-timeline tbody tr')).toHaveCount(2, { timeout: 30_000 });

    const resultsScreenshotPath = join(
      screenshotDir,
      `deck-history-results-${theme.name}-${projectName}.png`,
    );
    await page.screenshot({ path: resultsScreenshotPath, fullPage: true });
  }
});

test('with tool.deck-history.enabled OFF, /deck-history returns 404 and the Home tile is absent', async ({
  page,
}) => {
  await setToolEnabled(page, 'Deck History', false);

  const response = await page.goto('/deck-history');
  expect(response?.status(), '/deck-history should be 404 with flag OFF').toBe(404);

  await page.goto('/');
  await expect(page.locator('.hub-card[href$="/deck-history"]')).toHaveCount(0);
});
