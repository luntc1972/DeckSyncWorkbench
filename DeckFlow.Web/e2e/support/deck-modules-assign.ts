import { expect, type Page } from '@playwright/test';

/**
 * Moves the first entry in the `from` panel to the `to` panel using only keyboard input,
 * shared between the Deck Modules UI and analysis specs so both exercise the same
 * keyboard-accessible assignment flow.
 */
export async function assignWithKeyboard(page: Page, from: string, to: string): Promise<void> {
  const selection = page.locator(`[data-deck-modules-entries="${from}"] [data-deck-modules-select]`).first();
  await selection.focus();
  await page.keyboard.press('Space');
  await expect(selection).toBeChecked();

  const move = page.locator(`[data-deck-modules-move="${from}:${to}"]`);
  await move.focus();
  await page.keyboard.press('Enter');
  await expect(page.locator(`[data-deck-modules-filter="${to}"]`)).toBeFocused();
}
