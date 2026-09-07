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
  <section data-deck-modules-analysis hidden><p data-deck-modules-analysis-stale hidden>Cards changed since this analysis.</p><p data-deck-modules-core-only hidden></p><span data-deck-modules-analysis-health></span><span data-deck-modules-analysis-lands></span><span data-deck-modules-analysis-target></span><span data-deck-modules-analysis-land-delta></span><span data-deck-modules-analysis-ramp></span><span data-deck-modules-analysis-hardtocast></span><table><tbody data-deck-modules-analysis-colors></tbody></table><div data-deck-modules-disclosure hidden><span data-deck-modules-declared-profile></span><p data-deck-modules-declared-plan></p><p data-deck-modules-profile-note hidden></p></div></section>
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

    it('initialize_StoredDraftWithoutAnalysis_HidesAnalysisPanelWithoutThrowing', () => {
        window.sessionStorage.setItem('deckflow.deck-modules.v1', JSON.stringify({ version: 1, baselineToken: 'token', commandZone: [], baselineMainboardEntries: [], unassignedEntries: [], coreEntries: [], alternatives: [], selectedAlternativeId: '' }));
        expect(() => (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize()).not.toThrow(); expect(document.querySelector<HTMLElement>('[data-deck-modules-analysis]')!.hidden).toBe(true);
    });

    it('initialize_DeclaredPlayPlan_RendersAngleBracketsAsText', async () => {
        const playPlan = 'Win with <b>combat</b>.';
        const declaredAnalysis = { ...analysis, signals: { bracketNumber: 4, gameChangers: [], massLandDenialCards: [], extraTurnCards: [], comboDetectionAvailable: false, catalogEffectiveDate: '2026-09-01', declared: { profile: 'Bracket 4 High Power', playPlan, isDeclared: true, profileDisagreementNote: null } } };
        vi.stubGlobal('fetch', vi.fn().mockImplementation((url: string) => Promise.resolve({ ok: true, json: async () => url === '/deck-modules/analyze' ? { analysisKey: 'analysis-key', analysis: declaredAnalysis } : imported })));

        (globalThis.DeckFlowDeckModules as DeckModulesApi).initialize(); await importDraft(); await analyzeDeck();

        const plan = document.querySelector('[data-deck-modules-declared-plan]')!;
        expect(plan.textContent).toBe(playPlan); expect(plan.querySelector('b')).toBeNull();
    });
});
