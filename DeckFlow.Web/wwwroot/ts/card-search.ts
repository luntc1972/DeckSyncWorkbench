type CardSearchDeckFlowNamespace = {
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

const setCardSearchError = (message?: string): void => {
  const panel = document.querySelector<HTMLElement>('[data-api-panel="card-search-error"]');
  const text = document.querySelector<HTMLElement>('[data-api-field="card-search-error-text"]');
  if (!panel || !text) {
    return;
  }

  text.textContent = message ?? '';
  panel.classList.toggle('hidden', !message);
};

const ensureCardSearchAutocompleteAnchor = (input: HTMLInputElement): HTMLDivElement => {
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

const attachCardSearch = (): void => {
  const input = document.querySelector<HTMLInputElement>('input[name="CardName"]');
  if (!input) {
    return;
  }

  const anchor = ensureCardSearchAutocompleteAnchor(input);
  const deckFlowWindow = window as Window & { DeckFlow?: CardSearchDeckFlowNamespace };
  const panel = deckFlowWindow.DeckFlow?.createTypeaheadPanel?.(anchor);
  if (!panel) {
    return;
  }

  deckFlowWindow.DeckFlow?.attachTypeahead?.(input, panel, 2, () => {
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }, {
    onError: setCardSearchError,
  });
};

document.addEventListener('DOMContentLoaded', attachCardSearch);
