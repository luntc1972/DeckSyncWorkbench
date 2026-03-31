"use strict";
const debounce = (fn, delay) => {
    let timer;
    return () => {
        if (timer !== undefined) {
            window.clearTimeout(timer);
        }
        timer = window.setTimeout(fn, delay);
    };
};
const ensureCommanderAutocompleteAnchor = (input) => {
    const parent = input.parentElement;
    if (!parent) {
        throw new Error('Commander autocomplete input is missing a parent element.');
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
const getOrCreateCommanderSuggestionPanel = (input) => {
    const anchor = ensureCommanderAutocompleteAnchor(input);
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
const hideCommanderSuggestionPanel = (panel) => {
    panel.classList.add('hidden');
    panel.replaceChildren();
};
const renderSuggestions = (list, input, panel) => {
    panel.replaceChildren();
    if (list.length === 0) {
        hideCommanderSuggestionPanel(panel);
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
            hideCommanderSuggestionPanel(panel);
            input.dispatchEvent(new Event('change', { bubbles: true }));
        });
        panel.appendChild(option);
    });
    panel.classList.remove('hidden');
};
const setCommanderSearchError = (message) => {
    const panel = document.querySelector('[data-api-panel="commander-search-error"]');
    const text = document.querySelector('[data-api-field="commander-search-error-text"]');
    if (!panel || !text) {
        return;
    }
    text.textContent = message !== null && message !== void 0 ? message : '';
    panel.classList.toggle('hidden', !message);
};
const attachCommanderSearch = () => {
    const input = document.getElementById('commander-search-input');
    if (!input) {
        return;
    }
    const panel = getOrCreateCommanderSuggestionPanel(input);
    const fetchSuggestions = async () => {
        var _a, _b;
        const query = input.value.trim();
        if (query.length < 2) {
            hideCommanderSuggestionPanel(panel);
            setCommanderSearchError();
            return;
        }
        try {
            const response = await fetch(`/commander-categories/search?query=${encodeURIComponent(query)}`);
            if (!response.ok) {
                let payload = null;
                try {
                    payload = await response.json();
                }
                catch (_c) {
                    payload = null;
                }
                hideCommanderSuggestionPanel(panel);
                setCommanderSearchError((_b = (_a = payload === null || payload === void 0 ? void 0 : payload.message) !== null && _a !== void 0 ? _a : payload === null || payload === void 0 ? void 0 : payload.Message) !== null && _b !== void 0 ? _b : 'Scryfall could not be reached right now. Try again shortly.');
                return;
            }
            const names = await response.json();
            renderSuggestions(names, input, panel);
            setCommanderSearchError();
        }
        catch (error) {
            hideCommanderSuggestionPanel(panel);
            setCommanderSearchError(error instanceof Error ? error.message : 'Scryfall could not be reached right now. Try again shortly.');
            console.error('Failed to fetch commander suggestions', error);
        }
    };
    const debounced = debounce(fetchSuggestions, 350);
    input.addEventListener('input', debounced);
    input.addEventListener('focus', debounced);
    document.addEventListener('click', event => {
        if (!(event.target instanceof Node) || panel.contains(event.target) || input.contains(event.target)) {
            return;
        }
        hideCommanderSuggestionPanel(panel);
    });
};
document.addEventListener('DOMContentLoaded', attachCommanderSearch);
