"use strict";
const setCardSearchError = (message) => {
    const panel = document.querySelector('[data-api-panel="card-search-error"]');
    const text = document.querySelector('[data-api-field="card-search-error-text"]');
    if (!panel || !text) {
        return;
    }
    text.textContent = message !== null && message !== void 0 ? message : '';
    panel.classList.toggle('hidden', !message);
};
const ensureCardSearchAutocompleteAnchor = (input) => {
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
const attachCardSearch = () => {
    var _a, _b, _c, _d;
    const input = document.querySelector('input[name="CardName"]');
    if (!input) {
        return;
    }
    const anchor = ensureCardSearchAutocompleteAnchor(input);
    const deckFlowWindow = window;
    const panel = (_b = (_a = deckFlowWindow.DeckFlow) === null || _a === void 0 ? void 0 : _a.createTypeaheadPanel) === null || _b === void 0 ? void 0 : _b.call(_a, anchor);
    if (!panel) {
        return;
    }
    (_d = (_c = deckFlowWindow.DeckFlow) === null || _c === void 0 ? void 0 : _c.attachTypeahead) === null || _d === void 0 ? void 0 : _d.call(_c, input, panel, 2, () => {
        input.dispatchEvent(new Event('change', { bubbles: true }));
    }, {
        onError: setCardSearchError,
    });
};
document.addEventListener('DOMContentLoaded', attachCardSearch);
