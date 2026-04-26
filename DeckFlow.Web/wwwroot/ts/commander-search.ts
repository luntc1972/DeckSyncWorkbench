type CommanderSearchDeckFlowNamespace = {
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

const setCommanderSearchError = (message?: string): void => {
  const panel = document.querySelector<HTMLElement>('[data-api-panel="commander-search-error"]');
  const text = document.querySelector<HTMLElement>('[data-api-field="commander-search-error-text"]');
  if (!panel || !text) {
    return;
  }

  text.textContent = message ?? '';
  panel.classList.toggle('hidden', !message);
};

const ensureCommanderSearchAutocompleteAnchor = (input: HTMLInputElement): HTMLDivElement => {
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

const attachCommanderSearch = (): void => {
  const input = document.getElementById('commander-search-input') as HTMLInputElement | null;
  if (!input) {
    return;
  }

  const anchor = ensureCommanderSearchAutocompleteAnchor(input);
  const deckFlowWindow = window as Window & { DeckFlow?: CommanderSearchDeckFlowNamespace };
  const panel = deckFlowWindow.DeckFlow?.createTypeaheadPanel?.(anchor);
  if (!panel) {
    return;
  }

  deckFlowWindow.DeckFlow?.attachTypeahead?.(input, panel, 2, () => {
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }, {
    endpoint: '/commander-categories/search',
    debounceMs: 350,
    onError: setCommanderSearchError,
  });
};

document.addEventListener('DOMContentLoaded', attachCommanderSearch);
