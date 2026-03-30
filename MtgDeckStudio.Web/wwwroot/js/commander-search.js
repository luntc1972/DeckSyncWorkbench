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
const renderSuggestions = (list, datalist) => {
    datalist.innerHTML = '';
    list.forEach(name => {
        const option = document.createElement('option');
        option.value = name;
        datalist.appendChild(option);
    });
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
    const datalist = document.getElementById('commander-suggestions');
    if (!input || !datalist) {
        return;
    }
    const fetchSuggestions = async () => {
        var _a, _b;
        const query = input.value.trim();
        if (query.length < 2) {
            datalist.innerHTML = '';
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
                datalist.innerHTML = '';
                setCommanderSearchError((_b = (_a = payload === null || payload === void 0 ? void 0 : payload.message) !== null && _a !== void 0 ? _a : payload === null || payload === void 0 ? void 0 : payload.Message) !== null && _b !== void 0 ? _b : 'Scryfall could not be reached right now. Try again shortly.');
                return;
            }
            const names = await response.json();
            renderSuggestions(names, datalist);
            setCommanderSearchError();
        }
        catch (error) {
            datalist.innerHTML = '';
            setCommanderSearchError(error instanceof Error ? error.message : 'Scryfall could not be reached right now. Try again shortly.');
            console.error('Failed to fetch commander suggestions', error);
        }
    };
    const debounced = debounce(fetchSuggestions, 350);
    input.addEventListener('input', debounced);
};
document.addEventListener('DOMContentLoaded', attachCommanderSearch);
