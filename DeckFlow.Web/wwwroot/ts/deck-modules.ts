(() => {
    const storageKey = 'deckflow.deck-modules.v1';
    const version = 1;
    type Entry = { name: string; quantity: number };
    type Alternative = { id: string; name: string; profile: string; playPlan: string; mainboardEntries: Entry[]; manaSupportName: string; manaSupportEntries: Entry[] };
    type ConfigurationAnalysisSnapshot = { analysisKey?: never; landCount: number; targetLandCount: number; landDelta: number; health: string; rampSourceCount: number; hardToCastCount: number; isCoreOnly: boolean; attributedFindings: { displayColor: string; actualSources: number; requiredSources: number; deficit: number; drivingSpell: string; strength: 'None' | 'ModuleMembership' | 'NamedCard'; attributedCard?: string | null; attributedModule?: string | null; swapDirection?: 'added' | 'removed' | null }[]; analysisNotice?: string | null; signals?: { bracketNumber: number; gameChangers: string[]; massLandDenialCards: string[]; extraTurnCards: string[]; comboDetectionAvailable: boolean; catalogEffectiveDate: string; interactionAttributionAvailable: boolean; interactionsByModule: { moduleName: string; interactionCount: number }[]; declared: { profile: string; playPlan: string; isDeclared: boolean; profileDisagreementNote: string | null } | null } | null };
    type Draft = { version: number; baselineToken: string; commandZone: Entry[]; baselineMainboardEntries: Entry[]; unassignedEntries: Entry[]; coreEntries: Entry[]; alternatives: Alternative[]; selectedAlternativeId: string; analysis?: ConfigurationAnalysisSnapshot; analysisKey?: string; analysisStale?: boolean; comparisonAnalyses?: Record<string, { analysis: ConfigurationAnalysisSnapshot; analysisKey: string }> };
    type ComparisonSide = { configurationId: string; analysisKey: string; analysis?: ConfigurationAnalysisSnapshot };
    type Panel = 'unassigned' | 'core' | 'strategy' | 'mana';
    let draft: Draft | null = null;
    let compilation: Record<string, unknown> | null = null;
    let editingAlternative = false;

    const root = () => document.querySelector<HTMLElement>('[data-deck-modules]');
    const query = <T extends Element>(selector: string) => root()?.querySelector<T>(selector) ?? null;
    const renderHandoff = () => { const analysis = draft?.analysis as (ConfigurationAnalysisSnapshot & { manabaseHandoffKey?: string }) | undefined; const handoff = query<HTMLAnchorElement>('[data-deck-modules-manabase-handoff]'); const note = query<HTMLElement>('[data-deck-modules-handoff-note]'); if (!handoff || !note) return; const key = analysis?.manabaseHandoffKey; handoff.hidden = !key; handoff.href = key ? `/manabase?handoff=${encodeURIComponent(key)}` : '#'; note.textContent = `The handed-off result is available for a few minutes. It is not a saved or shareable link.${draft?.analysisStale ? ' It will show the earlier configuration.' : ''}`; };
    const observeHandoff = () => { const page = root(); if (!page) return; const observer = new MutationObserver(() => { observer.disconnect(); renderHandoff(); observer.observe(page, { attributes: true, childList: true, subtree: true }); }); observer.observe(page, { attributes: true, childList: true, subtree: true }); renderHandoff(); };
    const initializeWithHandoff = () => { initialize(); observeHandoff(); };
    const total = (items: Entry[]) => items.reduce((sum, item) => sum + item.quantity, 0);
    const active = () => draft?.alternatives.find(item => item.id === draft?.selectedAlternativeId) ?? draft?.alternatives[0] ?? null;
    const announce = (text: string) => { const live = query<HTMLElement>('[data-deck-modules-live]'); if (live) live.textContent = text; };
    const error = (text: string) => { const target = query<HTMLElement>('[data-deck-modules-error]'); if (target) target.textContent = text; };
    const save = () => { try { if (draft) sessionStorage.setItem(storageKey, JSON.stringify(draft)); } catch { /* Browser storage is optional. */ } };
    const clear = () => { try { sessionStorage.removeItem(storageKey); } catch { /* Browser storage is optional. */ } };
    const restore = () => { try { const raw = sessionStorage.getItem(storageKey); if (!raw) return; const stored = JSON.parse(raw) as Draft; if (stored.version === version) draft = stored; } catch { /* Invalid session drafts are ignored. */ } };
    const panelEntries = (panel: Panel): Entry[] => {
        const alternative = active();
        if (!draft || (panel === 'strategy' || panel === 'mana') && !alternative) return [];
        return panel === 'unassigned' ? draft.unassignedEntries : panel === 'core' ? draft.coreEntries : panel === 'strategy' ? alternative!.mainboardEntries : alternative!.manaSupportEntries;
    };
    const panelTitle = (panel: Panel) => panel === 'unassigned' ? 'Unassigned' : panel === 'core' ? 'Core' : panel === 'strategy' ? 'Strategy' : 'Mana Support';
    const addText = (parent: Element, tag: string, text: string) => { const element = document.createElement(tag); element.textContent = text; parent.append(element); return element; };
    const comparisonElements = () => {
        const panel = query<HTMLElement>('[data-deck-modules-comparison]');
        return {
            panel,
            reference: panel?.querySelector<HTMLSelectElement>('[data-deck-modules-compare-reference]') ?? null,
            other: panel?.querySelector<HTMLSelectElement>('[data-deck-modules-compare-other]') ?? null,
            button: panel?.querySelector<HTMLButtonElement>('[data-deck-modules-compare]') ?? null,
            message: panel?.querySelector<HTMLElement>('[data-deck-modules-comparison-message]') ?? null,
            head: panel?.querySelector<HTMLTableSectionElement>('[data-deck-modules-comparison-head]') ?? null,
            body: panel?.querySelector<HTMLTableSectionElement>('[data-deck-modules-comparison-body]') ?? null,
        };
    };
    const comparisonText = (value: unknown) => value === null || value === undefined ? 'Not analysed' : String(value);
    const comparisonDelta = (value: unknown) => value === 0 ? 'No change' : comparisonText(value);

    const renderRows = (panel: Panel) => {
        const body = query<HTMLTableSectionElement>(`[data-deck-modules-entries="${panel}"]`);
        if (!body) return;
        const selected = new Set<string>(); const oldInputs = Array.from(body.querySelectorAll<HTMLInputElement>('[data-deck-modules-select]'));
        oldInputs.filter(input => input.checked).forEach(input => { const name = input.getAttribute('aria-label')?.slice(7) ?? ''; const occurrence = oldInputs.slice(0, oldInputs.indexOf(input)).filter(item => item.getAttribute('aria-label')?.slice(7) === name).length; selected.add(`${name}:${occurrence}`); });
        body.replaceChildren(); const filter = query<HTMLInputElement>(`[data-deck-modules-filter="${panel}"]`)?.value.toLocaleLowerCase() ?? ''; const occurrences = new Map<string, number>();
        occurrences.clear();
        panelEntries(panel).forEach((entry, index) => {
            const occurrence = occurrences.get(entry.name) ?? 0; occurrences.set(entry.name, occurrence + 1);
            const row = document.createElement('tr'); if (filter && !entry.name.toLocaleLowerCase().includes(filter)) row.setAttribute('hidden', 'hidden');
            const select = document.createElement('input'); select.type = 'checkbox'; select.setAttribute('data-deck-modules-select', `${panel}:${index}`); select.setAttribute('aria-label', `Select ${entry.name}`); select.checked = selected.has(`${entry.name}:${occurrence}`);
            const selectCell = document.createElement('td'); selectCell.append(select); row.append(selectCell);
            addText(row, 'td', entry.quantity.toString()); addText(row, 'td', entry.name); body.append(row);
        });
    };
    const renderPanels = () => {
        const activeName = query<HTMLElement>('[data-deck-modules-active-name]'); if (activeName) activeName.textContent = active()?.name ?? '';
        (['unassigned', 'core', 'strategy', 'mana'] as Panel[]).forEach(panel => {
            const count = query<HTMLElement>(`[data-deck-modules-count="${panel}"]`); if (count) count.textContent = total(panelEntries(panel)).toString(); renderRows(panel);
        });
        const reconciliation = query<HTMLElement>('[data-deck-modules-reconciliation]');
        if (reconciliation && draft) reconciliation.textContent = `${total(draft.unassignedEntries) + total(draft.coreEntries) + total(panelEntries('strategy')) + total(panelEntries('mana'))} of ${total(draft.baselineMainboardEntries)} baseline cards assigned or unassigned.`;
    };
    const renderAlternatives = () => {
        const holder = query<HTMLElement>('[data-deck-modules-alternatives]'); if (!holder || !draft) return; holder.replaceChildren(); const selected = active();
        draft.alternatives.forEach(alternative => { const button = document.createElement('button'); button.type = 'button'; button.setAttribute('data-deck-modules-alternative', alternative.id); button.textContent = `${alternative.name} — ${total(alternative.mainboardEntries)} cards`; button.setAttribute('aria-pressed', String(alternative.id === selected?.id)); holder.append(button); });
    };
    const balanced = () => { const alternative = active(); return !!draft && !!alternative && draft.alternatives.length >= 2 && draft.alternatives.every(item => total(item.mainboardEntries) === total(alternative.mainboardEntries)); };
    const renderBalance = () => {
        const status = query<HTMLElement>('[data-deck-modules-balance]'); const okay = balanced(); const current = active();
        const report = query<HTMLElement>('.deck-modules__report, [data-deck-modules-report]'); if (report) report.hidden = compilation === null;
        if (status) status.textContent = okay ? 'Alternatives are balanced.' : draft && current ? draft.alternatives.filter(item => total(item.mainboardEntries) !== total(current.mainboardEntries)).map(item => `${item.name}: unbalanced — ${total(item.mainboardEntries)} cards, expected ${total(current.mainboardEntries)}`).join('; ') || 'Add at least two alternatives before compiling.' : '';
        const compileButton = query<HTMLButtonElement>('[data-deck-modules-compile]'); if (compileButton) { compileButton.disabled = !okay; compileButton.setAttribute('aria-describedby', 'deck-modules-balance'); }
        ['export', 'copy', 'analyze'].forEach(action => { const button = query<HTMLButtonElement>(`[data-deck-modules-${action}]`); if (button) { button.disabled = !okay || (action !== 'analyze' && !compilation); button.setAttribute('aria-describedby', 'deck-modules-balance'); } });
    };
    const renderCommandZone = () => { const holder = query<HTMLElement>('[data-deck-modules-command-zone]'); if (!holder) return; holder.replaceChildren(); (draft?.commandZone ?? []).forEach(entry => addText(holder, 'p', `${entry.quantity} ${entry.name}`)); };
    const render = () => { const alternative = active(); const summaryProfile = query<HTMLElement>('[data-deck-modules-summary-profile]'); if (summaryProfile) { const profile = query<HTMLSelectElement>('[data-deck-modules-profile]'); summaryProfile.textContent = alternative ? profile?.querySelector<HTMLOptionElement>(`option[value="${alternative.profile}"]`)?.textContent ?? alternative.profile : 'Not selected'; } const summaryPlan = query<HTMLElement>('[data-deck-modules-summary-plan]'); if (summaryPlan) summaryPlan.textContent = alternative?.playPlan ?? 'Not entered'; if (editingAlternative && alternative) { const name = query<HTMLInputElement>('[data-deck-modules-name]'); if (name) name.value = alternative.name; const profile = query<HTMLSelectElement>('[data-deck-modules-profile]'); if (profile) profile.value = alternative.profile; const plan = query<HTMLTextAreaElement>('[data-deck-modules-plan]'); if (plan) plan.value = alternative.playPlan; } renderCommandZone(); renderAlternatives(); renderPanels(); renderBalance(); renderAnalysis(); renderComparisonPicker(); save(); };
    const selected = (panel: Panel) => Array.from(root()?.querySelectorAll<HTMLInputElement>(`[data-deck-modules-select^="${panel}:"]:checked`) ?? []).map(input => Number(input.dataset.deckModulesSelect?.split(':')[1])).sort((a, b) => b - a);
    // Why (CR-05): a comparison snapshot recorded by analyze() describes the alternative as it
    // stood at that moment. Any draft mutation that can change an alternative's numbers --
    // moving cards, adding an alternative, or editing name/profile/plan -- must drop every
    // stored snapshot, not just mark the single-configuration panel stale, or compare() would
    // silently post and render outdated numbers with no staleness signal of its own.
    const invalidateAnalyses = () => {
        if (!draft) return;
        if (draft.analysis) draft.analysisStale = true;
        draft.comparisonAnalyses = {};
    };
    const move = (from: Panel, to: Panel) => {
        if (!draft || from === to) return; compilation = null; invalidateAnalyses(); const source = panelEntries(from); const destination = panelEntries(to); const picks = selected(from); if (!picks.length) { announce(`Select cards in ${panelTitle(from)} first.`); return; }
        picks.forEach(index => { const [entry] = source.splice(index, 1); if (entry) destination.push(entry); }); render(); query<HTMLInputElement>(`[data-deck-modules-filter="${to}"]`)?.focus(); announce(`Moved ${picks.length} card entries from ${panelTitle(from)} to ${panelTitle(to)}.`);
    };
    const request = async (url: string, body: unknown) => { const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }); if (!response.ok) { const detail = await response.json().catch(() => ({})) as { message?: string }; throw new Error(detail.message ?? 'Deck Modules request failed.'); } return response; };
    const compile = async () => { if (!draft || !balanced()) return; compilation = null; renderBalance(); try { const response = await request('/deck-modules/compile', { baselineToken: draft.baselineToken, commandZone: draft.commandZone, baselineMainboardEntries: draft.baselineMainboardEntries, coreEntries: draft.coreEntries, alternatives: draft.alternatives, selectedAlternativeId: draft.selectedAlternativeId }); compilation = await response.json() as Record<string, unknown>; renderReport(compilation); renderBalance(); announce('Deck Modules compilation complete.'); } catch (exception) { const message = exception instanceof Error ? exception.message : 'Deck Modules compilation failed.'; error(message.includes('expired') ? `${message} Import again to continue.` : message); } };
    const renderList = (selector: string, entries: unknown) => { const list = query<HTMLElement>(selector); if (!list) return; list.replaceChildren(); (Array.isArray(entries) ? entries : []).forEach(entry => { const item = entry as Entry; addText(list, 'li', `${item.quantity} ${item.name}`); }); };
    const renderDiagnostic = (item: unknown) => { if (!item || typeof item !== 'object' || typeof (item as { rule?: unknown }).rule !== 'string') return JSON.stringify(item); const diagnostic = item as { rule: string; affectedIdentifiers?: unknown }; const identifiers = Array.isArray(diagnostic.affectedIdentifiers) && diagnostic.affectedIdentifiers.every(identifier => typeof identifier === 'string') ? diagnostic.affectedIdentifiers as string[] : []; const list = identifiers.join('; '); switch (diagnostic.rule) { case 'MissingSelection': return 'Select a strategy before compiling.'; case 'UnknownStrategy': return `Unknown strategy module: ${list}.`; case 'InvalidQuantity': return `Quantity must be greater than zero: ${list}.`; case 'EmptyCommandZone': return 'The imported command zone is empty.'; case 'MissingLinkedManaSupport': return `Missing linked mana-support module: ${list}.`; case 'StrategyCount': return `A project needs between two and four strategy modules; found ${identifiers.length}: ${list}.`; case 'UnequalStrategySize': return `Strategy modules hold unequal card counts: ${list}.`; case 'Overlap': return `Card assigned to more than one source: ${list}.`; case 'CommandZoneMutation': return `Configurable entries cannot occupy the command zone: ${list}.`; case 'TotalCardCount': return `Compiled deck has ${identifiers[0]} cards; a Commander deck needs exactly 100.`; case 'BannedCard': return `Banned in Commander: ${list}.`; case 'Singleton': return `More than one copy of a non-exempt card: ${list}.`; case 'ColorIdentity': return `Outside the command zone color identity: ${list}.`; case 'UnverifiableCardFacts': return `Legality could not be verified for: ${list}.`; default: return `${diagnostic.rule}: ${list}.`; } };
    const renderReport = (report: Record<string, unknown>) => { const set = (selector: string, value: unknown) => { const target = query<HTMLElement>(selector); if (target) target.textContent = String(value ?? ''); }; set('[data-deck-modules-report-total]', report.totalCardCount); set('[data-deck-modules-report-strategy]', report.selectedStrategyName); set('[data-deck-modules-report-mana]', report.selectedManaSupportModuleName); renderList('[data-deck-modules-compiled]', report.entries); const diagnostics = query<HTMLElement>('[data-deck-modules-diagnostics]'); if (diagnostics) { diagnostics.replaceChildren(); (Array.isArray(report.diagnostics) ? report.diagnostics : []).forEach(item => addText(diagnostics, 'li', renderDiagnostic(item))); } const swap = report.swapPlan as Record<string, unknown> | undefined; renderList('[data-deck-modules-swap="add"]', swap?.toAdd); renderList('[data-deck-modules-swap="remove"]', swap?.toRemove); renderList('[data-deck-modules-swap="reset"]', swap?.toReset); };
    const importDeck = async () => { const source = query<HTMLInputElement>('[data-deck-modules-source]')?.value.trim() ?? ''; if (!source) return; try { const response = await request('/deck-modules/import', source.startsWith('http') ? { activeSource: 'PublicUrl', url: source } : { activeSource: 'PasteText', pasteText: source }); const imported = await response.json() as Omit<Draft, 'version' | 'unassignedEntries' | 'coreEntries' | 'alternatives' | 'selectedAlternativeId'> & { importNotice?: string }; compilation = null; editingAlternative = false; draft = { ...imported, version, unassignedEntries: [...imported.baselineMainboardEntries], coreEntries: [], alternatives: [], selectedAlternativeId: '' }; const notice = query<HTMLElement>('[data-deck-modules-notice]'); if (notice) notice.textContent = imported.importNotice ?? ''; render(); announce('Baseline imported. Add two alternatives to begin assignment.'); } catch (exception) { error(exception instanceof Error ? exception.message : 'Deck import failed.'); } };
    const addAlternative = () => { if (!draft || draft.alternatives.length >= 4) return; const name = query<HTMLInputElement>('[data-deck-modules-name]')?.value.trim() ?? ''; const profile = query<HTMLSelectElement>('[data-deck-modules-profile]')?.value ?? ''; const playPlan = query<HTMLTextAreaElement>('[data-deck-modules-plan]')?.value.trim() ?? ''; if (!name || !profile || !playPlan) { error('Enter a baseline strategy name, profile, and one-sentence play plan before assigning cards.'); return; } compilation = null; invalidateAnalyses(); const alternative = { id: crypto.randomUUID?.() ?? `${Date.now()}-${draft.alternatives.length}`, name, profile, playPlan, mainboardEntries: [], manaSupportName: `${name} Mana Support`, manaSupportEntries: [] }; draft.alternatives.push(alternative); draft.selectedAlternativeId = alternative.id; editingAlternative = false; render(); const nameInput = query<HTMLInputElement>('[data-deck-modules-name]'); if (nameInput) { nameInput.value = ''; nameInput.focus(); } const profileInput = query<HTMLSelectElement>('[data-deck-modules-profile]'); if (profileInput) profileInput.value = ''; const planInput = query<HTMLTextAreaElement>('[data-deck-modules-plan]'); if (planInput) planInput.value = ''; };
    const normalizeLine = (value: unknown) => String(value ?? '').replace(/\r|\n/g, ' ').trim();
    const buildExportText = (report: unknown) => { const compilationReport = report as { commandZoneEntries?: Entry[]; mainboardEntries?: Entry[]; swapPlan?: { toAdd?: Entry[]; toRemove?: Entry[]; toReset?: Entry[] } }; let text = ''; const appendEntries = (heading: string, entries: Entry[] | undefined) => { text += `== ${heading} ==\n`; (entries ?? []).forEach(entry => { text += `${entry.quantity} ${normalizeLine(entry.name)}\n`; }); text += '\n'; }; const appendSwapEntries = (prefix: string, sign: string, entries: Entry[] | undefined) => { (entries ?? []).forEach(entry => { text += `${prefix} - ${sign}${entry.quantity} ${normalizeLine(entry.name)}\n`; }); }; appendEntries('Command Zone', compilationReport.commandZoneEntries); appendEntries('Mainboard', compilationReport.mainboardEntries); appendSwapEntries('IN', '+', compilationReport.swapPlan?.toAdd); appendSwapEntries('OUT', '-', compilationReport.swapPlan?.toRemove); appendSwapEntries('RESET', '-', compilationReport.swapPlan?.toReset); return text; };
    const copy = async () => { if (!compilation) return; try { await navigator.clipboard.writeText(buildExportText(compilation)); announce('Compilation copied.'); } catch { announce('Copy failed; select the compiled list and copy it manually.'); } };
    const renderAnalysis = () => { const analysis = draft?.analysis; const panel = query<HTMLElement>('[data-deck-modules-analysis]'); if (panel) panel.hidden = !analysis; if (!analysis) return; const set = (selector: string, value: unknown) => { const target = query<HTMLElement>(selector); if (target) target.textContent = String(value ?? ''); }; set('[data-deck-modules-analysis-health]', analysis.health); set('[data-deck-modules-analysis-lands]', analysis.landCount); set('[data-deck-modules-analysis-target]', analysis.targetLandCount); set('[data-deck-modules-analysis-land-delta]', analysis.landDelta); set('[data-deck-modules-analysis-ramp]', analysis.rampSourceCount); set('[data-deck-modules-analysis-hardtocast]', analysis.hardToCastCount); const coreOnly = query<HTMLElement>('[data-deck-modules-core-only]'); if (coreOnly) { coreOnly.textContent = String(analysis.analysisNotice ?? ''); coreOnly.hidden = !analysis.isCoreOnly; } const colors = query<HTMLTableSectionElement>('[data-deck-modules-analysis-colors]'); if (colors) { colors.replaceChildren(); analysis.attributedFindings.forEach(row => { const entry = document.createElement('tr'); addText(entry, 'th', row.displayColor); addText(entry, 'td', `${row.actualSources} / ${row.requiredSources}`); addText(entry, 'td', String(row.deficit)); const spell = addText(entry, 'td', row.drivingSpell); if (row.strength === 'NamedCard' && row.attributedCard && row.swapDirection) { const cause = addText(spell, 'span', `${row.swapDirection} ${row.attributedCard}`); cause.className = 'deck-modules__cause--named'; cause.dataset.deckModulesCause = 'named'; } else if (row.strength === 'ModuleMembership' && row.attributedModule) { const cause = addText(spell, 'span', `likely from ${row.attributedModule}`); cause.className = 'deck-modules__cause--inferred'; cause.dataset.deckModulesCause = 'inferred'; } colors.append(entry); }); } const signals = query<HTMLElement>('[data-deck-modules-signals]'); if (signals) { signals.hidden = !analysis.signals; if (analysis.signals) { set('[data-deck-modules-bracket]', `Bracket ${analysis.signals.bracketNumber}`); const gameChangers = query<HTMLUListElement>('[data-deck-modules-gamechangers]'); if (gameChangers) { gameChangers.replaceChildren(); analysis.signals.gameChangers.forEach(card => addText(gameChangers, 'li', card)); } set('[data-deck-modules-combo-availability]', analysis.signals.comboDetectionAvailable ? 'Two-card combo detection ran for this configuration.' : 'Two-card combo detection did not run for this configuration.'); const interactions = query<HTMLTableSectionElement>('[data-deck-modules-interactions]'); const interactionTable = interactions?.closest('table'); const unavailable = query<HTMLElement>('[data-deck-modules-interactions-unavailable]'); const available = analysis.signals.interactionAttributionAvailable; if (interactions) { interactions.replaceChildren(); if (available) analysis.signals.interactionsByModule.forEach(row => { const entry = document.createElement('tr'); addText(entry, 'th', row.moduleName); addText(entry, 'td', String(row.interactionCount)); interactions.append(entry); }); } if (interactionTable) interactionTable.hidden = !available; if (unavailable) unavailable.hidden = available; } } const disclosure = query<HTMLElement>('[data-deck-modules-disclosure]'); const declared = analysis.signals?.declared; if (disclosure) { disclosure.hidden = !declared; if (declared) { set('[data-deck-modules-declared-profile]', declared.profile); set('[data-deck-modules-declared-plan]', declared.playPlan); const profileNote = query<HTMLElement>('[data-deck-modules-profile-note]'); if (profileNote) { profileNote.textContent = declared.profileDisagreementNote ?? ''; profileNote.hidden = !declared.profileDisagreementNote; } } } const stale = query<HTMLElement>('[data-deck-modules-analysis-stale]'); if (stale) stale.hidden = !draft?.analysisStale; };
    const analyze = async () => { if (!draft) return; try { const response = await request('/deck-modules/analyze', { configuration: { baselineToken: draft.baselineToken, commandZone: draft.commandZone, baselineMainboardEntries: draft.baselineMainboardEntries, coreEntries: draft.coreEntries, alternatives: draft.alternatives, selectedAlternativeId: draft.selectedAlternativeId }, mode: 'Casual' }); const payload = await response.json() as { analysisKey: string; analysis: ConfigurationAnalysisSnapshot }; draft.analysis = payload.analysis; draft.analysisKey = payload.analysisKey; draft.analysisStale = false; draft.comparisonAnalyses ??= {}; draft.comparisonAnalyses[draft.selectedAlternativeId] = { analysis: payload.analysis, analysisKey: payload.analysisKey }; save(); renderAnalysis(); } catch (exception) { error(exception instanceof Error ? exception.message : 'Deck Modules analysis failed.'); } };
    const download = async () => { if (!draft || !compilation) return; try { const response = await request('/deck-modules/export', { baselineToken: draft.baselineToken, commandZone: draft.commandZone, baselineMainboardEntries: draft.baselineMainboardEntries, coreEntries: draft.coreEntries, alternatives: draft.alternatives, selectedAlternativeId: draft.selectedAlternativeId }); const blob = await response.blob(); const link = document.createElement('a'); link.href = URL.createObjectURL(blob); link.download = 'deck-modules.txt'; link.click(); URL.revokeObjectURL(link.href); } catch (exception) { error(exception instanceof Error ? exception.message : 'Deck Modules export failed.'); } };
    const validateComparisonSelection = () => {
        const { reference, other, button, message } = comparisonElements();
        if (!reference || !other) return;
        const same = !!reference.value && reference.value === other.value;
        if (button) button.disabled = !reference.value || !other.value || same;
        if (message) message.textContent = same ? 'Choose two different alternatives.' : '';
    };
    const renderComparisonPicker = () => {
        const { panel, reference, other } = comparisonElements();
        if (!panel || !reference || !other) return;
        const alternatives = draft?.alternatives ?? [];
        panel.hidden = alternatives.length < 2;
        const fill = (control: HTMLSelectElement, selectedValue: string) => {
            control.replaceChildren();
            alternatives.forEach(alternative => {
                const option = new Option(alternative.name, alternative.id);
                option.selected = alternative.id === selectedValue;
                control.add(option);
            });
        };
        const referenceValue = alternatives.some(item => item.id === reference.value) ? reference.value : alternatives[0]?.id ?? '';
        const otherValue = alternatives.some(item => item.id === other.value) ? other.value : alternatives.find(item => item.id !== referenceValue)?.id ?? referenceValue;
        fill(reference, referenceValue); fill(other, otherValue);
        validateComparisonSelection();
    };
    const appendComparisonRow = (body: HTMLTableSectionElement, label: string, values: unknown[], deltas: unknown[] = []) => {
        const row = document.createElement('tr');
        (addText(row, 'th', label) as HTMLTableCellElement).scope = 'row';
        values.forEach((value, index) => {
            const deltaText = deltas[index] === undefined || deltas[index] === null ? '' : ` (${comparisonDelta(deltas[index])})`;
            addText(row, 'td', `${comparisonText(value)}${deltaText}`);
        });
        body.append(row);
    };
    const appendComparisonRowsFrom = (body: HTMLTableSectionElement, rows: any[], label: (row: any) => string, value: (entry: any) => unknown, deltaOf: (entry: any) => unknown) =>
        rows.forEach((row: any) => appendComparisonRow(body, label(row), (row.values ?? []).map((entry: any) => entry.isPresent ? value(entry) : null), (row.values ?? []).map(deltaOf)));
    const renderComparisonDelta = (head: HTMLTableSectionElement, body: HTMLTableSectionElement, payload: Record<string, any>) => {
        const columns = [payload.reference, ...(payload.columns ?? [])];
        head.replaceChildren(); body.replaceChildren();
        const header = document.createElement('tr'); header.append(document.createElement('th'));
        columns.forEach((column: any) => { (addText(header, 'th', `${column.configurationName ?? 'Not analysed'}${column.isCoreOnly ? ' — Core-only analysis' : ''}`) as HTMLTableCellElement).scope = 'col'; });
        head.append(header);
        const metricRows: [string, string, string?][] = [
            ['Land count', 'landCount', 'landCountDelta'],
            ['Land target delta', 'landTargetDelta'],
            ['Ramp source count', 'rampSourceCount', 'rampSourceCountDelta'],
            ['Hard-to-cast count', 'hardToCastCount', 'hardToCastCountDelta'],
            ['Health', 'health'],
        ];
        metricRows.forEach(([label, field, deltaField]) => appendComparisonRow(body, label, columns.map((column: any) => column.isAnalyzed ? column[field] : null), deltaField ? columns.map((column: any) => column[deltaField]) : []));
        appendComparisonRowsFrom(body, payload.colorRows ?? [], (row: any) => `${row.displayColor} sources`, (entry: any) => `${entry.actualSources} / ${entry.requiredSources}`, (entry: any) => entry.actualSourcesDelta);
        appendComparisonRowsFrom(body, payload.interactionRows ?? [], (row: any) => `${row.moduleName} interactions`, (entry: any) => entry.interactionCount, (entry: any) => entry.delta);
    };
    const compare = async () => {
        const { reference, other, message, head, body } = comparisonElements();
        if (!draft || !reference || !other || !message || !head || !body) return;
        if (reference.value === other.value) { message.textContent = 'Choose two different alternatives.'; return; }
        // Why: D-13 -- an expired server-side cache falls back to the analysis snapshot each
        // alternative recorded the moment it was last analysed (see analyze()), so recovery
        // costs one extra round trip and never a fresh resolution pass.
        const build = (includeAnalysis: boolean): ComparisonSide[] => [reference.value, other.value].map(configurationId => {
            const stored = draft?.comparisonAnalyses?.[configurationId];
            return { configurationId, analysisKey: stored?.analysisKey ?? '', ...(includeAnalysis && stored ? { analysis: stored.analysis } : {}) };
        });
        const post = (sides: ComparisonSide[]) => fetch('/deck-modules/compare', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sides, referenceConfigurationId: reference.value }) });
        let response = await post(build(false));
        if (!response.ok) {
            const failure = await response.json().catch(() => ({})) as { message?: string; missingConfigurationIds?: string[] };
            if (response.status !== 409 || !failure.missingConfigurationIds) { message.textContent = failure.message ?? 'Configuration comparison failed.'; return; }
            const missing = failure.missingConfigurationIds.find(id => !draft?.comparisonAnalyses?.[id]);
            if (missing) { message.textContent = `Analyse ${draft.alternatives.find(item => item.id === missing)?.name ?? 'that alternative'} first.`; return; }
            response = await post(build(true));
            if (!response.ok) { message.textContent = 'Configuration comparison failed.'; return; }
        }
        renderComparisonDelta(head, body, await response.json() as Record<string, any>); message.textContent = '';
    };
    const initialize = () => { const page = root(); if (!page) return; draft = null; compilation = null; editingAlternative = false; restore(); render(); page.addEventListener('submit', event => { if ((event.target as Element).matches('[data-deck-modules-import-form]')) { event.preventDefault(); void importDeck(); } }); page.addEventListener('input', event => { const target = event.target as HTMLElement; const panel = target.getAttribute('data-deck-modules-filter') as Panel | null; if (panel) { renderRows(panel); return; } if (!editingAlternative || !active() || !target.matches('[data-deck-modules-name], [data-deck-modules-profile], [data-deck-modules-plan]')) return; const alternative = active()!; if (target.hasAttribute('data-deck-modules-name')) { alternative.name = (target as HTMLInputElement).value; alternative.manaSupportName = `${alternative.name} Mana Support`; } else if (target.hasAttribute('data-deck-modules-profile')) alternative.profile = (target as HTMLSelectElement).value; else alternative.playPlan = (target as HTMLTextAreaElement).value; compilation = null; invalidateAnalyses(); render(); }); page.addEventListener('change', event => { if ((event.target as Element).matches('[data-deck-modules-compare-reference], [data-deck-modules-compare-other]')) validateComparisonSelection(); }); const activate = (target: HTMLElement) => { const moveSpec = target.dataset.deckModulesMove; if (moveSpec) { const [from, to] = moveSpec.split(':') as [Panel, Panel]; move(from, to); } else if (target.hasAttribute('data-deck-modules-add-alternative')) addAlternative(); else if (target.dataset.deckModulesAlternative && draft) { draft.selectedAlternativeId = target.dataset.deckModulesAlternative; compilation = null; if (draft?.analysis) draft.analysisStale = true; editingAlternative = true; render(); } else if (target.hasAttribute('data-deck-modules-compile')) void compile(); else if (target.hasAttribute('data-deck-modules-export')) void download(); else if (target.hasAttribute('data-deck-modules-copy')) void copy(); else if (target.hasAttribute('data-deck-modules-compare')) void compare(); else if (target.hasAttribute('data-deck-modules-restart')) { draft = null; compilation = null; editingAlternative = false; clear(); render(); announce('Draft cleared.'); } }; page.addEventListener('click', event => { const target = (event.target as Element).closest<HTMLElement>('[data-deck-modules-move], [data-deck-modules-add-alternative], [data-deck-modules-alternative], [data-deck-modules-compile], [data-deck-modules-export], [data-deck-modules-copy], [data-deck-modules-compare], [data-deck-modules-restart]'); if (target) activate(target); }); page.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { const target = (event.target as Element).closest<HTMLElement>('[data-deck-modules-move]'); if (target) { event.preventDefault(); activate(target); } } }); };
    (globalThis as unknown as { DeckFlowDeckModules: { initialize: () => void; buildExportText: (report: unknown) => string } }).DeckFlowDeckModules = { initialize: initializeWithHandoff, buildExportText };
    document.addEventListener('DOMContentLoaded', initializeWithHandoff, { once: true });
    document.addEventListener('click', event => { if ((event.target as Element).closest('[data-deck-modules-analyze]')) void analyze(); });
})();
