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
const deckFlowWindow = window;
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
    {
        selectName: 'DeckInputSource',
        urlSelector: '[data-sync-panel="chatgpt-deck-url"]',
        textSelector: '[data-sync-panel="chatgpt-deck-text"]',
    },
];
const moxfieldUrlPattern = /^https?:\/\/(?:www\.)?moxfield\.com\/decks\/[^/?#\s]+\/?$/i;
let extensionRequestCounter = 0;
const isSingleMoxfieldDeckUrl = (value) => moxfieldUrlPattern.test(value.trim());
const createExtensionRequestId = () => {
    extensionRequestCounter += 1;
    return `deckflow-extension-${extensionRequestCounter}`;
};
const getExtensionInstallUrl = () => { var _a; return (_a = document.body.dataset.deckflowExtensionInstallUrl) !== null && _a !== void 0 ? _a : '/extension-install.html'; };
const isMobileBrowser = () => {
    const userAgentData = navigator.userAgentData;
    if (typeof (userAgentData === null || userAgentData === void 0 ? void 0 : userAgentData.mobile) === 'boolean') {
        return userAgentData.mobile;
    }
    return /Android|iPhone|iPad|iPod|Mobile/i.test(navigator.userAgent);
};
const postExtensionBridgeRequest = async (type, payload, timeoutMs = 2500) => {
    const requestId = createExtensionRequestId();
    return await new Promise((resolve, reject) => {
        const timeoutId = window.setTimeout(() => {
            window.removeEventListener('message', handleMessage);
            reject(new Error('Timed out waiting for the DeckFlow browser extension.'));
        }, timeoutMs);
        const handleMessage = (event) => {
            if (event.source !== window) {
                return;
            }
            const message = event.data;
            if (!message || message.source !== 'deckflow-extension' || message.requestId !== requestId) {
                return;
            }
            window.clearTimeout(timeoutId);
            window.removeEventListener('message', handleMessage);
            resolve(message);
        };
        window.addEventListener('message', handleMessage);
        window.postMessage(Object.assign({ source: 'deckflow-web', type, requestId }, payload), window.location.origin);
    });
};
const getDeckFlowExtensionStatus = async () => {
    try {
        const response = await postExtensionBridgeRequest('deckflow-extension-ping', {}, 1200);
        if (response.type !== 'deckflow-extension-ping-response') {
            return { installed: true, allowed: false };
        }
        return {
            installed: true,
            allowed: response.allowed !== false,
            optionsUrl: response.optionsUrl
        };
    }
    catch (_a) {
        return { installed: false, allowed: false };
    }
};
const importMoxfieldDeckTextViaExtension = async (url) => {
    const response = await postExtensionBridgeRequest('deckflow-moxfield-import', { deckUrl: url }, 6000);
    if (response.type !== 'deckflow-moxfield-import-response') {
        throw new Error('The browser extension returned an unexpected response.');
    }
    if (!response.ok) {
        throw new Error(response.error || 'The browser extension could not import this Moxfield deck.');
    }
    return response.deckText;
};
const promptToConfigureMoxfieldExtensionOrigin = (optionsUrl) => {
    const shouldOpenOptions = window.confirm(`The DeckFlow extension is installed, but ${window.location.origin} is not allowed yet. Open the extension options to allow this origin?`);
    if (shouldOpenOptions && optionsUrl) {
        window.open(optionsUrl, '_blank', 'noopener');
    }
    return shouldOpenOptions;
};
const resubmitFormBypassingExtension = (form, submitter) => {
    form.dataset.extensionBridgeBypass = 'true';
    if (submitter instanceof HTMLButtonElement || submitter instanceof HTMLInputElement) {
        form.requestSubmit(submitter);
        return;
    }
    form.requestSubmit();
};
const createSelectBackedImportTask = (urlInput, textInput, sourceSelect) => {
    if (sourceSelect.value !== DeckInputSource.PublicUrl || !isSingleMoxfieldDeckUrl(urlInput.value)) {
        return null;
    }
    return {
        url: urlInput.value.trim(),
        applyImportedText: (deckText) => {
            textInput.value = deckText;
            urlInput.value = '';
            sourceSelect.value = DeckInputSource.PasteText;
            sourceSelect.dispatchEvent(new Event('change', { bubbles: true }));
        }
    };
};
const createTextareaImportTask = (sourceInput) => {
    if (!isSingleMoxfieldDeckUrl(sourceInput.value)) {
        return null;
    }
    return {
        url: sourceInput.value.trim(),
        applyImportedText: (deckText) => {
            sourceInput.value = deckText;
        }
    };
};
const collectMoxfieldImportTasks = (form) => {
    var _a, _b, _c;
    const cacheKey = form.dataset.cacheKey;
    if (!cacheKey) {
        return [];
    }
    if (cacheKey === 'deck-sync') {
        const tasks = [];
        const direction = (_b = (_a = form.querySelector('select[name="Direction"]')) === null || _a === void 0 ? void 0 : _a.value) !== null && _b !== void 0 ? _b : 'MoxfieldToArchidekt';
        const leftUsesMoxfield = direction !== 'ArchidektToArchidekt';
        const rightUsesMoxfield = direction === 'MoxfieldToMoxfield';
        if (leftUsesMoxfield) {
            const leftTask = createSelectBackedImportTask(form.querySelector('input[name="MoxfieldUrl"]'), form.querySelector('textarea[name="MoxfieldText"]'), form.querySelector('select[name="MoxfieldInputSource"]'));
            if (leftTask) {
                tasks.push(leftTask);
            }
        }
        if (rightUsesMoxfield) {
            const rightTask = createSelectBackedImportTask(form.querySelector('input[name="ArchidektUrl"]'), form.querySelector('textarea[name="ArchidektText"]'), form.querySelector('select[name="ArchidektInputSource"]'));
            if (rightTask) {
                tasks.push(rightTask);
            }
        }
        return tasks;
    }
    if (cacheKey === 'deck-convert') {
        const sourceFormat = (_c = form.querySelector('select[name="SourceFormat"]')) === null || _c === void 0 ? void 0 : _c.value;
        if (sourceFormat !== 'Moxfield') {
            return [];
        }
        const task = createSelectBackedImportTask(form.querySelector('input[name="DeckUrl"]'), form.querySelector('textarea[name="DeckText"]'), form.querySelector('select[name="InputSource"]'));
        return task ? [task] : [];
    }
    if (cacheKey === 'chatgpt-packets') {
        const task = createSelectBackedImportTask(form.querySelector('input[name="DeckUrl"]'), form.querySelector('textarea[name="DeckText"]'), form.querySelector('select[name="DeckInputSource"]'));
        return task ? [task] : [];
    }
    if (cacheKey === 'chatgpt-deck-comparison') {
        return [
            createTextareaImportTask(form.querySelector('textarea[name="DeckASource"]')),
            createTextareaImportTask(form.querySelector('textarea[name="DeckBSource"]'))
        ].filter((task) => task !== null);
    }
    if (cacheKey === 'chatgpt-cedh-meta-gap') {
        const task = createTextareaImportTask(form.querySelector('textarea[name="DeckSource"]'));
        return task ? [task] : [];
    }
    return [];
};
const attachMoxfieldExtensionImport = () => {
    document.addEventListener('submit', async (event) => {
        var _a;
        const form = event.target;
        if (!(form instanceof HTMLFormElement)) {
            return;
        }
        if (form.dataset.extensionBridgeBypass === 'true') {
            delete form.dataset.extensionBridgeBypass;
            return;
        }
        const tasks = collectMoxfieldImportTasks(form);
        if (tasks.length === 0) {
            return;
        }
        event.preventDefault();
        const submitter = event.submitter;
        if (isMobileBrowser()) {
            window.alert('Moxfield URLs require the desktop DeckFlow Bridge extension, which is not available on mobile browsers. '
                + 'Open your deck in Moxfield, tap Bulk Edit, copy the Main Deck contents, and paste them into the text field here. '
                + 'Tags are preserved.');
            return;
        }
        const extensionStatus = await getDeckFlowExtensionStatus();
        if (!extensionStatus.installed) {
            window.alert('Moxfield URLs require the DeckFlow Bridge extension. '
                + 'Opening the install page now — come back and retry after installing. '
                + 'If you cannot install the extension, switch this field to Paste text and use Moxfield Bulk Edit instead.');
            window.open(getExtensionInstallUrl(), '_blank', 'noopener');
            return;
        }
        if (!extensionStatus.allowed) {
            window.alert(`The DeckFlow Bridge extension is installed but ${window.location.origin} is not on its allow list. `
                + 'Opening extension Options now — add this origin, then retry.');
            if (extensionStatus.optionsUrl) {
                window.open(extensionStatus.optionsUrl, '_blank', 'noopener');
            }
            return;
        }
        try {
            for (const task of tasks) {
                const deckText = await importMoxfieldDeckTextViaExtension(task.url);
                task.applyImportedText(deckText);
            }
        }
        catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            const optionsUrl = error && typeof error === 'object' && 'optionsUrl' in error
                ? String((_a = error.optionsUrl) !== null && _a !== void 0 ? _a : '')
                : '';
            if (optionsUrl && /not allowed/i.test(message)) {
                promptToConfigureMoxfieldExtensionOrigin(optionsUrl);
            }
            else {
                window.alert(`DeckFlow could not import this Moxfield URL through the browser extension:\n\n${message}\n\nRetry, or switch to Paste text and use Moxfield Bulk Edit.`);
            }
            return;
        }
        resubmitFormBypassingExtension(form, submitter);
    }, true);
};
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
    const direction = directionSelect.value;
    const leftSystem = direction === 'ArchidektToArchidekt' ? 'Archidekt' : 'Moxfield';
    const rightSystem = direction === 'MoxfieldToMoxfield' ? 'Moxfield' : 'Archidekt';
    const leftIsSource = direction === 'MoxfieldToArchidekt' || direction === 'MoxfieldToMoxfield';
    const moxfieldStatus = document.querySelector('[data-sync-role="moxfield-status"]');
    const archidektStatus = document.querySelector('[data-sync-role="archidekt-status"]');
    const moxfieldTitle = document.querySelector('[data-sync-role="moxfield-title"]');
    const archidektTitle = document.querySelector('[data-sync-role="archidekt-title"]');
    const moxfieldDescription = document.querySelector('[data-sync-role="moxfield-description"]');
    const archidektDescription = document.querySelector('[data-sync-role="archidekt-description"]');
    const moxfieldUrlLabel = document.querySelector('[data-sync-role="moxfield-url-label"]');
    const archidektUrlLabel = document.querySelector('[data-sync-role="archidekt-url-label"]');
    const moxfieldTextLabel = document.querySelector('[data-sync-role="moxfield-text-label"]');
    const archidektTextLabel = document.querySelector('[data-sync-role="archidekt-text-label"]');
    const moxfieldHint = document.querySelector('[data-sync-role="moxfield-hint"]');
    const archidektHint = document.querySelector('[data-sync-role="archidekt-hint"]');
    const targetCategoryOption = document.querySelector('[data-sync-role="category-mode-target"]');
    const sourceCategoryOption = document.querySelector('[data-sync-role="category-mode-source"]');
    const moxfieldUrlInput = document.querySelector('input[name="MoxfieldUrl"]');
    const archidektUrlInput = document.querySelector('input[name="ArchidektUrl"]');
    const sourceLabelKind = leftIsSource
        ? (leftSystem === 'Archidekt' ? 'categories' : 'tags')
        : (rightSystem === 'Archidekt' ? 'categories' : 'tags');
    const targetLabelKind = leftIsSource
        ? (rightSystem === 'Archidekt' ? 'categories' : 'tags')
        : (leftSystem === 'Archidekt' ? 'categories' : 'tags');
    if (moxfieldStatus) {
        moxfieldStatus.textContent = leftIsSource ? 'Source deck' : 'Target deck';
    }
    if (archidektStatus) {
        archidektStatus.textContent = leftIsSource ? 'Target deck' : 'Source deck';
    }
    if (moxfieldTitle) {
        moxfieldTitle.textContent = leftSystem;
    }
    if (archidektTitle) {
        archidektTitle.textContent = rightSystem;
    }
    if (moxfieldDescription) {
        moxfieldDescription.textContent = `Provide the ${leftSystem} export or public URL for this deck.`;
    }
    if (archidektDescription) {
        archidektDescription.textContent = `Provide the ${rightSystem} export or public URL for this deck.`;
    }
    if (moxfieldUrlLabel) {
        moxfieldUrlLabel.textContent = `${leftSystem} public deck URL`;
    }
    if (archidektUrlLabel) {
        archidektUrlLabel.textContent = `${rightSystem} public deck URL`;
    }
    if (moxfieldTextLabel) {
        moxfieldTextLabel.textContent = `${leftSystem} export text`;
    }
    if (archidektTextLabel) {
        archidektTextLabel.textContent = `${rightSystem} export text`;
    }
    if (moxfieldHint) {
        moxfieldHint.textContent = `Use this when the ${leftSystem} deck is ${leftIsSource ? 'the source' : 'the target'}.`;
    }
    if (archidektHint) {
        archidektHint.textContent = `Use this when the ${rightSystem} deck is ${leftIsSource ? 'the target' : 'the source'}.`;
    }
    if (targetCategoryOption) {
        targetCategoryOption.textContent = `Use target ${targetLabelKind}`;
    }
    if (sourceCategoryOption) {
        sourceCategoryOption.textContent = `Use source ${sourceLabelKind}`;
    }
    if (moxfieldUrlInput) {
        moxfieldUrlInput.placeholder = leftSystem === 'Archidekt'
            ? 'https://archidekt.com/decks/...'
            : 'https://moxfield.com/decks/...';
    }
    if (archidektUrlInput) {
        archidektUrlInput.placeholder = rightSystem === 'Moxfield'
            ? 'https://moxfield.com/decks/...'
            : 'https://archidekt.com/decks/...';
    }
};
let syncInputModeInitialized = false;
const initializeSyncInputModeUi = () => {
    if (syncInputModeInitialized) {
        return;
    }
    syncInputModeInitialized = true;
    const inputSelectors = document.querySelectorAll('select[name="MoxfieldInputSource"], select[name="ArchidektInputSource"], select[name="DeckInputSource"]');
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
const copyElementValue = async (targetId) => {
    var _a;
    const target = document.getElementById(targetId);
    if (!target) {
        return;
    }
    const text = target instanceof HTMLTextAreaElement || target instanceof HTMLInputElement
        ? target.value
        : (_a = target.textContent) !== null && _a !== void 0 ? _a : '';
    if (!text) {
        return;
    }
    await navigator.clipboard.writeText(text);
};
const announceToScreenReader = (message) => {
    const announcer = document.querySelector('[data-copy-announcer]');
    if (!announcer)
        return;
    // Clearing then setting re-triggers the announcement for repeat copies.
    announcer.textContent = '';
    window.setTimeout(() => { announcer.textContent = message; }, 50);
};
const setTemporaryButtonText = (button, text, durationMs = 1800) => {
    var _a, _b, _c;
    const originalText = (_c = (_a = button.dataset.copyOriginalText) !== null && _a !== void 0 ? _a : (_b = button.textContent) === null || _b === void 0 ? void 0 : _b.trim()) !== null && _c !== void 0 ? _c : 'Copy';
    button.dataset.copyOriginalText = originalText;
    button.textContent = text;
    const state = text === 'Copied' ? 'is-copied' : text === 'Copy failed' ? 'is-copy-failed' : null;
    if (state) {
        button.classList.add(state);
        announceToScreenReader(text);
    }
    window.setTimeout(() => {
        button.textContent = originalText;
        button.classList.remove('is-copied', 'is-copy-failed');
    }, durationMs);
};
const attachActionButtons = () => {
    document.querySelectorAll('[data-copy-target]').forEach(button => {
        button.addEventListener('click', async () => {
            const targetId = button.dataset.copyTarget;
            if (!targetId) {
                return;
            }
            try {
                await copyElementValue(targetId);
                setTemporaryButtonText(button, 'Copied');
            }
            catch (_a) {
                setTemporaryButtonText(button, 'Copy failed');
            }
        });
    });
    document.querySelectorAll('[data-select-all-choice]').forEach(button => {
        button.addEventListener('click', () => {
            const choice = button.dataset.selectAllChoice;
            if (!choice) {
                return;
            }
            setAllPrintingChoices(choice);
        });
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
const antiForgeryFieldName = '__RequestVerificationToken';
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
const serializePersistedFormFields = (form) => {
    const state = {};
    const formData = new FormData(form);
    formData.forEach((value, key) => {
        if (typeof value !== 'string') {
            return;
        }
        if (key === antiForgeryFieldName) {
            return;
        }
        if (!state[key]) {
            state[key] = [];
        }
        state[key].push(value);
    });
    return state;
};
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
const cardPickerFieldName = 'CardSpecificQuestionCardNames';
const getCardPickerRowsContainer = (container) => container.querySelector('[data-card-picker-rows]');
const getCardPickerRows = (container) => Array.from(container.querySelectorAll('[data-card-picker-row]'));
const cardPickerSvgNamespace = 'http://www.w3.org/2000/svg';
const createCardPickerIcon = (lineCoordinates) => {
    const icon = document.createElementNS(cardPickerSvgNamespace, 'svg');
    icon.setAttribute('width', '16');
    icon.setAttribute('height', '16');
    icon.setAttribute('viewBox', '0 0 24 24');
    icon.setAttribute('fill', 'none');
    icon.setAttribute('stroke', 'currentColor');
    icon.setAttribute('stroke-width', '2');
    icon.setAttribute('stroke-linecap', 'round');
    icon.setAttribute('role', 'img');
    icon.setAttribute('aria-hidden', 'true');
    lineCoordinates.forEach(([x1, y1, x2, y2]) => {
        const line = document.createElementNS(cardPickerSvgNamespace, 'line');
        line.setAttribute('x1', x1);
        line.setAttribute('y1', y1);
        line.setAttribute('x2', x2);
        line.setAttribute('y2', y2);
        icon.appendChild(line);
    });
    return icon;
};
const syncCardPickerRemoveButtons = (container) => {
    const rowsContainer = getCardPickerRowsContainer(container);
    if (!rowsContainer) {
        return;
    }
    Array.from(rowsContainer.children).forEach((child, index) => {
        if (!(child instanceof HTMLElement) || !child.hasAttribute('data-card-picker-row')) {
            return;
        }
        const removeButton = child.querySelector('[data-card-picker-remove]');
        if (!removeButton) {
            return;
        }
        if (index === 0) {
            removeButton.hidden = true;
            removeButton.classList.add('hidden');
            return;
        }
        removeButton.hidden = false;
        removeButton.classList.remove('hidden');
    });
};
const createCardPickerRow = (value = '') => {
    const row = document.createElement('div');
    row.className = 'card-picker__row';
    row.setAttribute('data-card-picker-row', '');
    const inputShell = document.createElement('div');
    inputShell.className = 'autocomplete-anchor card-picker__input-shell';
    const input = document.createElement('input');
    input.type = 'text';
    input.name = cardPickerFieldName;
    input.value = value;
    input.className = 'card-picker__input';
    input.autocomplete = 'off';
    input.setAttribute('data-card-picker-input', '');
    inputShell.appendChild(input);
    const addButton = document.createElement('button');
    addButton.type = 'button';
    addButton.className = 'card-picker__add';
    addButton.setAttribute('data-card-picker-add', '');
    addButton.setAttribute('aria-label', 'Add another card');
    addButton.appendChild(createCardPickerIcon([
        ['12', '5', '12', '19'],
        ['5', '12', '19', '12']
    ]));
    const removeButton = document.createElement('button');
    removeButton.type = 'button';
    removeButton.className = 'card-picker__remove hidden';
    removeButton.setAttribute('data-card-picker-remove', '');
    removeButton.setAttribute('aria-label', 'Remove this card');
    removeButton.hidden = true;
    removeButton.appendChild(createCardPickerIcon([['5', '12', '19', '12']]));
    row.append(inputShell, addButton, removeButton);
    return row;
};
const attachCardPickerRow = (container, row) => {
    var _a, _b, _c, _d, _e;
    const rowsContainer = getCardPickerRowsContainer(container);
    const form = container.closest('form');
    const addButton = row.querySelector('[data-card-picker-add]');
    const input = row.querySelector('[data-card-picker-input]');
    const inputShell = row.querySelector('.card-picker__input-shell');
    const removeButton = row.querySelector('[data-card-picker-remove]');
    if (rowsContainer) {
        const isFirstRow = row === rowsContainer.firstElementChild;
        if (isFirstRow) {
            removeButton === null || removeButton === void 0 ? void 0 : removeButton.classList.add('hidden');
            if (removeButton) {
                removeButton.hidden = true;
            }
        }
        else {
            removeButton === null || removeButton === void 0 ? void 0 : removeButton.classList.remove('hidden');
            if (removeButton) {
                removeButton.hidden = false;
            }
        }
    }
    if (row.dataset.cardPickerAttached === 'true') {
        return;
    }
    row.dataset.cardPickerAttached = 'true';
    if (input && inputShell instanceof HTMLElement) {
        let suggestionPanel = inputShell.querySelector('.autocomplete-panel');
        if (!suggestionPanel) {
            suggestionPanel = (_c = (_b = (_a = deckFlowWindow.DeckFlow) === null || _a === void 0 ? void 0 : _a.createTypeaheadPanel) === null || _b === void 0 ? void 0 : _b.call(_a, inputShell)) !== null && _c !== void 0 ? _c : null;
        }
        if (suggestionPanel) {
            (_e = (_d = deckFlowWindow.DeckFlow) === null || _d === void 0 ? void 0 : _d.attachTypeahead) === null || _e === void 0 ? void 0 : _e.call(_d, input, suggestionPanel, 2, pickedName => {
                input.value = pickedName;
                input.dispatchEvent(new Event('change', { bubbles: true }));
            });
        }
    }
    addButton === null || addButton === void 0 ? void 0 : addButton.addEventListener('click', () => {
        var _a;
        const currentRowsContainer = getCardPickerRowsContainer(container);
        if (!currentRowsContainer) {
            return;
        }
        const newRow = createCardPickerRow();
        currentRowsContainer.appendChild(newRow);
        attachCardPickerRow(container, newRow);
        syncCardPickerRemoveButtons(container);
        form && persistFormState(form);
        (_a = newRow.querySelector('[data-card-picker-input]')) === null || _a === void 0 ? void 0 : _a.focus();
    });
    removeButton === null || removeButton === void 0 ? void 0 : removeButton.addEventListener('click', () => {
        const currentRowsContainer = getCardPickerRowsContainer(container);
        if (!currentRowsContainer || row === currentRowsContainer.firstElementChild) {
            syncCardPickerRemoveButtons(container);
            return;
        }
        row.remove();
        if (currentRowsContainer.querySelectorAll('[data-card-picker-row]').length === 0) {
            const replacementRow = createCardPickerRow();
            currentRowsContainer.appendChild(replacementRow);
            attachCardPickerRow(container, replacementRow);
        }
        syncCardPickerRemoveButtons(container);
        form && persistFormState(form);
    });
};
const attachCardPicker = (form) => {
    form.querySelectorAll('[data-card-picker]').forEach(container => {
        const rowsContainer = getCardPickerRowsContainer(container);
        if (!rowsContainer) {
            return;
        }
        if (rowsContainer.querySelectorAll('[data-card-picker-row]').length === 0) {
            rowsContainer.appendChild(createCardPickerRow());
        }
        getCardPickerRows(container).forEach(row => attachCardPickerRow(container, row));
        syncCardPickerRemoveButtons(container);
    });
};
const restoreCardPickerFields = (form, data) => {
    const container = form.querySelector('[data-card-picker]');
    if (!container) {
        return;
    }
    const rowsContainer = getCardPickerRowsContainer(container);
    if (!rowsContainer) {
        return;
    }
    const values = data[cardPickerFieldName];
    if (!values || values.length === 0) {
        return;
    }
    rowsContainer.replaceChildren();
    values.forEach(value => {
        rowsContainer.appendChild(createCardPickerRow(value));
    });
};
const restoreFormFields = (form, data) => {
    restoreCardPickerFields(form, data);
    form.querySelectorAll('[name]').forEach(element => {
        if (element.name === antiForgeryFieldName) {
            return;
        }
        if (element.name === cardPickerFieldName) {
            return;
        }
        const values = data[element.name];
        if (!values || values.length === 0) {
            return;
        }
        if (element instanceof HTMLInputElement) {
            if (element.type === 'checkbox' || element.type === 'radio') {
                element.checked = values.includes(element.value);
                return;
            }
            element.value = values[0];
            return;
        }
        if (element instanceof HTMLSelectElement && element.multiple) {
            Array.from(element.options).forEach(option => {
                option.selected = values.includes(option.value);
            });
            return;
        }
        element.value = values[0];
    });
};
const persistFormState = (form) => {
    const key = form.getAttribute('data-cache-key');
    if (!key || !storageAvailable) {
        return;
    }
    const state = serializePersistedFormFields(form);
    storageAvailable.setItem(`${formStateStoragePrefix}${key}`, JSON.stringify(state));
    storageAvailable.setItem(`${formStateStoragePrefix}${key}:savedAt`, Date.now().toString());
};
const clearPersistedFormState = (form) => {
    var _a;
    const key = form.getAttribute('data-cache-key');
    if (!key || !storageAvailable) {
        return;
    }
    storageAvailable.removeItem(`${formStateStoragePrefix}${key}`);
    storageAvailable.removeItem(`${formStateStoragePrefix}${key}:savedAt`);
    (_a = form.querySelector('[data-cache-pill]')) === null || _a === void 0 ? void 0 : _a.remove();
};
const formatCacheAge = (savedAtMs) => {
    const elapsedMs = Date.now() - savedAtMs;
    if (elapsedMs < 60000)
        return 'just now';
    const minutes = Math.floor(elapsedMs / 60000);
    if (minutes < 60)
        return `${minutes} min ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24)
        return `${hours} hr ago`;
    return `${Math.floor(hours / 24)} day ago`;
};
const showCachePill = (form, savedAtMs) => {
    if (form.querySelector('[data-cache-pill]'))
        return;
    const pill = document.createElement('div');
    pill.className = 'cache-pill';
    pill.setAttribute('data-cache-pill', '');
    pill.setAttribute('role', 'status');
    const label = document.createElement('span');
    label.textContent = `Restored from cache · ${formatCacheAge(savedAtMs)}`;
    const resetButton = document.createElement('button');
    resetButton.type = 'button';
    resetButton.className = 'cache-pill__reset';
    resetButton.textContent = 'Reset';
    resetButton.addEventListener('click', () => {
        clearPersistedFormState(form);
        form.reset();
    });
    pill.appendChild(label);
    pill.appendChild(resetButton);
    form.insertBefore(pill, form.firstChild);
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
        const savedAtRaw = storageAvailable.getItem(`${formStateStoragePrefix}${key}:savedAt`);
        const savedAtMs = savedAtRaw ? parseInt(savedAtRaw, 10) : NaN;
        if (Number.isFinite(savedAtMs)) {
            showCachePill(form, savedAtMs);
        }
    }
    catch (_a) {
        storageAvailable.removeItem(`${formStateStoragePrefix}${key}`);
        storageAvailable.removeItem(`${formStateStoragePrefix}${key}:savedAt`);
    }
};
const attachGenericPersistedForms = () => {
    if (!storageAvailable) {
        return;
    }
    const forms = Array.from(document.querySelectorAll('form[data-cache-key]'));
    forms.forEach(form => {
        if (form.id === 'deck-sync-form') {
            return;
        }
        hydrateFormState(form);
        const persist = () => persistFormState(form);
        form.addEventListener('input', persist);
        form.addEventListener('change', persist);
        window.addEventListener('pagehide', persist);
        const clearButton = form.querySelector('[data-clear-cache]');
        clearButton === null || clearButton === void 0 ? void 0 : clearButton.addEventListener('click', () => {
            const clearHref = clearButton.getAttribute('data-clear-href');
            if (clearHref) {
                clearPersistedFormState(form);
                window.location.href = clearHref;
                return;
            }
            form.reset();
            clearPersistedFormState(form);
            clearGenericFormUi(form);
        });
    });
    document.querySelectorAll('.tool-nav__link, .page-brand, .hub-card').forEach(link => {
        link.addEventListener('click', () => {
            forms.forEach(form => persistFormState(form));
        });
    });
};
const clearGenericFormUi = (form) => {
    const key = form.getAttribute('data-cache-key');
    if (key !== 'mechanic-lookup') {
        return;
    }
    const mechanicInput = form.querySelector('#mechanic-lookup-input, input[name="MechanicName"]');
    if (mechanicInput) {
        mechanicInput.value = '';
    }
    const results = document.getElementById('mechanic-lookup-results');
    if (results) {
        results.classList.add('hidden');
        results.innerHTML = '';
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
    hydrateFormState(form);
    updateSyncInputModeUi();
    updateSyncDirectionUi();
    const handler = () => persistFormState(form);
    form.addEventListener('input', handler);
    form.addEventListener('change', handler);
    window.addEventListener('pagehide', handler);
    form.addEventListener('submit', event => {
        handler();
        event.preventDefault();
        submitDeckSyncApi(form);
    });
    const clearButton = form.querySelector('[data-clear-cache]');
    clearButton === null || clearButton === void 0 ? void 0 : clearButton.addEventListener('click', () => {
        const clearHref = clearButton.getAttribute('data-clear-href');
        if (clearHref) {
            const cacheKey = form.getAttribute('data-cache-key');
            if (cacheKey === 'chatgpt-packets') {
                clearChatGptPacketsState(form);
            }
            else {
                clearPersistedFormState(form);
            }
            window.location.replace(clearHref);
            return;
        }
        form.reset();
        clearPersistedFormState(form);
        clearDeckSyncUi();
        updateSyncInputModeUi();
        updateSyncDirectionUi();
    });
    document.querySelectorAll('.tool-nav__link, .page-brand, .hub-card').forEach(link => {
        link.addEventListener('click', () => {
            persistFormState(form);
        });
    });
};
const parseChatGptStep = (value) => {
    const parsedValue = parseInt(value !== null && value !== void 0 ? value : '1', 10);
    return Number.isNaN(parsedValue) || parsedValue < 1 || parsedValue > 5 ? 1 : parsedValue;
};
const chatGptUiModeStorageKey = 'decksync-chatgpt-ui-mode';
const parseChatGptUiMode = (value) => {
    if (value === 'focused' || value === 'expert') {
        return value;
    }
    return 'guided';
};
const setChatGptValidationMessage = (message) => {
    const errorNode = document.querySelector('[data-chatgpt-validation-error]');
    if (!errorNode) {
        return;
    }
    if (!message) {
        errorNode.textContent = '';
        errorNode.classList.add('hidden');
        return;
    }
    errorNode.textContent = message;
    errorNode.classList.remove('hidden');
    errorNode.scrollIntoView({ behavior: 'smooth', block: 'center' });
};
const scrollChatGptResults = (form) => {
    const step = parseChatGptStep(form.dataset.chatgptCurrentStep);
    const activePanel = form.querySelector(`[data-chatgpt-step="${step}"]`);
    const resultAnchor = activePanel === null || activePanel === void 0 ? void 0 : activePanel.querySelector('[data-chatgpt-result-anchor]');
    if (!resultAnchor) {
        return;
    }
    window.setTimeout(() => {
        resultAnchor.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }, 120);
};
const showChatGptStep = (form, step) => {
    form.dataset.chatgptCurrentStep = step.toString();
    const workflowInput = form.querySelector('[data-chatgpt-workflow-step]');
    if (workflowInput) {
        workflowInput.value = step.toString();
    }
    form.querySelectorAll('[data-chatgpt-step]').forEach(panel => {
        const panelStep = parseChatGptStep(panel.dataset.chatgptStep);
        panel.classList.toggle('hidden', panelStep !== step);
        panel.setAttribute('aria-hidden', panelStep === step ? 'false' : 'true');
    });
    form.querySelectorAll('[data-chatgpt-show-step]').forEach(button => {
        const buttonStep = parseChatGptStep(button.dataset.chatgptShowStep);
        button.classList.toggle('is-active', buttonStep === step);
        button.setAttribute('aria-selected', buttonStep === step ? 'true' : 'false');
        button.setAttribute('tabindex', buttonStep === step ? '0' : '-1');
    });
};
const applyChatGptUiMode = (form, mode) => {
    form.dataset.chatgptUiMode = mode;
    document.body.dataset.chatgptUiMode = mode;
    document.querySelectorAll('[data-chatgpt-ui-mode-button]').forEach(button => {
        const buttonMode = parseChatGptUiMode(button.dataset.chatgptUiModeButton);
        button.classList.toggle('is-active', buttonMode === mode);
        button.setAttribute('aria-pressed', buttonMode === mode ? 'true' : 'false');
    });
};
const clearChatGptPacketsState = (form) => {
    clearPersistedFormState(form);
    storageAvailable === null || storageAvailable === void 0 ? void 0 : storageAvailable.removeItem(chatGptUiModeStorageKey);
    form.reset();
    applyChatGptUiMode(form, 'guided');
    showChatGptStep(form, 1);
    setChatGptValidationMessage(null);
};
const validateChatGptPacketsStep = (form, step) => {
    var _a, _b, _c, _d, _e, _f, _g, _h, _j, _k, _l, _m, _o, _p, _q, _r, _s, _t, _u, _v;
    const importArtifactsPath = (_b = (_a = form.querySelector('input[name="ImportArtifactsPath"]')) === null || _a === void 0 ? void 0 : _a.value.trim()) !== null && _b !== void 0 ? _b : '';
    if (importArtifactsPath) {
        // When importing a saved artifacts folder, the server rehydrates DeckProfileJson / SetUpgradeResponseJson —
        // skip client-side field validation and let the import path run.
        return null;
    }
    const deckInputSource = (_d = (_c = form.querySelector('select[name="DeckInputSource"]')) === null || _c === void 0 ? void 0 : _c.value) !== null && _d !== void 0 ? _d : DeckInputSource.PasteText;
    const deckSource = deckInputSource === DeckInputSource.PublicUrl
        ? (_f = (_e = form.querySelector('input[name="DeckUrl"]')) === null || _e === void 0 ? void 0 : _e.value.trim()) !== null && _f !== void 0 ? _f : ''
        : (_h = (_g = form.querySelector('textarea[name="DeckText"]')) === null || _g === void 0 ? void 0 : _g.value.trim()) !== null && _h !== void 0 ? _h : '';
    const deckProfileJson = (_k = (_j = form.querySelector('textarea[name="DeckProfileJson"]')) === null || _j === void 0 ? void 0 : _j.value.trim()) !== null && _k !== void 0 ? _k : '';
    const targetCommanderBracket = (_m = (_l = form.querySelector('select[name="TargetCommanderBracket"]')) === null || _l === void 0 ? void 0 : _l.value.trim()) !== null && _m !== void 0 ? _m : '';
    const cardSpecificQuestionCardNames = Array.from(form.querySelectorAll(`input[name="${cardPickerFieldName}"]`))
        .map(input => input.value.trim())
        .filter(value => value.length > 0);
    const budgetUpgradeAmount = (_p = (_o = form.querySelector('input[name="BudgetUpgradeAmount"]')) === null || _o === void 0 ? void 0 : _o.value.trim()) !== null && _p !== void 0 ? _p : '';
    const setPacketText = (_r = (_q = form.querySelector('textarea[name="SetPacketText"]')) === null || _q === void 0 ? void 0 : _q.value.trim()) !== null && _r !== void 0 ? _r : '';
    const selectedSetCodes = Array.from(form.querySelectorAll('select[name="SelectedSetCodes"] option:checked'));
    const selectedCardSpecificQuestions = form.querySelectorAll('input[name="SelectedAnalysisQuestions"][value="card-worth-it"]:checked, input[name="SelectedAnalysisQuestions"][value="better-alternatives"]:checked').length;
    const selectedBudgetQuestions = form.querySelectorAll('input[name="SelectedAnalysisQuestions"][value="budget-upgrades"]:checked').length;
    const selectedCategoryQuestions = form.querySelectorAll('input[name="SelectedAnalysisQuestions"][value="add-categories"]:checked, input[name="SelectedAnalysisQuestions"][value="update-categories"]:checked').length;
    const decklistExportFormat = (_t = (_s = form.querySelector('select[name="DecklistExportFormat"]')) === null || _s === void 0 ? void 0 : _s.value.trim()) !== null && _t !== void 0 ? _t : '';
    if (step < 3 && !deckSource) {
        return 'Paste a deck URL or deck export before generating ChatGPT packets.';
    }
    if (step === 2 && !targetCommanderBracket) {
        return 'Choose the target Commander bracket before generating the analysis packet.';
    }
    if (step === 2 && form.querySelectorAll('input[name="SelectedAnalysisQuestions"]:checked').length === 0) {
        return 'Select at least one analysis question before generating the analysis packet.';
    }
    if (step === 2 && selectedCardSpecificQuestions > 0 && cardSpecificQuestionCardNames.length === 0) {
        return 'Enter at least one card name for the selected card-specific analysis questions.';
    }
    if (step === 2 && selectedBudgetQuestions > 0 && !budgetUpgradeAmount) {
        return 'Enter a budget amount for the selected budget upgrade question.';
    }
    if (step === 2 && selectedCategoryQuestions > 0 && !decklistExportFormat) {
        return 'Choose Moxfield or Archidekt as the export format when assigning or updating categories — plain text does not support inline category formatting.';
    }
    if (step === 3 && !deckProfileJson) {
        return 'Paste the deck_profile JSON returned from ChatGPT before rendering the analysis summary.';
    }
    if (step === 4) {
        if (!deckSource) {
            return 'Paste a deck in Step 1 before generating the set upgrade packet.';
        }
        if (!setPacketText && selectedSetCodes.length > 1) {
            return 'Choose only one set or paste a condensed set packet override before generating the set-upgrade packet.';
        }
        if (!setPacketText && selectedSetCodes.length === 0) {
            return 'Select at least one set or paste a condensed set packet override before generating the set-upgrade packet.';
        }
    }
    if (step === 5) {
        const setUpgradeResponseJson = (_v = (_u = form.querySelector('textarea[name="SetUpgradeResponseJson"]')) === null || _u === void 0 ? void 0 : _u.value.trim()) !== null && _v !== void 0 ? _v : '';
        if (!setUpgradeResponseJson) {
            return 'Paste the set_upgrade_report JSON returned from ChatGPT before rendering the set upgrade results.';
        }
    }
    return null;
};
const syncCardSpecificQuestionField = (form) => {
    const field = form.querySelector('[data-card-specific-question-field]');
    if (!field) {
        return;
    }
    const hasCardSpecificQuestion = form.querySelectorAll('input[name="SelectedAnalysisQuestions"][value="card-worth-it"]:checked, input[name="SelectedAnalysisQuestions"][value="better-alternatives"]:checked').length > 0;
    field.classList.toggle('hidden', !hasCardSpecificQuestion);
};
const syncBudgetQuestionField = (form) => {
    const field = form.querySelector('[data-budget-question-field]');
    if (!field) {
        return;
    }
    const hasBudgetQuestion = form.querySelectorAll('input[name="SelectedAnalysisQuestions"][value="budget-upgrades"]:checked').length > 0;
    field.classList.toggle('hidden', !hasBudgetQuestion);
};
const syncPreferredCategoriesField = (form) => {
    const field = form.querySelector('[data-preferred-categories-field]');
    if (!field) {
        return;
    }
    const hasUpdateCategories = form.querySelectorAll('input[name="SelectedAnalysisQuestions"][value="update-categories"]:checked').length > 0;
    field.classList.toggle('hidden', !hasUpdateCategories);
};
const bracketToVersionQuestionId = {
    core: 'bracket-2-version',
    upgraded: 'bracket-3-version',
    optimized: 'bracket-4-version',
    cedh: 'bracket-5-version',
};
const syncVersioningBracketOptions = (form) => {
    var _a, _b;
    const bracketSelect = form.querySelector('select[name="TargetCommanderBracket"]');
    const selectedBracket = ((_a = bracketSelect === null || bracketSelect === void 0 ? void 0 : bracketSelect.value) !== null && _a !== void 0 ? _a : '').toLowerCase();
    const disabledQuestionId = (_b = bracketToVersionQuestionId[selectedBracket]) !== null && _b !== void 0 ? _b : null;
    Object.values(bracketToVersionQuestionId).forEach(questionId => {
        var _a;
        const checkbox = form.querySelector(`input[name="SelectedAnalysisQuestions"][value="${questionId}"]`);
        if (!checkbox)
            return;
        const shouldDisable = questionId === disabledQuestionId;
        checkbox.disabled = shouldDisable;
        if (shouldDisable && checkbox.checked) {
            checkbox.checked = false;
        }
        (_a = checkbox.closest('label')) === null || _a === void 0 ? void 0 : _a.classList.toggle('chatgpt-question-option--disabled', shouldDisable);
    });
    syncQuestionBucketState(form);
};
const syncQuestionBucketState = (form) => {
    form.querySelectorAll('[data-question-bucket]').forEach(bucketCheckbox => {
        var _a;
        const bucketId = (_a = bucketCheckbox.dataset.questionBucket) !== null && _a !== void 0 ? _a : '';
        const questionCheckboxes = Array.from(form.querySelectorAll(`input[data-question-option="${bucketId}"]`));
        if (questionCheckboxes.length === 0) {
            bucketCheckbox.checked = false;
            bucketCheckbox.indeterminate = false;
            return;
        }
        const checkedCount = questionCheckboxes.filter(checkbox => checkbox.checked).length;
        bucketCheckbox.checked = checkedCount === questionCheckboxes.length;
        bucketCheckbox.indeterminate = checkedCount > 0 && checkedCount < questionCheckboxes.length;
    });
};
const attachBucketToggles = (form) => {
    form.querySelectorAll('[data-bucket-toggle]').forEach(toggleBtn => {
        toggleBtn.addEventListener('click', () => {
            var _a;
            const bucketId = (_a = toggleBtn.dataset.bucketToggle) !== null && _a !== void 0 ? _a : '';
            const questionsDiv = form.querySelector(`[data-bucket-questions="${bucketId}"]`);
            if (!questionsDiv) {
                return;
            }
            const nowHidden = questionsDiv.classList.toggle('hidden');
            toggleBtn.setAttribute('aria-expanded', nowHidden ? 'false' : 'true');
        });
    });
};
const attachQuestionBucketSelection = (form) => {
    form.querySelectorAll('[data-question-bucket]').forEach(bucketCheckbox => {
        bucketCheckbox.addEventListener('change', () => {
            var _a;
            const bucketId = (_a = bucketCheckbox.dataset.questionBucket) !== null && _a !== void 0 ? _a : '';
            const questionsDiv = form.querySelector(`[data-bucket-questions="${bucketId}"]`);
            if (bucketId === 'deck-versioning') {
                // Checking the bucket header selects only the three-upgrade-paths question
                form.querySelectorAll(`input[data-question-option="${bucketId}"]`).forEach(questionCheckbox => {
                    questionCheckbox.checked = bucketCheckbox.checked && questionCheckbox.value === 'three-upgrade-paths' && !questionCheckbox.disabled;
                });
            }
            else {
                form.querySelectorAll(`input[data-question-option="${bucketId}"]`).forEach(questionCheckbox => {
                    questionCheckbox.checked = bucketCheckbox.checked;
                });
            }
            // Auto-expand the bucket when the select-all checkbox is checked
            if (bucketCheckbox.checked && (questionsDiv === null || questionsDiv === void 0 ? void 0 : questionsDiv.classList.contains('hidden'))) {
                questionsDiv.classList.remove('hidden');
                const toggleBtn = form.querySelector(`[data-bucket-toggle="${bucketId}"]`);
                toggleBtn === null || toggleBtn === void 0 ? void 0 : toggleBtn.setAttribute('aria-expanded', 'true');
            }
            syncQuestionBucketState(form);
            syncCardSpecificQuestionField(form);
            syncBudgetQuestionField(form);
        });
    });
    form.querySelectorAll('input[data-question-option]').forEach(questionCheckbox => {
        questionCheckbox.addEventListener('change', () => {
            var _a;
            const bucketId = (_a = questionCheckbox.dataset.questionOption) !== null && _a !== void 0 ? _a : '';
            // Single-select for deck-versioning: checking one unchecks all siblings
            if (bucketId === 'deck-versioning' && questionCheckbox.checked) {
                form.querySelectorAll(`input[data-question-option="${bucketId}"]`).forEach(sibling => {
                    if (sibling !== questionCheckbox) {
                        sibling.checked = false;
                    }
                });
            }
            syncQuestionBucketState(form);
            syncCardSpecificQuestionField(form);
            syncBudgetQuestionField(form);
            syncPreferredCategoriesField(form);
        });
    });
    syncQuestionBucketState(form);
    syncCardSpecificQuestionField(form);
    syncBudgetQuestionField(form);
    syncPreferredCategoriesField(form);
};
const loadSetOptionsAsync = () => {
    var _a, _b;
    const form = document.querySelector('[data-chatgpt-packets-form]');
    const select = form === null || form === void 0 ? void 0 : form.querySelector('[data-set-options-select]');
    if (!form || !select) {
        return;
    }
    const setOptionsUrl = (_a = form.dataset.setOptionsUrl) === null || _a === void 0 ? void 0 : _a.trim();
    if (!setOptionsUrl) {
        return;
    }
    const selectedCodes = new Set(((_b = select.dataset.selectedCodes) !== null && _b !== void 0 ? _b : '').split(',').map(c => c.trim().toLowerCase()).filter(Boolean));
    const SET_TYPE_LABELS = {
        expansion: 'Expansion',
        core: 'Core',
        masters: 'Masters',
        commander: 'Commander',
        draft_innovation: 'Draft Innovation',
        token: 'Token',
        promo: 'Promo',
        funny: 'Funny'
    };
    const SET_TYPE_ORDER = [
        'expansion',
        'core',
        'masters',
        'commander',
        'draft_innovation',
        'token',
        'promo',
        'funny'
    ];
    const prettifySetType = (value) => value
        .split('_')
        .filter(Boolean)
        .map(part => part.charAt(0).toUpperCase() + part.slice(1).toLowerCase())
        .join(' ');
    const getSetTypeLabel = (value) => { var _a; return (_a = SET_TYPE_LABELS[value]) !== null && _a !== void 0 ? _a : prettifySetType(value); };
    fetch(setOptionsUrl)
        .then(response => {
        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }
        return response.json();
    })
        .then(sets => {
        var _a, _b, _c, _d;
        select.innerHTML = '';
        const groupedSets = new Map();
        const otherSets = [];
        const unknownGroups = new Map();
        for (const set of sets) {
            const setType = (_b = (_a = set.setType) === null || _a === void 0 ? void 0 : _a.trim().toLowerCase()) !== null && _b !== void 0 ? _b : '';
            if (!setType) {
                otherSets.push(set);
                continue;
            }
            if (SET_TYPE_LABELS[setType]) {
                const existing = groupedSets.get(setType);
                if (existing) {
                    existing.push(set);
                }
                else {
                    groupedSets.set(setType, [set]);
                }
                continue;
            }
            const existing = unknownGroups.get(setType);
            if (existing) {
                existing.push(set);
            }
            else {
                unknownGroups.set(setType, [set]);
            }
        }
        for (const setType of SET_TYPE_ORDER) {
            const group = groupedSets.get(setType);
            if (!group || group.length === 0) {
                continue;
            }
            const optgroup = document.createElement('optgroup');
            optgroup.label = getSetTypeLabel(setType);
            for (const set of group) {
                const option = document.createElement('option');
                option.value = set.code;
                option.textContent = set.displayLabel;
                if (selectedCodes.has(set.code.toLowerCase())) {
                    option.selected = true;
                }
                optgroup.appendChild(option);
            }
            select.appendChild(optgroup);
        }
        Array.from(unknownGroups.entries())
            .sort(([leftKey, leftSets], [rightKey, rightSets]) => {
            const leftLabel = getSetTypeLabel(leftKey);
            const rightLabel = getSetTypeLabel(rightKey);
            return leftLabel.localeCompare(rightLabel) || leftKey.localeCompare(rightKey);
        })
            .forEach(([setType, group]) => {
            const optgroup = document.createElement('optgroup');
            optgroup.label = getSetTypeLabel(setType);
            for (const set of group) {
                const option = document.createElement('option');
                option.value = set.code;
                option.textContent = set.displayLabel;
                if (selectedCodes.has(set.code.toLowerCase())) {
                    option.selected = true;
                }
                optgroup.appendChild(option);
            }
            select.appendChild(optgroup);
        });
        if (otherSets.length > 0) {
            const optgroup = document.createElement('optgroup');
            optgroup.label = 'Other';
            for (const set of otherSets) {
                const option = document.createElement('option');
                option.value = set.code;
                option.textContent = set.displayLabel;
                if (selectedCodes.has(set.code.toLowerCase())) {
                    option.selected = true;
                }
                optgroup.appendChild(option);
            }
            select.appendChild(optgroup);
        }
        (_d = (_c = deckFlowWindow.DeckFlow) === null || _c === void 0 ? void 0 : _c.refreshDfSelect) === null || _d === void 0 ? void 0 : _d.call(_c, select);
    })
        .catch(() => {
        const errorHint = document.querySelector('[data-set-options-error]');
        errorHint === null || errorHint === void 0 ? void 0 : errorHint.classList.remove('hidden');
    });
};
const attachChatGptPacketsWorkflow = () => {
    const form = document.querySelector('[data-chatgpt-packets-form]');
    if (!form) {
        return;
    }
    const currentStep = parseChatGptStep(form.dataset.chatgptCurrentStep);
    const initialUiMode = parseChatGptUiMode(storageAvailable === null || storageAvailable === void 0 ? void 0 : storageAvailable.getItem(chatGptUiModeStorageKey));
    attachQuestionBucketSelection(form);
    attachBucketToggles(form);
    attachCardPicker(form);
    const bracketSelect = form.querySelector('select[name="TargetCommanderBracket"]');
    bracketSelect === null || bracketSelect === void 0 ? void 0 : bracketSelect.addEventListener('change', () => syncVersioningBracketOptions(form));
    syncVersioningBracketOptions(form);
    applyChatGptUiMode(form, initialUiMode);
    showChatGptStep(form, currentStep);
    setChatGptValidationMessage(null);
    scrollChatGptResults(form);
    document.querySelectorAll('[data-chatgpt-ui-mode-button]').forEach(button => {
        button.addEventListener('click', () => {
            const mode = parseChatGptUiMode(button.dataset.chatgptUiModeButton);
            applyChatGptUiMode(form, mode);
            storageAvailable === null || storageAvailable === void 0 ? void 0 : storageAvailable.setItem(chatGptUiModeStorageKey, mode);
        });
    });
    form.querySelectorAll('[data-chatgpt-show-step]').forEach(button => {
        button.addEventListener('click', () => {
            const step = parseChatGptStep(button.dataset.chatgptShowStep);
            showChatGptStep(form, step);
            setChatGptValidationMessage(null);
        });
    });
    form.querySelectorAll('[data-chatgpt-next-step]').forEach(button => {
        button.addEventListener('click', () => {
            var _a;
            const step = parseChatGptStep(button.dataset.chatgptNextStep);
            showChatGptStep(form, step);
            setChatGptValidationMessage(null);
            (_a = form.querySelector(`[data-chatgpt-step="${step}"]`)) === null || _a === void 0 ? void 0 : _a.scrollIntoView({ behavior: 'smooth', block: 'start' });
        });
    });
    form.addEventListener('submit', event => {
        var _a;
        const submitter = event.submitter;
        const step = parseChatGptStep((_a = submitter === null || submitter === void 0 ? void 0 : submitter.dataset.chatgptSubmitStep) !== null && _a !== void 0 ? _a : form.dataset.chatgptCurrentStep);
        const validationMessage = validateChatGptPacketsStep(form, step);
        if (!validationMessage) {
            setChatGptValidationMessage(null);
            showChatGptStep(form, step);
            return;
        }
        event.preventDefault();
        hideBusyIndicator();
        showChatGptStep(form, step);
        setChatGptValidationMessage(validationMessage);
    });
};
const parseChatGptComparisonStep = (value) => {
    const parsedValue = parseInt(value !== null && value !== void 0 ? value : '1', 10);
    return Number.isNaN(parsedValue) || parsedValue < 1 || parsedValue > 3 ? 1 : parsedValue;
};
const setChatGptComparisonValidationMessage = (message) => {
    const errorNode = document.querySelector('[data-chatgpt-comparison-validation-error]');
    if (!errorNode) {
        return;
    }
    if (!message) {
        errorNode.textContent = '';
        errorNode.classList.add('hidden');
        return;
    }
    errorNode.textContent = message;
    errorNode.classList.remove('hidden');
    errorNode.scrollIntoView({ behavior: 'smooth', block: 'center' });
};
const showChatGptComparisonStep = (form, step) => {
    form.dataset.chatgptComparisonCurrentStep = step.toString();
    const workflowInput = form.querySelector('[data-chatgpt-comparison-workflow-step]');
    if (workflowInput) {
        workflowInput.value = step.toString();
    }
    form.querySelectorAll('[data-chatgpt-comparison-step]').forEach(panel => {
        const panelStep = parseChatGptComparisonStep(panel.dataset.chatgptComparisonStep);
        panel.classList.toggle('hidden', panelStep !== step);
        panel.setAttribute('aria-hidden', panelStep === step ? 'false' : 'true');
    });
    form.querySelectorAll('[data-chatgpt-comparison-show-step]').forEach(button => {
        const buttonStep = parseChatGptComparisonStep(button.dataset.chatgptComparisonShowStep);
        button.classList.toggle('is-active', buttonStep === step);
        button.setAttribute('aria-selected', buttonStep === step ? 'true' : 'false');
        button.setAttribute('tabindex', buttonStep === step ? '0' : '-1');
    });
};
const scrollChatGptComparisonResults = (form) => {
    const step = parseChatGptComparisonStep(form.dataset.chatgptComparisonCurrentStep);
    const activePanel = form.querySelector(`[data-chatgpt-comparison-step="${step}"]`);
    const resultAnchor = activePanel === null || activePanel === void 0 ? void 0 : activePanel.querySelector('[data-chatgpt-comparison-result-anchor]');
    if (!resultAnchor) {
        return;
    }
    window.setTimeout(() => {
        resultAnchor.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }, 120);
};
const validateChatGptComparisonStep = (form, step) => {
    var _a, _b, _c, _d, _e, _f, _g, _h, _j, _k;
    const deckASource = (_b = (_a = form.querySelector('textarea[name="DeckASource"]')) === null || _a === void 0 ? void 0 : _a.value.trim()) !== null && _b !== void 0 ? _b : '';
    const deckBSource = (_d = (_c = form.querySelector('textarea[name="DeckBSource"]')) === null || _c === void 0 ? void 0 : _c.value.trim()) !== null && _d !== void 0 ? _d : '';
    const deckABracket = (_f = (_e = form.querySelector('select[name="DeckABracket"]')) === null || _e === void 0 ? void 0 : _e.value.trim()) !== null && _f !== void 0 ? _f : '';
    const deckBBracket = (_h = (_g = form.querySelector('select[name="DeckBBracket"]')) === null || _g === void 0 ? void 0 : _g.value.trim()) !== null && _h !== void 0 ? _h : '';
    const comparisonResponseJson = (_k = (_j = form.querySelector('textarea[name="ComparisonResponseJson"]')) === null || _j === void 0 ? void 0 : _j.value.trim()) !== null && _k !== void 0 ? _k : '';
    if (!deckASource) {
        return 'Enter Deck A URL or deck text before generating the comparison packet.';
    }
    if (!deckBSource) {
        return 'Enter Deck B URL or deck text before generating the comparison packet.';
    }
    if (!deckABracket) {
        return 'Choose a Commander bracket for Deck A before generating the comparison packet.';
    }
    if (!deckBBracket) {
        return 'Choose a Commander bracket for Deck B before generating the comparison packet.';
    }
    if (step >= 3 && !comparisonResponseJson) {
        return 'Paste the deck_comparison JSON returned from ChatGPT into Step 3 before rendering the summary.';
    }
    return null;
};
const attachChatGptComparisonWorkflow = () => {
    const form = document.querySelector('[data-chatgpt-comparison-form]');
    if (!form) {
        return;
    }
    const currentStep = parseChatGptComparisonStep(form.dataset.chatgptComparisonCurrentStep);
    showChatGptComparisonStep(form, currentStep);
    setChatGptComparisonValidationMessage(null);
    scrollChatGptComparisonResults(form);
    form.querySelectorAll('[data-chatgpt-comparison-show-step]').forEach(button => {
        button.addEventListener('click', () => {
            const step = parseChatGptComparisonStep(button.dataset.chatgptComparisonShowStep);
            showChatGptComparisonStep(form, step);
            setChatGptComparisonValidationMessage(null);
        });
    });
    form.querySelectorAll('[data-chatgpt-comparison-next-step]').forEach(button => {
        button.addEventListener('click', () => {
            var _a;
            const step = parseChatGptComparisonStep(button.dataset.chatgptComparisonNextStep);
            const validationMessage = validateChatGptComparisonStep(form, Math.min(step, 2));
            if (validationMessage) {
                setChatGptComparisonValidationMessage(validationMessage);
                return;
            }
            showChatGptComparisonStep(form, step);
            setChatGptComparisonValidationMessage(null);
            (_a = form.querySelector(`[data-chatgpt-comparison-step="${step}"]`)) === null || _a === void 0 ? void 0 : _a.scrollIntoView({ behavior: 'smooth', block: 'start' });
        });
    });
    form.addEventListener('submit', event => {
        var _a;
        const submitter = event.submitter;
        const step = parseChatGptComparisonStep((_a = submitter === null || submitter === void 0 ? void 0 : submitter.dataset.chatgptComparisonSubmitStep) !== null && _a !== void 0 ? _a : form.dataset.chatgptComparisonCurrentStep);
        const validationMessage = validateChatGptComparisonStep(form, step);
        if (!validationMessage) {
            setChatGptComparisonValidationMessage(null);
            showChatGptComparisonStep(form, step);
            return;
        }
        event.preventDefault();
        hideBusyIndicator();
        showChatGptComparisonStep(form, step);
        setChatGptComparisonValidationMessage(validationMessage);
    });
};
const parseChatGptCedhStep = (value) => {
    const parsedValue = parseInt(value !== null && value !== void 0 ? value : '1', 10);
    return Number.isNaN(parsedValue) || parsedValue < 1 || parsedValue > 3 ? 1 : parsedValue;
};
const parseChatGptCedhPage = (value) => {
    const parsedValue = parseInt(value !== null && value !== void 0 ? value : '1', 10);
    return Number.isNaN(parsedValue) || parsedValue < 1 ? 1 : parsedValue;
};
const maxChatGptCedhReferences = 3;
const setChatGptCedhValidationMessage = (message) => {
    const errorNode = document.querySelector('[data-chatgpt-cedh-validation-error]');
    if (!errorNode) {
        return;
    }
    if (!message) {
        errorNode.textContent = '';
        errorNode.classList.add('hidden');
        return;
    }
    errorNode.textContent = message;
    errorNode.classList.remove('hidden');
    errorNode.scrollIntoView({ behavior: 'smooth', block: 'center' });
};
const showChatGptCedhStep = (form, step) => {
    form.dataset.chatgptCedhCurrentStep = step.toString();
    const workflowInput = form.querySelector('[data-chatgpt-cedh-workflow-step]');
    if (workflowInput) {
        workflowInput.value = step.toString();
    }
    form.querySelectorAll('[data-chatgpt-cedh-step]').forEach(panel => {
        const panelStep = parseChatGptCedhStep(panel.dataset.chatgptCedhStep);
        panel.classList.toggle('hidden', panelStep !== step);
        panel.setAttribute('aria-hidden', panelStep === step ? 'false' : 'true');
    });
    form.querySelectorAll('[data-chatgpt-cedh-show-step]').forEach(button => {
        const buttonStep = parseChatGptCedhStep(button.dataset.chatgptCedhShowStep);
        button.classList.toggle('is-active', buttonStep === step);
        button.setAttribute('aria-selected', buttonStep === step ? 'true' : 'false');
        button.setAttribute('tabindex', buttonStep === step ? '0' : '-1');
    });
};
const scrollChatGptCedhResults = (form) => {
    const step = parseChatGptCedhStep(form.dataset.chatgptCedhCurrentStep);
    const activePanel = form.querySelector(`[data-chatgpt-cedh-step="${step}"]`);
    const resultAnchor = activePanel === null || activePanel === void 0 ? void 0 : activePanel.querySelector('[data-chatgpt-cedh-result-anchor]');
    if (!resultAnchor) {
        return;
    }
    window.setTimeout(() => {
        resultAnchor.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }, 120);
};
const validateChatGptCedhStep = (form, step) => {
    var _a, _b, _c, _d;
    if (step === 1) {
        const deckSource = (_b = (_a = form.querySelector('textarea[name="DeckSource"]')) === null || _a === void 0 ? void 0 : _a.value.trim()) !== null && _b !== void 0 ? _b : '';
        if (!deckSource) {
            return 'Paste your deck URL or deck text before fetching EDH Top 16 reference decks.';
        }
    }
    if (step === 2) {
        const checkedReferences = form.querySelectorAll('[data-chatgpt-cedh-reference-checkbox]:checked').length;
        if (checkedReferences < 1) {
            return 'Select at least 1 EDH Top 16 reference deck before generating the prompt.';
        }
        if (checkedReferences > maxChatGptCedhReferences) {
            return `Select no more than ${maxChatGptCedhReferences} EDH Top 16 reference decks before generating the prompt.`;
        }
    }
    if (step === 3) {
        const responseJson = (_d = (_c = form.querySelector('textarea[name="MetaGapResponseJson"]')) === null || _c === void 0 ? void 0 : _c.value.trim()) !== null && _d !== void 0 ? _d : '';
        if (!responseJson) {
            return 'Paste the meta_gap JSON returned from ChatGPT into Step 3 before rendering the analysis.';
        }
    }
    return null;
};
const syncChatGptCedhCheckboxState = (form) => {
    const checkboxes = Array.from(form.querySelectorAll('[data-chatgpt-cedh-reference-checkbox]'));
    const checkedCount = checkboxes.filter(checkbox => checkbox.checked).length;
    checkboxes.forEach(checkbox => {
        checkbox.disabled = !checkbox.checked && checkedCount >= maxChatGptCedhReferences;
    });
};
const showChatGptCedhReferencePage = (form, page) => {
    const rowsWithPages = Array.from(form.querySelectorAll('[data-chatgpt-cedh-reference-row]')).map(row => ({
        row,
        page: parseChatGptCedhPage(row.dataset.chatgptCedhPage)
    }));
    if (rowsWithPages.length === 0) {
        return;
    }
    const maxPage = Math.max(...rowsWithPages.map(({ page: rowPage }) => rowPage));
    const nextPage = Math.min(Math.max(page, 1), maxPage);
    rowsWithPages.forEach(({ row, page: rowPage }) => {
        row.classList.toggle('hidden', rowPage !== nextPage);
    });
    form.dataset.chatgptCedhReferencePage = nextPage.toString();
    const status = form.querySelector('[data-chatgpt-cedh-page-status]');
    if (status) {
        status.textContent = `Page ${nextPage} of ${maxPage}`;
    }
    const prevButton = form.querySelector('[data-chatgpt-cedh-page-nav="prev"]');
    const nextButton = form.querySelector('[data-chatgpt-cedh-page-nav="next"]');
    if (prevButton) {
        prevButton.disabled = nextPage <= 1;
    }
    if (nextButton) {
        nextButton.disabled = nextPage >= maxPage;
    }
};
const attachChatGptCedhWorkflow = () => {
    const form = document.querySelector('[data-chatgpt-cedh-form]');
    if (!form) {
        return;
    }
    const currentStep = parseChatGptCedhStep(form.dataset.chatgptCedhCurrentStep);
    showChatGptCedhStep(form, currentStep);
    setChatGptCedhValidationMessage(null);
    syncChatGptCedhCheckboxState(form);
    showChatGptCedhReferencePage(form, parseChatGptCedhPage(form.dataset.chatgptCedhReferencePage));
    scrollChatGptCedhResults(form);
    form.querySelectorAll('[data-chatgpt-cedh-reference-checkbox]').forEach(checkbox => {
        checkbox.addEventListener('change', () => {
            syncChatGptCedhCheckboxState(form);
            setChatGptCedhValidationMessage(null);
        });
    });
    form.querySelectorAll('[data-chatgpt-cedh-show-step]').forEach(button => {
        button.addEventListener('click', () => {
            const step = parseChatGptCedhStep(button.dataset.chatgptCedhShowStep);
            showChatGptCedhStep(form, step);
            setChatGptCedhValidationMessage(null);
        });
    });
    form.querySelectorAll('[data-chatgpt-cedh-next-step]').forEach(button => {
        button.addEventListener('click', () => {
            var _a;
            const step = parseChatGptCedhStep(button.dataset.chatgptCedhNextStep);
            showChatGptCedhStep(form, step);
            setChatGptCedhValidationMessage(null);
            (_a = form.querySelector(`[data-chatgpt-cedh-step="${step}"]`)) === null || _a === void 0 ? void 0 : _a.scrollIntoView({ behavior: 'smooth', block: 'start' });
        });
    });
    form.querySelectorAll('[data-chatgpt-cedh-page-nav]').forEach(button => {
        button.addEventListener('click', () => {
            const currentPage = parseChatGptCedhPage(form.dataset.chatgptCedhReferencePage);
            const delta = button.dataset.chatgptCedhPageNav === 'next' ? 1 : -1;
            showChatGptCedhReferencePage(form, currentPage + delta);
        });
    });
    form.addEventListener('submit', event => {
        var _a;
        const submitter = event.submitter;
        const step = parseChatGptCedhStep((_a = submitter === null || submitter === void 0 ? void 0 : submitter.dataset.chatgptCedhSubmitStep) !== null && _a !== void 0 ? _a : form.dataset.chatgptCedhCurrentStep);
        const validationMessage = validateChatGptCedhStep(form, step);
        if (!validationMessage) {
            setChatGptCedhValidationMessage(null);
            showChatGptCedhStep(form, step);
            return;
        }
        event.preventDefault();
        hideBusyIndicator();
        showChatGptCedhStep(form, step);
        setChatGptCedhValidationMessage(validationMessage);
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
    attachActionButtons();
    attachGenericPersistedForms();
    attachDeckSyncPersistence();
    attachChatGptPacketsWorkflow();
    attachChatGptComparisonWorkflow();
    attachChatGptCedhWorkflow();
    attachMoxfieldExtensionImport();
    loadSetOptionsAsync();
    loadSavedSessionsAsync();
    attachConvertForm();
};
const loadSavedSessionsAsync = () => {
    const panel = document.querySelector('[data-saved-sessions-url]');
    const select = document.querySelector('[data-saved-sessions-select]');
    const pathInput = document.querySelector('[data-chatgpt-import-path]');
    if (!panel || !select || !pathInput)
        return;
    const url = panel.dataset.savedSessionsUrl;
    if (!url)
        return;
    fetch(url)
        .then(response => {
        if (!response.ok)
            throw new Error(`HTTP ${response.status}`);
        return response.json();
    })
        .then(sessions => {
        var _a, _b, _c;
        select.innerHTML = '';
        const blankOption = document.createElement('option');
        blankOption.value = '';
        blankOption.textContent = sessions.length === 0 ? 'No saved sessions' : '— Pick a saved session —';
        select.appendChild(blankOption);
        for (const session of sessions) {
            const option = document.createElement('option');
            option.value = session.relativePath;
            const created = new Date(session.createdUtc);
            option.textContent = `${session.commander} · ${session.timestamp}  (${created.toLocaleString()})`;
            select.appendChild(option);
        }
        (_b = (_a = deckFlowWindow.DeckFlow) === null || _a === void 0 ? void 0 : _a.refreshDfSelect) === null || _b === void 0 ? void 0 : _b.call(_a, select);
        if (sessions.length === 0) {
            (_c = document.querySelector('[data-saved-sessions-empty]')) === null || _c === void 0 ? void 0 : _c.removeAttribute('hidden');
        }
    })
        .catch(() => {
        select.innerHTML = '<option value="">Could not load saved sessions</option>';
    });
    select.addEventListener('change', () => {
        if (select.value) {
            pathInput.value = select.value;
        }
    });
};
const attachConvertForm = () => {
    const form = document.querySelector('form[data-cache-key="deck-convert"]');
    if (!form)
        return;
    const inputSourceSelect = form.querySelector('select[name="InputSource"]');
    const sourceFormatSelect = form.querySelector('[data-convert-source]');
    const urlPanel = form.querySelector('[data-convert-panel="url"]');
    const textPanel = form.querySelector('[data-convert-panel="text"]');
    const commanderPanel = form.querySelector('[data-convert-panel="commander"]');
    const syncConvertPanels = () => {
        const isUrl = (inputSourceSelect === null || inputSourceSelect === void 0 ? void 0 : inputSourceSelect.value) === 'PublicUrl';
        urlPanel === null || urlPanel === void 0 ? void 0 : urlPanel.classList.toggle('hidden', !isUrl);
        textPanel === null || textPanel === void 0 ? void 0 : textPanel.classList.toggle('hidden', isUrl);
        const isMoxfield = (sourceFormatSelect === null || sourceFormatSelect === void 0 ? void 0 : sourceFormatSelect.value) === 'Moxfield';
        commanderPanel === null || commanderPanel === void 0 ? void 0 : commanderPanel.classList.toggle('hidden', !isMoxfield);
    };
    inputSourceSelect === null || inputSourceSelect === void 0 ? void 0 : inputSourceSelect.addEventListener('change', syncConvertPanels);
    sourceFormatSelect === null || sourceFormatSelect === void 0 ? void 0 : sourceFormatSelect.addEventListener('change', syncConvertPanels);
    syncConvertPanels();
    const commanderInput = form.querySelector('input[data-commander-search]');
    if (commanderInput) {
        const endpoint = commanderInput.dataset.commanderSearch;
        const datalist = document.getElementById('commander-suggestions');
        let debounceTimer;
        commanderInput.addEventListener('input', () => {
            window.clearTimeout(debounceTimer);
            const query = commanderInput.value.trim();
            if (query.length < 2) {
                if (datalist)
                    datalist.innerHTML = '';
                return;
            }
            debounceTimer = window.setTimeout(async () => {
                try {
                    const response = await fetch(`${endpoint}?q=${encodeURIComponent(query)}`);
                    if (!response.ok || !datalist)
                        return;
                    const names = await response.json();
                    datalist.innerHTML = '';
                    names.forEach(name => {
                        const option = document.createElement('option');
                        option.value = name;
                        datalist.appendChild(option);
                    });
                }
                catch (_a) {
                    // ignore — typeahead is best-effort
                }
            }, 300);
        });
    }
};
document.addEventListener('DOMContentLoaded', bootstrapDeckSync);
if (document.readyState !== 'loading') {
    bootstrapDeckSync();
}
