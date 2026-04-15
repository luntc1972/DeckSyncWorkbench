"use strict";
(() => {
    'use strict';
    const formStateStoragePrefix = 'decksync-form-state-';
    const formResultStoragePrefix = 'decksync-form-result-';
    const tabNavigationKey = 'decksync-tab-navigation';
    const toggleSuggestionPanel = (selector, visible) => {
        const element = document.querySelector(`[data-api-panel="${selector}"]`);
        if (!element) {
            return;
        }
        element.classList.toggle('hidden', !visible);
    };
    const setFieldText = (field, value) => {
        const element = document.querySelector(`[data-api-field="${field}"]`);
        if (!element) {
            return;
        }
        if (element instanceof HTMLTextAreaElement || element instanceof HTMLInputElement) {
            element.value = value !== null && value !== void 0 ? value : '';
            return;
        }
        element.textContent = value !== null && value !== void 0 ? value : '';
    };
    const createTextCell = (value) => {
        const cell = document.createElement('td');
        cell.textContent = value.toString();
        return cell;
    };
    const handleError = (panel, message) => {
        if (!message) {
            toggleSuggestionPanel(panel, false);
            return;
        }
        setFieldText(panel === 'suggest-error' ? 'suggest-error-text' : 'commander-error-text', message);
        toggleSuggestionPanel(panel, true);
    };
    const resetCardUi = () => {
        toggleSuggestionPanel('suggest-error', false);
        toggleSuggestionPanel('cache-info', false);
        toggleSuggestionPanel('source-summary', false);
        toggleSuggestionPanel('exact', false);
        toggleSuggestionPanel('inferred', false);
        toggleSuggestionPanel('edhrec', false);
        toggleSuggestionPanel('tagger', false);
        toggleSuggestionPanel('no-suggestions', false);
        toggleSuggestionPanel('lookup-hint', false);
        toggleSuggestionPanel('commander-results', false);
    };
    const resetCommanderUi = () => {
        toggleSuggestionPanel('commander-error', false);
        toggleSuggestionPanel('commander-results', false);
        toggleSuggestionPanel('commander-no-results', false);
        toggleSuggestionPanel('commander-hint', false);
    };
    const scrollPanelIntoCenter = (selector) => {
        const element = document.querySelector(selector);
        if (!element) {
            return;
        }
        window.setTimeout(() => {
            element.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }, 100);
    };
    const busyConfigByMode = {
        CachedData: {
            title: 'Finding Categories',
            message: 'Checking the local store and recent Archidekt decks.',
            progress: 'Refreshing cached store|Scanning recent Archidekt decks|Finalizing category matches',
        },
        ReferenceDeck: {
            title: 'Finding Categories',
            message: 'Loading the reference deck and matching categories.',
            progress: 'Loading reference deck|Matching card categories|Finalizing results',
        },
        ScryfallTagger: {
            title: 'Looking Up Tags',
            message: 'Fetching functional tags from Scryfall Tagger.',
            progress: 'Resolving card|Querying Scryfall Tagger|Finalizing tags',
        },
        All: {
            title: 'Finding Categories',
            message: 'Checking all sources: cached store and Scryfall Tagger.',
            progress: 'Refreshing cached store|Querying Scryfall Tagger|Finalizing results',
        },
    };
    const updateBusyAttributes = (form, mode) => {
        var _a;
        const config = (_a = busyConfigByMode[mode]) !== null && _a !== void 0 ? _a : busyConfigByMode['CachedData'];
        form.setAttribute('data-busy-title', config.title);
        form.setAttribute('data-busy-message', config.message);
        form.setAttribute('data-busy-progress', config.progress);
    };
    const updateSuggestionInputModeUi = () => {
        const modeSelect = document.querySelector('select[name="Mode"]');
        const archidektModeSelect = document.querySelector('select[name="ArchidektInputSource"]');
        if (!modeSelect || !archidektModeSelect) {
            return;
        }
        const mode = modeSelect.value;
        const showReference = mode === 'ReferenceDeck';
        const showUrl = archidektModeSelect.value === 'PublicUrl';
        const showText = archidektModeSelect.value === 'PasteText';
        document.querySelectorAll('.reference-controls').forEach(element => {
            element.classList.toggle('hidden', !showReference);
        });
        document.querySelectorAll('[data-suggest-panel="url"]').forEach(element => {
            element.classList.toggle('hidden', !showReference || !showUrl);
        });
        document.querySelectorAll('[data-suggest-panel="text"]').forEach(element => {
            element.classList.toggle('hidden', !showReference || !showText);
        });
        const form = modeSelect.closest('form');
        if (form) {
            updateBusyAttributes(form, mode);
        }
    };
    const handleCardResponse = (form, response) => {
        var _a, _b;
        resetCardUi();
        handleError('suggest-error', null);
        const modeSelect = form.querySelector('select[name="Mode"]');
        const mode = (_a = modeSelect === null || modeSelect === void 0 ? void 0 : modeSelect.value) !== null && _a !== void 0 ? _a : 'CachedData';
        const showAll = mode === 'All';
        const hintText = response.noSuggestionsFound && response.noSuggestionsMessage
            ? response.noSuggestionsMessage
            : `The cached store tracks Archidekt categories that appear on decks containing ${response.cardName}. Click Suggest to keep scanning public decks for another 20 seconds so those categories can populate.`;
        setFieldText('lookup-hint-text', hintText);
        toggleSuggestionPanel('lookup-hint', true);
        const showCached = mode === 'CachedData' || showAll;
        if (showCached && response.cardDeckTotals.totalDeckCount > 0) {
            setFieldText('cache-info-count', response.cardDeckTotals.totalDeckCount.toString());
            setFieldText('cache-info-text', `The cached store currently contains ${response.cardDeckTotals.totalDeckCount} deck(s) featuring ${response.cardName}.`);
            toggleSuggestionPanel('cache-info', true);
        }
        if (response.suggestionSourceSummary) {
            setFieldText('source-summary-text', response.suggestionSourceSummary);
            toggleSuggestionPanel('source-summary', true);
        }
        const showExact = mode === 'ReferenceDeck' && response.hasExactCategories;
        toggleSuggestionPanel('exact', showExact);
        setFieldText('exact-context', response.exactSuggestionContextText);
        setFieldText('exact-text', response.exactCategoriesText);
        const showInferred = showCached && response.hasInferredCategories;
        toggleSuggestionPanel('inferred', showInferred);
        setFieldText('inferred-context', response.inferredSuggestionContextText);
        setFieldText('inferred-text', response.inferredCategoriesText);
        setFieldText('cache-info-detail', showCached && response.cardDeckTotals.totalDeckCount > 0
            ? `${response.cardDeckTotals.totalDeckCount} deck(s) in the cache include ${response.cardName}.`
            : '');
        const showEdhrec = showCached && response.hasEdhrecCategories;
        toggleSuggestionPanel('edhrec', showEdhrec);
        setFieldText('edhrec-context', response.edhrecSuggestionContextText);
        setFieldText('edhrec-text', response.edhrecCategoriesText);
        const showTagger = (mode === 'ScryfallTagger' || showAll) && response.hasTaggerCategories;
        toggleSuggestionPanel('tagger', showTagger);
        setFieldText('tagger-context', response.taggerSuggestionContextText);
        setFieldText('tagger-text', response.taggerCategoriesText);
        toggleSuggestionPanel('no-suggestions', response.noSuggestionsFound);
        if (response.noSuggestionsFound) {
            setFieldText('no-suggestions-text', (_b = response.noSuggestionsMessage) !== null && _b !== void 0 ? _b : `No category suggestions were found for ${response.cardName}.`);
            scrollPanelIntoCenter('[data-api-panel="no-suggestions"]');
            return;
        }
        if (showTagger) {
            scrollPanelIntoCenter('[data-api-panel="tagger"]');
            return;
        }
        if (showInferred) {
            scrollPanelIntoCenter('#cached-store-matches');
            return;
        }
        if (showExact) {
            scrollPanelIntoCenter('[data-api-panel="exact"]');
            return;
        }
        if (showEdhrec) {
            scrollPanelIntoCenter('[data-api-panel="edhrec"]');
        }
    };
    const handleCommanderResponse = (response) => {
        var _a;
        resetCommanderUi();
        const hasResults = response.summaries.length > 0;
        const hintText = hasResults
            ? `Commander categories for ${response.commanderName} were sourced from the cached store.`
            : `No commander categories for ${response.commanderName} have been observed in the cached data yet. Run Show Categories again to refresh the cache.`;
        setFieldText('commander-hint-text', hintText);
        toggleSuggestionPanel('commander-hint', true);
        toggleSuggestionPanel('commander-results', hasResults);
        toggleSuggestionPanel('commander-no-results', !hasResults);
        if (!hasResults) {
            setFieldText('commander-no-results-text', (_a = response.noResultsMessage) !== null && _a !== void 0 ? _a : hintText);
            scrollPanelIntoCenter('[data-api-panel="commander-no-results"]');
            return;
        }
        setFieldText('commander-cards-count', `${response.cardRowCount} cards contributed to this summary.`);
        setFieldText('commander-deck-count', `Derived from ${response.harvestedDeckCount} cached decks with ${response.categoryCount} distinct categories.`);
        setFieldText('commander-card-deck-count', `${response.commanderName} appears in ${response.cardDeckTotals.totalDeckCount} cached commander deck(s).`);
        setFieldText('commander-card-count', response.cardDeckTotals.totalDeckCount.toString());
        const body = document.querySelector('[data-api-field="commander-summary-body"]');
        if (!body) {
            return;
        }
        body.innerHTML = '';
        response.summaries.forEach(summary => {
            const row = document.createElement('tr');
            row.appendChild(createTextCell(summary.category));
            row.appendChild(createTextCell(summary.count));
            row.appendChild(createTextCell(summary.deckCount));
            body.appendChild(row);
        });
        scrollPanelIntoCenter('#commander-results-anchor');
    };
    const readRequestData = (form) => {
        const data = {};
        form.querySelectorAll('[name]').forEach(element => {
            if (!element.name) {
                return;
            }
            if (element instanceof HTMLInputElement && (element.type === 'checkbox' || element.type === 'radio') && !element.checked) {
                return;
            }
            data[element.name] = element.value;
        });
        return data;
    };
    const restoreFormState = (form) => {
        const key = form.dataset.cacheKey;
        if (!key) {
            return;
        }
        const payload = sessionStorage.getItem(`${formStateStoragePrefix}${key}`);
        if (!payload) {
            return;
        }
        try {
            const values = JSON.parse(payload);
            Object.entries(values).forEach(([name, value]) => {
                const element = form.querySelector(`[name="${name}"]`);
                if (!element) {
                    return;
                }
                element.value = value;
            });
        }
        catch (error) {
            console.warn('Unable to restore cached form state', error);
            sessionStorage.removeItem(`${formStateStoragePrefix}${key}`);
        }
    };
    const persistFormState = (form) => {
        const key = form.dataset.cacheKey;
        if (!key) {
            return;
        }
        sessionStorage.setItem(`${formStateStoragePrefix}${key}`, JSON.stringify(readRequestData(form)));
    };
    const persistResultState = (form, envelope) => {
        const key = form.dataset.cacheKey;
        if (!key) {
            return;
        }
        sessionStorage.setItem(`${formResultStoragePrefix}${key}`, JSON.stringify(envelope));
    };
    const restoreResultState = (form) => {
        const key = form.dataset.cacheKey;
        if (!key) {
            return;
        }
        const payload = sessionStorage.getItem(`${formResultStoragePrefix}${key}`);
        if (!payload) {
            return;
        }
        try {
            const envelope = JSON.parse(payload);
            if (envelope.type === 'card') {
                handleCardResponse(form, envelope.payload);
                return;
            }
            handleCommanderResponse(envelope.payload);
        }
        catch (error) {
            console.warn('Unable to restore cached suggestion result', error);
            sessionStorage.removeItem(`${formResultStoragePrefix}${key}`);
        }
    };
    const clearStoredState = (form) => {
        const key = form.dataset.cacheKey;
        if (!key) {
            return;
        }
        sessionStorage.removeItem(`${formStateStoragePrefix}${key}`);
        sessionStorage.removeItem(`${formResultStoragePrefix}${key}`);
    };
    const submitSuggestion = async (form) => {
        var _a, _b, _c, _d, _e, _f;
        const endpoint = form.dataset.suggestionApi;
        if (!endpoint) {
            return;
        }
        const type = form.dataset.suggestionsType;
        try {
            const response = await fetch(endpoint, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(readRequestData(form))
            });
            if (!response.ok) {
                let payload = null;
                try {
                    payload = await response.json();
                }
                catch (_g) {
                    payload = null;
                }
                handleError(type === 'commander' ? 'commander-error' : 'suggest-error', (_b = (_a = payload === null || payload === void 0 ? void 0 : payload.message) !== null && _a !== void 0 ? _a : payload === null || payload === void 0 ? void 0 : payload.Message) !== null && _b !== void 0 ? _b : 'Unable to fetch suggestions.');
                (_c = window.hideBusyIndicator) === null || _c === void 0 ? void 0 : _c.call(window);
                return;
            }
            if (type === 'card') {
                const payload = await response.json();
                persistResultState(form, { type: 'card', payload });
                handleCardResponse(form, payload);
                (_d = window.hideBusyIndicator) === null || _d === void 0 ? void 0 : _d.call(window);
                return;
            }
            if (type === 'commander') {
                const payload = await response.json();
                persistResultState(form, { type: 'commander', payload });
                handleCommanderResponse(payload);
                (_e = window.hideBusyIndicator) === null || _e === void 0 ? void 0 : _e.call(window);
            }
        }
        catch (error) {
            const message = error instanceof Error ? error.message : 'Unable to fetch suggestions.';
            handleError(type === 'commander' ? 'commander-error' : 'suggest-error', message);
            (_f = window.hideBusyIndicator) === null || _f === void 0 ? void 0 : _f.call(window);
        }
    };
    const attachSuggestionHandlers = () => {
        document.querySelectorAll('form[data-suggestions-type]').forEach(form => {
            const restoredFromTabs = sessionStorage.getItem(tabNavigationKey) === '1';
            if (restoredFromTabs) {
                restoreFormState(form);
                restoreResultState(form);
            }
            else {
                clearStoredState(form);
            }
            sessionStorage.removeItem(tabNavigationKey);
            updateSuggestionInputModeUi();
            form.addEventListener('submit', event => {
                event.preventDefault();
                persistFormState(form);
                submitSuggestion(form);
            });
            form.addEventListener('input', () => persistFormState(form));
            form.addEventListener('change', event => {
                persistFormState(form);
                const target = event.target;
                if (target instanceof HTMLSelectElement && (target.name === 'Mode' || target.name === 'ArchidektInputSource')) {
                    updateSuggestionInputModeUi();
                }
            });
            const clearButton = form.querySelector('[data-clear-cache]');
            if (clearButton) {
                clearButton.addEventListener('click', () => {
                    form.reset();
                    clearStoredState(form);
                    if (form.dataset.suggestionsType === 'card') {
                        resetCardUi();
                    }
                    else {
                        resetCommanderUi();
                    }
                    updateSuggestionInputModeUi();
                });
            }
            const retryButton = form.querySelector('[data-api-action="retry"]');
            if (retryButton) {
                retryButton.addEventListener('click', () => {
                    persistFormState(form);
                    form.requestSubmit();
                });
            }
            document.querySelectorAll('.tool-nav__link').forEach(link => {
                link.addEventListener('click', () => {
                    persistFormState(form);
                    sessionStorage.setItem(tabNavigationKey, '1');
                });
            });
        });
    };
    document.addEventListener('DOMContentLoaded', attachSuggestionHandlers);
})();
