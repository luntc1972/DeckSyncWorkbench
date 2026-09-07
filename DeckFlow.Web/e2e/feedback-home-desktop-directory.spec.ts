import { expect, test } from '@playwright/test';

test('feedback form keeps all fields inside a calm desktop panel', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/feedback');

  const panel = page.locator('.feedback-panel');
  await expect(panel).toBeVisible();
  await expect(page.locator('select[name="Type"]')).toHaveCount(1);
  await expect(page.locator('.df-select__trigger[aria-label="Type"]')).toBeVisible();
  await expect(page.getByLabel('Message')).toBeVisible();
  await expect(page.getByLabel('Email (optional)')).toBeVisible();
  await expect(page.locator('input[name="Website"]')).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Send Feedback' })).toBeVisible();

  const [panelBox, pageWidth, panelPadding] = await Promise.all([
    panel.boundingBox(),
    page.locator('body').evaluate((body) => body.clientWidth),
    panel.evaluate((element) => Number.parseFloat(getComputedStyle(element).paddingTop)),
  ]);

  expect(panelBox).not.toBeNull();
  expect(panelBox!.width).toBeGreaterThanOrEqual(560);
  expect(panelBox!.width).toBeLessThan(pageWidth);
  expect(panelPadding).toBeGreaterThanOrEqual(30);
  await expect(page.locator('body')).toHaveJSProperty('scrollWidth', pageWidth);
});
