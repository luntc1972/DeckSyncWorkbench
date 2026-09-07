import { mkdirSync } from 'node:fs';
import path from 'node:path';

/**
 * Keeps UI-design screenshots out of tracked repo trees by default while still allowing
 * a shared override for CI or local sweeps that need a stable sink.
 */
export function uiDesignDir(area: string): string {
  const root =
    process.env.DECKFLOW_UI_DESIGN_DIR && process.env.DECKFLOW_UI_DESIGN_DIR.length > 0
      ? process.env.DECKFLOW_UI_DESIGN_DIR
      : path.resolve(__dirname, '../../../artifacts/ui-design');
  const screenshotDir = path.join(root, area, 'screenshots');
  mkdirSync(screenshotDir, { recursive: true });
  return screenshotDir;
}
