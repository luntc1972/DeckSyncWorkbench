((): void => {
  'use strict';

  type DeckFlowNamespace = {
    attachTypeahead?: (
      input: HTMLInputElement,
      panel: HTMLDivElement,
      minChars: number,
      onPick: (name: string) => void,
      options?: {
        endpoint?: string;
        debounceMs?: number;
        onError?: (message?: string) => void;
      }
    ) => void;
    createTypeaheadPanel?: (anchor: HTMLElement) => HTMLDivElement;
  };

  type TypeaheadWindow = Window & {
    DeckFlow?: DeckFlowNamespace;
  };

  const typeaheadWindow = window as TypeaheadWindow;

  const debounceCardLookupSearch = (fn: () => void, delay: number) => {
    let timer: number | undefined;
    return () => {
      if (timer !== undefined) {
        window.clearTimeout(timer);
      }

      timer = window.setTimeout(fn, delay);
    };
  };

  const hideLookupSuggestionPanel = (panel: HTMLElement): void => {
    panel.classList.add('hidden');
    panel.replaceChildren();
  };

  const getErrorMessage = async (response: Response): Promise<string> => {
    try {
      const payload = await response.json() as { message?: string; Message?: string };
      return payload.message ?? payload.Message ?? 'Scryfall could not be reached right now. Try again shortly.';
    } catch {
      return 'Scryfall could not be reached right now. Try again shortly.';
    }
  };

  const createTypeaheadPanel = (anchor: HTMLElement): HTMLDivElement => {
    const panel = document.createElement('div');
    panel.className = 'autocomplete-panel hidden';
    panel.setAttribute('role', 'listbox');
    anchor.appendChild(panel);
    return panel;
  };

  const attachTypeahead = (
    input: HTMLInputElement,
    panel: HTMLDivElement,
    minChars: number,
    onPick: (name: string) => void,
    options?: {
      endpoint?: string;
      debounceMs?: number;
      onError?: (message?: string) => void;
    }
  ): void => {
    const endpoint = options?.endpoint ?? '/suggest-categories/card-search';
    const debounceMs = options?.debounceMs ?? 250;
    const onError = options?.onError;

    const fetchSuggestions = async (): Promise<void> => {
      const query = input.value.trim();
      if (query.length < minChars) {
        hideLookupSuggestionPanel(panel);
        onError?.(undefined);
        return;
      }

      try {
        const response = await fetch(`${endpoint}?query=${encodeURIComponent(query)}`);
        if (!response.ok) {
          hideLookupSuggestionPanel(panel);
          onError?.(await getErrorMessage(response));
          return;
        }

        const names: string[] = await response.json();
        onError?.(undefined);
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
      } catch {
        hideLookupSuggestionPanel(panel);
        onError?.('Scryfall could not be reached right now. Try again shortly.');
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

  typeaheadWindow.DeckFlow = typeaheadWindow.DeckFlow ?? {};
  typeaheadWindow.DeckFlow.attachTypeahead = attachTypeahead;
  typeaheadWindow.DeckFlow.createTypeaheadPanel = createTypeaheadPanel;
})();
