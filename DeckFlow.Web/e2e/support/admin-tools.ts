import { expect, test, type Locator, type Page } from '@playwright/test';
import { acquireAdminLockForTest, releaseAdminLockForTest } from './admin-lock';
import { resolveE2EPort } from './e2e-port';

type LockHandle = Awaited<ReturnType<typeof acquireAdminLockForTest>>;

const adminUser = process.env.FEEDBACK_ADMIN_USER ?? 'admin';
const adminPassword = process.env.FEEDBACK_ADMIN_PASSWORD ?? 'changeme-local';
const adminToolsUrl = `http://${adminUser}:${adminPassword}@localhost:${resolveE2EPort()}/Admin/Tools`;

export async function gotoAdminTools(page: Page): Promise<void> {
  const response = await page.goto(adminToolsUrl);
  expect(response?.ok(), '/Admin/Tools must return 200').toBeTruthy();
}

export async function setToolEnabled(page: Page, label: string, enabled: boolean): Promise<void> {
  await gotoAdminTools(page);

  const row = getAdminToolRow(page, label);
  const status = row.locator('[data-label="Status"]');
  const currentStatus = (await status.textContent())?.trim();
  const desiredStatus = enabled ? 'On' : 'Off';

  if (currentStatus === desiredStatus) {
    return;
  }

  const actionButton = row.getByRole('button', { name: enabled ? 'Enable' : 'Disable', exact: true });
  await actionButton.click();
  await expect(page.locator('.admin-banner--success')).toContainText(
    `Tool '${label}' is now ${enabled ? 'enabled' : 'disabled'}.`,
  );
  await expect(getAdminToolRow(page, label).locator('[data-label="Status"]')).toHaveText(desiredStatus);
}

/**
 * Reads a tool's current flag state.
 *
 * Why: a spec that needs a tool enabled must restore whatever it found rather than
 * assuming the tool ships off. Flag state persists in the dev database, so guessing
 * wrong leaves the tool disabled for every later run — which is how the /sync smoke,
 * scripts and responsive specs were broken by a Batch G run.
 */
export async function getToolEnabled(page: Page, label: string): Promise<boolean> {
  await gotoAdminTools(page);
  const status = getAdminToolRow(page, label).locator('[data-label="Status"]');
  return (await status.textContent())?.trim() === 'On';
}

export function withToolEnabled(label: string): void {
  let heldLock: LockHandle | null = null;
  let wasEnabled = false;

  test.beforeEach(async ({ page }) => {
    heldLock = await acquireAdminLockForTest(page);
    wasEnabled = await getToolEnabled(page, label);
    await setToolEnabled(page, label, true);
  });

  test.afterEach(async ({ page }) => {
    if (heldLock) {
      await setToolEnabled(page, label, wasEnabled);
      await releaseAdminLockForTest(heldLock);
      heldLock = null;
    }
  });
}

export async function boxOf(page: Page, selector: string): Promise<{ x: number; y: number; width: number; height: number }> {
  const element = page.locator(selector).first();
  await expect(element).toBeVisible();
  const box = await element.boundingBox();
  expect(box, `${selector} should have a layout box`).not.toBeNull();
  return box!;
}

function getAdminToolRow(page: Page, label: string): Locator {
  return page.locator('tbody tr').filter({
    has: page.locator('td[data-label="Tool"] span', { hasText: label }),
  });
}
