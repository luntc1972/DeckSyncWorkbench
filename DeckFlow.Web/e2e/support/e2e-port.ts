const E2E_PORT_MIN = 20000;
const E2E_PORT_RANGE = 10000;

/**
 * Resolves the E2E server port for a checkout.
 *
 * Unconfigured checkouts map deterministically into ports 20000 through 29999,
 * avoiding common development-server ports while allowing concurrent worktrees.
 */
export function resolveE2EPort(rootPath: string = process.cwd()): number {
  const configuredPort = process.env.DECKFLOW_E2E_PORT;
  if (configuredPort !== undefined) {
    return Number.parseInt(configuredPort, 10);
  }

  let hash = 0;
  for (const char of rootPath) {
    hash = (hash * 31 + char.charCodeAt(0)) % E2E_PORT_RANGE;
  }

  return E2E_PORT_MIN + hash;
}
