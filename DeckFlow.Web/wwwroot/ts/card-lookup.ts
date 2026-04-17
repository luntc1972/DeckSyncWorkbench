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

const attachLookaheadInput = (
  input: HTMLInputElement,
  panel: HTMLDivElement,
  minChars: number,
  onPick: (name: string) => void
): void => {
  const fetchSuggestions = async (): Promise<void> => {
    const query = input.value.trim();
    if (query.length < minChars) {
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
          onPick(name);
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

const initializeSingleCardMode = (): void => {
  const input = document.querySelector<HTMLInputElement>('[data-card-lookup-single-input]');
  const submitButton = document.querySelector<HTMLButtonElement>('[data-card-lookup-single-submit]');
  const clearButton = document.querySelector<HTMLButtonElement>('[data-card-lookup-single-clear]');
  const errorBanner = document.querySelector<HTMLElement>('[data-card-lookup-single-error]');
  const resultPanel = document.querySelector<HTMLElement>('[data-card-lookup-single-result]');
  const resultTextarea = document.querySelector<HTMLTextAreaElement>('[data-card-lookup-single-output]');
  const resultLabel = document.querySelector<HTMLElement>('[data-card-lookup-single-name-label]');
  const anchor = input?.parentElement;
  if (!input || !submitButton || !errorBanner || !resultPanel || !resultTextarea || !resultLabel || !anchor) {
    return;
  }

  const suggestionPanel = createLookupSuggestionPanel(anchor);

  const showError = (message: string): void => {
    errorBanner.textContent = message;
    errorBanner.classList.remove('hidden');
    resultPanel.classList.add('hidden');
  };

  const clearError = (): void => {
    errorBanner.textContent = '';
    errorBanner.classList.add('hidden');
  };

  const askJudgeLink = document.querySelector<HTMLAnchorElement>('[data-card-lookup-ask-judge-link]');
  const askJudgeBaseHref = askJudgeLink?.getAttribute('href') ?? '/judge-questions';

  const showResult = (name: string, verifiedText: string): void => {
    clearError();
    resultLabel.textContent = name;
    resultTextarea.value = verifiedText;
    resultPanel.classList.remove('hidden');
    if (askJudgeLink) {
      askJudgeLink.href = `${askJudgeBaseHref}?card=${encodeURIComponent(name)}`;
    }
  };

  const runLookup = async (name: string): Promise<void> => {
    const query = name.trim();
    if (!query) {
      showError('Enter a card name first.');
      return;
    }

    submitButton.disabled = true;
    clearError();
    try {
      const response = await fetch(`/card-lookup/single?name=${encodeURIComponent(query)}`);
      const payload = await response.json().catch(() => null) as { verifiedText?: string; message?: string } | null;
      if (!response.ok) {
        showError(payload?.message ?? 'Scryfall could not be reached right now. Try again shortly.');
        return;
      }

      if (!payload?.verifiedText) {
        showError('No card details were returned.');
        return;
      }

      showResult(query, payload.verifiedText);
    } catch (error) {
      showError(error instanceof Error ? error.message : 'Lookup failed.');
    } finally {
      submitButton.disabled = false;
    }
  };

  attachLookaheadInput(input, suggestionPanel, 4, name => {
    input.value = name;
    runLookup(name);
  });

  submitButton.addEventListener('click', () => {
    runLookup(input.value);
  });

  input.addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      runLookup(input.value);
    }
  });

  clearButton?.addEventListener('click', () => {
    input.value = '';
    resultPanel.classList.add('hidden');
    resultTextarea.value = '';
    clearError();
    hideLookupSuggestionPanel(suggestionPanel);
    input.focus();
  });
};

const initializeModePicker = (): void => {
  const picker = document.querySelector<HTMLElement>('[data-card-lookup-mode-picker]');
  if (!picker) {
    return;
  }

  const panels = Array.from(document.querySelectorAll<HTMLElement>('[data-card-lookup-mode-panel]'));
  const buttons = Array.from(picker.querySelectorAll<HTMLButtonElement>('[data-card-lookup-mode-button]'));

  const activate = (mode: string): void => {
    buttons.forEach(button => {
      const active = button.dataset.cardLookupModeButton === mode;
      button.classList.toggle('is-active', active);
      button.setAttribute('aria-pressed', active ? 'true' : 'false');
    });
    panels.forEach(panel => {
      const active = panel.dataset.cardLookupModePanel === mode;
      panel.classList.toggle('hidden', !active);
    });
  };

  buttons.forEach(button => {
    button.addEventListener('click', () => {
      const mode = button.dataset.cardLookupModeButton;
      if (mode) {
        activate(mode);
      }
    });
  });

  activate('single');
};

const initializeCardListMode = (): void => {
  const form = document.querySelector<HTMLFormElement>('form[data-cache-key="card-lookup"]');
  if (!form) {
    return;
  }

  const textArea = form.querySelector<HTMLTextAreaElement>('textarea[name="CardList"]');
  const counter = document.querySelector<HTMLElement>('[data-verify-lines-count]');
  const validationMessage = document.querySelector<HTMLElement>('[data-verify-lines-error]');
  const submitButtons = form.querySelectorAll<HTMLButtonElement>('button[type="submit"]');
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
    attachLookaheadInput(cardInput, suggestionPanel, 2, name => {
      cardInput.value = name;
      syncTextareaFromEditor();
    });

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
  form.addEventListener('submit', () => {
    window.setTimeout(() => {
      window.hideBusyIndicator?.();
    }, 400);
  });
  if (textArea.value.trim().length > 0) {
    rebuildLineEditor();
  }
  updateUi();
};

const initializeCardLookupForm = (): void => {
  initializeModePicker();
  initializeSingleCardMode();
  initializeCardListMode();
};

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', initializeCardLookupForm);
} else {
  initializeCardLookupForm();
}
