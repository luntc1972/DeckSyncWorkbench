import { expect, test } from '@playwright/test';

test('mobile Help index and topic stack without overflow and keep destinations touchable', async ({ page }) => {
  test.skip(!test.info().project.name.includes('mobile'), 'mobile-only coverage');

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/help');

  const indexLinks = page.locator('.help-index__link');
  const feedback = page.locator('.help-index__feedback a');
  const [pageWidth, indexBoxes, firstLink, secondLink, feedbackBox] = await Promise.all([
    page.locator('body').evaluate((body) => body.clientWidth),
    indexLinks.evaluateAll((links) => links.map((link) => {
      const rect = link.getBoundingClientRect();
      return { height: rect.height, width: rect.width };
    })),
    indexLinks.first().boundingBox(),
    indexLinks.nth(1).boundingBox(),
    feedback.boundingBox(),
  ]);

  expect(await indexLinks.count()).toBeGreaterThan(1);
  expect(firstLink).not.toBeNull();
  expect(secondLink).not.toBeNull();
  expect(feedbackBox).not.toBeNull();
  expect(firstLink!.width).toBeLessThanOrEqual(pageWidth);
  expect(firstLink!.height).toBeGreaterThanOrEqual(44);
  for (const box of indexBoxes) {
    expect(box.width).toBeLessThanOrEqual(pageWidth);
    expect(box.height).toBeGreaterThanOrEqual(44);
  }
  expect(secondLink!.y).toBeGreaterThan(firstLink!.y + firstLink!.height);
  expect(feedbackBox!.height).toBeGreaterThanOrEqual(44);
  expect(feedbackBox!.y).toBeGreaterThan(firstLink!.y + firstLink!.height);
  await expect(page.locator('body')).toHaveJSProperty('scrollWidth', pageWidth);

  await page.goto('/help/deck-analysis');

  const backLink = page.locator('.help-breadcrumb__back');
  const article = page.locator('.help-prose');
  const [topicPageWidth, backBox, articleBox] = await Promise.all([
    page.locator('body').evaluate((body) => body.clientWidth),
    backLink.boundingBox(),
    article.boundingBox(),
  ]);

  expect(backBox).not.toBeNull();
  expect(articleBox).not.toBeNull();
  expect(backBox!.height).toBeGreaterThanOrEqual(44);
  expect(articleBox!.y).toBeGreaterThan(backBox!.y + backBox!.height);
  expect(articleBox!.width).toBeLessThanOrEqual(topicPageWidth);
  await expect(page.locator('body')).toHaveJSProperty('scrollWidth', topicPageWidth);
});
