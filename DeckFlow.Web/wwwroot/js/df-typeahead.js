"use strict";
(() => {
    'use strict';
    var _a;
    const typeaheadWindow = window;
    const debounceCardLookupSearch = (fn, delay) => {
        let timer;
        return () => {
            if (timer !== undefined) {
                window.clearTimeout(timer);
            }
            timer = window.setTimeout(fn, delay);
        };
    };
    const hideLookupSuggestionPanel = (panel) => {
        panel.classList.add('hidden');
        panel.replaceChildren();
    };
    const getErrorMessage = async (response) => {
        var _a, _b;
        try {
            const payload = await response.json();
            return (_b = (_a = payload.message) !== null && _a !== void 0 ? _a : payload.Message) !== null && _b !== void 0 ? _b : 'Scryfall could not be reached right now. Try again shortly.';
        }
        catch (_c) {
            return 'Scryfall could not be reached right now. Try again shortly.';
        }
    };
    const createTypeaheadPanel = (anchor) => {
        const panel = document.createElement('div');
        panel.className = 'autocomplete-panel hidden';
        panel.setAttribute('role', 'listbox');
        anchor.appendChild(panel);
        return panel;
    };
    const attachTypeahead = (input, panel, minChars, onPick, options) => {
        var _a, _b;
        const endpoint = (_a = options === null || options === void 0 ? void 0 : options.endpoint) !== null && _a !== void 0 ? _a : '/suggest-categories/card-search';
        const debounceMs = (_b = options === null || options === void 0 ? void 0 : options.debounceMs) !== null && _b !== void 0 ? _b : 250;
        const onError = options === null || options === void 0 ? void 0 : options.onError;
        const fetchSuggestions = async () => {
            const query = input.value.trim();
            if (query.length < minChars) {
                hideLookupSuggestionPanel(panel);
                onError === null || onError === void 0 ? void 0 : onError(undefined);
                return;
            }
            try {
                const response = await fetch(`${endpoint}?query=${encodeURIComponent(query)}`);
                if (!response.ok) {
                    hideLookupSuggestionPanel(panel);
                    onError === null || onError === void 0 ? void 0 : onError(await getErrorMessage(response));
                    return;
                }
                const names = await response.json();
                onError === null || onError === void 0 ? void 0 : onError(undefined);
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
                onError === null || onError === void 0 ? void 0 : onError('Scryfall could not be reached right now. Try again shortly.');
            }
        };
        const debounced = debounceCardLookupSearch(fetchSuggestions, debounceMs);
        input.addEventListener('input', debounced);
        input.addEventListener('focus', debounced);
        document.addEventListener('click', event => {
            if (!(event.target instanceof Node) || panel.contains(event.target) || input.contains(event.target)) {
                return;
            }
            hideLookupSuggestionPanel(panel);
        });
    };
    typeaheadWindow.DeckFlow = (_a = typeaheadWindow.DeckFlow) !== null && _a !== void 0 ? _a : {};
    typeaheadWindow.DeckFlow.attachTypeahead = attachTypeahead;
    typeaheadWindow.DeckFlow.createTypeaheadPanel = createTypeaheadPanel;
})();
