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
const initializeCardLookupForm = () => {
    const form = document.querySelector('form[action="/card-lookup"]');
    if (!form) {
        return;
    }
    const textArea = form.querySelector('textarea[name="CardList"]');
    const counter = document.querySelector('[data-verify-lines-count]');
    const validationMessage = document.querySelector('[data-verify-lines-error]');
    const submitButtons = form.querySelectorAll('button[type="submit"]');
    const downloadButton = form.querySelector('button[formaction="/card-lookup/download"]');
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
    const attachLookupSearch = (input, panel) => {
        const fetchSuggestions = async () => {
            const query = input.value.trim();
            if (query.length < 2) {
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
                        syncTextareaFromEditor();
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
        attachLookupSearch(cardInput, suggestionPanel);
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
    downloadButton === null || downloadButton === void 0 ? void 0 : downloadButton.addEventListener('click', () => {
        window.setTimeout(() => {
            var _a;
            (_a = window.hideBusyIndicator) === null || _a === void 0 ? void 0 : _a.call(window);
        }, 300);
    });
    if (textArea.value.trim().length > 0) {
        rebuildLineEditor();
    }
    updateUi();
};
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initializeCardLookupForm);
}
else {
    initializeCardLookupForm();
}
