const countNonEmptyLines = (value: string): number =>
  value
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(line => line.length > 0)
    .length;

const debounceCardLookupSearch = (fn: () => void, delay: number) => {
  let timer: number | undefined;
  return () => {
    if (timer !== undefined) {
      window.clearTimeout(timer);
    }

    timer = window.setTimeout(fn, delay);
  };
};

type ParsedLookupLine = {
  quantity: string;
  cardName: string;
};

const parseLookupLine = (line: string): ParsedLookupLine => {
  const trimmed = line.trim();
  const match = trimmed.match(/^(\d+)\s+(.+)$/);
  if (!match) {
    return { quantity: '', cardName: trimmed };
  }

  return {
    quantity: match[1] ?? '',
    cardName: (match[2] ?? '').trim()
  };
};

const buildLookupLine = (quantity: string, cardName: string): string => {
  const trimmedName = cardName.trim();
  const trimmedQuantity = quantity.trim();
  if (!trimmedName) {
    return '';
  }

  return trimmedQuantity ? `${trimmedQuantity} ${trimmedName}` : trimmedName;
};

const createLookupSuggestionPanel = (anchor: HTMLElement): HTMLDivElement => {
  const panel = document.createElement('div');
  panel.className = 'autocomplete-panel hidden';
  panel.setAttribute('role', 'listbox');
  anchor.appendChild(panel);
  return panel;
};

const hideLookupSuggestionPanel = (panel: HTMLElement): void => {
  panel.classList.add('hidden');
  panel.replaceChildren();
};

const initializeCardLookupForm = (): void => {
  const form = document.querySelector<HTMLFormElement>('form[action="/card-lookup"]');
  if (!form) {
    return;
  }

  const textArea = form.querySelector<HTMLTextAreaElement>('textarea[name="CardList"]');
  const counter = document.querySelector<HTMLElement>('[data-verify-lines-count]');
  const validationMessage = document.querySelector<HTMLElement>('[data-verify-lines-error]');
  const submitButtons = form.querySelectorAll<HTMLButtonElement>('button[type="submit"]');
  const downloadButton = form.querySelector<HTMLButtonElement>('button[formaction="/card-lookup/download"]');
  const buildLinesButton = form.querySelector<HTMLButtonElement>('[data-card-lookup-build-lines]');
  const addLineButton = form.querySelector<HTMLButtonElement>('[data-card-lookup-add-line]');
  const lineEditor = form.querySelector<HTMLElement>('[data-card-lookup-line-editor]');
  if (!textArea || !counter || !validationMessage) {
    return;
  }

  const updateUi = (): void => {
    const lineCount = countNonEmptyLines(textArea.value);
    const overLimit = lineCount > 100;

    counter.textContent = `${lineCount}/100 lines`;
    validationMessage.classList.toggle('hidden', !overLimit);
    validationMessage.textContent = overLimit
      ? 'Card Lookup accepts up to 100 non-empty lines per submission.'
      : '';

    textArea.setCustomValidity(overLimit ? 'Card Lookup accepts up to 100 non-empty lines per submission.' : '');
    submitButtons.forEach(button => {
      button.disabled = overLimit;
    });
  };

  const syncTextareaFromEditor = (): void => {
    if (!lineEditor) {
      return;
    }

    const lines = Array.from(lineEditor.querySelectorAll<HTMLElement>('[data-card-lookup-line]'))
      .map(row => {
        const quantity = row.querySelector<HTMLInputElement>('[data-card-lookup-quantity]')?.value ?? '';
        const cardName = row.querySelector<HTMLInputElement>('[data-card-lookup-name]')?.value ?? '';
        return buildLookupLine(quantity, cardName);
      })
      .filter(line => line.length > 0);

    textArea.value = lines.join('\n');
    updateUi();
  };

  const attachLookupSearch = (input: HTMLInputElement, panel: HTMLDivElement): void => {
    const fetchSuggestions = async (): Promise<void> => {
      const query = input.value.trim();
      if (query.length < 2) {
        hideLookupSuggestionPanel(panel);
        return;
      }

      try {
        const response = await fetch(`/suggest-categories/card-search?query=${encodeURIComponent(query)}`);
        if (!response.ok) {
          hideLookupSuggestionPanel(panel);
          return;
        }

        const names: string[] = await response.json();
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
            syncTextareaFromEditor();
          });
          panel.appendChild(option);
        });
        panel.classList.remove('hidden');
      } catch {
        hideLookupSuggestionPanel(panel);
      }
    };

    const debounced = debounceCardLookupSearch(fetchSuggestions, 250);
    input.addEventListener('input', debounced);
    input.addEventListener('focus', debounced);
    document.addEventListener('click', event => {
      if (!(event.target instanceof Node) || panel.contains(event.target) || input.contains(event.target)) {
        return;
      }

      hideLookupSuggestionPanel(panel);
    });
  };

  const createLookupLineRow = (line: ParsedLookupLine): HTMLElement => {
    const row = document.createElement('div');
    row.className = 'card-lookup-line-row';
    row.dataset.cardLookupLine = 'true';

    const quantityInput = document.createElement('input');
    quantityInput.type = 'text';
    quantityInput.inputMode = 'numeric';
    quantityInput.placeholder = 'Qty';
    quantityInput.value = line.quantity;
    quantityInput.dataset.cardLookupQuantity = 'true';
    quantityInput.className = 'card-lookup-line-row__quantity';

    const cardInputShell = document.createElement('div');
    cardInputShell.className = 'autocomplete-anchor card-lookup-line-row__name-shell';

    const cardInput = document.createElement('input');
    cardInput.type = 'text';
    cardInput.placeholder = 'Card name';
    cardInput.value = line.cardName;
    cardInput.dataset.cardLookupName = 'true';
    cardInput.className = 'card-lookup-line-row__name';
    cardInputShell.appendChild(cardInput);
    const suggestionPanel = createLookupSuggestionPanel(cardInputShell);
    attachLookupSearch(cardInput, suggestionPanel);

    const removeButton = document.createElement('button');
    removeButton.type = 'button';
    removeButton.className = 'clear-cache-button card-lookup-line-row__remove';
    removeButton.textContent = 'Remove';
    removeButton.addEventListener('click', () => {
      row.remove();
      syncTextareaFromEditor();
      if (lineEditor && lineEditor.querySelectorAll('[data-card-lookup-line]').length === 0) {
        addLineButton?.classList.remove('hidden');
      }
    });

    quantityInput.addEventListener('input', syncTextareaFromEditor);
    cardInput.addEventListener('input', syncTextareaFromEditor);
    cardInput.addEventListener('change', syncTextareaFromEditor);

    row.append(quantityInput, cardInputShell, removeButton);
    return row;
  };

  const rebuildLineEditor = (): void => {
    if (!lineEditor) {
      return;
    }

    const lines = textArea.value
      .split(/\r?\n/)
      .map(line => line.trim())
      .filter(line => line.length > 0)
      .map(parseLookupLine);

    lineEditor.replaceChildren(...(lines.length > 0 ? lines : [{ quantity: '', cardName: '' }]).map(createLookupLineRow));
    lineEditor.classList.remove('hidden');
    addLineButton?.classList.remove('hidden');
  };

  textArea.addEventListener('input', updateUi);
  buildLinesButton?.addEventListener('click', rebuildLineEditor);
  addLineButton?.addEventListener('click', () => {
    if (!lineEditor) {
      return;
    }

    lineEditor.classList.remove('hidden');
    lineEditor.appendChild(createLookupLineRow({ quantity: '', cardName: '' }));
    addLineButton.classList.remove('hidden');
  });
  downloadButton?.addEventListener('click', () => {
    window.setTimeout(() => {
      window.hideBusyIndicator?.();
    }, 300);
  });
  if (textArea.value.trim().length > 0) {
    rebuildLineEditor();
  }
  updateUi();
};

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', initializeCardLookupForm);
} else {
  initializeCardLookupForm();
}
