const debounce = (fn: () => void, delay: number) => {
  let timer: number | undefined;
  return () => {
    if (timer !== undefined) {
      window.clearTimeout(timer);
    }
    timer = window.setTimeout(fn, delay);
  };
};

const ensureCommanderAutocompleteAnchor = (input: HTMLInputElement): HTMLDivElement => {
  const parent = input.parentElement;
  if (!parent) {
    throw new Error('Commander autocomplete input is missing a parent element.');
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

const getOrCreateCommanderSuggestionPanel = (input: HTMLInputElement): HTMLDivElement => {
  const anchor = ensureCommanderAutocompleteAnchor(input);
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

const hideCommanderSuggestionPanel = (panel: HTMLElement): void => {
  panel.classList.add('hidden');
  panel.replaceChildren();
};

const renderSuggestions = (list: string[], input: HTMLInputElement, panel: HTMLDivElement): void => {
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

const setCommanderSearchError = (message?: string): void => {
  const panel = document.querySelector<HTMLElement>('[data-api-panel="commander-search-error"]');
  const text = document.querySelector<HTMLElement>('[data-api-field="commander-search-error-text"]');
  if (!panel || !text) {
    return;
  }

  text.textContent = message ?? '';
  panel.classList.toggle('hidden', !message);
};

const attachCommanderSearch = (): void => {
  const input = document.getElementById('commander-search-input') as HTMLInputElement | null;
  if (!input) {
    return;
  }
  const panel = getOrCreateCommanderSuggestionPanel(input);

  const fetchSuggestions = async (): Promise<void> => {
    const query = input.value.trim();
    if (query.length < 2) {
      hideCommanderSuggestionPanel(panel);
      setCommanderSearchError();
      return;
    }

    try {
      const response = await fetch(`/commander-categories/search?query=${encodeURIComponent(query)}`);
      if (!response.ok) {
        let payload: { message?: string; Message?: string } | null = null;
        try {
          payload = await response.json() as { message?: string; Message?: string };
        } catch {
          payload = null;
        }

        hideCommanderSuggestionPanel(panel);
        setCommanderSearchError(payload?.message ?? payload?.Message ?? 'Scryfall could not be reached right now. Try again shortly.');
        return;
      }
      const names: string[] = await response.json();
      renderSuggestions(names, input, panel);
      setCommanderSearchError();
    } catch (error) {
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
