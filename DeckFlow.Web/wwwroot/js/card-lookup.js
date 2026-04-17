"use strict";
const countNonEmptyLines = (value) => value
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(line => line.length > 0)
    .length;
const debounceCardLookupSearch = (fn, delay) => {
    let timer;
    return () => {
        if (timer !== undefined) {
            window.clearTimeout(timer);
        }
        timer = window.setTimeout(fn, delay);
    };
};
const parseLookupLine = (line) => {
    var _a, _b;
    const trimmed = line.trim();
    const match = trimmed.match(/^(\d+)\s+(.+)$/);
    if (!match) {
        return { quantity: '', cardName: trimmed };
    }
    return {
        quantity: (_a = match[1]) !== null && _a !== void 0 ? _a : '',
        cardName: ((_b = match[2]) !== null && _b !== void 0 ? _b : '').trim()
    };
};
const buildLookupLine = (quantity, cardName) => {
    const trimmedName = cardName.trim();
    const trimmedQuantity = quantity.trim();
    if (!trimmedName) {
        return '';
    }
    return trimmedQuantity ? `${trimmedQuantity} ${trimmedName}` : trimmedName;
};
const createLookupSuggestionPanel = (anchor) => {
    const panel = document.createElement('div');
    panel.className = 'autocomplete-panel hidden';
    panel.setAttribute('role', 'listbox');
    anchor.appendChild(panel);
    return panel;
};
const hideLookupSuggestionPanel = (panel) => {
    panel.classList.add('hidden');
    panel.replaceChildren();
};
const attachLookaheadInput = (input, panel, minChars, onPick) => {
    const fetchSuggestions = async () => {
        const query = input.value.trim();
        if (query.length < minChars) {
            hideLookupSuggestionPanel(panel);
            return;
        }
        try {
            const response = await fetch(`/suggest-categories/card-search?query=${encodeURIComponent(query)}`);
            if (!response.ok) {
                hideLookupSuggestionPanel(panel);
                return;
            }
            const names = await response.json();
            panel.replaceChildren();
            if (names.length === 0) {
                hideLookupSuggestionPanel(panel);
                return;
            }
            names.forEach(name => {
                const option = document.createElement('button');
                option.type = 'button';
                option.className = 'autocomplete-option';
                option.textContent = name;
                option.addEventListener('mousedown', event => {
                    event.preventDefault();
                    input.value = name;
                    hideLookupSuggestionPanel(panel);
                    onPick(name);
                });
                panel.appendChild(option);
            });
            panel.classList.remove('hidden');
        }
        catch (_a) {
            hideLookupSuggestionPanel(panel);
        }
    };
    const debounced = debounceCardLookupSearch(fetchSuggestions, 250);
    input.addEventListener('input', debounced);
    input.addEventListener('focus', debounced);
    document.addEventListener('click', event => {
        if (!(event.target instanceof Node) || panel.contains(event.target) || input.contains(event.target)) {
            return;
        }
        hideLookupSuggestionPanel(panel);
    });
};
const initializeSingleCardMode = () => {
    var _a;
    const input = document.querySelector('[data-card-lookup-single-input]');
    const submitButton = document.querySelector('[data-card-lookup-single-submit]');
    const clearButton = document.querySelector('[data-card-lookup-single-clear]');
    const errorBanner = document.querySelector('[data-card-lookup-single-error]');
    const resultPanel = document.querySelector('[data-card-lookup-single-result]');
    const resultTextarea = document.querySelector('[data-card-lookup-single-output]');
    const resultLabel = document.querySelector('[data-card-lookup-single-name-label]');
    const anchor = input === null || input === void 0 ? void 0 : input.parentElement;
    if (!input || !submitButton || !errorBanner || !resultPanel || !resultTextarea || !resultLabel || !anchor) {
        return;
    }
    const suggestionPanel = createLookupSuggestionPanel(anchor);
    const showError = (message) => {
        errorBanner.textContent = message;
        errorBanner.classList.remove('hidden');
        resultPanel.classList.add('hidden');
    };
    const clearError = () => {
        errorBanner.textContent = '';
        errorBanner.classList.add('hidden');
    };
    const askJudgeLink = document.querySelector('[data-card-lookup-ask-judge-link]');
    const askJudgeBaseHref = (_a = askJudgeLink === null || askJudgeLink === void 0 ? void 0 : askJudgeLink.getAttribute('href')) !== null && _a !== void 0 ? _a : '/judge-questions';
    const showResult = (name, verifiedText) => {
        clearError();
        resultLabel.textContent = name;
        resultTextarea.value = verifiedText;
        resultPanel.classList.remove('hidden');
        if (askJudgeLink) {
            askJudgeLink.href = `${askJudgeBaseHref}?card=${encodeURIComponent(name)}`;
        }
    };
    const runLookup = async (name) => {
        var _a;
        const query = name.trim();
        if (!query) {
            showError('Enter a card name first.');
            return;
        }
        submitButton.disabled = true;
        clearError();
        try {
            const response = await fetch(`/card-lookup/single?name=${encodeURIComponent(query)}`);
            const payload = await response.json().catch(() => null);
            if (!response.ok) {
                showError((_a = payload === null || payload === void 0 ? void 0 : payload.message) !== null && _a !== void 0 ? _a : 'Scryfall could not be reached right now. Try again shortly.');
                return;
            }
            if (!(payload === null || payload === void 0 ? void 0 : payload.verifiedText)) {
                showError('No card details were returned.');
                return;
            }
            showResult(query, payload.verifiedText);
        }
        catch (error) {
            showError(error instanceof Error ? error.message : 'Lookup failed.');
        }
        finally {
            submitButton.disabled = false;
        }
    };
    attachLookaheadInput(input, suggestionPanel, 4, name => {
        input.value = name;
        runLookup(name);
    });
    submitButton.addEventListener('click', () => {
        runLookup(input.value);
    });
    input.addEventListener('keydown', event => {
        if (event.key === 'Enter') {
            event.preventDefault();
            runLookup(input.value);
        }
    });
    clearButton === null || clearButton === void 0 ? void 0 : clearButton.addEventListener('click', () => {
        input.value = '';
        resultPanel.classList.add('hidden');
        resultTextarea.value = '';
        clearError();
        hideLookupSuggestionPanel(suggestionPanel);
        input.focus();
    });
};
const initializeModePicker = () => {
    const picker = document.querySelector('[data-card-lookup-mode-picker]');
    if (!picker) {
        return;
    }
    const panels = Array.from(document.querySelectorAll('[data-card-lookup-mode-panel]'));
    const buttons = Array.from(picker.querySelectorAll('[data-card-lookup-mode-button]'));
    const activate = (mode) => {
        buttons.forEach(button => {
            const active = button.dataset.cardLookupModeButton === mode;
            button.classList.toggle('is-active', active);
            button.setAttribute('aria-pressed', active ? 'true' : 'false');
        });
        panels.forEach(panel => {
            const active = panel.dataset.cardLookupModePanel === mode;
            panel.classList.toggle('hidden', !active);
        });
    };
    buttons.forEach(button => {
        button.addEventListener('click', () => {
            const mode = button.dataset.cardLookupModeButton;
            if (mode) {
                activate(mode);
            }
        });
    });
    activate('single');
};
const initializeCardListMode = () => {
    const form = document.querySelector('form[data-cache-key="card-lookup"]');
    if (!form) {
        return;
    }
    const textArea = form.querySelector('textarea[name="CardList"]');
    const counter = document.querySelector('[data-verify-lines-count]');
    const validationMessage = document.querySelector('[data-verify-lines-error]');
    const submitButtons = form.querySelectorAll('button[type="submit"]');
    const buildLinesButton = form.querySelector('[data-card-lookup-build-lines]');
    const addLineButton = form.querySelector('[data-card-lookup-add-line]');
    const lineEditor = form.querySelector('[data-card-lookup-line-editor]');
    if (!textArea || !counter || !validationMessage) {
        return;
    }
    const updateUi = () => {
        const lineCount = countNonEmptyLines(textArea.value);
        const overLimit = lineCount > 100;
        counter.textContent = `${lineCount}/100 lines`;
        validationMessage.classList.toggle('hidden', !overLimit);
        validationMessage.textContent = overLimit
            ? 'Card Lookup accepts up to 100 non-empty lines per submission.'
            : '';
        textArea.setCustomValidity(overLimit ? 'Card Lookup accepts up to 100 non-empty lines per submission.' : '');
        submitButtons.forEach(button => {
            button.disabled = overLimit;
        });
    };
    const syncTextareaFromEditor = () => {
        if (!lineEditor) {
            return;
        }
        const lines = Array.from(lineEditor.querySelectorAll('[data-card-lookup-line]'))
            .map(row => {
            var _a, _b, _c, _d;
            const quantity = (_b = (_a = row.querySelector('[data-card-lookup-quantity]')) === null || _a === void 0 ? void 0 : _a.value) !== null && _b !== void 0 ? _b : '';
            const cardName = (_d = (_c = row.querySelector('[data-card-lookup-name]')) === null || _c === void 0 ? void 0 : _c.value) !== null && _d !== void 0 ? _d : '';
            return buildLookupLine(quantity, cardName);
        })
            .filter(line => line.length > 0);
        textArea.value = lines.join('\n');
        updateUi();
    };
    const createLookupLineRow = (line) => {
        const row = document.createElement('div');
        row.className = 'card-lookup-line-row';
        row.dataset.cardLookupLine = 'true';
        const quantityInput = document.createElement('input');
        quantityInput.type = 'text';
        quantityInput.inputMode = 'numeric';
        quantityInput.placeholder = 'Qty';
        quantityInput.value = line.quantity;
        quantityInput.dataset.cardLookupQuantity = 'true';
        quantityInput.className = 'card-lookup-line-row__quantity';
        const cardInputShell = document.createElement('div');
        cardInputShell.className = 'autocomplete-anchor card-lookup-line-row__name-shell';
        const cardInput = document.createElement('input');
        cardInput.type = 'text';
        cardInput.placeholder = 'Card name';
        cardInput.value = line.cardName;
        cardInput.dataset.cardLookupName = 'true';
        cardInput.className = 'card-lookup-line-row__name';
        cardInputShell.appendChild(cardInput);
        const suggestionPanel = createLookupSuggestionPanel(cardInputShell);
        attachLookaheadInput(cardInput, suggestionPanel, 2, name => {
            cardInput.value = name;
            syncTextareaFromEditor();
        });
        const removeButton = document.createElement('button');
        removeButton.type = 'button';
        removeButton.className = 'clear-cache-button card-lookup-line-row__remove';
        removeButton.textContent = 'Remove';
        removeButton.addEventListener('click', () => {
            row.remove();
            syncTextareaFromEditor();
            if (lineEditor && lineEditor.querySelectorAll('[data-card-lookup-line]').length === 0) {
                addLineButton === null || addLineButton === void 0 ? void 0 : addLineButton.classList.remove('hidden');
            }
        });
        quantityInput.addEventListener('input', syncTextareaFromEditor);
        cardInput.addEventListener('input', syncTextareaFromEditor);
        cardInput.addEventListener('change', syncTextareaFromEditor);
        row.append(quantityInput, cardInputShell, removeButton);
        return row;
    };
    const rebuildLineEditor = () => {
        if (!lineEditor) {
            return;
        }
        const lines = textArea.value
            .split(/\r?\n/)
            .map(line => line.trim())
            .filter(line => line.length > 0)
            .map(parseLookupLine);
        lineEditor.replaceChildren(...(lines.length > 0 ? lines : [{ quantity: '', cardName: '' }]).map(createLookupLineRow));
        lineEditor.classList.remove('hidden');
        addLineButton === null || addLineButton === void 0 ? void 0 : addLineButton.classList.remove('hidden');
    };
    textArea.addEventListener('input', updateUi);
    buildLinesButton === null || buildLinesButton === void 0 ? void 0 : buildLinesButton.addEventListener('click', rebuildLineEditor);
    addLineButton === null || addLineButton === void 0 ? void 0 : addLineButton.addEventListener('click', () => {
        if (!lineEditor) {
            return;
        }
        lineEditor.classList.remove('hidden');
        lineEditor.appendChild(createLookupLineRow({ quantity: '', cardName: '' }));
        addLineButton.classList.remove('hidden');
    });
    form.addEventListener('submit', () => {
        window.setTimeout(() => {
            var _a;
            (_a = window.hideBusyIndicator) === null || _a === void 0 ? void 0 : _a.call(window);
        }, 400);
    });
    if (textArea.value.trim().length > 0) {
        rebuildLineEditor();
    }
    updateUi();
};
const initializeCardLookupForm = () => {
    initializeModePicker();
    initializeSingleCardMode();
    initializeCardListMode();
};
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initializeCardLookupForm);
}
else {
    initializeCardLookupForm();
}
