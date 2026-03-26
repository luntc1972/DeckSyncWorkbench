"use strict";
const togglePanel = (selector, shouldHide) => {
    document.querySelectorAll(selector).forEach(element => {
        element.classList.toggle('hidden', shouldHide);
        element.style.display = shouldHide ? 'none' : '';
    });
};
const DeckInputSource = {
    PasteText: 'PasteText',
    PublicUrl: 'PublicUrl',
};
const panelConfigs = [
    {
        selectName: 'MoxfieldInputSource',
        urlSelector: '[data-sync-panel="moxfield-url"]',
        textSelector: '[data-sync-panel="moxfield-text"]',
    },
    {
        selectName: 'ArchidektInputSource',
        urlSelector: '[data-sync-panel="archidekt-url"]',
        textSelector: '[data-sync-panel="archidekt-text"]',
    },
];
const updateSyncInputModeUi = () => {
    panelConfigs.forEach(config => {
        const select = document.querySelector(`select[name="${config.selectName}"]`);
        if (!select) {
            return;
        }
        const selectedValue = select.value;
        const showUrl = selectedValue === DeckInputSource.PublicUrl;
        const showText = selectedValue === DeckInputSource.PasteText;
        togglePanel(config.urlSelector, !showUrl);
        togglePanel(config.textSelector, !showText);
    });
};
const updateSyncDirectionUi = () => {
    const directionSelect = document.querySelector('select[name="Direction"]');
    if (!directionSelect) {
        return;
    }
    const moxfieldIsSource = directionSelect.value === 'DeckSyncWorkbench';
    const moxfieldStatus = document.querySelector('[data-sync-role="moxfield-status"]');
    const archidektStatus = document.querySelector('[data-sync-role="archidekt-status"]');
    const moxfieldHint = document.querySelector('[data-sync-role="moxfield-hint"]');
    const archidektHint = document.querySelector('[data-sync-role="archidekt-hint"]');
    if (moxfieldStatus) {
        moxfieldStatus.textContent = moxfieldIsSource ? 'Source deck' : 'Target deck';
    }
    if (archidektStatus) {
        archidektStatus.textContent = moxfieldIsSource ? 'Target deck' : 'Source deck';
    }
    if (moxfieldHint) {
        moxfieldHint.textContent = `Use this when the Moxfield deck is ${moxfieldIsSource ? 'the source' : 'the target'}.`;
    }
    if (archidektHint) {
        archidektHint.textContent = `Use this when the Archidekt deck is ${moxfieldIsSource ? 'the target' : 'the source'}.`;
    }
};
let syncInputModeInitialized = false;
const initializeSyncInputModeUi = () => {
    if (syncInputModeInitialized) {
        return;
    }
    syncInputModeInitialized = true;
    const inputSelectors = document.querySelectorAll('select[name="MoxfieldInputSource"], select[name="ArchidektInputSource"]');
    inputSelectors.forEach(element => {
        element.addEventListener('change', updateSyncInputModeUi);
    });
    const directionSelect = document.querySelector('select[name="Direction"]');
    directionSelect === null || directionSelect === void 0 ? void 0 : directionSelect.addEventListener('change', updateSyncDirectionUi);
    updateSyncInputModeUi();
    updateSyncDirectionUi();
};
const scrollResults = () => {
    const anchor = document.getElementById('results-anchor');
    if (anchor) {
        anchor.scrollIntoView({ behavior: 'smooth' });
    }
};
const setAllPrintingChoices = (value) => {
    const selector = `input[type="radio"][name^="Resolutions["][value="${value}"]`;
    document.querySelectorAll(selector).forEach(input => {
        input.checked = true;
    });
};
let busyProgressTimer;
let busyHideTimer;
const formatProgressText = (steps, index) => `Step ${index + 1}/${steps.length}: ${steps[index]}`;
const clearBusyProgress = () => {
    if (busyProgressTimer !== undefined) {
        window.clearInterval(busyProgressTimer);
        busyProgressTimer = undefined;
    }
};
const hideBusyIndicator = () => {
    const container = document.getElementById('busy-indicator');
    const progressNode = document.getElementById('busy-indicator-progress');
    if (!container) {
        return;
    }
    container.classList.add('hidden');
    if (progressNode) {
        progressNode.textContent = '';
        delete progressNode.dataset.currentIndex;
    }
    clearBusyProgress();
    if (busyHideTimer !== undefined) {
        window.clearTimeout(busyHideTimer);
        busyHideTimer = undefined;
    }
};
const scheduleBusyHide = (durationMs) => {
    if (!durationMs || durationMs <= 0) {
        return;
    }
    if (busyHideTimer !== undefined) {
        window.clearTimeout(busyHideTimer);
    }
    busyHideTimer = window.setTimeout(() => {
        hideBusyIndicator();
    }, durationMs);
};
const showBusyIndicator = (title, message, progressSteps, durationMs, holdFinalStep = false) => {
    const container = document.getElementById('busy-indicator');
    const titleNode = document.getElementById('busy-indicator-title');
    const messageNode = document.getElementById('busy-indicator-message');
    const progressNode = document.getElementById('busy-indicator-progress');
    if (!container || !titleNode || !messageNode) {
        return;
    }
    titleNode.textContent = title || 'Working';
    messageNode.textContent = message || 'Request in progress.';
    container.classList.remove('hidden');
    clearBusyProgress();
    if (progressNode) {
        if (progressSteps && progressSteps.length > 0) {
            const finalIndex = progressSteps.length - 1;
            let currentIndex = 0;
            progressNode.textContent = formatProgressText(progressSteps, currentIndex);
            progressNode.dataset.currentIndex = currentIndex.toString();
            busyProgressTimer = window.setInterval(() => {
                currentIndex++;
                if (currentIndex > finalIndex) {
                    currentIndex = holdFinalStep ? finalIndex : 0;
                }
                progressNode.dataset.currentIndex = currentIndex.toString();
                progressNode.textContent = formatProgressText(progressSteps, currentIndex);
                if (holdFinalStep && currentIndex === finalIndex) {
                    clearBusyProgress();
                }
            }, 4000);
        }
        else {
            progressNode.textContent = '';
        }
    }
    if (durationMs && durationMs > 0) {
        scheduleBusyHide(durationMs);
    }
};
const registerBusyIndicator = () => {
    document.querySelectorAll('form[data-busy-title]').forEach(form => {
        form.addEventListener('submit', () => {
            const title = form.getAttribute('data-busy-title');
            const message = form.getAttribute('data-busy-message');
            const stepsAttr = form.getAttribute('data-busy-progress');
            const steps = stepsAttr
                ? stepsAttr
                    .split('|')
                    .map(step => step.trim())
                    .filter(step => step.length > 0)
                : [];
            const durationAttr = form.getAttribute('data-busy-duration');
            const duration = durationAttr ? parseInt(durationAttr, 10) : undefined;
            const holdFinalAttr = form.getAttribute('data-busy-hold-final-step');
            const holdFinalStep = holdFinalAttr !== null && holdFinalAttr.toLowerCase() === 'true';
            showBusyIndicator(title !== null && title !== void 0 ? title : undefined, message !== null && message !== void 0 ? message : undefined, steps.length > 0 ? steps : undefined, duration, holdFinalStep);
        });
    });
};
const formStateStoragePrefix = 'decksync-form-state-';
const tabNavigationKey = 'decksync-tab-navigation';
const storageAvailable = (() => {
    try {
        const testKey = '__decksync_test_key__';
        window.sessionStorage.setItem(testKey, '1');
        window.sessionStorage.removeItem(testKey);
        return window.sessionStorage;
    }
    catch (_a) {
        return null;
    }
})();
const serializeFormFields = (form) => {
    const state = {};
    form.querySelectorAll('[name]').forEach(element => {
        if (element.disabled || !element.name) {
            return;
        }
        if (element instanceof HTMLInputElement && (element.type === 'checkbox' || element.type === 'radio')) {
            if (!element.checked) {
                return;
            }
        }
        state[element.name] = element.value;
    });
    return state;
};
const restoreFormFields = (form, data) => {
    form.querySelectorAll('[name]').forEach(element => {
        const value = data[element.name];
        if (value === undefined) {
            return;
        }
        if (element instanceof HTMLInputElement && (element.type === 'checkbox' || element.type === 'radio')) {
            element.checked = element.value === value;
            return;
        }
        element.value = value;
    });
};
const persistFormState = (form) => {
    const key = form.getAttribute('data-cache-key');
    if (!key || !storageAvailable) {
        return;
    }
    const state = serializeFormFields(form);
    storageAvailable.setItem(`${formStateStoragePrefix}${key}`, JSON.stringify(state));
};
const hydrateFormState = (form) => {
    const key = form.getAttribute('data-cache-key');
    if (!key || !storageAvailable) {
        return;
    }
    const json = storageAvailable.getItem(`${formStateStoragePrefix}${key}`);
    if (!json) {
        return;
    }
    try {
        const state = JSON.parse(json);
        restoreFormFields(form, state);
    }
    catch (_a) {
        storageAvailable.removeItem(`${formStateStoragePrefix}${key}`);
    }
};
const clearDeckSyncUi = () => {
    const results = document.getElementById('deck-sync-results');
    const error = document.getElementById('deck-sync-error');
    if (results) {
        results.classList.add('hidden');
    }
    if (error) {
        error.classList.add('hidden');
        error.textContent = '';
    }
};
const setDeckSyncResultLabels = (sourceSystem, targetSystem) => {
    document.querySelectorAll('[data-sync-result="source-system"]').forEach(node => {
        node.textContent = sourceSystem;
    });
    document.querySelectorAll('[data-sync-result="target-system"]').forEach(node => {
        node.textContent = targetSystem;
    });
};
const buildConflictCellText = (system, conflict) => {
    var _a, _b;
    if (system === 'Archidekt') {
        const categorySuffix = conflict.archidektCategory ? ` [${conflict.archidektCategory}]` : '';
        return `(${conflict.archidektSetCode}) ${conflict.archidektCollectorNumber}${categorySuffix}`;
    }
    const setCode = (_a = conflict.moxfieldSetCode) !== null && _a !== void 0 ? _a : '';
    const collectorNumber = (_b = conflict.moxfieldCollectorNumber) !== null && _b !== void 0 ? _b : '';
    return `(${setCode}) ${collectorNumber}`.trim();
};
const renderDeckSyncConflicts = (printingConflicts, sourceSystem, targetSystem) => {
    const panel = document.getElementById('deck-sync-conflicts-js');
    const body = document.getElementById('deck-sync-conflicts-body');
    if (!panel || !body) {
        return;
    }
    body.replaceChildren();
    if (printingConflicts.length === 0) {
        panel.classList.add('hidden');
        return;
    }
    printingConflicts.forEach(conflict => {
        const row = document.createElement('tr');
        const cardCell = document.createElement('td');
        cardCell.textContent = conflict.cardName;
        const targetCell = document.createElement('td');
        targetCell.textContent = buildConflictCellText(targetSystem, conflict);
        const sourceCell = document.createElement('td');
        sourceCell.textContent = buildConflictCellText(sourceSystem, conflict);
        row.appendChild(cardCell);
        row.appendChild(targetCell);
        row.appendChild(sourceCell);
        body.appendChild(row);
    });
    panel.classList.remove('hidden');
};
const renderDeckSyncResponse = (response) => {
    const error = document.getElementById('deck-sync-error');
    const results = document.getElementById('deck-sync-results');
    const report = document.getElementById('deck-sync-report');
    const delta = document.getElementById('delta-output');
    const fullImport = document.getElementById('full-import-output');
    const instructions = document.getElementById('deck-sync-instructions');
    if (error) {
        error.classList.add('hidden');
        error.textContent = '';
    }
    if (report) {
        report.textContent = response.reportText;
    }
    if (delta) {
        delta.value = response.deltaText;
    }
    if (fullImport) {
        fullImport.value = response.fullImportText;
    }
    if (instructions) {
        instructions.textContent = response.instructionsText;
    }
    setDeckSyncResultLabels(response.sourceSystem, response.targetSystem);
    renderDeckSyncConflicts(response.printingConflicts, response.sourceSystem, response.targetSystem);
    results === null || results === void 0 ? void 0 : results.classList.remove('hidden');
    window.setTimeout(scrollResults, 100);
};
const submitDeckSyncApi = async (form) => {
    var _a, _b, _c, _d, _e, _f;
    const endpoint = form.dataset.deckSyncApi;
    if (!endpoint) {
        return;
    }
    const error = document.getElementById('deck-sync-error');
    try {
        const response = await fetch(endpoint, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(serializeFormFields(form))
        });
        if (!response.ok) {
            let payload = null;
            try {
                payload = await response.json();
            }
            catch (_g) {
                payload = null;
            }
            if (error) {
                const validationSummary = (payload === null || payload === void 0 ? void 0 : payload.errors)
                    ? Object.values(payload.errors)
                        .reduce((messages, current) => messages.concat(current), [])
                        .join(' ')
                    : null;
                error.textContent = (_d = (_c = (_b = (_a = payload === null || payload === void 0 ? void 0 : payload.message) !== null && _a !== void 0 ? _a : payload === null || payload === void 0 ? void 0 : payload.Message) !== null && _b !== void 0 ? _b : validationSummary) !== null && _c !== void 0 ? _c : payload === null || payload === void 0 ? void 0 : payload.title) !== null && _d !== void 0 ? _d : 'Unable to run deck sync.';
                error.classList.remove('hidden');
            }
            (_e = document.getElementById('deck-sync-results')) === null || _e === void 0 ? void 0 : _e.classList.add('hidden');
            hideBusyIndicator();
            return;
        }
        renderDeckSyncResponse(await response.json());
        hideBusyIndicator();
    }
    catch (requestError) {
        if (error) {
            error.textContent = requestError instanceof Error ? requestError.message : 'Unable to run deck sync.';
            error.classList.remove('hidden');
        }
        (_f = document.getElementById('deck-sync-results')) === null || _f === void 0 ? void 0 : _f.classList.add('hidden');
        hideBusyIndicator();
    }
};
const attachDeckSyncPersistence = () => {
    const form = document.getElementById('deck-sync-form');
    if (!form) {
        return;
    }
    const key = form.getAttribute('data-cache-key');
    if (!key || !storageAvailable) {
        updateSyncInputModeUi();
        updateSyncDirectionUi();
        return;
    }
    const restoredFromTabs = storageAvailable.getItem(tabNavigationKey) === '1';
    if (restoredFromTabs) {
        hydrateFormState(form);
        storageAvailable.removeItem(tabNavigationKey);
    }
    else {
        storageAvailable.removeItem(`${formStateStoragePrefix}${key}`);
    }
    updateSyncInputModeUi();
    updateSyncDirectionUi();
    const handler = () => persistFormState(form);
    form.addEventListener('input', handler);
    form.addEventListener('change', handler);
    form.addEventListener('submit', event => {
        handler();
        event.preventDefault();
        submitDeckSyncApi(form);
    });
    const clearButton = form.querySelector('[data-clear-cache]');
    clearButton === null || clearButton === void 0 ? void 0 : clearButton.addEventListener('click', () => {
        form.reset();
        storageAvailable.removeItem(`${formStateStoragePrefix}${key}`);
        clearDeckSyncUi();
        updateSyncInputModeUi();
        updateSyncDirectionUi();
    });
    document.querySelectorAll('.tab-bar .tab-link').forEach(link => {
        link.addEventListener('click', () => {
            persistFormState(form);
            storageAvailable.setItem(tabNavigationKey, '1');
        });
    });
};
window.setAllPrintingChoices = setAllPrintingChoices;
window.hideBusyIndicator = hideBusyIndicator;
let deckSyncBootstrapped = false;
const bootstrapDeckSync = () => {
    if (deckSyncBootstrapped) {
        return;
    }
    deckSyncBootstrapped = true;
    initializeSyncInputModeUi();
    registerBusyIndicator();
    attachDeckSyncPersistence();
};
document.addEventListener('DOMContentLoaded', bootstrapDeckSync);
if (document.readyState !== 'loading') {
    bootstrapDeckSync();
}
