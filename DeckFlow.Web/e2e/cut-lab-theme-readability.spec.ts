import { expect, test, type Locator, type Page } from '@playwright/test';
import { acquireAdminLockForTest, releaseAdminLockForTest } from './support/admin-lock';
import { setToolEnabled } from './support/admin-tools';
import { contrastRatio, resolveContrast, type RgbColor } from './support/contrast';
import { clickManabasePillRadio } from './support/manabase-pill';

import { resolveE2EPort } from './support/e2e-port';

const baseUrl = `http://localhost:${resolveE2EPort()}`;

const themes = [
  { name: 'classic', cookie: 'site.css' },
  { name: 'abzan', cookie: 'site-abzan.css' },
  { name: 'azorius', cookie: 'site-azorius.css' },
  { name: 'bant', cookie: 'site-bant.css' },
  { name: 'boros', cookie: 'site-boros.css' },
  { name: 'commander-table', cookie: 'site-commander-table.css' },
  { name: 'dimir', cookie: 'site-dimir.css' },
  { name: 'esper', cookie: 'site-esper.css' },
  { name: 'golgari', cookie: 'site-golgari.css' },
  { name: 'grixis', cookie: 'site-grixis.css' },
  { name: 'gruul', cookie: 'site-gruul.css' },
  { name: 'izzet', cookie: 'site-izzet.css' },
  { name: 'jeskai', cookie: 'site-jeskai.css' },
  { name: 'jund', cookie: 'site-jund.css' },
  { name: 'mardu', cookie: 'site-mardu.css' },
  { name: 'naya', cookie: 'site-naya.css' },
  { name: 'nyx', cookie: 'site-nyx.css' },
  { name: 'orzhov', cookie: 'site-orzhov.css' },
  { name: 'planeswalker-dark', cookie: 'site-planeswalker-dark.css' },
  { name: 'rakdos', cookie: 'site-rakdos.css' },
  { name: 'selesnya', cookie: 'site-selesnya.css' },
  { name: 'simic', cookie: 'site-simic.css' },
  { name: 'sultai', cookie: 'site-sultai.css' },
  { name: 'temur', cookie: 'site-temur.css' },
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
type FocusIndicatorSnapshot = {
  focusVisible: boolean;
  source: string;
  background: RgbColor;
  indicator: RgbColor | null;
};

let heldLock: LockHandle | null = null;

test.describe.configure({ mode: 'serial' });

const formatColor = ({ r, g, b }: RgbColor): string => `rgb(${r}, ${g}, ${b})`;

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
  await page.locator('details.cutlab-role-group').filter({ hasText: 'Lands' }).locator(':scope > summary').click();
  await expect(page.locator('[data-cut-lab-lock-role="lands"]')).toBeVisible();
  await expect(page.locator('tr[data-cut-lab-card="Zur the Enchanter"]')).toHaveAttribute('data-cut-lab-commander', 'true');
};

const ensureDetailsOpen = async (details: Locator): Promise<void> => {
  if ((await details.getAttribute('open')) !== null) {
    return;
  }

  await details.locator(':scope > summary').click();
  await expect(details).toHaveAttribute('open', '');
};

const ensureCutRoundsVisible = async (page: Page): Promise<void> => {
  const findings = page.locator('[data-cut-lab-structural-findings]');
  await expect(findings).toBeVisible();

  const stickyBar = page.locator('.cutlab-sticky-bar[data-cut-lab-sticky-target]');
  if (!(await stickyBar.isVisible())) {
    const acceptButton = page.locator('.cutlab-proposal .cutlab-decision-btn--accept');
    const decideResponse = page.waitForResponse(response =>
      response.url().includes('/api/cut-lab/decide') && response.request().method() === 'POST');
    await acceptButton.click();
    const response = await decideResponse;
    expect(response.ok(), 'Cut Lab decide request must succeed while revealing readability targets').toBeTruthy();
  }

  await expect(stickyBar).toBeVisible();
  await expect(page.locator('.cutlab-proposal .cutlab-decision-btn--accept')).toBeVisible();
};

const createFastManaPackage = async (page: Page): Promise<Locator> => {
  const packagesDetails = page.locator('#cut-lab-section-packages');
  await ensureDetailsOpen(packagesDetails);

  const existing = page.locator('[data-cut-lab-package-id]').filter({ hasText: 'Fast mana' });
  if (await existing.count()) {
    const existingToggle = existing.locator('input[data-cut-lab-package-toggle]');
    await existingToggle.check();
    await expect(existing).toHaveClass(/cutlab-package--locked/);
    return existing;
  }

  await page.locator('select[data-cut-lab-package-card="Sol Ring"]').selectOption('__new__');
  await page.locator('[data-cut-lab-new-package-input]').fill('Fast mana');
  await page.locator('[data-cut-lab-new-package-save]').click();
  await page.locator('select[data-cut-lab-package-card="Arcane Signet"]').selectOption({ label: 'Fast mana' });

  const packagePanel = page.locator('[data-cut-lab-package-id]').filter({ hasText: 'Fast mana' });
  await expect(packagePanel).toBeVisible({ timeout: 30_000 });

  const packageToggle = packagePanel.locator('input[data-cut-lab-package-toggle]');
  await packageToggle.check();
  await expect(packagePanel).toHaveClass(/cutlab-package--locked/);
  return packagePanel;
};

const openCardModal = async (trigger: Locator, page: Page): Promise<Locator> => {
  const modal = page.locator('dialog#cutlab-card-modal');
  await trigger.click();
  await expect(modal).toHaveAttribute('open', '');
  await expect(modal.locator('[data-cutlab-modal-oracle]')).toBeVisible();
  return modal;
};

const assertContrastFloor = async (
  themeName: string,
  elementName: string,
  locator: Locator,
  minimumRatio: number,
): Promise<void> => {
  await expect(locator, `${themeName}: ${elementName} should be visible`).toBeVisible();
  const { foreground, background, ratio } = await resolveContrast(locator);
  expect(
    ratio,
    `${themeName}: ${elementName} contrast ${ratio.toFixed(2)} is below ${minimumRatio} (fg ${formatColor(foreground)} vs bg ${formatColor(background)})`,
  ).toBeGreaterThanOrEqual(minimumRatio);
};

const resolveFocusIndicator = async (page: Page, locator: Locator): Promise<FocusIndicatorSnapshot> => {
  await page.keyboard.press('Tab');
  return locator.evaluate((element) => {
    type BrowserRgbColor = { r: number; g: number; b: number };
    type BrowserRgbaColor = BrowserRgbColor & { a: number };

    const clampChannelValue = (value: number): number => Math.min(255, Math.max(0, Math.round(value)));
    const clampAlphaValue = (value: number): number => Math.min(1, Math.max(0, value));
    const rgbPattern =
      /^rgba?\(\s*(\d{1,3}(?:\.\d+)?)\s*,\s*(\d{1,3}(?:\.\d+)?)\s*,\s*(\d{1,3}(?:\.\d+)?)(?:\s*,\s*(\d*\.?\d+))?\s*\)$/i;
    const hexPattern = /^#([0-9a-f]{3,8})$/i;

    const parseColor = (input: string): BrowserRgbaColor => {
      const trimmed = input.trim();
      if (/^transparent$/i.test(trimmed)) {
        return { r: 0, g: 0, b: 0, a: 0 };
      }

      const rgbMatch = trimmed.match(rgbPattern);
      if (rgbMatch) {
        return {
          r: clampChannelValue(Number(rgbMatch[1])),
          g: clampChannelValue(Number(rgbMatch[2])),
          b: clampChannelValue(Number(rgbMatch[3])),
          a: clampAlphaValue(rgbMatch[4] === undefined ? 1 : Number(rgbMatch[4])),
        };
      }

      const hexMatch = trimmed.match(hexPattern);
      if (hexMatch) {
        const [, hex] = hexMatch;
        if (hex.length === 3 || hex.length === 4) {
          const [r, g, b, a = 'f'] = hex.split('');
          return {
            r: clampChannelValue(Number.parseInt(`${r}${r}`, 16)),
            g: clampChannelValue(Number.parseInt(`${g}${g}`, 16)),
            b: clampChannelValue(Number.parseInt(`${b}${b}`, 16)),
            a: clampAlphaValue(Number.parseInt(`${a}${a}`, 16) / 255),
          };
        }

        if (hex.length === 6 || hex.length === 8) {
          return {
            r: clampChannelValue(Number.parseInt(hex.slice(0, 2), 16)),
            g: clampChannelValue(Number.parseInt(hex.slice(2, 4), 16)),
            b: clampChannelValue(Number.parseInt(hex.slice(4, 6), 16)),
            a: clampAlphaValue(hex.length === 8 ? Number.parseInt(hex.slice(6, 8), 16) / 255 : 1),
          };
        }
      }

      throw new Error(`Unsupported CSS color: ${input}`);
    };

    const composite = (foreground: BrowserRgbaColor, background: BrowserRgbaColor): BrowserRgbaColor => {
      const alpha = foreground.a + (background.a * (1 - foreground.a));
      if (alpha <= 0) {
        return { r: 255, g: 255, b: 255, a: 0 };
      }

      return {
        r: clampChannelValue(((foreground.r * foreground.a) + (background.r * background.a * (1 - foreground.a))) / alpha),
        g: clampChannelValue(((foreground.g * foreground.a) + (background.g * background.a * (1 - foreground.a))) / alpha),
        b: clampChannelValue(((foreground.b * foreground.a) + (background.b * background.a * (1 - foreground.a))) / alpha),
        a: clampAlphaValue(alpha),
      };
    };

    const toRgb = (color: BrowserRgbaColor): BrowserRgbColor => ({ r: color.r, g: color.g, b: color.b });

    const effectiveBackground = (node: HTMLElement | null): BrowserRgbColor => {
      const fallback: BrowserRgbaColor = { r: 255, g: 255, b: 255, a: 1 };
      let current = node;
      let effective: BrowserRgbaColor | null = null;

      while (current !== null) {
        const background = parseColor(getComputedStyle(current).backgroundColor);
        if (background.a > 0) {
          effective = effective === null ? background : composite(effective, background);
          if (effective.a >= 0.999) {
            break;
          }
        }

        current = current.parentElement;
      }

      if (effective === null) {
        effective = fallback;
      } else if (effective.a < 0.999) {
        effective = composite(effective, fallback);
      }

      return toRgb(effective);
    };

    const splitShadowList = (value: string): string[] => {
      const parts: string[] = [];
      let current = '';
      let depth = 0;
      for (const char of value) {
        if (char === '(') {
          depth += 1;
        } else if (char === ')') {
          depth = Math.max(0, depth - 1);
        }

        if (char === ',' && depth === 0) {
          if (current.trim().length > 0) {
            parts.push(current.trim());
          }
          current = '';
          continue;
        }

        current += char;
      }

      if (current.trim().length > 0) {
        parts.push(current.trim());
      }

      return parts;
    };

    const extractShadowColor = (boxShadow: string): BrowserRgbColor | null => {
      if (boxShadow === 'none') {
        return null;
      }

      for (const shadow of splitShadowList(boxShadow)) {
        const match = shadow.match(/(rgba?\([^)]+\)|#[0-9a-f]{3,8}|transparent)/i);
        if (!match) {
          continue;
        }

        const color = parseColor(match[1]);
        if (color.a > 0) {
          return toRgb(color);
        }
      }

      return null;
    };

    const target = element as HTMLElement;
    const beforeStyle = getComputedStyle(target);
    const backgroundColorBeforeValue = beforeStyle.backgroundColor;
    const borderTopColorBeforeValue = beforeStyle.borderTopColor;
    const backgroundBefore = effectiveBackground(target);
    const backgroundColorBefore = parseColor(backgroundColorBeforeValue);
    target.focus();
    const focusVisible = target.matches(':focus-visible');
    const afterStyle = getComputedStyle(target);
    const backgroundColorAfter = parseColor(afterStyle.backgroundColor);

    const outlineWidth = Number.parseFloat(afterStyle.outlineWidth || '0');
    if (outlineWidth > 0 && afterStyle.outlineStyle !== 'none') {
      const outlineColor = parseColor(afterStyle.outlineColor);
      if (outlineColor.a > 0) {
        return { focusVisible, source: 'outline', background: backgroundBefore, indicator: toRgb(outlineColor) };
      }
    }

    const shadowColor = extractShadowColor(afterStyle.boxShadow);
    if (shadowColor !== null) {
      return { focusVisible, source: 'box-shadow', background: backgroundBefore, indicator: shadowColor };
    }

    const borderWidth = Number.parseFloat(afterStyle.borderTopWidth || '0');
    const borderColorAfter = parseColor(afterStyle.borderTopColor);
    const borderColorBefore = parseColor(borderTopColorBeforeValue);
    if (
      borderWidth > 0
      && borderColorAfter.a > 0
      && (
        borderColorAfter.r !== borderColorBefore.r
        || borderColorAfter.g !== borderColorBefore.g
        || borderColorAfter.b !== borderColorBefore.b
      )
    ) {
      return { focusVisible, source: 'border', background: backgroundBefore, indicator: toRgb(borderColorAfter) };
    }

    if (
      backgroundColorAfter.a > 0
      && (
        backgroundColorAfter.r !== backgroundColorBefore.r
        || backgroundColorAfter.g !== backgroundColorBefore.g
        || backgroundColorAfter.b !== backgroundColorBefore.b
      )
    ) {
      return { focusVisible, source: 'background', background: backgroundBefore, indicator: toRgb(backgroundColorAfter) };
    }

    return { focusVisible, source: 'none', background: backgroundBefore, indicator: null };
  });
};

const assertFocusIndicatorContrast = async (
  themeName: string,
  elementName: string,
  page: Page,
  locator: Locator,
  minimumRatio: number,
): Promise<void> => {
  await expect(locator, `${themeName}: ${elementName} should be visible before focus-visible check`).toBeVisible();
  const snapshot = await resolveFocusIndicator(page, locator);
  expect(snapshot.focusVisible, `${themeName}: ${elementName} should match :focus-visible after focus()`).toBe(true);
  expect(snapshot.indicator, `${themeName}: ${elementName} should expose a focus indicator color`).not.toBeNull();

  const indicator = snapshot.indicator!;
  const ratio = contrastRatio(indicator, snapshot.background);
  expect(
    ratio,
    `${themeName}: ${elementName} focus indicator ${snapshot.source} contrast ${ratio.toFixed(2)} is below ${minimumRatio} (indicator ${formatColor(indicator)} vs bg ${formatColor(snapshot.background)})`,
  ).toBeGreaterThanOrEqual(minimumRatio);
};

test.beforeEach(async ({ page }) => {
  heldLock = await acquireAdminLockForTest(page);
  await setToolEnabled(page, 'Cut Lab', true);
});

test.afterEach(async () => {
  await releaseAdminLockForTest(heldLock);
  heldLock = null;
});

test('keeps the Cut Lab named elements readable across every supported theme', async ({ page }) => {
  for (const theme of themes) {
    await page.context().clearCookies();
    await page.context().addCookies([{ name: 'deckflow-theme', value: theme.cookie, url: baseUrl }]);

    await importPool(page);
    await ensureCutRoundsVisible(page);

    const landsGroup = page.locator('details.cutlab-role-group').filter({ hasText: 'Lands' });
    await ensureDetailsOpen(landsGroup);

    const packagePanel = await createFastManaPackage(page);
    const packageToggle = packagePanel.locator('input[data-cut-lab-package-toggle]');
    const packageToggleLabel = packageToggle.locator('xpath=ancestor::label[1]');
    const packageMemberChip = packagePanel.locator('.kb-chip-area__chips .kb-chip').first();
    const packageHelper = page.locator('.cutlab-package-help');
    const findingsPanel = page.locator('[data-cut-lab-structural-findings]');
    const stickyBar = page.locator('.cutlab-sticky-bar[data-cut-lab-sticky-target]');
    const lockAllPill = page.locator('[data-cut-lab-lock-role="lands"]');
    const roleChip = landsGroup.locator('button.cutlab-role-chip').first();
    const selectTrigger = page.locator('.df-select__trigger').first();
    const planInput = page.locator('#cut-lab-primary-plan');
    const decisionButton = page.locator('.cutlab-decision-btn--accept').first();

    await expect(packagePanel, `${theme.name}: Fast mana package panel should exist for CLUP-19 package coverage`).toBeVisible();
    await expect(packagePanel, `${theme.name}: Fast mana package panel should be locked after the deterministic toggle step`).toHaveClass(/cutlab-package--locked/);
    await expect(packageToggle, `${theme.name}: Fast mana package toggle should exist for CLUP-19 package coverage`).toBeVisible();
    await expect(packageMemberChip, `${theme.name}: Fast mana package member chip should exist for CLUP-19 package coverage`).toBeVisible();

    // Lock pills are short, bold button labels, so WCAG AA permits the 3.0 large-text floor.
    await assertContrastFloor(theme.name, 'Lock All lands pill', lockAllPill, 3.0);
    // Role chips are compact chip buttons with emphasized labels, so 3.0 is the AA floor here.
    await assertContrastFloor(theme.name, 'Lands role chip', roleChip, 3.0);
    // Sticky round/count text is normal-size status copy, so it must clear the full 4.5 AA body-text floor.
    await assertContrastFloor(theme.name, 'sticky status bar', stickyBar, 4.5);
    // Structural findings carry explanatory body copy and evidence context, so they use the 4.5 AA body-text floor.
    await assertContrastFloor(theme.name, 'structural findings panel', findingsPanel, 4.5);
    // The enhanced df-select trigger renders normal form-control text, so it must clear 4.5 AA.
    await assertContrastFloor(theme.name, 'input source select trigger', selectTrigger, 4.5);
    // Plan textareas render editable body text, so the normal-text AA floor is 4.5.
    await assertContrastFloor(theme.name, 'primary plan input', planInput, 4.5);
    // Accept cut is a bold CTA button label, so the 3.0 large/bold UI-text floor applies.
    await assertContrastFloor(theme.name, 'accept-cut button', decisionButton, 3.0);
    const cardPopup = await openCardModal(roleChip, page);
    // The popup lock control is a bold CTA-style button label, so the 3.0 large/bold UI-text floor applies.
    await assertContrastFloor(theme.name, 'card popup lock button', cardPopup.locator('[data-cutlab-modal-lock]'), 3.0);
    await cardPopup.locator('[data-cutlab-modal-close]').click();
    await expect(cardPopup).not.toHaveAttribute('open', '');
    // Package helper copy is explanatory body text, so it must clear the 4.5 AA body-text floor.
    await assertContrastFloor(theme.name, 'package helper copy', packageHelper, 4.5);
    // Package panels contain ordinary heading/body copy, so the conservative body-text floor is 4.5.
    await assertContrastFloor(theme.name, 'Fast mana package panel', packagePanel, 4.5);
    // The package toggle label is rendered as a chip-style control, so the 3.0 large/bold UI-text floor applies.
    await assertContrastFloor(theme.name, 'Fast mana package toggle label', packageToggleLabel, 3.0);
    // Package member chips are pill/chip UI labels, so 3.0 is the AA floor for their emphasized text.
    await assertContrastFloor(theme.name, 'Fast mana package member chip', packageMemberChip, 3.0);

    await assertFocusIndicatorContrast(theme.name, 'input source select trigger', page, selectTrigger, 3.0);
    await assertFocusIndicatorContrast(theme.name, 'primary plan input', page, planInput, 3.0);
    await assertFocusIndicatorContrast(theme.name, 'accept-cut button', page, decisionButton, 3.0);
    await assertFocusIndicatorContrast(theme.name, 'Lock All lands pill', page, lockAllPill, 3.0);
    await assertFocusIndicatorContrast(theme.name, 'Lands role chip', page, roleChip, 3.0);
    await assertFocusIndicatorContrast(theme.name, 'Fast mana package toggle', page, packageToggle, 3.0);
  }
});
