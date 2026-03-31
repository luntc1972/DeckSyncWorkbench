const debounceCardSearch = (fn: () => void, delay: number) => {
  let timer: number | undefined;
  return () => {
    if (timer !== undefined) {
      window.clearTimeout(timer);
    }

    timer = window.setTimeout(fn, delay);
  };
};

const setCardSearchError = (message?: string): void => {
  const panel = document.querySelector<HTMLElement>('[data-api-panel="card-search-error"]');
  const text = document.querySelector<HTMLElement>('[data-api-field="card-search-error-text"]');
  if (!panel || !text) {
    return;
  }

  text.textContent = message ?? '';
  panel.classList.toggle('hidden', !message);
};

const ensureAutocompleteAnchor = (input: HTMLInputElement): HTMLDivElement => {
  const parent = input.parentElement;
  if (!parent) {
    throw new Error('Autocomplete input is missing a parent element.');
  }

  if (parent.classList.contains('autocomplete-anchor')) {
    return parent as HTMLDivElement;
  }

  const anchor = document.createElement('div');
  anchor.className = 'autocomplete-anchor';
  input.insertAdjacentElement('beforebegin', anchor);
  anchor.appendChild(input);
  return anchor;
};

const getOrCreateSuggestionPanel = (input: HTMLInputElement): HTMLDivElement => {
  const anchor = ensureAutocompleteAnchor(input);
  const existingPanel = anchor.querySelector<HTMLDivElement>('.autocomplete-panel');
  if (existingPanel) {
    return existingPanel;
  }

  const panel = document.createElement('div');
  panel.className = 'autocomplete-panel hidden';
  panel.setAttribute('role', 'listbox');
  anchor.appendChild(panel);
  return panel;
};

const hideSuggestionPanel = (panel: HTMLElement): void => {
  panel.classList.add('hidden');
  panel.replaceChildren();
};

const renderCardSuggestions = (list: string[], input: HTMLInputElement, panel: HTMLDivElement): void => {
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

const attachCardSearch = (): void => {
  const input = document.querySelector<HTMLInputElement>('input[name="CardName"]');
  if (!input) {
    return;
  }
  const panel = getOrCreateSuggestionPanel(input);

  const fetchSuggestions = async (): Promise<void> => {
    const query = input.value.trim();
    if (query.length < 2) {
      hideSuggestionPanel(panel);
      setCardSearchError();
      return;
    }

    try {
      const response = await fetch(`/suggest-categories/card-search?query=${encodeURIComponent(query)}`);
      if (!response.ok) {
        let payload: { message?: string; Message?: string } | null = null;
        try {
          payload = await response.json() as { message?: string; Message?: string };
        } catch {
          payload = null;
        }

        hideSuggestionPanel(panel);
        setCardSearchError(payload?.message ?? payload?.Message ?? 'Scryfall could not be reached right now. Try again shortly.');
        return;
      }

      const names: string[] = await response.json();
      renderCardSuggestions(names, input, panel);
      setCardSearchError();
    } catch (error) {
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
