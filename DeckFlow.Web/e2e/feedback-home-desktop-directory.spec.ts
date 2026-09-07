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

test('mobile Feedback stacks controls with 44px touch targets', async ({ page }) => {
  test.skip(!test.info().project.name.includes('mobile'), 'mobile-only coverage');

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/feedback');

  const type = page.locator('.df-select__trigger[aria-label="Type"]');
  const message = page.getByLabel('Message');
  const email = page.getByLabel('Email (optional)');
  const submit = page.getByRole('button', { name: 'Send Feedback' });
  const controls = [type, message, email, submit];

  const [pageWidth, ...boxes] = await Promise.all([
    page.locator('body').evaluate((body) => body.clientWidth),
    ...controls.map((control) => control.boundingBox()),
  ]);

  for (const box of boxes) {
    expect(box).not.toBeNull();
    expect(box!.width).toBeLessThanOrEqual(pageWidth);
    expect(box!.height).toBeGreaterThanOrEqual(44);
  }

  expect(boxes[1]!.y).toBeGreaterThan(boxes[0]!.y + boxes[0]!.height);
  expect(boxes[2]!.y).toBeGreaterThan(boxes[1]!.y + boxes[1]!.height);
  expect(boxes[3]!.y).toBeGreaterThan(boxes[2]!.y + boxes[2]!.height);
  expect(boxes[3]!.width).toBeGreaterThanOrEqual(boxes[2]!.width - 1);
  await expect(page.locator('body')).toHaveJSProperty('scrollWidth', pageWidth);
});
