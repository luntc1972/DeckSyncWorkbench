import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { acquireAdminLockForTest, releaseAdminLockForTest } from './support/admin-lock';
import { getToolEnabled, setToolEnabled } from './support/admin-tools';
import { clickManabasePillRadio } from './support/manabase-pill';

// Live smoke spec for the /bracket Bracket Check tool (flag-gated, tool.bracket.enabled).
//
// What this spec covers:
//   1. GET /bracket renders the empty classification form when the flag is ON.
//   2. POST a known high-power Commander deck (B4 via 5 GCs + MLD) with target B3:
//      asserts the bracket-badge--bN modifier, WHY THIS BRACKET reasons, FLOOR VIOLATIONS,
//      STARTER CUTS, effective-date stamp (BRACKET-05), and a non-empty copy-prompt textarea.
//   3. Captures 6 screenshots across Classic / Azorius / Nyx themes at the current Playwright
//      project viewport (chromium-desktop 1280px or chromium-mobile 390px).
//   4. With the flag turned OFF: /bracket returns 404 and the Home tile / nav tab are absent.
//
// Run:
//   1. Start the app headless: scripts/run-web-test.sh (sets DECKFLOW_DISABLE_AUTO_BROWSER=true)
//   2. cd DeckFlow.Web && DECKFLOW_DISABLE_AUTO_BROWSER=true \
//        npx --no-install playwright test e2e/bracket-smoke.spec.ts --reporter=line
//
// Admin creds: read from FEEDBACK_ADMIN_USER / FEEDBACK_ADMIN_PASSWORD env vars.
// A transient flag toggle is used for this run (reverted in afterEach). No prod flag seed change.

import { resolveE2EPort } from './support/e2e-port';

const baseUrl = `http://localhost:${resolveE2EPort()}`;

// Why: __dirname is DeckFlow.Web/e2e → resolve up 2 levels for the repo root, then into
// .planning/ui-design/cycle13/screenshots/ where Phase 75 screenshots already live.
const screenshotDir = resolve(__dirname, '../../.planning/ui-design/cycle13/screenshots');

// A high-power Commander deck that classifies above B3 (→ B4) via:
//   - 5 Game Changers (Force of Will, Cyclonic Rift, Demonic Tutor, Vampiric Tutor, Necropotence)
//     ≥ 4 GCs triggers the B4 hard-floor per BracketClassifier.HardFloorGameChangerCount.
//   - 1 mass land denial card (Armageddon) — also triggers the B4 hard-floor independently.
// No URL import is needed; pasted Moxfield format is parsed locally (no Scryfall, no network).
// The deck does not need to be exactly 100 cards; BracketClassificationService only checks
// that at least one entry is present. Filler lands make the list look realistic.
const HIGH_POWER_DECK = [
  'Commander',
  '1 Zur the Enchanter',
  '',
  'Deck',
  '1 Force of Will',
  '1 Cyclonic Rift',
  '1 Demonic Tutor',
  '1 Vampiric Tutor',
  '1 Necropotence',
  '1 Armageddon',
  '1 Sol Ring',
  '1 Arcane Signet',
  '10 Plains',
  '10 Island',
  '10 Swamp',
].join('\n');

// Theme cookie values (CSS filename) matching the deckflow-theme cookie convention.
const themes = [
  { name: 'classic', cookie: 'site.css' },
  { name: 'azorius', cookie: 'site-azorius.css' },
  { name: 'nyx', cookie: 'site-nyx.css' },
] as const;

type LockHandle = Awaited<ReturnType<typeof acquireAdminLockForTest>>;

let heldLock: LockHandle | null = null;
let bracketCheckWasEnabled = false;

test.describe.configure({ mode: 'serial' });

test.beforeEach(async ({ page }) => {
  // Serialize against other /Admin/* specs that share the same process-level lock file.
  heldLock = await acquireAdminLockForTest(page);
  bracketCheckWasEnabled = await getToolEnabled(page, 'Bracket Check');
  // Force tool.bracket.enabled ON for this run rather than trusting the seed default: the
  // flag-OFF test below toggles it off, and afterEach reverts it regardless of pass/fail.
  await setToolEnabled(page, 'Bracket Check', true);
});

test.afterEach(async ({ page }) => {
  try {
    // Restore the captured flag state so no persistent state leaks between test runs.
    await setToolEnabled(page, 'Bracket Check', bracketCheckWasEnabled);
  } finally {
    await releaseAdminLockForTest(heldLock);
    heldLock = null;
  }
});

// ── Test 1: empty GET ──────────────────────────────────────────────────────────────────────────

test('/bracket renders the classification form when the flag is ON', async ({ page }) => {
  const response = await page.goto('/bracket');
  expect(response?.ok(), '/bracket should return 200 with flag ON').toBeTruthy();

  // Hero section and form must be present.
  await expect(page.locator('h1')).toHaveText('Bracket Check');
  await expect(page.locator('form[action*="/bracket"]')).toBeVisible();
  await expect(page.locator('#bracket-input-source')).toBeVisible();

  // Target picker pills (B1–B5) must be present for the optional target selection.
  await expect(page.locator('input[name="TargetBracketNumber"][value="3"]')).toBeAttached();

  // No result panel before a deck is submitted.
  await expect(page.locator('.bracket-badge')).toHaveCount(0);
});

// ── Test 2: full classify → balancer flow ──────────────────────────────────────────────────────

test('POST classifies a high-power deck and renders badge / reasons / violations / cuts / stamp / copy-prompt', async ({ page }) => {
  await page.goto('/bracket');

  // Switch to paste-text input mode.
  await page.locator('#bracket-input-source').selectOption('PasteText');
  await page.locator('#bracket-deck-text').fill(HIGH_POWER_DECK);

  // Select target bracket B3 (Upgraded). With 5 GCs + MLD the deck lands at B4, triggering
  // IsOverTarget, floor violations, and starter cuts.
    await clickManabasePillRadio(page, 'TargetBracketNumber', '3');

  // Submit. Bracket classification is computed locally — no Scryfall / external HTTP needed.
  await page.getByRole('button', { name: 'Classify deck' }).click();

  // Wait for the result section (server-side POST; allows up to 30 s for slow CI).
  await expect(page.locator('.bracket-badge')).toBeVisible({ timeout: 30_000 });

  // ── Badge: must carry a bracket-badge--bN modifier ──
  const badge = page.locator('.bracket-badge');
  await expect(badge).toBeVisible();
  const badgeClass = await badge.getAttribute('class') ?? '';
  expect(badgeClass, 'bracket-badge must carry a --b{N} level modifier').toMatch(
    /bracket-badge--b[1-5]/,
  );

  // ── WHY THIS BRACKET reasons ──
  await expect(page.locator('.manabase-verdict')).toBeVisible();
  await expect(
    page.locator('.manabase-verdict-heading', { hasText: 'WHY THIS BRACKET' }),
  ).toBeVisible();
  await expect(page.locator('.manabase-verdict .manabase-verdict-list li').first()).toBeVisible();

  // ── FLOOR VIOLATIONS (IsOverTarget is true: B4 > B3) ──
  await expect(
    page.locator('.manabase-verdict-heading', { hasText: 'FLOOR VIOLATIONS' }),
  ).toBeVisible();
  await expect(page.locator('.bracket-violation-list')).toBeVisible();
  // At least one violation item must be present.
  const violationCount = await page.locator('.bracket-violation').count();
  expect(violationCount, 'floor violations list must have at least one row').toBeGreaterThan(0);
  // Armageddon is a mass-land-denial violation — its tag pill must render.
  await expect(page.locator('.bracket-violation__tag--mld').first()).toBeVisible();

  // ── STARTER CUTS ──
  await expect(
    page.locator('.manabase-verdict-heading', { hasText: 'STARTER CUTS' }),
  ).toBeVisible();
  const cutsList = page.locator('.manabase-verdict-list').last();
  await expect(cutsList.locator('li').first()).toBeVisible();

  // ── Effective-date stamp (BRACKET-05) ──
  const stamp = page.locator('.bracket-stamp');
  await expect(stamp).toBeVisible();
  await expect(stamp).toContainText('Game Changers list effective');

  // ── Copy-prompt textarea (balancer artifact must be non-empty) ──
  // The collapsible may be closed; open it if necessary before reading the value.
  const copyDetails = page.locator('details:has(#bracket-prompt)');
  if ((await copyDetails.count()) > 0) {
    const summaryEl = copyDetails.locator('summary').first();
    if (!(await copyDetails.getAttribute('open'))) {
      await summaryEl.click();
    }
  }
  const promptTextarea = page.locator('#bracket-prompt');
  await expect(promptTextarea).toBeAttached();
  const promptValue = await promptTextarea.inputValue();
  expect(promptValue.trim(), 'copy-prompt textarea must be non-empty').not.toBe('');
  // Why: the ChatGPT variant always emits "## WHY THIS BRACKET" in the prompt body —
  // confirms the classification block was included, not just a header or empty artifact.
  expect(promptValue, 'copy-prompt must include WHY THIS BRACKET classification section').toContain(
    'WHY THIS BRACKET',
  );
});

// ── Test 3: cross-theme screenshots ──────────────────────────────────────────────────────────

test('captures screenshots across Classic / Azorius / Nyx at the current project viewport', async ({
  page,
}) => {
  mkdirSync(screenshotDir, { recursive: true });

  const projectName = test.info().project.name;

  for (const theme of themes) {
    // Set the deckflow-theme cookie (same mechanism as the theme-selector widget in the UI).
    await page.context().addCookies([
      { name: 'deckflow-theme', value: theme.cookie, url: baseUrl },
    ]);

    // Navigate, fill, and POST so the result panel is visible in the screenshot.
    await page.goto('/bracket');
    await page.locator('#bracket-input-source').selectOption('PasteText');
    await page.locator('#bracket-deck-text').fill(HIGH_POWER_DECK);
  await clickManabasePillRadio(page, 'TargetBracketNumber', '3');
    await page.getByRole('button', { name: 'Classify deck' }).click();
    await expect(page.locator('.bracket-badge')).toBeVisible({ timeout: 30_000 });

    // Open the copy-prompt collapsible so it appears in the full-page screenshot.
    const copyDetails = page.locator('details:has(#bracket-prompt)');
    if ((await copyDetails.count()) > 0) {
      const summaryEl = copyDetails.locator('summary').first();
      if (!(await copyDetails.getAttribute('open'))) {
        await summaryEl.click();
      }
    }

    // Capture. Filename: bracket-{theme}-{project}.png (e.g. bracket-azorius-chromium-mobile.png).
    const screenshotPath = join(screenshotDir, `bracket-${theme.name}-${projectName}.png`);
    await page.screenshot({ path: screenshotPath, fullPage: true });
  }
});

// ── Test 4: flag OFF gating ───────────────────────────────────────────────────────────────────

test('with tool.bracket.enabled OFF, /bracket returns 404 and the tile/tab are absent', async ({
  page,
}) => {
  // The flag is ON from beforeEach. Disable it to exercise the gated-off state.
  // afterEach restores the flag state captured before this test.
  await setToolEnabled(page, 'Bracket Check', false);

  // /bracket must return 404.
  const bracketResponse = await page.goto('/bracket');
  expect(bracketResponse?.status(), '/bracket should be 404 with flag OFF').toBe(404);

  // Home page must not have a Bracket hub tile.
  await page.goto('/');
  await expect(page.locator('.hub-card[href$="/bracket"]')).toHaveCount(0);

  // Any Analyze tool nav must not have a Bracket tab link.
  await page.goto('/deck-analysis');
  await expect(page.locator('#deck-tool-nav a[href$="/bracket"]')).toHaveCount(0);
});
