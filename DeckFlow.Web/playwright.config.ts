import { existsSync } from 'node:fs';
import { defineConfig, devices } from '@playwright/test';

const windowsDotnetPath = '/mnt/c/Program Files/dotnet/dotnet.exe';
const dotnetCommand = existsSync(windowsDotnetPath) ? `"${windowsDotnetPath}"` : 'dotnet';
const reuseExistingServer = !process.env.CI || Boolean(process.env.WSL_DISTRO_NAME);
// Why: concurrent worktree E2E runs must not collide on the same port.
const e2ePort = process.env.DECKFLOW_E2E_PORT ?? '5173';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  retries: process.env.CI ? 1 : 0,
  // Single-worker on CI: the cut-lab decide/tuning specs each drive a
  // simulation-heavy /api/cut-lab/decide loop, and on the 2-core CI runner
  // parallel workers starve each other's decide computation past the per-accept
  // timeout. One worker gives each sim-heavy request the full 2 cores. Local dev
  // (fast, many cores) keeps Playwright's core-count default.
  workers: process.env.CI ? 1 : undefined,
  reporter: process.env.CI ? 'github' : 'list',
  use: {
    // Force headless so a local WSL run never surfaces a browser window on the Windows host via WSLg.
    headless: true,
    baseURL: `http://localhost:${e2ePort}`,
    httpCredentials: {
      username: process.env.FEEDBACK_ADMIN_USER ?? 'admin',
      password: process.env.FEEDBACK_ADMIN_PASSWORD ?? 'changeme-local',
      // Send Basic auth proactively (not only after a 401 challenge) so the
      // admin pages don't race the challenge round-trip under parallel workers.
      send: 'always',
    },
  },
  projects: [
    {
      name: 'chromium-desktop',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1280, height: 900 },
      },
    },
    {
      name: 'chromium-mobile',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 390, height: 844 },
      },
    },
    {
      name: 'webkit-mobile',
      use: {
        ...devices['iPhone 13'],
      },
      // Why: WebKit is the engine every iOS browser uses, and the project had no
      // WebKit coverage at all — which is how the readonly-textarea clamp shipped.
      // Scoped to the mobile/responsive specs only: the full suite includes
      // simulation-heavy cut-lab specs that already run single-worker on the
      // 2-core CI runner, and duplicating those on a second engine would blow the
      // CI budget for no added signal.
      testMatch: /(ui-responsive|sibling-pages-mobile|deck-analysis-mobile)\.spec\.ts/,
    },
  ],
  // NOTE: WSL verification runs start the app first via scripts/run-web-test.sh and
  // then execute Playwright with CI=1 to mirror CI retries/parallelism. Detect
  // WSL so those local CI-mode runs still reuse the already-running headless
  // server, while real CI keeps owning server startup itself.
  webServer: {
    // Use the http-no-browser launch profile, NOT http. The app's Development
    // auto-open-browser is gated on DECKFLOW_DISABLE_AUTO_BROWSER, but a var set
    // in this `env` block does NOT cross the WSL→Windows boundary into the
    // Windows dotnet.exe this command spawns (verified: cmd.exe sees it unset),
    // so a plain `--launch-profile http` run pops a Windows Chrome. The
    // http-no-browser profile bakes DECKFLOW_DISABLE_AUTO_BROWSER=true into the
    // profile's environmentVariables, which `dotnet run` applies in-process
    // Windows-side — the only reliable suppression across WSL interop. The env
    // block below is kept as belt-and-suspenders for native-Linux/CI runs.
    //
    // CI runs the server in Release (reusing the Release build the workflow
    // already produced via --no-build). `dotnet run` otherwise defaults to a
    // Debug build, whose un-optimized, cold-JIT simulation code makes the first
    // /api/cut-lab/decide take >15s on the 2-core runner and deterministically
    // times out the cut-lab decide/tuning specs. Local dev keeps the default
    // Debug build (fast enough on dev hardware, no Release build required).
    command: `${dotnetCommand} run${process.env.CI ? ' -c Release --no-build' : ''} --launch-profile http-no-browser --urls http://localhost:${e2ePort}`,
    url: `http://localhost:${e2ePort}`,
    reuseExistingServer,
    timeout: 120_000,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      DECKFLOW_DISABLE_AUTO_BROWSER: 'true',
      FEEDBACK_ADMIN_USER: 'admin',
      FEEDBACK_ADMIN_PASSWORD: 'changeme-local',
    },
  },
});
