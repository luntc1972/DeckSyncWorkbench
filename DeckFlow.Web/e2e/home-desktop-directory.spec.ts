import { expect, test } from '@playwright/test';

test('home tool directory keeps links discoverable in three wide desktop columns', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');

  const cards = page.locator('.hub-grid .hub-card');
  await expect(cards.first()).toBeVisible();

  const cardBoxes = await cards.evaluateAll((elements) => elements
    .map((element) => {
      const { x, y, width } = element.getBoundingClientRect();
      return { x, y, width };
    })
    .filter((box) => box.width > 0));

  expect(cardBoxes.length).toBeGreaterThan(3);
  expect(Math.max(...cardBoxes.map((box) => box.width))).toBeGreaterThanOrEqual(340);

  for (const card of await cards.all()) {
    await expect(card).toBeVisible();
    await expect(card).toHaveAttribute('href', /\S/);
  }
});

test('home leads with the directory prompt before the primary Deck Analysis workflow', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');

  const intro = page.locator('.hub-title');
  const primaryWorkflow = page.locator('.hub-hero--primary');

  await expect(intro).toHaveText('Find the right tool for your deck');
  await expect(primaryWorkflow).toHaveAttribute('href', '/deck-analysis');
  await expect(primaryWorkflow.locator('.hub-hero__title')).toHaveText('Deck Analysis');

  const [introBox, workflowBox] = await Promise.all([
    intro.boundingBox(),
    primaryWorkflow.boundingBox(),
  ]);
  expect(introBox).not.toBeNull();
  expect(workflowBox).not.toBeNull();
  expect(workflowBox!.y).toBeGreaterThan(introBox!.y + introBox!.height);
});

test('home gives Deck Analysis a full-width directory lead-in on desktop', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');

  const intro = page.locator('.hub-title');
  const primaryWorkflow = page.locator('.hub-hero--primary');
  const directory = page.locator('.hub-grid').first();

  await expect(intro).toHaveText('Find the right tool for your deck');

  const [workflowBox, directoryBox] = await Promise.all([
    primaryWorkflow.boundingBox(),
    directory.boundingBox(),
  ]);
  expect(workflowBox).not.toBeNull();
  expect(directoryBox).not.toBeNull();
  expect(workflowBox!.width).toBeGreaterThanOrEqual(directoryBox!.width - 1);
});

test('home directory remains usable with the Dimir theme', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.context().addCookies([
    { name: 'deckflow-theme', value: 'site-dimir.css', url: 'http://localhost:5173' },
  ]);
  await page.goto('/');

  await expect(page.locator('#theme-stylesheet')).toHaveAttribute('href', /site-dimir\.css/);
  await expect(page.locator('.hub-hero--primary')).toBeVisible();
  await expect(page.locator('.home-directory .hub-card').first()).toBeVisible();
});

test('home directory fits every visible tool card in a standard desktop viewport', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');

  const lastCard = page.locator('.home-directory .hub-card').last();
  await expect(lastCard).toBeVisible();

  const lastCardBox = await lastCard.boundingBox();
  expect(lastCardBox).not.toBeNull();
  expect(lastCardBox!.y + lastCardBox!.height).toBeLessThanOrEqual(900);
});

test('home directory gives every tool a concise source-faithful description', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');

  const descriptions = await page.locator('.home-directory .hub-card__description').allTextContents();
  expect(descriptions.length).toBeGreaterThan(3);
  expect(descriptions.every((description) => description.trim().length <= 72)).toBe(true);
});

test('home uses a balanced three-column directory with a calm primary workflow row', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');

  const title = page.locator('.hub-title');
  const primaryWorkflow = page.locator('.hub-hero--primary');
  const directoryGroups = page.locator('.home-directory > .hub-group');
  const lastCard = page.locator('.home-directory .hub-card').last();

  await expect(title).toHaveCSS('text-align', 'left');
  await expect(directoryGroups).toHaveCount(3);
  await expect(primaryWorkflow.locator('.hub-hero__icon')).toBeVisible();

  const lastCardBox = await lastCard.boundingBox();
  expect(lastCardBox).not.toBeNull();
  expect(lastCardBox!.y + lastCardBox!.height).toBeLessThan(900);
});

test('desktop keeps the existing tool menu beside the DeckFlow brand', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');

  const brand = page.locator('.page-brand');
  const toolNav = page.locator('#deck-tool-nav');
  await expect(toolNav.locator('[data-tool-nav-menu-toggle]')).toBeVisible();

  const [brandBox, toolNavBox] = await Promise.all([
    brand.boundingBox(),
    toolNav.boundingBox(),
  ]);
  expect(brandBox).not.toBeNull();
  expect(toolNavBox).not.toBeNull();
  expect(toolNavBox!.x).toBeGreaterThan(brandBox!.x + brandBox!.width);
  expect(toolNavBox!.y).toBeLessThanOrEqual(brandBox!.y + brandBox!.height);
});

test('desktop exposes one Tools disclosure before showing grouped tool routes', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');

  const toolNav = page.locator('#deck-tool-nav');
  const menuToggle = toolNav.locator('[data-tool-nav-menu-toggle]');
  const groups = toolNav.locator('[data-tool-nav-group]');

  await expect(menuToggle).toBeVisible();
  await expect(groups.first()).toBeHidden();
  await menuToggle.click();
  await expect(groups.first()).toBeVisible();
});

test('home footer contains the real share actions before the legal disclosure', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');

  const footer = page.locator('.page-footer');
  const shareBar = footer.locator('.share-bar');

  await expect(shareBar).toBeVisible();
  await expect(shareBar.getByRole('button', { name: 'Copy link' })).toBeVisible();
  await expect(shareBar.getByRole('link', { name: 'Reddit' })).toBeVisible();
  await expect(footer.locator('.page-footer__legal')).toBeVisible();

  const [shareBarBox, legalBox] = await Promise.all([
    shareBar.boundingBox(),
    footer.locator('.page-footer__legal').boundingBox(),
  ]);
  expect(shareBarBox).not.toBeNull();
  expect(legalBox).not.toBeNull();
  expect(shareBarBox!.y).toBeLessThan(legalBox!.y);
});

test('shared desktop header keeps a 40px Tools target on a non-Home page', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/manabase');

  const tools = page.locator('#deck-tool-nav [data-tool-nav-menu-toggle]');
  const theme = page.locator('.theme-picker');
  const help = page.locator('.page-nav');

  await expect(tools).toHaveCSS('min-height', '40px');
  await tools.click();
  await expect(page.locator('#deck-tool-nav [data-tool-nav-group]').first()).toBeVisible();

  const [themeBox, helpBox] = await Promise.all([theme.boundingBox(), help.boundingBox()]);
  expect(themeBox).not.toBeNull();
  expect(helpBox).not.toBeNull();
  expect(themeBox!.x).toBeLessThan(helpBox!.x);
});

test('large desktop Home uses a wide shell and one compact footer utility row', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');

  const contentShell = page.locator('.content-shell');
  const pageFrame = page.locator('.page-frame');
  const footer = page.locator('.page-footer');
  const shareBar = footer.locator('.share-bar');
  const about = footer.getByRole('link', { name: 'About' });
  const legal = footer.locator('.page-footer__legal');

  const [contentBox, pageFrameBox, shareBox, aboutBox, legalBox] = await Promise.all([
    contentShell.boundingBox(),
    pageFrame.boundingBox(),
    shareBar.boundingBox(),
    about.boundingBox(),
    legal.boundingBox(),
  ]);
  expect(contentBox).not.toBeNull();
  expect(pageFrameBox).not.toBeNull();
  expect(shareBox).not.toBeNull();
  expect(aboutBox).not.toBeNull();
  expect(legalBox).not.toBeNull();
  expect(contentBox!.width).toBeGreaterThanOrEqual(1360);
  expect(pageFrameBox!.width).toBeGreaterThanOrEqual(1360);
  expect(Math.abs(shareBox!.y - aboutBox!.y)).toBeLessThanOrEqual(8);
  expect(Math.abs(legalBox!.y - shareBox!.y)).toBeLessThanOrEqual(8);
});

test('wide Home header keeps Theme clear of the Tools disclosure', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');

  const [toolsBox, themeBox] = await Promise.all([
    page.locator('#deck-tool-nav').boundingBox(),
    page.locator('.theme-picker').boundingBox(),
  ]);
  expect(toolsBox).not.toBeNull();
  expect(themeBox).not.toBeNull();
  expect(themeBox!.x).toBeGreaterThan(toolsBox!.x + toolsBox!.width + 8);
});

test('wide Home footer gives site links priority over compact share utilities', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');

  const footer = page.locator('.page-footer');
  const feedback = footer.getByRole('link', { name: 'Feedback' });
  const shareLabel = footer.locator('.share-bar__label');
  const copyButton = footer.getByRole('button', { name: 'Copy link' });

  const [feedbackBox, shareLabelBox, copyButtonBox] = await Promise.all([
    feedback.boundingBox(),
    shareLabel.boundingBox(),
    copyButton.boundingBox(),
  ]);
  expect(feedbackBox).not.toBeNull();
  expect(shareLabelBox).not.toBeNull();
  expect(copyButtonBox).not.toBeNull();
  expect(shareLabelBox!.x).toBeGreaterThan(feedbackBox!.x + feedbackBox!.width);
  expect(copyButtonBox!.height).toBeLessThan(30);
});

test('desktop Tools menu shows existing tool icons in a wide dropdown', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');

  const toolNav = page.locator('#deck-tool-nav');
  await toolNav.locator('[data-tool-nav-menu-toggle]').click();
  await toolNav.locator('[data-tool-nav-group]').first().getByRole('button').click();

  const dropdown = toolNav.locator('.tool-nav__dropdown').first();
  await expect(dropdown).toBeVisible();
  await expect(dropdown.locator('.tool-nav__link-icon').first()).toBeVisible();

  const dropdownBox = await dropdown.boundingBox();
  expect(dropdownBox).not.toBeNull();
  expect(dropdownBox!.width).toBeGreaterThanOrEqual(240);
});

test('wide Home arranges footer links, share actions, and legal text as zones', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');

  const footer = page.locator('.page-footer');
  const links = footer.locator('.page-footer__links');
  const shareBar = footer.locator('.share-bar');
  const legal = footer.locator('.page-footer__legal');

  await expect(links).toBeVisible();
  const [linksBox, shareBox, legalBox] = await Promise.all([
    links.boundingBox(),
    shareBar.boundingBox(),
    legal.boundingBox(),
  ]);
  expect(linksBox).not.toBeNull();
  expect(shareBox).not.toBeNull();
  expect(legalBox).not.toBeNull();
  expect(linksBox!.x).toBeLessThan(shareBox!.x);
  expect(shareBox!.x).toBeLessThan(legalBox!.x);
  expect(Math.abs(linksBox!.y - shareBox!.y)).toBeLessThanOrEqual(8);
  expect(Math.abs(shareBox!.y - legalBox!.y)).toBeLessThanOrEqual(8);
});

test('back-to-top stays hidden until an overflowing page has been meaningfully scrolled', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');

  const backToTop = page.locator('#back-to-top-button');
  await expect(backToTop).toBeHidden();
  await expect(backToTop).toHaveAttribute('aria-hidden', 'true');

  await page.evaluate(() => {
    const overflow = document.createElement('div');
    overflow.style.height = '2000px';
    document.body.append(overflow);
  });
  await page.evaluate(() => window.scrollTo(0, 600));
  await page.waitForFunction(() => window.scrollY >= 600);

  await expect(backToTop).toBeVisible();
  await expect(backToTop).toHaveAttribute('aria-hidden', 'false');
  await expect(backToTop).toHaveAttribute('tabindex', '0');
});

test('Tools disclosure closes on Escape and outside click', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');

  const toolNav = page.locator('#deck-tool-nav');
  const menuToggle = toolNav.locator('[data-tool-nav-menu-toggle]');

  await menuToggle.click();
  await expect(menuToggle).toHaveAttribute('aria-expanded', 'true');
  await page.keyboard.press('Escape');
  await expect(menuToggle).toHaveAttribute('aria-expanded', 'false');
  await expect(toolNav.locator('[data-tool-nav-group]').first()).toBeHidden();

  await menuToggle.click();
  await expect(menuToggle).toHaveAttribute('aria-expanded', 'true');
  await page.locator('.hub-title').click();
  await expect(menuToggle).toHaveAttribute('aria-expanded', 'false');
  await expect(toolNav.locator('[data-tool-nav-group]').first()).toBeHidden();
});

test('desktop Tools menu keeps icon links aligned in the Classic theme', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');
  await page.locator('[data-tool-nav-menu-toggle]').click();

  const toolLink = page.locator('.tool-nav__link').first();
  await expect(toolLink).toHaveCSS('display', 'flex');
  await expect(toolLink).toHaveCSS('align-items', 'center');
});

test('wide Home footer presents share actions as aligned text utilities', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');

  const footer = page.locator('.page-footer');
  const about = footer.getByRole('link', { name: 'About' });
  const shareLabel = footer.locator('.share-bar__label');
  const copyButton = footer.getByRole('button', { name: 'Copy link' });

  await expect(copyButton).toHaveCSS('border-top-width', '1px');

  const [aboutBox, shareLabelBox, copyButtonBox] = await Promise.all([
    about.boundingBox(),
    shareLabel.boundingBox(),
    copyButton.boundingBox(),
  ]);
  expect(aboutBox).not.toBeNull();
  expect(shareLabelBox).not.toBeNull();
  expect(copyButtonBox).not.toBeNull();
  expect(Math.abs(shareLabelBox!.y - aboutBox!.y)).toBeLessThanOrEqual(4);
  expect(Math.abs(copyButtonBox!.y - aboutBox!.y)).toBeLessThanOrEqual(4);
});

test('wide Home footer keeps compact, aligned link, share, and legal zones', async ({ page }) => {
  test.skip(!test.info().project.name.includes('desktop'), 'desktop-only coverage');

  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');

  const lastCard = page.locator('.home-directory .hub-card').last();
  const footer = page.locator('.page-footer');
  const feedback = footer.getByRole('link', { name: 'Feedback' });
  const legal = footer.locator('.page-footer__legal');

  await expect(feedback).toHaveCSS('border-top-width', '1px');
  await expect(legal).toHaveCSS('text-align', 'left');

  const [lastCardBox, footerBox] = await Promise.all([
    lastCard.boundingBox(),
    footer.boundingBox(),
  ]);
  expect(lastCardBox).not.toBeNull();
  expect(footerBox).not.toBeNull();
  expect(footerBox!.y - (lastCardBox!.y + lastCardBox!.height)).toBeLessThanOrEqual(120);
});
