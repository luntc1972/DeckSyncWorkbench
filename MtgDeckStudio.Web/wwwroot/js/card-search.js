"use strict";
const debounceCardSearch = (fn, delay) => {
    let timer;
    return () => {
        if (timer !== undefined) {
            window.clearTimeout(timer);
        }
        timer = window.setTimeout(fn, delay);
    };
};
const setCardSearchError = (message) => {
    const panel = document.querySelector('[data-api-panel="card-search-error"]');
    const text = document.querySelector('[data-api-field="card-search-error-text"]');
    if (!panel || !text) {
        return;
    }
    text.textContent = message !== null && message !== void 0 ? message : '';
    panel.classList.toggle('hidden', !message);
};
const ensureAutocompleteAnchor = (input) => {
    const parent = input.parentElement;
    if (!parent) {
        throw new Error('Autocomplete input is missing a parent element.');
    }
    if (parent.classList.contains('autocomplete-anchor')) {
        return parent;
    }
    const anchor = document.createElement('div');
    anchor.className = 'autocomplete-anchor';
    input.insertAdjacentElement('beforebegin', anchor);
    anchor.appendChild(input);
    return anchor;
};
const getOrCreateSuggestionPanel = (input) => {
    const anchor = ensureAutocompleteAnchor(input);
    const existingPanel = anchor.querySelector('.autocomplete-panel');
    if (existingPanel) {
        return existingPanel;
    }
    const panel = document.createElement('div');
    panel.className = 'autocomplete-panel hidden';
    panel.setAttribute('role', 'listbox');
    anchor.appendChild(panel);
    return panel;
};
const hideSuggestionPanel = (panel) => {
    panel.classList.add('hidden');
    panel.replaceChildren();
};
const renderCardSuggestions = (list, input, panel) => {
    panel.replaceChildren();
    if (list.length === 0) {
        hideSuggestionPanel(panel);
        return;
    }
    list.forEach(name => {
        const option = document.createElement('button');
        option.type = 'button';
        option.className = 'autocomplete-option';
        option.textContent = name;
        option.addEventListener('mousedown', event => {
            event.preventDefault();
            input.value = name;
            hideSuggestionPanel(panel);
            input.dispatchEvent(new Event('change', { bubbles: true }));
        });
        panel.appendChild(option);
    });
    panel.classList.remove('hidden');
};
const attachCardSearch = () => {
    const input = document.querySelector('input[name="CardName"]');
    if (!input) {
        return;
    }
    const panel = getOrCreateSuggestionPanel(input);
    const fetchSuggestions = async () => {
        var _a, _b;
        const query = input.value.trim();
        if (query.length < 2) {
            hideSuggestionPanel(panel);
            setCardSearchError();
            return;
        }
        try {
            const response = await fetch(`/suggest-categories/card-search?query=${encodeURIComponent(query)}`);
            if (!response.ok) {
                let payload = null;
                try {
                    payload = await response.json();
                }
                catch (_c) {
                    payload = null;
                }
                hideSuggestionPanel(panel);
                setCardSearchError((_b = (_a = payload === null || payload === void 0 ? void 0 : payload.message) !== null && _a !== void 0 ? _a : payload === null || payload === void 0 ? void 0 : payload.Message) !== null && _b !== void 0 ? _b : 'Scryfall could not be reached right now. Try again shortly.');
                return;
            }
            const names = await response.json();
            renderCardSuggestions(names, input, panel);
            setCardSearchError();
        }
        catch (error) {
            hideSuggestionPanel(panel);
            setCardSearchError(error instanceof Error ? error.message : 'Scryfall could not be reached right now. Try again shortly.');
            console.error('Failed to fetch card suggestions', error);
        }
    };
    const debounced = debounceCardSearch(fetchSuggestions, 250);
    input.addEventListener('input', debounced);
    input.addEventListener('focus', debounced);
    document.addEventListener('click', event => {
        if (!(event.target instanceof Node) || panel.contains(event.target) || input.contains(event.target)) {
            return;
        }
        hideSuggestionPanel(panel);
    });
};
document.addEventListener('DOMContentLoaded', attachCardSearch);
