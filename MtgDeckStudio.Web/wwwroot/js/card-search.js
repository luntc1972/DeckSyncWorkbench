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
const renderCardSuggestions = (list, datalist) => {
    datalist.innerHTML = '';
    list.forEach(name => {
        const option = document.createElement('option');
        option.value = name;
        datalist.appendChild(option);
    });
};
const attachCardSearch = () => {
    const input = document.querySelector('input[name="CardName"]');
    const datalist = document.getElementById('card-suggestions');
    if (!input || !datalist) {
        return;
    }
    const fetchSuggestions = async () => {
        var _a, _b;
        const query = input.value.trim();
        if (query.length < 2) {
            datalist.innerHTML = '';
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
                datalist.innerHTML = '';
                setCardSearchError((_b = (_a = payload === null || payload === void 0 ? void 0 : payload.message) !== null && _a !== void 0 ? _a : payload === null || payload === void 0 ? void 0 : payload.Message) !== null && _b !== void 0 ? _b : 'Scryfall could not be reached right now. Try again shortly.');
                return;
            }
            const names = await response.json();
            renderCardSuggestions(names, datalist);
            setCardSearchError();
        }
        catch (error) {
            datalist.innerHTML = '';
            setCardSearchError(error instanceof Error ? error.message : 'Scryfall could not be reached right now. Try again shortly.');
            console.error('Failed to fetch card suggestions', error);
        }
    };
    const debounced = debounceCardSearch(fetchSuggestions, 250);
    input.addEventListener('input', debounced);
};
document.addEventListener('DOMContentLoaded', attachCardSearch);
