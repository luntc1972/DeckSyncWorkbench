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

type SingleCardMechanicRule = {
  mechanicName?: string;
  ruleReference?: string;
  matchType?: string;
  rulesText?: string;
  summaryText?: string;
};

type SingleCardLookupPayload = {
  cardName?: string;
  verifiedText?: string;
  mechanicRules?: SingleCardMechanicRule[];
  message?: string;
};

type TypeaheadWindow = Window & {
  DeckFlow?: {
    [key: string]: any;
  };
};

const typeaheadWindow = window as TypeaheadWindow;

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

const hideLookupSuggestionPanel = (panel: HTMLElement): void => {
  panel.classList.add('hidden');
  panel.replaceChildren();
};

const createTypeaheadPanel = (anchor: HTMLElement): HTMLDivElement => typeaheadWindow.DeckFlow!.createTypeaheadPanel!(anchor);

const toggleMechanic = (row: HTMLButtonElement): void => {
  const article = row.closest<HTMLElement>('.card-lookup-mechanic');
  const body = article?.querySelector<HTMLElement>('.mechanic-body');
  if (!article || !body) {
    return;
  }
  const expanded = row.getAttribute('aria-expanded') === 'true';
  const next = !expanded;
  row.setAttribute('aria-expanded', String(next));
  article.dataset.expanded = String(next);
  body.hidden = !next;
};

const SINGLE_CARD_STATE_KEY = 'card-lookup-single-state';

type StoredSingleCardState = {
  cardName: string;
  verifiedText: string;
  mechanicRules: SingleCardMechanicRule[];
};

const saveSingleCardState = (state: StoredSingleCardState): void => {
  try {
    window.sessionStorage.setItem(SINGLE_CARD_STATE_KEY, JSON.stringify(state));
  } catch {
    // sessionStorage may be disabled (private mode quotas, etc.) — silently skip.
  }
};

const loadSingleCardState = (): StoredSingleCardState | null => {
  try {
    const raw = window.sessionStorage.getItem(SINGLE_CARD_STATE_KEY);
    if (!raw) {
      return null;
    }
    const parsed = JSON.parse(raw) as Partial<StoredSingleCardState> | null;
    if (!parsed || typeof parsed.cardName !== 'string' || typeof parsed.verifiedText !== 'string' || !Array.isArray(parsed.mechanicRules)) {
      return null;
    }
    return {
      cardName: parsed.cardName,
      verifiedText: parsed.verifiedText,
      mechanicRules: parsed.mechanicRules as SingleCardMechanicRule[]
    };
  } catch {
    return null;
  }
};

const clearSingleCardState = (): void => {
  try {
    window.sessionStorage.removeItem(SINGLE_CARD_STATE_KEY);
  } catch {
    // ignore
  }
};

const attachDynamicCopyButton = (button: HTMLButtonElement): void => {
  button.addEventListener('click', async () => {
    const targetId = button.dataset.copyTarget;
    if (!targetId) {
      return;
    }

    const target = document.getElementById(targetId) as HTMLTextAreaElement | HTMLInputElement | HTMLElement | null;
    if (!target) {
      button.textContent = 'Copy failed';
      return;
    }

    const text = 'value' in target && typeof target.value === 'string'
      ? target.value
      : target.textContent ?? '';

    const originalText = button.dataset.copyOriginalText ?? button.textContent?.trim() ?? 'Copy';
    button.dataset.copyOriginalText = originalText;

    try {
      await navigator.clipboard.writeText(text);
      button.textContent = 'Copied';
      button.classList.add('is-copied');
    } catch {
      button.textContent = 'Copy failed';
      button.classList.add('is-copy-failed');
    }

    window.setTimeout(() => {
      button.textContent = originalText;
      button.classList.remove('is-copied', 'is-copy-failed');
    }, 1800);
  });
};

const initializeSingleCardMode = (): void => {
  const input = document.querySelector<HTMLInputElement>('[data-card-lookup-single-input]');
  const submitButton = document.querySelector<HTMLButtonElement>('[data-card-lookup-single-submit]');
  const clearButton = document.querySelector<HTMLButtonElement>('[data-card-lookup-single-clear]');
  const errorBanner = document.querySelector<HTMLElement>('[data-card-lookup-single-error]');
  const resultPanel = document.querySelector<HTMLElement>('[data-card-lookup-single-result]');
  const resultOutput = document.querySelector<HTMLElement>('[data-card-lookup-single-output]');
  const resultLabel = document.querySelector<HTMLElement>('[data-card-lookup-single-name-label]');
  const mechanicsPanel = document.querySelector<HTMLElement>('[data-card-lookup-single-mechanics-panel]');
  const mechanicsLabel = document.querySelector<HTMLElement>('[data-card-lookup-single-mechanics-label]');
  const mechanicsContainer = document.querySelector<HTMLElement>('[data-card-lookup-single-mechanics]');
  const anchor = input?.parentElement;
  if (!input || !submitButton || !errorBanner || !resultPanel || !resultOutput || !resultLabel || !mechanicsPanel || !mechanicsLabel || !mechanicsContainer || !anchor) {
    return;
  }

  const suggestionPanel = createTypeaheadPanel(anchor);

  const showError = (message: string): void => {
    errorBanner.textContent = message;
    errorBanner.classList.remove('hidden');
    resultPanel.classList.add('hidden');
    clearMechanics();
  };

  const clearError = (): void => {
    errorBanner.textContent = '';
    errorBanner.classList.add('hidden');
  };

  const clearMechanics = (): void => {
    mechanicsContainer.replaceChildren();
    mechanicsLabel.textContent = '';
    mechanicsPanel.classList.add('hidden');
  };

  const askJudgeLink = document.querySelector<HTMLAnchorElement>('[data-card-lookup-ask-judge-link]');
  const askJudgeBaseHref = askJudgeLink?.getAttribute('href') ?? '/judge-questions';

  const showResult = (name: string, verifiedText: string, mechanicRules: SingleCardMechanicRule[]): void => {
    clearError();
    resultLabel.textContent = name;
    resultOutput.textContent = verifiedText;
    resultPanel.classList.remove('hidden');
    clearMechanics();
    const visibleMechanics = mechanicRules.filter(rule => rule.rulesText && rule.mechanicName);
    if (visibleMechanics.length > 0) {
      mechanicsLabel.textContent = `${visibleMechanics.length} official rules entr${visibleMechanics.length === 1 ? 'y' : 'ies'} found on this card.`;
      const autoExpand = visibleMechanics.length === 1;
      const items = visibleMechanics.map((rule, index) => {
        const wrapper = document.createElement('article');
        wrapper.className = 'card-lookup-mechanic';
        wrapper.dataset.expanded = autoExpand ? 'true' : 'false';

        const bodyId = `card-lookup-single-mechanic-body-${index}`;
        const preId = `card-lookup-single-mechanic-${index}`;

        const row = document.createElement('button');
        row.type = 'button';
        row.className = 'mechanic-row';
        row.setAttribute('aria-expanded', autoExpand ? 'true' : 'false');
        row.setAttribute('aria-controls', bodyId);

        const name = document.createElement('span');
        name.className = 'kw-name';
        name.textContent = rule.mechanicName ?? 'Mechanic';

        const section = document.createElement('span');
        section.className = 'kw-section';
        section.textContent = [rule.matchType, rule.ruleReference].filter(Boolean).join(' · ');

        const chevron = document.createElement('span');
        chevron.className = 'chevron';
        chevron.setAttribute('aria-hidden', 'true');
        chevron.textContent = '▸';

        row.append(name, section, chevron);
        row.addEventListener('click', () => toggleMechanic(row));
        wrapper.appendChild(row);

        if (rule.summaryText) {
          const summary = document.createElement('p');
          summary.className = 'card-lookup-mechanic__summary';
          summary.textContent = rule.summaryText;
          wrapper.appendChild(summary);
        }

        const body = document.createElement('div');
        body.className = 'mechanic-body';
        body.id = bodyId;
        body.hidden = !autoExpand;

        const copyButton = document.createElement('button');
        copyButton.type = 'button';
        copyButton.className = 'copy-button copy-button--icon';
        copyButton.setAttribute('data-copy-target', preId);
        copyButton.setAttribute('aria-label', `Copy comprehensive rules text for ${rule.mechanicName ?? 'mechanic'}`);
        copyButton.setAttribute('title', 'Copy CR text');
        const glyph = document.createElement('span');
        glyph.setAttribute('aria-hidden', 'true');
        glyph.textContent = '📋';
        copyButton.appendChild(glyph);
        attachDynamicCopyButton(copyButton);

        const pre = document.createElement('pre');
        pre.id = preId;
        pre.className = 'cr-text';
        pre.textContent = rule.rulesText ?? '';

        const bottomActions = document.createElement('div');
        bottomActions.className = 'panel-footer-actions';

        const bottomCopyButton = document.createElement('button');
        bottomCopyButton.type = 'button';
        bottomCopyButton.className = 'copy-button';
        bottomCopyButton.setAttribute('data-copy-target', preId);
        bottomCopyButton.setAttribute('aria-label', `Copy comprehensive rules text for ${rule.mechanicName ?? 'mechanic'}`);
        const bottomGlyph = document.createElement('span');
        bottomGlyph.setAttribute('aria-hidden', 'true');
        bottomGlyph.textContent = '📋';
        bottomCopyButton.append(bottomGlyph, document.createTextNode(' Copy CR text'));
        attachDynamicCopyButton(bottomCopyButton);
        bottomActions.appendChild(bottomCopyButton);

        body.append(copyButton, pre, bottomActions);
        wrapper.appendChild(body);
        return wrapper;
      });

      const list = document.createElement('div');
      list.className = 'card-lookup-mechanics-list';
      list.append(...items);
      mechanicsContainer.appendChild(list);
      mechanicsPanel.classList.remove('hidden');
    }

    if (askJudgeLink) {
      askJudgeLink.href = `${askJudgeBaseHref}?card=${encodeURIComponent(name)}`;
    }

    saveSingleCardState({ cardName: name, verifiedText, mechanicRules });
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
      const payload = await response.json().catch(() => null) as SingleCardLookupPayload | null;
      if (!response.ok) {
        showError(payload?.message ?? 'Scryfall could not be reached right now. Try again shortly.');
        return;
      }

      if (!payload?.verifiedText) {
        showError('No card details were returned.');
        return;
      }

      showResult(payload.cardName?.trim() || query, payload.verifiedText, payload.mechanicRules ?? []);
    } catch (error) {
      showError(error instanceof Error ? error.message : 'Lookup failed.');
    } finally {
      submitButton.disabled = false;
    }
  };

  typeaheadWindow.DeckFlow!.attachTypeahead!(input, suggestionPanel, 2, (name: string) => {
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
    resultOutput.textContent = '';
    clearMechanics();
    clearError();
    hideLookupSuggestionPanel(suggestionPanel);
    input.focus();
    clearSingleCardState();
  });

  const stored = loadSingleCardState();
  if (stored) {
    input.value = stored.cardName;
    showResult(stored.cardName, stored.verifiedText, stored.mechanicRules);
  }
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
    const suggestionPanel = createTypeaheadPanel(cardInputShell);
    typeaheadWindow.DeckFlow!.attachTypeahead!(cardInput, suggestionPanel, 2, (name: string) => {
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
