import { afterEach, describe, expect, it } from 'vitest';
import { resolveE2EPort } from '../e2e/support/e2e-port';

const originalE2EPort = process.env.DECKFLOW_E2E_PORT;

afterEach(() => {
    if (originalE2EPort === undefined) {
        delete process.env.DECKFLOW_E2E_PORT;
        return;
    }

    process.env.DECKFLOW_E2E_PORT = originalE2EPort;
});

describe('resolveE2EPort', () => {
    it('returns the configured E2E port when set', () => {
        process.env.DECKFLOW_E2E_PORT = '27182';

        expect(resolveE2EPort('/checkout-a')).toBe(27182);
    });

    it('derives a deterministic port in the checkout-safe range when unset', () => {
        delete process.env.DECKFLOW_E2E_PORT;

        const firstPort = resolveE2EPort('/checkout-a');

        expect(firstPort).toBe(resolveE2EPort('/checkout-a'));
        expect(firstPort).toBeGreaterThanOrEqual(20000);
        expect(firstPort).toBeLessThanOrEqual(29999);
    });

    it('derives different ports for different worktree checkouts', () => {
        delete process.env.DECKFLOW_E2E_PORT;

        // Mirrors dev-agent's real run-worktree layout: same repo, distinct run ids.
        const runA = resolveE2EPort('/repo/.dev-agent-worktrees/deckflow/2026-08-28-f6a24241/DeckFlow.Web');
        const runB = resolveE2EPort('/repo/.dev-agent-worktrees/deckflow/2026-08-30-c766d500/DeckFlow.Web');

        expect(runA).not.toBe(runB);
    });
});
