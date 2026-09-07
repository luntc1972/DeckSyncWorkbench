import { beforeEach, describe, expect, it, vi } from 'vitest';

import '../wwwroot/ts/deck-modules';

interface DeckModulesApi { initialize(): void; buildExportText(report: unknown): string; }

const markup = () => `
<main data-deck-modules>
  <p data-deck-modules-live aria-live="polite"></p><p data-deck-modules-notice></p><p data-deck-modules-error></p>
  <form data-deck-modules-import-form><input data-deck-modules-source><button>Import</button></form>
  <section data-deck-modules-configuration><input data-deck-modules-name><select data-deck-modules-profile><option value="">Choose a profile</option><option value="Casual">Casual</option><option value="Bracket4HighPower">Bracket 4 High Power</option><option value="Cedh">cEDH</option></select><textarea data-deck-modules-plan></textarea><button data-deck-modules-add-alternative>Add</button><div data-deck-modules-alternatives></div><span data-deck-modules-summary-profile></span><span data-deck-modules-summary-plan></span></section>
  <section data-deck-modules-command-zone></section><p data-deck-modules-reconciliation></p>
  ${['unassigned', 'core', 'strategy', 'mana'].map(panel => `<section data-deck-modules-panel="${panel}"><span data-deck-modules-count="${panel}"></span>${panel === 'strategy' ? '<span data-deck-modules-active-name></span>' : ''}<input data-deck-modules-filter="${panel}"><table><tbody data-deck-modules-entries="${panel}"></tbody></table><button data-deck-modules-move="${panel}:unassigned">Move</button><button data-deck-modules-move="${panel}:core">Move</button><button data-deck-modules-move="${panel}:strategy">Move</button><button data-deck-modules-move="${panel}:mana">Move</button></section>`).join('')}
  <p data-deck-modules-balance></p><button data-deck-modules-compile>Compile</button><button data-deck-modules-analyze>Analyze mana base</button><button data-deck-modules-export>Export</button><button data-deck-modules-copy>Copy</button><button data-deck-modules-download>Download</button><button data-deck-modules-restart>Restart</button>
  <section data-deck-modules-report><span data-deck-modules-report-total></span><span data-deck-modules-report-strategy></span><span data-deck-modules-report-mana></span><ul data-deck-modules-diagnostics></ul><ul data-deck-modules-compiled></ul><ul data-deck-modules-swap="add"></ul><ul data-deck-modules-swap="remove"></ul><ul data-deck-modules-swap="reset"></ul></section>
  <section data-deck-modules-analysis hidden><p data-deck-modules-analysis-stale hidden>Cards changed since this analysis.</p><p data-deck-modules-core-only hidden></p><a data-deck-modules-manabase-handoff hidden></a><p data-deck-modules-handoff-note></p><span data-deck-modules-analysis-health></span><span data-deck-modules-analysis-lands></span><span data-deck-modules-analysis-target></span><span data-deck-modules-analysis-land-delta></span><span data-deck-modules-analysis-ramp></span><span data-deck-modules-analysis-hardtocast></span><table><tbody data-deck-modules-analysis-colors></tbody></table><div data-deck-modules-signals hidden><span data-deck-modules-bracket></span><ul data-deck-modules-gamechangers></ul><p data-deck-modules-combo-availability></p><table data-deck-modules-interactions-table><tbody data-deck-modules-interactions></tbody></table><p data-deck-modules-interactions-unavailable hidden></p></div><div data-deck-modules-disclosure hidden><span data-deck-modules-declared-profile></span><p data-deck-modules-declared-plan></p><p data-deck-modules-profile-note hidden></p></div></section>
  <section data-deck-modules-comparison hidden><select data-deck-modules-compare-reference></select><select data-deck-modules-compare-other></select><button data-deck-modules-compare disabled>Compare</button><p data-deck-modules-comparison-message></p><table data-deck-modules-comparison-table><thead data-deck-modules-comparison-head></thead><tbody data-deck-modules-comparison-body></tbody></table></section>
</main>`;

const imported = { baselineToken: 'token with exact bytes ==', commandZone: [{ name: 'Commander', quantity: 1 }], baselineMainboardEntries: [{ name: 'Arcane Signet', quantity: 1 }, { name: 'Swords // Plowshares', quantity: 1 }, { name: 'Lightning Bolt', quantity: 1 }], importNotice: 'Imported.' };
const importDraft = async () => {
    if (!vi.isMockFunction(globalThis.fetch)) vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => imported }));
    document.querySelector<HTMLInputElement>('[data-deck-modules-source]')!.value = 'https://example.test/deck';
    document.querySelector<HTMLFormElement>('[data-deck-modules-import-form]')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(document.querySelector('[data-deck-modules-count="unassigned"]')!.textContent).toBe('3'));
};

const analysis = { landCount: 29, targetLandCount: 31, landDelta: -2, health: 'Needs attention', rampSourceCount: 8, hardToCastCount: 2, isCoreOnly: false, attributedFindings: [{ displayColor: 'Blue', actualSources: 18, requiredSources: 20, deficit: -2, drivingSpell: 'Counterspell', strength: 'NamedCard', attributedCard: 'Ancient Tomb', swapDirection: 'added' }], analysisNotice: null };
const analyzeDeck = async () => {
    document.querySelector<HTMLButtonElement>('[data-deck-modules-analyze]')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await vi.waitFor(() => expect(document.querySelector<HTMLElement>('[data-deck-modules-analysis]')!.hidden).toBe(false));
};

const prepareComparison = async () => {
    (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
    document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'A.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
    document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan B'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'B.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
    const stored = JSON.parse(window.sessionStorage.getItem('deckflow.deck-modules.v1')!); const ids = stored.alternatives.map((alternative: { id: string }) => alternative.id);
    document.querySelector<HTMLSelectElement>('[data-deck-modules-compare-reference]')!.value = ids[0]; const other = document.querySelector<HTMLSelectElement>('[data-deck-modules-compare-other]')!; other.value = ids[1]; other.dispatchEvent(new Event('change', { bubbles: true }));
    return { ids, stored };
};

const comparisonDelta = (reference: Record<string, unknown> = {}, other: Record<string, unknown> = {}) => ({ reference: { configurationName: 'Plan A', isAnalyzed: true, landCount: 30, landTargetDelta: 0, rampSourceCount: 8, hardToCastCount: 1, health: 'Healthy', ...reference }, columns: [{ configurationName: 'Plan B', isAnalyzed: true, landCount: 31, landCountDelta: 1, landTargetDelta: 1, rampSourceCount: 9, rampSourceCountDelta: 1, hardToCastCount: 2, hardToCastCountDelta: 1, health: 'Needs attention', ...other }], colorRows: [], interactionRows: [] });

describe('DeckFlowDeckModules', () => {
    beforeEach(() => { document.body.innerHTML = markup(); window.sessionStorage.clear(); vi.unstubAllGlobals(); });

    it('initialize_Filtering_HidesRowsWithoutChangingStateOrCount', async () => {
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        const filter = document.querySelector<HTMLInputElement>('[data-deck-modules-filter="unassigned"]')!;
        filter.value = 'arcane'; filter.dispatchEvent(new Event('input', { bubbles: true }));
        expect(document.querySelectorAll('[data-deck-modules-entries="unassigned"] tr:not([hidden])')).toHaveLength(1);
        expect(document.querySelector('[data-deck-modules-count="unassigned"]')!.textContent).toBe('3');
    });

    it('initialize_Filtering_PreservesSelectionAcrossFilterRerenders', async () => {
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        const filter = document.querySelector<HTMLInputElement>('[data-deck-modules-filter="unassigned"]')!;
        const arcane = document.querySelector<HTMLInputElement>('[data-deck-modules-select="unassigned:0"]')!;
        arcane.checked = true;
        filter.value = 'lightning'; filter.dispatchEvent(new Event('input', { bubbles: true }));
        expect(document.querySelector<HTMLInputElement>('[data-deck-modules-select="unassigned:0"]')!.checked).toBe(true);
        document.querySelector<HTMLButtonElement>('[data-deck-modules-move="unassigned:core"]')!.click();
        expect(document.querySelector('[data-deck-modules-count="core"]')!.textContent).toBe('1');
    });

    it('initialize_MoveSelected_DoesNotCarrySelectionToNextEntry', async () => {
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-select="unassigned:0"]')!.checked = true;
        document.querySelector<HTMLButtonElement>('[data-deck-modules-move="unassigned:core"]')!.click();
        expect(document.querySelectorAll('[data-deck-modules-entries="unassigned"] [data-deck-modules-select]:checked')).toHaveLength(0);
    });

    it('initialize_ReportIsHiddenUntilCompilation', async () => {
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize();
        const report = document.querySelector<HTMLElement>('[data-deck-modules-report]')!;
        expect(report.hidden).toBe(true);
        await importDraft();
        expect(report.hidden).toBe(true);
    });

    it('initialize_ReportIsRevealedAfterCompilation', async () => {
        const compilation = { totalCardCount: 3, diagnostics: [{ rule: 'TotalCardCount', affectedIdentifiers: ['3'] }, { rule: 'BannedCard', affectedIdentifiers: ['Black Lotus', 'Winota, Joiner of Forces'] }, { rule: 'FutureRule', affectedIdentifiers: ['Card X'] }] };
        const fetchMock = vi.fn().mockResolvedValueOnce({ ok: true, json: async () => imported }).mockResolvedValueOnce({ ok: true, json: async () => compilation }); vi.stubGlobal('fetch', fetchMock);
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Turbo'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win quickly.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Midrange'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win later.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        document.querySelector<HTMLButtonElement>('[data-deck-modules-compile]')!.click(); await vi.waitFor(() => expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-copy]')!.disabled).toBe(false));
        expect(document.querySelector<HTMLElement>('[data-deck-modules-report]')!.hidden).toBe(false);
        expect([...document.querySelectorAll('[data-deck-modules-diagnostics] li')].map(item => item.textContent)).toEqual(['Compiled deck has 3 cards; a Commander deck needs exactly 100.', 'Banned in Commander: Black Lotus; Winota, Joiner of Forces.', 'FutureRule: Card X.']);
        expect(document.querySelector('[data-deck-modules-diagnostics]')!.textContent).not.toContain('[object Object]');
    });

    it('buildExportText_Compilation_FormatsDecklistAndSwapPlan', () => {
        const report = { commandZoneEntries: [{ name: "Krenko's Command", quantity: 1 }], mainboardEntries: [{ name: 'Sol Ring', quantity: 1 }], swapPlan: { toAdd: [{ name: 'Sol Ring', quantity: 1, action: 'Add' }], toRemove: [{ name: 'Llanowar Elves', quantity: 1, action: 'Remove' }], toReset: [{ name: 'Arcane Signet', quantity: 1, action: 'Remove' }] } };
        expect((globalThis.DeckFlowDeckModules as DeckModulesApi).buildExportText(report)).toBe("== Command Zone ==\n1 Krenko's Command\n\n== Mainboard ==\n1 Sol Ring\n\nIN - +1 Sol Ring\nOUT - -1 Llanowar Elves\nRESET - -1 Arcane Signet\n");
    });

    it('initialize_CalledTwiceOnTheSamePageElement_DoesNotDoubleRegisterListeners', async () => {
        // WR-11: re-initializing against the same page element previously double-registered every
        // listener (initializeWithHandoff also leaked a MutationObserver on top). A single click
        // must fire its handler exactly once no matter how many times initialize() ran.
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize();
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize();
        await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A';
        document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh';
        document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win.';
        document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        const stored = JSON.parse(window.sessionStorage.getItem('deckflow.deck-modules.v1')!);
        expect(stored.alternatives).toHaveLength(1);
    });

    it('initialize_AddAlternativeWithoutName_ReportsValidationError', async () => {
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win with cards.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        expect(document.querySelector('[data-deck-modules-error]')!.textContent).toContain('Enter a baseline strategy name');
    });

    it('initialize_CompiledDraftThenMove_DisablesCopyAndExport', async () => {
        const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => imported }); vi.stubGlobal('fetch', fetchMock); (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Turbo'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win quickly.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click(); document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Midrange'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win later.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-copy]')!.disabled).toBe(true); expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-export]')!.disabled).toBe(true); document.querySelector<HTMLButtonElement>('[data-deck-modules-compile]')!.click(); await vi.waitFor(() => expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-copy]')!.disabled).toBe(false)); expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-export]')!.disabled).toBe(false); document.querySelector<HTMLInputElement>('[data-deck-modules-entries="unassigned"] input')!.checked = true; document.querySelector<HTMLButtonElement>('[data-deck-modules-move="unassigned:strategy"]')!.click(); expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-copy]')!.disabled).toBe(true); expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-export]')!.disabled).toBe(true);
    });

    it('initialize_MoveSelectedUnassignedToCore_UpdatesCountsAndTables', async () => {
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-entries="unassigned"] input')!.checked = true;
        document.querySelector<HTMLButtonElement>('[data-deck-modules-move="unassigned:core"]')!.click();
        expect(document.querySelector('[data-deck-modules-count="unassigned"]')!.textContent).toBe('2');
        expect(document.querySelector('[data-deck-modules-count="core"]')!.textContent).toBe('1');
        expect(document.querySelector('[data-deck-modules-entries="core"]')!.textContent).toContain('Arcane Signet');
    });

    it('initialize_KeyboardMoveSelectedCard_MovesAndFocusesDestination', async () => {
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-entries="unassigned"] input')!.checked = true;
        const move = document.querySelector<HTMLButtonElement>('[data-deck-modules-move="unassigned:core"]')!;
        move.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
        expect(document.activeElement).toBe(document.querySelector('[data-deck-modules-filter="core"]'));
    });

    it('initialize_SelectAlternative_SwitchesStrategyAndManaPanels', async () => {
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win with cards.';
        document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click(); document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan B'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win another way.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click(); document.querySelectorAll<HTMLButtonElement>('[data-deck-modules-alternative]')[1].click();
        expect(document.querySelectorAll<HTMLButtonElement>('[data-deck-modules-alternative]')[1].textContent).toContain('Plan B —');
    });

    it('initialize_UnbalancedAlternatives_DisablesCompileAndExport', async () => {
        document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh';
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win with cards.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click(); document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan B'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win another way.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        document.querySelector<HTMLInputElement>('[data-deck-modules-entries="unassigned"] input')!.checked = true; document.querySelector<HTMLButtonElement>('[data-deck-modules-move="unassigned:strategy"]')!.click();
        expect(document.querySelector('[data-deck-modules-balance]')!.textContent).toContain('unbalanced');
        expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-compile]')!.disabled).toBe(true); expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-export]')!.disabled).toBe(true);
        document.querySelectorAll<HTMLButtonElement>('[data-deck-modules-alternative]')[0].click(); document.querySelector<HTMLInputElement>('[data-deck-modules-entries="unassigned"] input')!.checked = true; document.querySelector<HTMLButtonElement>('[data-deck-modules-move="unassigned:strategy"]')!.click();
        expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-compile]')!.disabled).toBe(false); expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-export]')!.disabled).toBe(true); expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-copy]')!.disabled).toBe(true);
    });

    it('initialize_BrokenStorageMalformedAndWrongVersion_DegradesWithoutApplyingDraft', () => {
        vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); }); expect(() => (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize()).not.toThrow(); vi.restoreAllMocks();
        window.sessionStorage.setItem('deckflow.deck-modules.v1', '{'); expect(() => (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize()).not.toThrow();
        window.sessionStorage.setItem('deckflow.deck-modules.v1', JSON.stringify({ version: 9, baselineToken: 'old' })); (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize();
        expect(document.querySelector('[data-deck-modules-count="unassigned"]')!.textContent).toBe('0');
    });

    it('initialize_ClipboardRejects_AnnouncesFallback', async () => {
        document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh';
        const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => imported }); vi.stubGlobal('fetch', fetchMock); vi.stubGlobal('navigator', { clipboard: { writeText: vi.fn().mockRejectedValue(new Error('denied')) } }); (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win with cards.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click(); document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan B'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win another way.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click(); document.querySelector<HTMLButtonElement>('[data-deck-modules-compile]')!.click(); await vi.waitFor(() => expect(document.querySelector<HTMLButtonElement>('[data-deck-modules-copy]')!.disabled).toBe(false)); document.querySelector<HTMLButtonElement>('[data-deck-modules-copy]')!.click();
        await vi.waitFor(() => expect(document.querySelector('[data-deck-modules-live]')!.textContent).toContain('Copy failed'));
    });

    it('initialize_Compile_EchoesBaselineTokenByteForByte', async () => {
        const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => imported }); vi.stubGlobal('fetch', fetchMock); (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win with cards.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click(); document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan B'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win another way.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click(); document.querySelector<HTMLButtonElement>('[data-deck-modules-compile]')!.click();
        await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2)); expect(JSON.parse(fetchMock.mock.calls[1][1].body).baselineToken).toBe('token with exact bytes ==');
    });

    it('initialize_AddAlternative_DisplaysProfileLabelAndPreservesValue', async () => {
        const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => imported }); vi.stubGlobal('fetch', fetchMock);
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A';
        document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh';
        document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win with cards.';
        document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        expect(document.querySelector('[data-deck-modules-summary-profile]')!.textContent).toBe('cEDH');
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan B';
        document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh';
        document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win another way.';
        document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        document.querySelector<HTMLInputElement>('[data-deck-modules-entries="unassigned"] input')!.checked = true;
        document.querySelector<HTMLButtonElement>('[data-deck-modules-move="unassigned:strategy"]')!.click();
        document.querySelectorAll<HTMLButtonElement>('[data-deck-modules-alternative]')[0].click();
        document.querySelector<HTMLInputElement>('[data-deck-modules-entries="unassigned"] input')!.checked = true;
        document.querySelector<HTMLButtonElement>('[data-deck-modules-move="unassigned:strategy"]')!.click();
        document.querySelector<HTMLButtonElement>('[data-deck-modules-compile]')!.click();
        await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
        expect(JSON.parse(fetchMock.mock.calls[1][1].body).alternatives.find((alternative: { profile: string }) => alternative.profile === 'Cedh')).toBeTruthy();
        document.querySelector<HTMLButtonElement>('[data-deck-modules-alternative]')!.click();
        expect(document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value).toBe('Cedh');
    });

    it('initialize_AnalysisSucceeds_PersistsAndRendersSnapshot', async () => {
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis } : imported })));
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); await analyzeDeck();
        const stored = JSON.parse(window.sessionStorage.getItem('deckflow.deck-modules.v1')!);
        expect(stored.analysis).toEqual(analysis); expect(stored.analysisKey).toBe('analysis-key'); expect(document.querySelector('[data-deck-modules-analysis-health]')!.textContent).toBe('Needs attention');
    });

    it('initialize_AnalyzeCedhAlternative_PostsCedhMode', async () => {
        // WR-15: the analysis mode was hardcoded to 'Casual' regardless of the selected
        // alternative's declared profile, quietly contradicting the declared-profile disclosure
        // rendered next to the numbers for a cEDH-declared configuration.
        const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis } : imported }));
        vi.stubGlobal('fetch', fetchMock);
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A';
        document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh';
        document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win fast.';
        document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        await analyzeDeck();
        const analyzeCall = fetchMock.mock.calls.find(call => call[0] === '/deck-modules/analyze')!;
        expect(JSON.parse(analyzeCall[1].body).mode).toBe('Cedh');
    });

    it('initialize_AnalyzeBracket4Alternative_PostsFocusedMode', async () => {
        const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis } : imported }));
        vi.stubGlobal('fetch', fetchMock);
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A';
        document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Bracket4HighPower';
        document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win eventually.';
        document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        await analyzeDeck();
        const analyzeCall = fetchMock.mock.calls.find(call => call[0] === '/deck-modules/analyze')!;
        expect(JSON.parse(analyzeCall[1].body).mode).toBe('Focused');
    });

    it('initialize_AnalyzeDoubleClickWhileInFlight_FiresOnlyOneRequest', async () => {
        // WR-08: analyze() is the expensive Scryfall-backed path; an impatient double-click must
        // not fire concurrent analyses (CLAUDE.md records live Cloudflare IP blocks from this).
        let resolveAnalyze: ((value: unknown) => void) | null = null;
        const fetchMock = vi.fn().mockImplementation((url: string) => url === '/deck-modules/analyze'
            ? new Promise(resolve => { resolveAnalyze = resolve; }).then(() => ({ ok: true, json: async () => ({ analysisKey: 'analysis-key', analysis }) }))
            : Promise.resolve({ ok: true, json: async () => imported }));
        vi.stubGlobal('fetch', fetchMock);
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        const button = document.querySelector<HTMLButtonElement>('[data-deck-modules-analyze]')!;
        button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
        await vi.waitFor(() => expect(fetchMock.mock.calls.filter(call => call[0] === '/deck-modules/analyze')).toHaveLength(1));
        expect(button.disabled).toBe(true);
        button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
        button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
        resolveAnalyze!(undefined);
        await vi.waitFor(() => expect(document.querySelector<HTMLElement>('[data-deck-modules-analysis]')!.hidden).toBe(false));
        expect(fetchMock.mock.calls.filter(call => call[0] === '/deck-modules/analyze')).toHaveLength(1);
    });

    it('initialize_AnalysisWithHandoffKey_ShowsEncodedFullReportLink', async () => {
        const handoffAnalysis = { ...analysis, manabaseHandoffKey: 'handoff key' };
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis: handoffAnalysis } : imported })));
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); await analyzeDeck();
        const handoff = document.querySelector<HTMLAnchorElement>('[data-deck-modules-manabase-handoff]')!;
        await vi.waitFor(() => expect(handoff.hidden).toBe(false)); expect(handoff.href).toContain('/manabase?handoff=handoff%20key');
    });

    it('initialize_StaleAnalysisWithHandoffKey_KeepsLinkAndExplainsEarlierConfiguration', async () => {
        const handoffAnalysis = { ...analysis, manabaseHandoffKey: 'handoff-key' };
        window.sessionStorage.setItem('deckflow.deck-modules.v1', JSON.stringify({ version: 1, baselineToken: 'token', commandZone: [], baselineMainboardEntries: [], unassignedEntries: [], coreEntries: [], alternatives: [], selectedAlternativeId: '', analysis: handoffAnalysis, analysisKey: 'analysis-key', analysisStale: true }));
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize();
        await vi.waitFor(() => expect(document.querySelector<HTMLAnchorElement>('[data-deck-modules-manabase-handoff]')!.hidden).toBe(false));
        expect(document.querySelector<HTMLElement>('[data-deck-modules-handoff-note]')!.textContent).toContain('earlier configuration');
    });

    it('initialize_NamedAttributedFinding_RendersNamedCause', async () => {
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis } : imported })));
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); await analyzeDeck();
        expect(document.querySelector('[data-deck-modules-cause="named"]')).not.toBeNull();
    });

    it('initialize_InferredAttributedFinding_RendersInferredCause', async () => {
        const inferred = { ...analysis, attributedFindings: [{ ...analysis.attributedFindings[0], strength: 'ModuleMembership', attributedCard: null, attributedModule: 'Strategy B', swapDirection: null }] };
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis: inferred } : imported })));
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); await analyzeDeck();
        expect(document.querySelector('[data-deck-modules-cause="inferred"]')).not.toBeNull();
    });

    it('initialize_UnattributedFinding_RendersNoCause', async () => {
        const none = { ...analysis, attributedFindings: [{ ...analysis.attributedFindings[0], strength: 'None', attributedCard: null, attributedModule: null, swapDirection: null }] };
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis: none } : imported })));
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); await analyzeDeck();
        expect(document.querySelector('[data-deck-modules-cause]')).toBeNull();
    });

    it('initialize_AnalysisThenPanelMove_PreservesNumbersAndShowsStaleMarker', async () => {
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis } : imported })));
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); await analyzeDeck();
        const health = document.querySelector('[data-deck-modules-analysis-health]')!.textContent; document.querySelector<HTMLInputElement>('[data-deck-modules-entries="unassigned"] input')!.checked = true; document.querySelector<HTMLButtonElement>('[data-deck-modules-move="unassigned:core"]')!.click();
        expect(document.querySelector('[data-deck-modules-analysis-health]')!.textContent).toBe(health); expect(document.querySelector<HTMLElement>('[data-deck-modules-analysis-stale]')!.hidden).toBe(false);
    });

    it('initialize_AnalysisThenAlternativeSelection_ShowsStaleMarker', async () => {
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis } : imported })));
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); await analyzeDeck();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click(); document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan B'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win later.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click(); document.querySelectorAll<HTMLButtonElement>('[data-deck-modules-alternative]')[0].click();
        expect(document.querySelector<HTMLElement>('[data-deck-modules-analysis-stale]')!.hidden).toBe(false);
    });

    it('initialize_AnalysisThenAddAlternative_ShowsStaleMarker', async () => {
        // CR-03: addAlternative() re-points selectedAlternativeId at the new alternative, so a
        // prior analysis must be marked stale the same way move()/selection/edit already do --
        // otherwise the panel keeps showing the previous alternative's numbers as current.
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis } : imported })));
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        await analyzeDeck();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan B'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win later.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        expect(document.querySelector<HTMLElement>('[data-deck-modules-analysis-stale]')!.hidden).toBe(false);
    });

    it('initialize_AnalysisThenAlternativeEdit_ShowsStaleMarker', async () => {
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis } : imported })));
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click(); document.querySelector<HTMLButtonElement>('[data-deck-modules-alternative]')!.click(); await analyzeDeck();
        const name = document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!; name.value = 'Edited'; name.dispatchEvent(new Event('input', { bubbles: true }));
        expect(document.querySelector<HTMLElement>('[data-deck-modules-analysis-stale]')!.hidden).toBe(false);
    });

    it('initialize_ReanalyzeAfterStale_ReplacesNumbersAndHidesMarker', async () => {
        const refreshed = { ...analysis, health: 'Healthy' }; const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis: fetchMock.mock.calls.filter(call => call[0] === '/deck-modules/analyze').length === 1 ? analysis : refreshed } : imported })); vi.stubGlobal('fetch', fetchMock);
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); await analyzeDeck(); document.querySelector<HTMLInputElement>('[data-deck-modules-entries="unassigned"] input')!.checked = true; document.querySelector<HTMLButtonElement>('[data-deck-modules-move="unassigned:core"]')!.click(); await analyzeDeck(); await vi.waitFor(() => expect(document.querySelector('[data-deck-modules-analysis-health]')!.textContent).toBe('Healthy'));
        expect(document.querySelector('[data-deck-modules-analysis-health]')!.textContent).toBe('Healthy'); expect(document.querySelector<HTMLElement>('[data-deck-modules-analysis-stale]')!.hidden).toBe(true);
    });

    it('initialize_StoredAnalysis_RestoresSnapshotAndStaleState', async () => {
        window.sessionStorage.setItem('deckflow.deck-modules.v1', JSON.stringify({ version: 1, baselineToken: 'token', commandZone: [], baselineMainboardEntries: [], unassignedEntries: [], coreEntries: [], alternatives: [], selectedAlternativeId: '', analysis, analysisKey: 'analysis-key', analysisStale: true }));
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize();
        expect(document.querySelector<HTMLElement>('[data-deck-modules-analysis]')!.hidden).toBe(false); expect(document.querySelector('[data-deck-modules-analysis-health]')!.textContent).toBe('Needs attention'); expect(document.querySelector<HTMLElement>('[data-deck-modules-analysis-stale]')!.hidden).toBe(false);
    });

    it('initialize_MalformedRestoredProfileValue_DoesNotAbortRenderWithSyntaxError', () => {
        // WR-13: alternative.profile round-trips through sessionStorage unvalidated. Building a
        // dynamic attribute selector from it (`option[value="${...}"]`) let a value containing `"`
        // throw a querySelector SyntaxError, aborting the rest of render() -- panels, balance
        // state, and the comparison picker all stopped updating with no error surfaced.
        window.sessionStorage.setItem('deckflow.deck-modules.v1', JSON.stringify({
            version: 1, baselineToken: 'token', commandZone: [], baselineMainboardEntries: [{ name: 'Sol Ring', quantity: 1 }], unassignedEntries: [{ name: 'Sol Ring', quantity: 1 }], coreEntries: [],
            alternatives: [{ id: 'a', name: 'Plan A', profile: 'Cedh"][malformed', playPlan: 'Win.', mainboardEntries: [], manaSupportName: 'Mana', manaSupportEntries: [] }],
            selectedAlternativeId: 'a',
        }));

        expect(() => (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize()).not.toThrow();
        // Panels rendered past the malformed-selector line rather than aborting.
        expect(document.querySelector('[data-deck-modules-count="unassigned"]')!.textContent).toBe('1');
        expect(document.querySelector<HTMLElement>('[data-deck-modules-summary-profile]')!.textContent).toBe('Cedh"][malformed');
    });

    it('initialize_StoredDraftWithoutAnalysis_HidesAnalysisPanelWithoutThrowing', () => {
        window.sessionStorage.setItem('deckflow.deck-modules.v1', JSON.stringify({ version: 1, baselineToken: 'token', commandZone: [], baselineMainboardEntries: [], unassignedEntries: [], coreEntries: [], alternatives: [], selectedAlternativeId: '' }));
        expect(() => (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize()).not.toThrow(); expect(document.querySelector<HTMLElement>('[data-deck-modules-analysis]')!.hidden).toBe(true);
    });

    it('initialize_DeclaredPlayPlan_RendersAngleBracketsAsText', async () => {
        const playPlan = 'Win with <b>combat</b>.';
        const declaredAnalysis = { ...analysis, signals: { bracketNumber: 4, gameChangers: [], massLandDenialCards: [], extraTurnCards: [], comboDetectionAvailable: false, catalogEffectiveDate: '2026-09-01', interactionAttributionAvailable: false, interactionsByModule: [], declared: { profile: 'Bracket 4 High Power', playPlan, isDeclared: true, profileDisagreementNote: null } } };
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis: declaredAnalysis } : imported })));

        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); await analyzeDeck();

        const plan = document.querySelector('[data-deck-modules-declared-plan]')!;
        expect(plan.textContent).toBe(playPlan); expect(plan.querySelector('b')).toBeNull();
    });

    it('initialize_InteractionAttributionUnavailable_ShowsUnavailableLineAndHidesTable', async () => {
        const unavailableAnalysis = { ...analysis, signals: { bracketNumber: 4, gameChangers: [], massLandDenialCards: [], extraTurnCards: [], comboDetectionAvailable: false, catalogEffectiveDate: '2026-09-01', interactionAttributionAvailable: false, interactionsByModule: [], declared: null } };
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis: unavailableAnalysis } : imported })));

        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); await analyzeDeck();

        expect(document.querySelector<HTMLElement>('[data-deck-modules-interactions-unavailable]')!.hidden).toBe(false);
        expect(document.querySelector<HTMLElement>('[data-deck-modules-interactions]')!.closest('table')!.hidden).toBe(true);
    });

    it('initialize_InteractionRowsAvailable_RendersIntoTheTbodyWithoutDestroyingIt', async () => {
        // WR-10: data-deck-modules-interactions previously lived on both the <table> and its
        // <tbody>, so query() resolved to the (first-in-document-order) <table> and
        // replaceChildren() deleted the <tbody> outright, appending rows as direct <table>
        // children instead. The outer hook is now data-deck-modules-interactions-table, so the
        // attribute identifies exactly one element and the rows must land inside a surviving
        // <tbody>.
        const availableAnalysis = { ...analysis, signals: { bracketNumber: 4, gameChangers: [], massLandDenialCards: [], extraTurnCards: [], comboDetectionAvailable: false, catalogEffectiveDate: '2026-09-01', interactionAttributionAvailable: true, interactionsByModule: [{ moduleName: 'Strategy', interactionCount: 2 }], declared: null } };
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis: availableAnalysis } : imported })));

        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); await analyzeDeck();

        const tbody = document.querySelector<HTMLElement>('[data-deck-modules-interactions]')!;
        expect(tbody.tagName).toBe('TBODY');
        expect(tbody.parentElement?.tagName).toBe('TABLE');
        expect(tbody.querySelectorAll('tr')).toHaveLength(1);
        expect(tbody.textContent).toContain('Strategy');
    });

    it('initialize_ComparisonPicker_ListsCompiledAlternativesAndRejectsSameSide', async () => {
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win.';
        document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click(); document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan B'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'Win differently.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        const reference = document.querySelector<HTMLSelectElement>('[data-deck-modules-compare-reference]')!;
        const other = document.querySelector<HTMLSelectElement>('[data-deck-modules-compare-other]')!;
        expect(reference.options).toHaveLength(2); expect(other.options).toHaveLength(2);
        other.value = reference.value; other.dispatchEvent(new Event('change', { bubbles: true }));
        expect(document.querySelector<HTMLElement>('[data-deck-modules-comparison-message]')!.textContent).toContain('different');
    });

    it('initialize_ComparisonCacheMiss_RepostsStoredPayloadWithoutAnalyzing', async () => {
        const delta = { reference: { configurationName: 'Plan A', isAnalyzed: true, landCount: 30, landTargetDelta: 0, rampSourceCount: 8, hardToCastCount: 1, health: 'Healthy' }, columns: [{ configurationName: 'Plan B', isAnalyzed: true, landCount: 30, landCountDelta: 0, landTargetDelta: 0, rampSourceCount: 8, rampSourceCountDelta: 0, hardToCastCount: 1, hardToCastCountDelta: 0, health: 'Healthy', isCoreOnly: true }], colorRows: [], interactionRows: [] };
        let ids: string[] = []; const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve(url === '/deck-modules/compare' && fetchMock.mock.calls.filter(call => call[0] === url).length === 1 ? { ok: false, status: 409, json: async () => ({ missingConfigurationIds: ids }) } : { ok: true, status: 200, json: async () => url === '/deck-modules/compare' ? delta : imported }));
        vi.stubGlobal('fetch', fetchMock); (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'A.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan B'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'B.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        const stored = JSON.parse(window.sessionStorage.getItem('deckflow.deck-modules.v1')!); ids = stored.alternatives.map((alternative: { id: string }) => alternative.id); stored.comparisonAnalyses = Object.fromEntries(ids.map((id, index) => [id, { analysis, analysisKey: `${index}-key` }])); window.sessionStorage.setItem('deckflow.deck-modules.v1', JSON.stringify(stored));
        // comparisonAnalyses is held in-memory (populated by analyze(), mirroring what a live tab
        // actually holds), so simulate a reload against a fresh page element.
        document.body.innerHTML = markup(); (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize();
        document.querySelector<HTMLSelectElement>('[data-deck-modules-compare-reference]')!.value = ids[0]; const other = document.querySelector<HTMLSelectElement>('[data-deck-modules-compare-other]')!; other.value = ids[1]; other.dispatchEvent(new Event('change', { bubbles: true })); document.querySelector<HTMLButtonElement>('[data-deck-modules-compare]')!.click();
        await vi.waitFor(() => expect(fetchMock.mock.calls.filter(call => call[0] === '/deck-modules/compare')).toHaveLength(2));
        expect(fetchMock.mock.calls.filter(call => call[0] === '/deck-modules/analyze')).toHaveLength(0); expect(document.querySelector('[data-deck-modules-comparison-body]')!.textContent).toContain('No change');
    });

    it('initialize_ComparisonAnalysedSides_PostsOnceAndRendersTwoColumns', async () => {
        const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, status: 200, json: async () => url === '/deck-modules/compare' ? comparisonDelta() : imported })); vi.stubGlobal('fetch', fetchMock);
        const { ids, stored } = await prepareComparison(); stored.comparisonAnalyses = Object.fromEntries(ids.map((id, index) => [id, { analysis, analysisKey: `${index}-key` }])); window.sessionStorage.setItem('deckflow.deck-modules.v1', JSON.stringify(stored));
        document.querySelector<HTMLButtonElement>('[data-deck-modules-compare]')!.click();
        await vi.waitFor(() => expect(document.querySelectorAll('[data-deck-modules-comparison-body] tr:first-child td')).toHaveLength(2));
        expect(fetchMock.mock.calls.filter(call => call[0] === '/deck-modules/compare')).toHaveLength(1);
    });

    it('initialize_ComparisonCacheMissWithoutStoredAnalysis_PromptsToAnalyseAndDoesNotRetry', async () => {
        const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve(url === '/deck-modules/compare' ? { ok: false, status: 409, json: async () => ({ missingConfigurationIds: [secondId] }) } : { ok: true, status: 200, json: async () => imported })); let secondId = '';
        vi.stubGlobal('fetch', fetchMock); const { ids, stored } = await prepareComparison(); secondId = ids[1]; stored.comparisonAnalyses = { [ids[0]]: { analysis, analysisKey: 'first-key' } }; window.sessionStorage.setItem('deckflow.deck-modules.v1', JSON.stringify(stored));
        document.querySelector<HTMLButtonElement>('[data-deck-modules-compare]')!.click();
        await vi.waitFor(() => expect(document.querySelector<HTMLElement>('[data-deck-modules-comparison-message]')!.textContent).toBe('Analyse Plan B first.'));
        expect(fetchMock.mock.calls.filter(call => call[0] === '/deck-modules/compare')).toHaveLength(1);
    });

    it('initialize_ComparisonNetworkFault_ShowsFailureMessageInsteadOfUnhandledRejection', async () => {
        // WR-09: a network fault rejects postJson()'s fetch promise; without a catch, the
        // rejection propagated out of `void compare()` as an unhandled promise rejection and the
        // panel silently did nothing -- no message rendered at all.
        const fetchMock = vi.fn().mockImplementation((url: string) => url === '/deck-modules/compare' ? Promise.reject(new TypeError('Failed to fetch')) : Promise.resolve({ ok: true, status: 200, json: async () => imported }));
        vi.stubGlobal('fetch', fetchMock);
        await prepareComparison();
        document.querySelector<HTMLButtonElement>('[data-deck-modules-compare]')!.click();
        await vi.waitFor(() => expect(document.querySelector<HTMLElement>('[data-deck-modules-comparison-message]')!.textContent).toBe('Configuration comparison failed.'));
    });

    it('initialize_ComparisonAfterMoveInvalidatesSnapshots_PromptsToReanalyseInsteadOfRenderingStaleTable', async () => {
        // CR-05: comparisonAnalyses snapshots must be invalidated by the same mutations that mark
        // the single-configuration panel stale, or Compare silently posts and renders outdated
        // numbers with no staleness signal of its own.
        let ids: string[] = [];
        const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve(
            url === '/deck-modules/compare' ? { ok: false, status: 409, json: async () => ({ missingConfigurationIds: ids }) }
                : url === '/deck-modules/analyze' ? { ok: true, status: 200, json: async () => ({ analysisKey: 'analysis-key', analysis }) }
                    : { ok: true, status: 200, json: async () => imported }));
        vi.stubGlobal('fetch', fetchMock);
        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan A'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'A.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        await analyzeDeck();
        document.querySelector<HTMLInputElement>('[data-deck-modules-name]')!.value = 'Plan B'; document.querySelector<HTMLSelectElement>('[data-deck-modules-profile]')!.value = 'Cedh'; document.querySelector<HTMLTextAreaElement>('[data-deck-modules-plan]')!.value = 'B.'; document.querySelector<HTMLButtonElement>('[data-deck-modules-add-alternative]')!.click();
        await analyzeDeck();
        document.querySelectorAll<HTMLButtonElement>('[data-deck-modules-alternative]')[0].click();
        await analyzeDeck();
        const stored = JSON.parse(window.sessionStorage.getItem('deckflow.deck-modules.v1')!); ids = stored.alternatives.map((alternative: { id: string }) => alternative.id);
        document.querySelector<HTMLSelectElement>('[data-deck-modules-compare-reference]')!.value = ids[0]; const other = document.querySelector<HTMLSelectElement>('[data-deck-modules-compare-other]')!; other.value = ids[1]; other.dispatchEvent(new Event('change', { bubbles: true }));
        // Move a card -- this must invalidate every stored comparison snapshot, not just the
        // single-configuration analysis panel.
        document.querySelector<HTMLInputElement>('[data-deck-modules-entries="unassigned"] input')!.checked = true; document.querySelector<HTMLButtonElement>('[data-deck-modules-move="unassigned:core"]')!.click();
        document.querySelector<HTMLButtonElement>('[data-deck-modules-compare]')!.click();
        await vi.waitFor(() => expect(document.querySelector<HTMLElement>('[data-deck-modules-comparison-message]')!.textContent).toContain('first.'));
        expect(document.querySelectorAll('[data-deck-modules-comparison-body] tr')).toHaveLength(0);
    });

    it('initialize_ComparisonNotAnalysedColumn_MarksEveryMetricWhileOtherValuesRender', async () => {
        const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, status: 200, json: async () => url === '/deck-modules/compare' ? comparisonDelta({ isAnalyzed: false }) : imported })); vi.stubGlobal('fetch', fetchMock);
        await prepareComparison(); document.querySelector<HTMLButtonElement>('[data-deck-modules-compare]')!.click();
        await vi.waitFor(() => expect(document.querySelectorAll('[data-deck-modules-comparison-body] tr')).toHaveLength(5));
        document.querySelectorAll('[data-deck-modules-comparison-body] tr').forEach(row => { expect(row.querySelectorAll('td')[0].textContent).toBe('Not analysed'); expect(row.querySelectorAll('td')[1].textContent).not.toBe('Not analysed'); });
    });

    it('initialize_ComparisonCoreOnlyColumn_MarksHeaderAsIncomplete', async () => {
        const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, status: 200, json: async () => url === '/deck-modules/compare' ? comparisonDelta({}, { isCoreOnly: true }) : imported })); vi.stubGlobal('fetch', fetchMock);
        await prepareComparison(); document.querySelector<HTMLButtonElement>('[data-deck-modules-compare]')!.click();
        await vi.waitFor(() => expect(document.querySelectorAll('[data-deck-modules-comparison-head] th')).toHaveLength(3));
        expect(document.querySelectorAll('[data-deck-modules-comparison-head] th')[2].textContent).toBe('Plan B — Core-only analysis');
    });

    it('initialize_ComparisonColorRow_RendersSameCellCountAsMetricRows', async () => {
        // CR-01: ColorRows.Values must carry one value per analysis (reference included) so the
        // rendered row aligns 1:1 with the [Reference, ...Columns] header -- same td count as the
        // metric rows above it, not one short.
        const delta = { ...comparisonDelta(), colorRows: [{ color: 'U', displayColor: 'Blue', values: [{ configurationId: 'a', actualSources: 18, requiredSources: 20, actualSourcesDelta: null, isPresent: true }, { configurationId: 'b', actualSources: 20, requiredSources: 20, actualSourcesDelta: 2, isPresent: true }] }] };
        const fetchMock = vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, status: 200, json: async () => url === '/deck-modules/compare' ? delta : imported })); vi.stubGlobal('fetch', fetchMock);
        await prepareComparison(); document.querySelector<HTMLButtonElement>('[data-deck-modules-compare]')!.click();
        await vi.waitFor(() => expect(document.querySelectorAll('[data-deck-modules-comparison-body] tr')).toHaveLength(6));
        const rows = document.querySelectorAll('[data-deck-modules-comparison-body] tr');
        const metricCellCount = rows[0].querySelectorAll('td').length;
        const colorRow = Array.from(rows).find(row => row.querySelector('th')?.textContent === 'Blue sources')!;
        expect(colorRow.querySelectorAll('td')).toHaveLength(metricCellCount);
    });
});
