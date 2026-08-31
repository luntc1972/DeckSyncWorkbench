import { test, type Page } from '@playwright/test';
import { open, readFile, unlink } from 'node:fs/promises';
import { resolveE2EPort } from './e2e-port';

const adminUser = process.env.FEEDBACK_ADMIN_USER ?? 'admin';
const adminPassword = process.env.FEEDBACK_ADMIN_PASSWORD ?? 'changeme-local';
const basicAuthHeader = `Basic ${Buffer.from(`${adminUser}:${adminPassword}`).toString('base64')}`;

const adminLockPort = resolveE2EPort();
export const adminLockPath = `/tmp/deckflow-admin-e2e-${adminLockPort}.lock`;
export const adminLockTimeoutMs = 90_000;

type LockHandle = Awaited<ReturnType<typeof open>>;
type LockFilePayload = {
  pid: number;
  createdAt: number;
};

function getAdminForwardedIp(): string {
  const info = test.info();
  const key = `${info.project.name}:${info.file}:${info.title}:${info.retry}`;
  let hash = 0;
  for (const char of key) {
    hash = (hash * 31 + char.charCodeAt(0)) % 200;
  }

  return `203.0.113.${hash + 1}`;
}

async function tryReclaimStaleLock(): Promise<void> {
  let payload: LockFilePayload | null = null;

  try {
    payload = JSON.parse(await readFile(adminLockPath, 'utf8')) as LockFilePayload;
  } catch (error: unknown) {
    const code = typeof error === 'object' && error !== null && 'code' in error ? String(error.code) : '';
    if (code === 'ENOENT') {
      return;
    }

    payload = null;
  }

  const pidAlive =
    typeof payload?.pid === 'number' &&
    Number.isInteger(payload.pid) &&
    payload.pid > 0 &&
    (() => {
      try {
        process.kill(payload.pid, 0);
        return true;
      } catch (error: unknown) {
        const code = typeof error === 'object' && error !== null && 'code' in error ? String(error.code) : '';
        if (code === 'ESRCH') {
          return false;
        }

        return true;
      }
    })();

  const lockAgeMs =
    typeof payload?.createdAt === 'number' && Number.isFinite(payload.createdAt)
      ? Date.now() - payload.createdAt
      : Number.POSITIVE_INFINITY;

  if (pidAlive && lockAgeMs < adminLockTimeoutMs) {
    return;
  }

  try {
    await unlink(adminLockPath);
  } catch (error: unknown) {
    const code = typeof error === 'object' && error !== null && 'code' in error ? String(error.code) : '';
    if (code !== 'ENOENT') {
      throw error;
    }
  }
}

async function acquireAdminLock(): Promise<LockHandle> {
  const startedAt = Date.now();

  while (Date.now() - startedAt < adminLockTimeoutMs) {
    try {
      const handle = await open(adminLockPath, 'wx');
      await handle.writeFile(
        JSON.stringify({
          pid: process.pid,
          createdAt: Date.now(),
        }),
      );
      return handle;
    } catch (error: unknown) {
      const code = typeof error === 'object' && error !== null && 'code' in error ? String(error.code) : '';
      if (code !== 'EEXIST') {
        throw error;
      }
    }

    await tryReclaimStaleLock();
    await new Promise((resolve) => setTimeout(resolve, 250));
  }

  throw new Error(`Timed out waiting for admin e2e lock at ${adminLockPath}`);
}

export async function acquireAdminLockForTest(page: Page): Promise<LockHandle> {
  test.setTimeout(adminLockTimeoutMs + 30_000);
  await page.setExtraHTTPHeaders({
    Authorization: basicAuthHeader,
    'CF-Connecting-IP': getAdminForwardedIp(),
  });

  return acquireAdminLock();
}

export async function releaseAdminLockForTest(handle: LockHandle | null): Promise<void> {
  if (!handle) {
    return;
  }

  try {
    await handle.close();
  } finally {
    try {
      await unlink(adminLockPath);
    } catch (error: unknown) {
      const code = typeof error === 'object' && error !== null && 'code' in error ? String(error.code) : '';
      if (code !== 'ENOENT') {
        throw error;
      }
    }
  }
}
