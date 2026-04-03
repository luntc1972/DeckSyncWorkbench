((): void => {
  'use strict';

  type SuggestionForm = HTMLFormElement & {
    dataset: DOMStringMap & {
      cacheKey?: string;
      suggestionApi?: string;
      suggestionsType?: string;
    };
  };

  type SuggestionResponseEnvelope =
    | { type: 'card'; payload: CardSuggestionResponse }
    | { type: 'commander'; payload: CommanderSuggestionResponse };

  interface Window {
    hideBusyIndicator?: () => void;
  }

  const formStateStoragePrefix = 'decksync-form-state-';
  const formResultStoragePrefix = 'decksync-form-result-';
  const tabNavigationKey = 'decksync-tab-navigation';

  type CardSuggestionResponse = {
    cardName: string;
    exactCategoriesText: string;
    exactSuggestionContextText: string;
    inferredCategoriesText: string;
    inferredSuggestionContextText: string;
    edhrecCategoriesText: string;
    edhrecSuggestionContextText: string;
    hasExactCategories: boolean;
    hasInferredCategories: boolean;
    hasEdhrecCategories: boolean;
    suggestionSourceSummary?: string | null;
    noSuggestionsFound: boolean;
    noSuggestionsMessage?: string | null;
    cardDeckTotals: {
      totalDeckCount: number;
    };
  };

  type CommanderSuggestionResponse = {
    commanderName: string;
    cardRowCount: number;
    categoryCount: number;
    harvestedDeckCount: number;
    noResultsMessage?: string | null;
    cardDeckTotals: {
      totalDeckCount: number;
    };
    summaries: Array<{
      category: string;
      count: number;
      deckCount: number;
    }>;
  };

  const toggleSuggestionPanel = (selector: string, visible: boolean): void => {
    const element = document.querySelector<HTMLElement>(`[data-api-panel="${selector}"]`);
    if (!element) {
      return;
    }

    element.classList.toggle('hidden', !visible);
  };

  const setFieldText = (field: string, value?: string | null): void => {
    const element = document.querySelector<HTMLElement>(`[data-api-field="${field}"]`);
    if (!element) {
      return;
    }

    if (element instanceof HTMLTextAreaElement || element instanceof HTMLInputElement) {
      element.value = value ?? '';
      return;
    }

    element.textContent = value ?? '';
  };

  const createTextCell = (value: string | number): HTMLTableCellElement => {
    const cell = document.createElement('td');
    cell.textContent = value.toString();
    return cell;
  };

  const handleError = (panel: 'suggest-error' | 'commander-error', message?: string | null): void => {
    if (!message) {
      toggleSuggestionPanel(panel, false);
      return;
    }

    setFieldText(panel === 'suggest-error' ? 'suggest-error-text' : 'commander-error-text', message);
    toggleSuggestionPanel(panel, true);
  };

  const resetCardUi = (): void => {
    toggleSuggestionPanel('suggest-error', false);
    toggleSuggestionPanel('cache-info', false);
    toggleSuggestionPanel('source-summary', false);
    toggleSuggestionPanel('exact', false);
    toggleSuggestionPanel('inferred', false);
    toggleSuggestionPanel('edhrec', false);
    toggleSuggestionPanel('no-suggestions', false);
    toggleSuggestionPanel('lookup-hint', false);
    toggleSuggestionPanel('commander-results', false);
  };

  const resetCommanderUi = (): void => {
    toggleSuggestionPanel('commander-error', false);
    toggleSuggestionPanel('commander-results', false);
    toggleSuggestionPanel('commander-no-results', false);
    toggleSuggestionPanel('commander-hint', false);
  };

  const scrollPanelIntoCenter = (selector: string): void => {
    const element = document.querySelector<HTMLElement>(selector);
    if (!element) {
      return;
    }

    window.setTimeout(() => {
      element.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }, 100);
  };

  const updateSuggestionInputModeUi = (): void => {
    const modeSelect = document.querySelector<HTMLSelectElement>('select[name="Mode"]');
    const archidektModeSelect = document.querySelector<HTMLSelectElement>('select[name="ArchidektInputSource"]');
    if (!modeSelect || !archidektModeSelect) {
      return;
    }

    const useReferenceDeck = modeSelect.value === 'ReferenceDeck';
    const showUrl = archidektModeSelect.value === 'PublicUrl';
    const showText = archidektModeSelect.value === 'PasteText';

    document.querySelectorAll<HTMLElement>('.reference-controls').forEach(element => {
      element.classList.toggle('hidden', !useReferenceDeck);
    });

    document.querySelectorAll<HTMLElement>('[data-suggest-panel="url"]').forEach(element => {
      element.classList.toggle('hidden', !useReferenceDeck || !showUrl);
    });

    document.querySelectorAll<HTMLElement>('[data-suggest-panel="text"]').forEach(element => {
      element.classList.toggle('hidden', !useReferenceDeck || !showText);
    });
  };

  const handleCardResponse = (form: SuggestionForm, response: CardSuggestionResponse): void => {
    resetCardUi();
    handleError('suggest-error', null);

    const hintText = response.noSuggestionsFound && response.noSuggestionsMessage
      ? response.noSuggestionsMessage
      : `The cached store tracks Archidekt categories that appear on decks containing ${response.cardName}. Click Suggest to keep scanning public decks for another 20 seconds so those categories can populate.`;

    setFieldText('lookup-hint-text', hintText);
    toggleSuggestionPanel('lookup-hint', true);

    if (response.cardDeckTotals.totalDeckCount > 0) {
      setFieldText('cache-info-count', response.cardDeckTotals.totalDeckCount.toString());
      setFieldText('cache-info-text', `The cached store currently contains ${response.cardDeckTotals.totalDeckCount} deck(s) featuring ${response.cardName}.`);
      toggleSuggestionPanel('cache-info', true);
    }

    if (response.suggestionSourceSummary) {
      setFieldText('source-summary-text', response.suggestionSourceSummary);
      toggleSuggestionPanel('source-summary', true);
    }

    const modeSelect = form.querySelector<HTMLSelectElement>('select[name="Mode"]');
    const isReferenceMode = modeSelect?.value === 'ReferenceDeck';
    toggleSuggestionPanel('exact', response.hasExactCategories && isReferenceMode);
    setFieldText('exact-context', response.exactSuggestionContextText);
    setFieldText('exact-text', response.exactCategoriesText);

    toggleSuggestionPanel('inferred', true);
    setFieldText('inferred-context', response.inferredSuggestionContextText);
    setFieldText('inferred-text', response.inferredCategoriesText);
    setFieldText(
      'cache-info-detail',
      response.cardDeckTotals.totalDeckCount > 0
        ? `${response.cardDeckTotals.totalDeckCount} deck(s) in the cache include ${response.cardName}.`
        : ''
    );

    toggleSuggestionPanel('edhrec', response.hasEdhrecCategories);
    setFieldText('edhrec-context', response.edhrecSuggestionContextText);
    setFieldText('edhrec-text', response.edhrecCategoriesText);

    toggleSuggestionPanel('no-suggestions', response.noSuggestionsFound);
    if (response.noSuggestionsFound) {
      setFieldText('no-suggestions-text', response.noSuggestionsMessage ?? `No category suggestions were found for ${response.cardName}.`);
      scrollPanelIntoCenter('[data-api-panel="no-suggestions"]');
      return;
    }

    if (response.hasInferredCategories) {
      scrollPanelIntoCenter('#cached-store-matches');
      return;
    }

    if (response.hasExactCategories) {
      scrollPanelIntoCenter('[data-api-panel="exact"]');
      return;
    }

    if (response.hasEdhrecCategories) {
      scrollPanelIntoCenter('[data-api-panel="edhrec"]');
    }
  };

  const handleCommanderResponse = (response: CommanderSuggestionResponse): void => {
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
      setFieldText('commander-no-results-text', response.noResultsMessage ?? hintText);
      scrollPanelIntoCenter('[data-api-panel="commander-no-results"]');
      return;
    }

    setFieldText('commander-cards-count', `${response.cardRowCount} cards contributed to this summary.`);
    setFieldText('commander-deck-count', `Derived from ${response.harvestedDeckCount} cached decks with ${response.categoryCount} distinct categories.`);
    setFieldText('commander-card-deck-count', `${response.commanderName} appears in ${response.cardDeckTotals.totalDeckCount} cached commander deck(s).`);
    setFieldText('commander-card-count', response.cardDeckTotals.totalDeckCount.toString());

    const body = document.querySelector<HTMLElement>('[data-api-field="commander-summary-body"]');
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

  const readRequestData = (form: HTMLFormElement): Record<string, string> => {
    const data: Record<string, string> = {};
    form.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>('[name]').forEach(element => {
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

  const restoreFormState = (form: SuggestionForm): void => {
    const key = form.dataset.cacheKey;
    if (!key) {
      return;
    }

    const payload = sessionStorage.getItem(`${formStateStoragePrefix}${key}`);
    if (!payload) {
      return;
    }

    try {
      const values = JSON.parse(payload) as Record<string, string>;
      Object.entries(values).forEach(([name, value]) => {
        const element = form.querySelector<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(`[name="${name}"]`);
        if (!element) {
          return;
        }

        element.value = value;
      });
    } catch (error) {
      console.warn('Unable to restore cached form state', error);
      sessionStorage.removeItem(`${formStateStoragePrefix}${key}`);
    }
  };

  const persistFormState = (form: SuggestionForm): void => {
    const key = form.dataset.cacheKey;
    if (!key) {
      return;
    }

    sessionStorage.setItem(`${formStateStoragePrefix}${key}`, JSON.stringify(readRequestData(form)));
  };

  const persistResultState = (form: SuggestionForm, envelope: SuggestionResponseEnvelope): void => {
    const key = form.dataset.cacheKey;
    if (!key) {
      return;
    }

    sessionStorage.setItem(`${formResultStoragePrefix}${key}`, JSON.stringify(envelope));
  };

  const restoreResultState = (form: SuggestionForm): void => {
    const key = form.dataset.cacheKey;
    if (!key) {
      return;
    }

    const payload = sessionStorage.getItem(`${formResultStoragePrefix}${key}`);
    if (!payload) {
      return;
    }

    try {
      const envelope = JSON.parse(payload) as SuggestionResponseEnvelope;
      if (envelope.type === 'card') {
        handleCardResponse(form, envelope.payload);
        return;
      }

      handleCommanderResponse(envelope.payload);
    } catch (error) {
      console.warn('Unable to restore cached suggestion result', error);
      sessionStorage.removeItem(`${formResultStoragePrefix}${key}`);
    }
  };

  const clearStoredState = (form: SuggestionForm): void => {
    const key = form.dataset.cacheKey;
    if (!key) {
      return;
    }

    sessionStorage.removeItem(`${formStateStoragePrefix}${key}`);
    sessionStorage.removeItem(`${formResultStoragePrefix}${key}`);
  };

  const submitSuggestion = async (form: SuggestionForm): Promise<void> => {
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
        let payload: { message?: string; Message?: string } | null = null;
        try {
          payload = await response.json() as { message?: string; Message?: string };
        } catch {
          payload = null;
        }

        handleError(type === 'commander' ? 'commander-error' : 'suggest-error', payload?.message ?? payload?.Message ?? 'Unable to fetch suggestions.');
        window.hideBusyIndicator?.();
        return;
      }

      if (type === 'card') {
        const payload = await response.json() as CardSuggestionResponse;
        persistResultState(form, { type: 'card', payload });
        handleCardResponse(form, payload);
        window.hideBusyIndicator?.();
        return;
      }

      if (type === 'commander') {
        const payload = await response.json() as CommanderSuggestionResponse;
        persistResultState(form, { type: 'commander', payload });
        handleCommanderResponse(payload);
        window.hideBusyIndicator?.();
      }
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unable to fetch suggestions.';
      handleError(type === 'commander' ? 'commander-error' : 'suggest-error', message);
      window.hideBusyIndicator?.();
    }
  };

  const attachSuggestionHandlers = (): void => {
    document.querySelectorAll<SuggestionForm>('form[data-suggestions-type]').forEach(form => {
      const restoredFromTabs = sessionStorage.getItem(tabNavigationKey) === '1';
      if (restoredFromTabs) {
        restoreFormState(form);
        restoreResultState(form);
      } else {
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

      const clearButton = form.querySelector<HTMLElement>('[data-clear-cache]');
      if (clearButton) {
        clearButton.addEventListener('click', () => {
          form.reset();
          clearStoredState(form);

          if (form.dataset.suggestionsType === 'card') {
            resetCardUi();
          } else {
            resetCommanderUi();
          }

          updateSuggestionInputModeUi();
        });
      }

      const retryButton = form.querySelector<HTMLElement>('[data-api-action="retry"]');
      if (retryButton) {
        retryButton.addEventListener('click', () => {
          persistFormState(form);
          form.requestSubmit();
        });
      }

      document.querySelectorAll<HTMLAnchorElement>('.tool-nav__link').forEach(link => {
        link.addEventListener('click', () => {
          persistFormState(form);
          sessionStorage.setItem(tabNavigationKey, '1');
        });
      });
    });
  };

  document.addEventListener('DOMContentLoaded', attachSuggestionHandlers);
})();
