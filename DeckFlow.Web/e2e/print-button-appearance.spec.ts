import { expect, test, type Page } from '@playwright/test';
import { resolveE2EPort } from './support/e2e-port';

// Screen (not print) appearance of the "Print results" button: it must render as
// a SECONDARY outline action beside the primary "Download session (.zip)" button,
// in both light and dark themes, on desktop and mobile. The two Playwright
// projects (chromium-desktop 1280, chromium-mobile 390) cover both viewports.
const deckProfileJson = `
\`\`\`json
{
  "deck_profile": {
    "format": "commander",
    "commander": "Sokka, Tenacious Tactician",
    "game_plan": "Value creatures into repeated combat triggers.",
    "primary_axes": ["combat"],
    "speed": "midrange",
    "strengths": [{ "name": "Efficient curve", "description": "Pressure on turns 1-3." }],
    "weaknesses": [{ "name": "Stack interaction", "description": "Weak to fast combo." }],
    "deck_needs": [{ "need": "Burst draw", "description": "Reload after a sweeper." }],
    "weak_slots": [],
    "synergy_tags": ["blink"],
    "question_answers": []
  }
}
\`\`\`
`;

async function renderAnalysis(page: Page): Promise<void> {
  const response = await page.goto('/deck-analysis');
  expect(response?.ok()).toBeTruthy();
  await page.locator('[data-prompt-show-step="2"][role="tab"]').click();
  await page.locator('select[name="TargetCommanderBracket"]').selectOption({ index: 1 });
  await page.locator('[data-prompt-show-step="3"][role="tab"]').click();
  await page.locator('textarea[name="DeckProfileJson"]').fill(deckProfileJson);
  await page.getByRole('button', { name: 'Render Analysis Summary' }).click();
  await expect(page.locator('section.summary-panel[data-print-result]')).toBeVisible();
}

const themes: Array<{ name: string; cookie: string | null }> = [
  { name: 'default-light', cookie: null },
  { name: 'dimir-dark', cookie: 'site-dimir.css' },
];

for (const theme of themes) {
  test(`print button is secondary beside download (${theme.name})`, async ({ page, baseURL }, testInfo) => {
    if (theme.cookie) {
      await page.context().addCookies([
        { name: 'deckflow-theme', value: theme.cookie, url: baseURL ?? `http://localhost:${resolveE2EPort()}` },
      ]);
    }

    await renderAnalysis(page);

    const toolbar = page.locator('section.summary-panel[data-print-result] .prompt-step-actions').first();
    const download = toolbar.getByRole('button', { name: 'Download session (.zip)' });
    const printBtn = toolbar.getByRole('button', { name: 'Print results' });

    await expect(download).toBeVisible();
    await expect(printBtn).toBeVisible();

    // Secondary treatment applied: the print button's background is transparent,
    // whereas the primary Download button is a solid/gradient fill. This proves
    // the `.run-button.prompt-print-button` override wins over the themed
    // `.run-button` in this theme.
    const printBg = await printBtn.evaluate((el) => getComputedStyle(el).backgroundColor);
    const printImage = await printBtn.evaluate((el) => getComputedStyle(el).backgroundImage);
    expect(printBg).toBe('rgba(0, 0, 0, 0)');
    expect(printImage).toBe('none');

    const downloadBg = await download.evaluate((el) => getComputedStyle(el).backgroundColor);
    const downloadImage = await download.evaluate((el) => getComputedStyle(el).backgroundImage);
    // Download stays primary: a solid color or a gradient image (never both none/transparent).
    expect(downloadBg !== 'rgba(0, 0, 0, 0)' || downloadImage !== 'none').toBeTruthy();

    // Hover must not swap the label to hardcoded white — that would be low-contrast
    // in the dark themes whose --accent-strong is a light glow tint. The label keeps
    // its accent color (theme-verified against the page bg) on a subtle accent wash.
    await printBtn.hover();
    const hoverColor = await printBtn.evaluate((el) => getComputedStyle(el).color);
    expect(hoverColor).not.toBe('rgb(255, 255, 255)');

    await toolbar.screenshot({ path: `${testInfo.outputDir}/toolbar-${theme.name}-${testInfo.project.name}.png` });
  });
}
