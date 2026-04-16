const togglePanel = (selector: string, shouldHide: boolean): void => {
  document.querySelectorAll<HTMLElement>(selector).forEach(element => {
    element.classList.toggle('hidden', shouldHide);
    element.style.display = shouldHide ? 'none' : '';
  });
};

const DeckInputSource = {
  PasteText: 'PasteText',
  PublicUrl: 'PublicUrl',
} as const;

type DeckInputSourceValue = (typeof DeckInputSource)[keyof typeof DeckInputSource];

type PanelConfig = {
  selectName: string;
  urlSelector: string;
  textSelector: string;
};

type DeckSyncApiResponse = {
  reportText: string;
  deltaText: string;
  fullImportText: string;
  instructionsText: string;
  sourceSystem: string;
  targetSystem: string;
  printingConflicts: Array<{
    cardName: string;
    archidektSetCode: string;
    archidektCollectorNumber: string;
    archidektCategory?: string | null;
    moxfieldSetCode?: string | null;
    moxfieldCollectorNumber?: string | null;
  }>;
};

type DeckSyncSystem = 'Moxfield' | 'Archidekt';

const panelConfigs: PanelConfig[] = [
  {
    selectName: 'MoxfieldInputSource',
    urlSelector: '[data-sync-panel="moxfield-url"]',
    textSelector: '[data-sync-panel="moxfield-text"]',
  },
  {
    selectName: 'ArchidektInputSource',
    urlSelector: '[data-sync-panel="archidekt-url"]',
    textSelector: '[data-sync-panel="archidekt-text"]',
  },
];

const updateSyncInputModeUi = (): void => {
  panelConfigs.forEach(config => {
    const select = document.querySelector<HTMLSelectElement>(`select[name="${config.selectName}"]`);
    if (!select) {
      return;
    }

    const selectedValue = select.value as DeckInputSourceValue;
    const showUrl = selectedValue === DeckInputSource.PublicUrl;
    const showText = selectedValue === DeckInputSource.PasteText;

    togglePanel(config.urlSelector, !showUrl);
    togglePanel(config.textSelector, !showText);
  });
};

const updateSyncDirectionUi = (): void => {
  const directionSelect = document.querySelector<HTMLSelectElement>('select[name="Direction"]');
  if (!directionSelect) {
    return;
  }

  const direction = directionSelect.value;
  const leftSystem = direction === 'ArchidektToArchidekt' ? 'Archidekt' : 'Moxfield';
  const rightSystem = direction === 'MoxfieldToMoxfield' ? 'Moxfield' : 'Archidekt';
  const leftIsSource = direction === 'MoxfieldToArchidekt' || direction === 'MoxfieldToMoxfield';
  const moxfieldStatus = document.querySelector<HTMLElement>('[data-sync-role="moxfield-status"]');
  const archidektStatus = document.querySelector<HTMLElement>('[data-sync-role="archidekt-status"]');
  const moxfieldTitle = document.querySelector<HTMLElement>('[data-sync-role="moxfield-title"]');
  const archidektTitle = document.querySelector<HTMLElement>('[data-sync-role="archidekt-title"]');
  const moxfieldDescription = document.querySelector<HTMLElement>('[data-sync-role="moxfield-description"]');
  const archidektDescription = document.querySelector<HTMLElement>('[data-sync-role="archidekt-description"]');
  const moxfieldUrlLabel = document.querySelector<HTMLElement>('[data-sync-role="moxfield-url-label"]');
  const archidektUrlLabel = document.querySelector<HTMLElement>('[data-sync-role="archidekt-url-label"]');
  const moxfieldTextLabel = document.querySelector<HTMLElement>('[data-sync-role="moxfield-text-label"]');
  const archidektTextLabel = document.querySelector<HTMLElement>('[data-sync-role="archidekt-text-label"]');
  const moxfieldHint = document.querySelector<HTMLElement>('[data-sync-role="moxfield-hint"]');
  const archidektHint = document.querySelector<HTMLElement>('[data-sync-role="archidekt-hint"]');
  const targetCategoryOption = document.querySelector<HTMLOptionElement>('[data-sync-role="category-mode-target"]');
  const sourceCategoryOption = document.querySelector<HTMLOptionElement>('[data-sync-role="category-mode-source"]');
  const moxfieldUrlInput = document.querySelector<HTMLInputElement>('input[name="MoxfieldUrl"]');
  const archidektUrlInput = document.querySelector<HTMLInputElement>('input[name="ArchidektUrl"]');
  const sourceLabelKind = leftIsSource
    ? (leftSystem === 'Archidekt' ? 'categories' : 'tags')
    : (rightSystem === 'Archidekt' ? 'categories' : 'tags');
  const targetLabelKind = leftIsSource
    ? (rightSystem === 'Archidekt' ? 'categories' : 'tags')
    : (leftSystem === 'Archidekt' ? 'categories' : 'tags');

  if (moxfieldStatus) {
    moxfieldStatus.textContent = leftIsSource ? 'Source deck' : 'Target deck';
  }

  if (archidektStatus) {
    archidektStatus.textContent = leftIsSource ? 'Target deck' : 'Source deck';
  }

  if (moxfieldTitle) {
    moxfieldTitle.textContent = leftSystem;
  }

  if (archidektTitle) {
    archidektTitle.textContent = rightSystem;
  }

  if (moxfieldDescription) {
    moxfieldDescription.textContent = `Provide the ${leftSystem} export or public URL for this deck.`;
  }

  if (archidektDescription) {
    archidektDescription.textContent = `Provide the ${rightSystem} export or public URL for this deck.`;
  }

  if (moxfieldUrlLabel) {
    moxfieldUrlLabel.textContent = `${leftSystem} public deck URL`;
  }

  if (archidektUrlLabel) {
    archidektUrlLabel.textContent = `${rightSystem} public deck URL`;
  }

  if (moxfieldTextLabel) {
    moxfieldTextLabel.textContent = `${leftSystem} export text`;
  }

  if (archidektTextLabel) {
    archidektTextLabel.textContent = `${rightSystem} export text`;
  }

  if (moxfieldHint) {
    moxfieldHint.textContent = `Use this when the ${leftSystem} deck is ${leftIsSource ? 'the source' : 'the target'}.`;
  }

  if (archidektHint) {
    archidektHint.textContent = `Use this when the ${rightSystem} deck is ${leftIsSource ? 'the target' : 'the source'}.`;
  }

  if (targetCategoryOption) {
    targetCategoryOption.textContent = `Use target ${targetLabelKind}`;
  }

  if (sourceCategoryOption) {
    sourceCategoryOption.textContent = `Use source ${sourceLabelKind}`;
  }

  if (moxfieldUrlInput) {
    moxfieldUrlInput.placeholder = leftSystem === 'Archidekt'
      ? 'https://archidekt.com/decks/...'
      : 'https://moxfield.com/decks/...';
  }

  if (archidektUrlInput) {
    archidektUrlInput.placeholder = rightSystem === 'Moxfield'
      ? 'https://moxfield.com/decks/...'
      : 'https://archidekt.com/decks/...';
  }
};

let syncInputModeInitialized = false;

const initializeSyncInputModeUi = (): void => {
  if (syncInputModeInitialized) {
    return;
  }

  syncInputModeInitialized = true;
  const inputSelectors = document.querySelectorAll<HTMLSelectElement>('select[name="MoxfieldInputSource"], select[name="ArchidektInputSource"]');
  inputSelectors.forEach(element => {
    element.addEventListener('change', updateSyncInputModeUi);
  });

  const directionSelect = document.querySelector<HTMLSelectElement>('select[name="Direction"]');
  directionSelect?.addEventListener('change', updateSyncDirectionUi);

  updateSyncInputModeUi();
  updateSyncDirectionUi();
};

const scrollResults = (): void => {
  const anchor = document.getElementById('results-anchor');
  if (anchor) {
    anchor.scrollIntoView({ behavior: 'smooth' });
  }
};

const setAllPrintingChoices = (value: string): void => {
  const selector = `input[type="radio"][name^="Resolutions["][value="${value}"]`;
  document.querySelectorAll<HTMLInputElement>(selector).forEach(input => {
    input.checked = true;
  });
};

const copyElementValue = async (targetId: string): Promise<void> => {
  const target = document.getElementById(targetId);
  if (!target) {
    return;
  }

  const text = target instanceof HTMLTextAreaElement || target instanceof HTMLInputElement
    ? target.value
    : target.textContent ?? '';

  if (!text) {
    return;
  }

  await navigator.clipboard.writeText(text);
};

const setTemporaryButtonText = (button: HTMLElement, text: string, durationMs = 1800): void => {
  const originalText = button.dataset.copyOriginalText ?? button.textContent?.trim() ?? 'Copy';
  button.dataset.copyOriginalText = originalText;
  button.textContent = text;

  window.setTimeout(() => {
    button.textContent = originalText;
  }, durationMs);
};

const attachActionButtons = (): void => {
  document.querySelectorAll<HTMLElement>('[data-copy-target]').forEach(button => {
    button.addEventListener('click', async () => {
      const targetId = button.dataset.copyTarget;
      if (!targetId) {
        return;
      }

      try {
        await copyElementValue(targetId);
        setTemporaryButtonText(button, 'Copied');
      } catch {
        setTemporaryButtonText(button, 'Copy failed');
      }
    });
  });

  document.querySelectorAll<HTMLElement>('[data-select-all-choice]').forEach(button => {
    button.addEventListener('click', () => {
      const choice = button.dataset.selectAllChoice;
      if (!choice) {
        return;
      }

      setAllPrintingChoices(choice);
    });
  });
};

let busyProgressTimer: number | undefined;
let busyHideTimer: number | undefined;

const formatProgressText = (steps: string[], index: number) => `Step ${index + 1}/${steps.length}: ${steps[index]}`;

const clearBusyProgress = (): void => {
  if (busyProgressTimer !== undefined) {
    window.clearInterval(busyProgressTimer);
    busyProgressTimer = undefined;
  }
};

const hideBusyIndicator = (): void => {
  const container = document.getElementById('busy-indicator');
  const progressNode = document.getElementById('busy-indicator-progress');
  if (!container) {
    return;
  }

  container.classList.add('hidden');
  if (progressNode) {
    progressNode.textContent = '';
    delete progressNode.dataset.currentIndex;
  }

  clearBusyProgress();
  if (busyHideTimer !== undefined) {
    window.clearTimeout(busyHideTimer);
    busyHideTimer = undefined;
  }
};

const scheduleBusyHide = (durationMs: number): void => {
  if (!durationMs || durationMs <= 0) {
    return;
  }

  if (busyHideTimer !== undefined) {
    window.clearTimeout(busyHideTimer);
  }

  busyHideTimer = window.setTimeout(() => {
    hideBusyIndicator();
  }, durationMs);
};

const showBusyIndicator = (
  title?: string,
  message?: string,
  progressSteps?: string[],
  durationMs?: number,
  holdFinalStep = false
): void => {
  const container = document.getElementById('busy-indicator');
  const titleNode = document.getElementById('busy-indicator-title');
  const messageNode = document.getElementById('busy-indicator-message');
  const progressNode = document.getElementById('busy-indicator-progress');
  if (!container || !titleNode || !messageNode) {
    return;
  }

  titleNode.textContent = title || 'Working';
  messageNode.textContent = message || 'Request in progress.';
  container.classList.remove('hidden');

  clearBusyProgress();
  if (progressNode) {
    if (progressSteps && progressSteps.length > 0) {
      const finalIndex = progressSteps.length - 1;
      let currentIndex = 0;
      progressNode.textContent = formatProgressText(progressSteps, currentIndex);
      progressNode.dataset.currentIndex = currentIndex.toString();

      busyProgressTimer = window.setInterval(() => {
        currentIndex++;

        if (currentIndex > finalIndex) {
          currentIndex = holdFinalStep ? finalIndex : 0;
        }

        progressNode.dataset.currentIndex = currentIndex.toString();
        progressNode.textContent = formatProgressText(progressSteps, currentIndex);

        if (holdFinalStep && currentIndex === finalIndex) {
          clearBusyProgress();
        }
      }, 4000);
    } else {
      progressNode.textContent = '';
    }
  }
  if (durationMs && durationMs > 0) {
    scheduleBusyHide(durationMs);
  }
};

const registerBusyIndicator = (): void => {
  document.querySelectorAll<HTMLFormElement>('form[data-busy-title]').forEach(form => {
    form.addEventListener('submit', () => {
      const title = form.getAttribute('data-busy-title');
      const message = form.getAttribute('data-busy-message');
      const stepsAttr = form.getAttribute('data-busy-progress');
      const steps = stepsAttr
        ? stepsAttr
            .split('|')
            .map(step => step.trim())
            .filter(step => step.length > 0)
        : [];
      const durationAttr = form.getAttribute('data-busy-duration');
      const duration = durationAttr ? parseInt(durationAttr, 10) : undefined;
      const holdFinalAttr = form.getAttribute('data-busy-hold-final-step');
      const holdFinalStep = holdFinalAttr !== null && holdFinalAttr.toLowerCase() === 'true';
      showBusyIndicator(
        title ?? undefined,
        message ?? undefined,
        steps.length > 0 ? steps : undefined,
        duration,
        holdFinalStep
      );
    });
  });
};

const formStateStoragePrefix = 'decksync-form-state-';
const tabNavigationKey = 'decksync-tab-navigation';
const storageAvailable = (() => {
  try {
    const testKey = '__decksync_test_key__';
    window.sessionStorage.setItem(testKey, '1');
    window.sessionStorage.removeItem(testKey);
    return window.sessionStorage;
  } catch {
    return null;
  }
})();

const serializePersistedFormFields = (form: HTMLFormElement): Record<string, string[]> => {
  const state: Record<string, string[]> = {};
  const formData = new FormData(form);

  formData.forEach((value, key) => {
    if (typeof value !== 'string') {
      return;
    }

    if (!state[key]) {
      state[key] = [];
    }

    state[key].push(value);
  });

  return state;
};

const serializeFormFields = (form: HTMLFormElement): Record<string, string> => {
  const state: Record<string, string> = {};
  form.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>('[name]').forEach(element => {
    if (element.disabled || !element.name) {
      return;
    }

    if (element instanceof HTMLInputElement && (element.type === 'checkbox' || element.type === 'radio')) {
      if (!element.checked) {
        return;
      }
    }

    state[element.name] = element.value;
  });

  return state;
};

const restoreFormFields = (form: HTMLFormElement, data: Record<string, string[]>) => {
  form.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>('[name]').forEach(element => {
    const values = data[element.name];
    if (!values || values.length === 0) {
      return;
    }

    if (element instanceof HTMLInputElement) {
      if (element.type === 'checkbox' || element.type === 'radio') {
        element.checked = values.includes(element.value);
        return;
      }

      element.value = values[0];
      return;
    }

    if (element instanceof HTMLSelectElement && element.multiple) {
      Array.from(element.options).forEach(option => {
        option.selected = values.includes(option.value);
      });
      return;
    }

    element.value = values[0];
  });
};

const persistFormState = (form: HTMLFormElement): void => {
  const key = form.getAttribute('data-cache-key');
  if (!key || !storageAvailable) {
    return;
  }

  const state = serializePersistedFormFields(form);
  storageAvailable.setItem(`${formStateStoragePrefix}${key}`, JSON.stringify(state));
};

const clearPersistedFormState = (form: HTMLFormElement): void => {
  const key = form.getAttribute('data-cache-key');
  if (!key || !storageAvailable) {
    return;
  }

  storageAvailable.removeItem(`${formStateStoragePrefix}${key}`);
};

const hydrateFormState = (form: HTMLFormElement): void => {
  const key = form.getAttribute('data-cache-key');
  if (!key || !storageAvailable) {
    return;
  }

  const json = storageAvailable.getItem(`${formStateStoragePrefix}${key}`);
  if (!json) {
    return;
  }

  try {
    const state = JSON.parse(json) as Record<string, string[]>;
    restoreFormFields(form, state);
  } catch {
    storageAvailable.removeItem(`${formStateStoragePrefix}${key}`);
  }
};

const attachGenericPersistedForms = (): void => {
  if (!storageAvailable) {
    return;
  }

  const forms = Array.from(document.querySelectorAll<HTMLFormElement>('form[data-cache-key]'));
  const restoredFromTabs = storageAvailable.getItem(tabNavigationKey) === '1';

  forms.forEach(form => {
    if (form.id === 'deck-sync-form') {
      return;
    }

    if (restoredFromTabs) {
      hydrateFormState(form);
    }

    const persist = () => persistFormState(form);
    form.addEventListener('input', persist);
    form.addEventListener('change', persist);

        const clearButton = form.querySelector<HTMLElement>('[data-clear-cache]');
        clearButton?.addEventListener('click', () => {
      const clearHref = clearButton.getAttribute('data-clear-href');
      if (clearHref) {
        clearPersistedFormState(form);
        window.location.href = clearHref;
        return;
      }

      form.reset();
      clearPersistedFormState(form);
    });
  });

  document.querySelectorAll<HTMLAnchorElement>('.tool-nav__link').forEach(link => {
    link.addEventListener('click', () => {
      forms.forEach(form => persistFormState(form));
      storageAvailable.setItem(tabNavigationKey, '1');
    });
  });

  if (restoredFromTabs) {
    storageAvailable.removeItem(tabNavigationKey);
  }
};

const clearDeckSyncUi = (): void => {
  const results = document.getElementById('deck-sync-results');
  const error = document.getElementById('deck-sync-error');

  if (results) {
    results.classList.add('hidden');
  }

  if (error) {
    error.classList.add('hidden');
    error.textContent = '';
  }
};

const setDeckSyncResultLabels = (sourceSystem: string, targetSystem: string): void => {
  document.querySelectorAll<HTMLElement>('[data-sync-result="source-system"]').forEach(node => {
    node.textContent = sourceSystem;
  });

  document.querySelectorAll<HTMLElement>('[data-sync-result="target-system"]').forEach(node => {
    node.textContent = targetSystem;
  });
};

const buildConflictCellText = (
  system: DeckSyncSystem,
  conflict: DeckSyncApiResponse['printingConflicts'][number]
): string => {
  if (system === 'Archidekt') {
    const categorySuffix = conflict.archidektCategory ? ` [${conflict.archidektCategory}]` : '';
    return `(${conflict.archidektSetCode}) ${conflict.archidektCollectorNumber}${categorySuffix}`;
  }

  const setCode = conflict.moxfieldSetCode ?? '';
  const collectorNumber = conflict.moxfieldCollectorNumber ?? '';
  return `(${setCode}) ${collectorNumber}`.trim();
};

const renderDeckSyncConflicts = (
  printingConflicts: DeckSyncApiResponse['printingConflicts'],
  sourceSystem: string,
  targetSystem: string
): void => {
  const panel = document.getElementById('deck-sync-conflicts-js');
  const body = document.getElementById('deck-sync-conflicts-body');
  if (!panel || !body) {
    return;
  }

  body.replaceChildren();

  if (printingConflicts.length === 0) {
    panel.classList.add('hidden');
    return;
  }

  printingConflicts.forEach(conflict => {
    const row = document.createElement('tr');
    const cardCell = document.createElement('td');
    cardCell.textContent = conflict.cardName;

    const targetCell = document.createElement('td');
    targetCell.textContent = buildConflictCellText(targetSystem as DeckSyncSystem, conflict);

    const sourceCell = document.createElement('td');
    sourceCell.textContent = buildConflictCellText(sourceSystem as DeckSyncSystem, conflict);

    row.appendChild(cardCell);
    row.appendChild(targetCell);
    row.appendChild(sourceCell);
    body.appendChild(row);
  });

  panel.classList.remove('hidden');
};

const renderDeckSyncResponse = (response: DeckSyncApiResponse): void => {
  const error = document.getElementById('deck-sync-error');
  const results = document.getElementById('deck-sync-results');
  const report = document.getElementById('deck-sync-report');
  const delta = document.getElementById('delta-output') as HTMLTextAreaElement | null;
  const fullImport = document.getElementById('full-import-output') as HTMLTextAreaElement | null;
  const instructions = document.getElementById('deck-sync-instructions');

  if (error) {
    error.classList.add('hidden');
    error.textContent = '';
  }

  if (report) {
    report.textContent = response.reportText;
  }

  if (delta) {
    delta.value = response.deltaText;
  }

  if (fullImport) {
    fullImport.value = response.fullImportText;
  }

  if (instructions) {
    instructions.textContent = response.instructionsText;
  }

  setDeckSyncResultLabels(response.sourceSystem, response.targetSystem);
  renderDeckSyncConflicts(response.printingConflicts, response.sourceSystem, response.targetSystem);

  results?.classList.remove('hidden');
  window.setTimeout(scrollResults, 100);
};

const submitDeckSyncApi = async (form: HTMLFormElement): Promise<void> => {
  const endpoint = form.dataset.deckSyncApi;
  if (!endpoint) {
    return;
  }

  const error = document.getElementById('deck-sync-error');

  try {
    const response = await fetch(endpoint, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json'
      },
      body: JSON.stringify(serializeFormFields(form))
    });

    if (!response.ok) {
      let payload: { message?: string; Message?: string; title?: string; errors?: Record<string, string[]> } | null = null;
      try {
        payload = await response.json() as { message?: string; Message?: string; title?: string; errors?: Record<string, string[]> };
      } catch {
        payload = null;
      }

      if (error) {
        const validationSummary = payload?.errors
          ? Object.values(payload.errors)
              .reduce((messages, current) => messages.concat(current), [] as string[])
              .join(' ')
          : null;
        error.textContent = payload?.message ?? payload?.Message ?? validationSummary ?? payload?.title ?? 'Unable to run deck sync.';
        error.classList.remove('hidden');
      }

      document.getElementById('deck-sync-results')?.classList.add('hidden');
      hideBusyIndicator();
      return;
    }

    renderDeckSyncResponse(await response.json() as DeckSyncApiResponse);
    hideBusyIndicator();
  } catch (requestError) {
    if (error) {
      error.textContent = requestError instanceof Error ? requestError.message : 'Unable to run deck sync.';
      error.classList.remove('hidden');
    }

    document.getElementById('deck-sync-results')?.classList.add('hidden');
    hideBusyIndicator();
  }
};

const attachDeckSyncPersistence = (): void => {
  const form = document.getElementById('deck-sync-form') as HTMLFormElement | null;
  if (!form) {
    return;
  }

  const key = form.getAttribute('data-cache-key');
  if (!key || !storageAvailable) {
    updateSyncInputModeUi();
    updateSyncDirectionUi();
    return;
  }

  const restoredFromTabs = storageAvailable.getItem(tabNavigationKey) === '1';
  if (restoredFromTabs) {
    hydrateFormState(form);
  }

  updateSyncInputModeUi();
  updateSyncDirectionUi();

  const handler = () => persistFormState(form);
  form.addEventListener('input', handler);
  form.addEventListener('change', handler);
  form.addEventListener('submit', event => {
    handler();
    event.preventDefault();
    submitDeckSyncApi(form);
  });

  const clearButton = form.querySelector<HTMLElement>('[data-clear-cache]');
  clearButton?.addEventListener('click', () => {
    const clearHref = clearButton.getAttribute('data-clear-href');
    if (clearHref) {
      clearPersistedFormState(form);
      window.location.href = clearHref;
      return;
    }

    form.reset();
    clearPersistedFormState(form);
    clearDeckSyncUi();
    updateSyncInputModeUi();
    updateSyncDirectionUi();
  });

  document.querySelectorAll<HTMLAnchorElement>('.tool-nav__link').forEach(link => {
    link.addEventListener('click', () => {
      persistFormState(form);
      storageAvailable.setItem(tabNavigationKey, '1');
    });
  });
};

const parseChatGptStep = (value: string | undefined | null): number => {
  const parsedValue = parseInt(value ?? '1', 10);
  return Number.isNaN(parsedValue) || parsedValue < 1 || parsedValue > 5 ? 1 : parsedValue;
};

type ChatGptUiMode = 'guided' | 'focused' | 'expert';

const chatGptUiModeStorageKey = 'decksync-chatgpt-ui-mode';

const parseChatGptUiMode = (value: string | undefined | null): ChatGptUiMode => {
  if (value === 'focused' || value === 'expert') {
    return value;
  }

  return 'guided';
};

const setChatGptValidationMessage = (message: string | null): void => {
  const errorNode = document.querySelector<HTMLElement>('[data-chatgpt-validation-error]');
  if (!errorNode) {
    return;
  }

  if (!message) {
    errorNode.textContent = '';
    errorNode.classList.add('hidden');
    return;
  }

  errorNode.textContent = message;
  errorNode.classList.remove('hidden');
  errorNode.scrollIntoView({ behavior: 'smooth', block: 'center' });
};

const scrollChatGptResults = (form: HTMLFormElement): void => {
  const step = parseChatGptStep(form.dataset.chatgptCurrentStep);
  const activePanel = form.querySelector<HTMLElement>(`[data-chatgpt-step="${step}"]`);
  const resultAnchor = activePanel?.querySelector<HTMLElement>('[data-chatgpt-result-anchor]');
  if (!resultAnchor) {
    return;
  }

  window.setTimeout(() => {
    resultAnchor.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }, 120);
};

const showChatGptStep = (form: HTMLFormElement, step: number): void => {
  form.dataset.chatgptCurrentStep = step.toString();
  const workflowInput = form.querySelector<HTMLInputElement>('[data-chatgpt-workflow-step]');
  if (workflowInput) {
    workflowInput.value = step.toString();
  }

  form.querySelectorAll<HTMLElement>('[data-chatgpt-step]').forEach(panel => {
    const panelStep = parseChatGptStep(panel.dataset.chatgptStep);
    panel.classList.toggle('hidden', panelStep !== step);
    panel.setAttribute('aria-hidden', panelStep === step ? 'false' : 'true');
  });

  form.querySelectorAll<HTMLElement>('[data-chatgpt-show-step]').forEach(button => {
    const buttonStep = parseChatGptStep(button.dataset.chatgptShowStep);
    button.classList.toggle('is-active', buttonStep === step);
    button.setAttribute('aria-selected', buttonStep === step ? 'true' : 'false');
    button.setAttribute('tabindex', buttonStep === step ? '0' : '-1');
  });
};

const applyChatGptUiMode = (form: HTMLFormElement, mode: ChatGptUiMode): void => {
  form.dataset.chatgptUiMode = mode;
  document.body.dataset.chatgptUiMode = mode;
  document.querySelectorAll<HTMLElement>('[data-chatgpt-ui-mode-button]').forEach(button => {
    const buttonMode = parseChatGptUiMode(button.dataset.chatgptUiModeButton);
    button.classList.toggle('is-active', buttonMode === mode);
    button.setAttribute('aria-pressed', buttonMode === mode ? 'true' : 'false');
  });
};

const validateChatGptPacketsStep = (form: HTMLFormElement, step: number): string | null => {
  const importArtifactsPath = form.querySelector<HTMLInputElement>('input[name="ImportArtifactsPath"]')?.value.trim() ?? '';
  if (importArtifactsPath) {
    // When importing a saved artifacts folder, the server rehydrates DeckProfileJson / SetUpgradeResponseJson —
    // skip client-side field validation and let the import path run.
    return null;
  }

  const deckSource = form.querySelector<HTMLTextAreaElement>('textarea[name="DeckSource"]')?.value.trim() ?? '';
  const deckProfileJson = form.querySelector<HTMLTextAreaElement>('textarea[name="DeckProfileJson"]')?.value.trim() ?? '';
  const targetCommanderBracket = form.querySelector<HTMLSelectElement>('select[name="TargetCommanderBracket"]')?.value.trim() ?? '';
  const cardSpecificQuestionCardName = form.querySelector<HTMLInputElement>('input[name="CardSpecificQuestionCardName"]')?.value.trim() ?? '';
  const budgetUpgradeAmount = form.querySelector<HTMLInputElement>('input[name="BudgetUpgradeAmount"]')?.value.trim() ?? '';
  const setPacketText = form.querySelector<HTMLTextAreaElement>('textarea[name="SetPacketText"]')?.value.trim() ?? '';
  const selectedSetCodes = Array.from(
    form.querySelectorAll<HTMLOptionElement>('select[name="SelectedSetCodes"] option:checked')
  );
  const selectedCardSpecificQuestions = form.querySelectorAll<HTMLInputElement>(
    'input[name="SelectedAnalysisQuestions"][value="card-worth-it"]:checked, input[name="SelectedAnalysisQuestions"][value="better-alternatives"]:checked'
  ).length;
  const selectedBudgetQuestions = form.querySelectorAll<HTMLInputElement>(
    'input[name="SelectedAnalysisQuestions"][value="budget-upgrades"]:checked'
  ).length;
  const selectedCategoryQuestions = form.querySelectorAll<HTMLInputElement>(
    'input[name="SelectedAnalysisQuestions"][value="add-categories"]:checked, input[name="SelectedAnalysisQuestions"][value="update-categories"]:checked'
  ).length;
  const decklistExportFormat = form.querySelector<HTMLSelectElement>('select[name="DecklistExportFormat"]')?.value.trim() ?? '';

  if (step < 3 && !deckSource) {
    return 'Paste a deck URL or deck export before generating ChatGPT packets.';
  }

  if (step === 2 && !targetCommanderBracket) {
    return 'Choose the target Commander bracket before generating the analysis packet.';
  }

  if (step === 2 && form.querySelectorAll<HTMLInputElement>('input[name="SelectedAnalysisQuestions"]:checked').length === 0) {
    return 'Select at least one analysis question before generating the analysis packet.';
  }

  if (step === 2 && selectedCardSpecificQuestions > 0 && !cardSpecificQuestionCardName) {
    return 'Enter a card name for the selected card-specific analysis questions.';
  }

  if (step === 2 && selectedBudgetQuestions > 0 && !budgetUpgradeAmount) {
    return 'Enter a budget amount for the selected budget upgrade question.';
  }

  if (step === 2 && selectedCategoryQuestions > 0 && !decklistExportFormat) {
    return 'Choose Moxfield or Archidekt as the export format when assigning or updating categories — plain text does not support inline category formatting.';
  }

  if (step === 3 && !deckProfileJson) {
    return 'Paste the deck_profile JSON returned from ChatGPT before rendering the analysis summary.';
  }

  if (step === 4) {
    if (!deckSource) {
      return 'Paste a deck in Step 1 before generating the set upgrade packet.';
    }

    if (!setPacketText && selectedSetCodes.length > 1) {
      return 'Choose only one set or paste a condensed set packet override before generating the set-upgrade packet.';
    }

    if (!setPacketText && selectedSetCodes.length === 0) {
      return 'Select at least one set or paste a condensed set packet override before generating the set-upgrade packet.';
    }
  }

  if (step === 5) {
    const setUpgradeResponseJson = form.querySelector<HTMLTextAreaElement>('textarea[name="SetUpgradeResponseJson"]')?.value.trim() ?? '';
    if (!setUpgradeResponseJson) {
      return 'Paste the set_upgrade_report JSON returned from ChatGPT before rendering the set upgrade results.';
    }
  }

  return null;
};

const syncCardSpecificQuestionField = (form: HTMLFormElement): void => {
  const field = form.querySelector<HTMLElement>('[data-card-specific-question-field]');
  if (!field) {
    return;
  }

  const hasCardSpecificQuestion = form.querySelectorAll<HTMLInputElement>(
    'input[name="SelectedAnalysisQuestions"][value="card-worth-it"]:checked, input[name="SelectedAnalysisQuestions"][value="better-alternatives"]:checked'
  ).length > 0;

  field.classList.toggle('hidden', !hasCardSpecificQuestion);
};

const syncBudgetQuestionField = (form: HTMLFormElement): void => {
  const field = form.querySelector<HTMLElement>('[data-budget-question-field]');
  if (!field) {
    return;
  }

  const hasBudgetQuestion = form.querySelectorAll<HTMLInputElement>(
    'input[name="SelectedAnalysisQuestions"][value="budget-upgrades"]:checked'
  ).length > 0;

  field.classList.toggle('hidden', !hasBudgetQuestion);
};

const syncPreferredCategoriesField = (form: HTMLFormElement): void => {
  const field = form.querySelector<HTMLElement>('[data-preferred-categories-field]');
  if (!field) {
    return;
  }

  const hasUpdateCategories = form.querySelectorAll<HTMLInputElement>(
    'input[name="SelectedAnalysisQuestions"][value="update-categories"]:checked'
  ).length > 0;

  field.classList.toggle('hidden', !hasUpdateCategories);
};

const bracketToVersionQuestionId: Readonly<Record<string, string>> = {
  core: 'bracket-2-version',
  upgraded: 'bracket-3-version',
  optimized: 'bracket-4-version',
  cedh: 'bracket-5-version',
};

const syncVersioningBracketOptions = (form: HTMLFormElement): void => {
  const bracketSelect = form.querySelector<HTMLSelectElement>('select[name="TargetCommanderBracket"]');
  const selectedBracket = (bracketSelect?.value ?? '').toLowerCase();
  const disabledQuestionId = bracketToVersionQuestionId[selectedBracket] ?? null;

  Object.values(bracketToVersionQuestionId).forEach(questionId => {
    const checkbox = form.querySelector<HTMLInputElement>(`input[name="SelectedAnalysisQuestions"][value="${questionId}"]`);
    if (!checkbox) return;
    const shouldDisable = questionId === disabledQuestionId;
    checkbox.disabled = shouldDisable;
    if (shouldDisable && checkbox.checked) {
      checkbox.checked = false;
    }
    checkbox.closest('label')?.classList.toggle('chatgpt-question-option--disabled', shouldDisable);
  });

  syncQuestionBucketState(form);
};

const syncQuestionBucketState = (form: HTMLFormElement): void => {
  form.querySelectorAll<HTMLInputElement>('[data-question-bucket]').forEach(bucketCheckbox => {
    const bucketId = bucketCheckbox.dataset.questionBucket ?? '';
    const questionCheckboxes = Array.from(
      form.querySelectorAll<HTMLInputElement>(`input[data-question-option="${bucketId}"]`)
    );

    if (questionCheckboxes.length === 0) {
      bucketCheckbox.checked = false;
      bucketCheckbox.indeterminate = false;
      return;
    }

    const checkedCount = questionCheckboxes.filter(checkbox => checkbox.checked).length;
    bucketCheckbox.checked = checkedCount === questionCheckboxes.length;
    bucketCheckbox.indeterminate = checkedCount > 0 && checkedCount < questionCheckboxes.length;
  });
};

const attachBucketToggles = (form: HTMLFormElement): void => {
  form.querySelectorAll<HTMLButtonElement>('[data-bucket-toggle]').forEach(toggleBtn => {
    toggleBtn.addEventListener('click', () => {
      const bucketId = toggleBtn.dataset.bucketToggle ?? '';
      const questionsDiv = form.querySelector<HTMLElement>(`[data-bucket-questions="${bucketId}"]`);
      if (!questionsDiv) {
        return;
      }
      const nowHidden = questionsDiv.classList.toggle('hidden');
      toggleBtn.setAttribute('aria-expanded', nowHidden ? 'false' : 'true');
    });
  });
};

const attachQuestionBucketSelection = (form: HTMLFormElement): void => {
  form.querySelectorAll<HTMLInputElement>('[data-question-bucket]').forEach(bucketCheckbox => {
    bucketCheckbox.addEventListener('change', () => {
      const bucketId = bucketCheckbox.dataset.questionBucket ?? '';
      const questionsDiv = form.querySelector<HTMLElement>(`[data-bucket-questions="${bucketId}"]`);

      if (bucketId === 'deck-versioning') {
        // Checking the bucket header selects only the three-upgrade-paths question
        form.querySelectorAll<HTMLInputElement>(`input[data-question-option="${bucketId}"]`).forEach(questionCheckbox => {
          questionCheckbox.checked = bucketCheckbox.checked && questionCheckbox.value === 'three-upgrade-paths' && !questionCheckbox.disabled;
        });
      } else {
        form.querySelectorAll<HTMLInputElement>(`input[data-question-option="${bucketId}"]`).forEach(questionCheckbox => {
          questionCheckbox.checked = bucketCheckbox.checked;
        });
      }

      // Auto-expand the bucket when the select-all checkbox is checked
      if (bucketCheckbox.checked && questionsDiv?.classList.contains('hidden')) {
        questionsDiv.classList.remove('hidden');
        const toggleBtn = form.querySelector<HTMLButtonElement>(`[data-bucket-toggle="${bucketId}"]`);
        toggleBtn?.setAttribute('aria-expanded', 'true');
      }

      syncQuestionBucketState(form);
      syncCardSpecificQuestionField(form);
      syncBudgetQuestionField(form);
    });
  });

  form.querySelectorAll<HTMLInputElement>('input[data-question-option]').forEach(questionCheckbox => {
    questionCheckbox.addEventListener('change', () => {
      const bucketId = questionCheckbox.dataset.questionOption ?? '';

      // Single-select for deck-versioning: checking one unchecks all siblings
      if (bucketId === 'deck-versioning' && questionCheckbox.checked) {
        form.querySelectorAll<HTMLInputElement>(`input[data-question-option="${bucketId}"]`).forEach(sibling => {
          if (sibling !== questionCheckbox) {
            sibling.checked = false;
          }
        });
      }

      syncQuestionBucketState(form);
      syncCardSpecificQuestionField(form);
      syncBudgetQuestionField(form);
      syncPreferredCategoriesField(form);
    });
  });

  syncQuestionBucketState(form);
  syncCardSpecificQuestionField(form);
  syncBudgetQuestionField(form);
  syncPreferredCategoriesField(form);
};

const loadSetOptionsAsync = (): void => {
  const form = document.querySelector<HTMLFormElement>('[data-chatgpt-packets-form]');
  const select = form?.querySelector<HTMLSelectElement>('[data-set-options-select]');
  if (!form || !select) {
    return;
  }

  const setOptionsUrl = form.dataset.setOptionsUrl?.trim();
  if (!setOptionsUrl) {
    return;
  }

  const selectedCodes = new Set(
    (select.dataset.selectedCodes ?? '').split(',').map(c => c.trim().toLowerCase()).filter(Boolean)
  );

  fetch(setOptionsUrl)
    .then(response => {
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      return response.json() as Promise<Array<{ code: string; displayLabel: string }>>;
    })
    .then(sets => {
      select.innerHTML = '';
      for (const set of sets) {
        const option = document.createElement('option');
        option.value = set.code;
        option.textContent = set.displayLabel;
        if (selectedCodes.has(set.code.toLowerCase())) {
          option.selected = true;
        }
        select.appendChild(option);
      }
    })
    .catch(() => {
      const errorHint = document.querySelector<HTMLElement>('[data-set-options-error]');
      errorHint?.classList.remove('hidden');
    });
};

const attachChatGptPacketsWorkflow = (): void => {
  const form = document.querySelector<HTMLFormElement>('[data-chatgpt-packets-form]');
  if (!form) {
    return;
  }

  const currentStep = parseChatGptStep(form.dataset.chatgptCurrentStep);
  const initialUiMode = parseChatGptUiMode(storageAvailable?.getItem(chatGptUiModeStorageKey));
  attachQuestionBucketSelection(form);
  attachBucketToggles(form);

  const bracketSelect = form.querySelector<HTMLSelectElement>('select[name="TargetCommanderBracket"]');
  bracketSelect?.addEventListener('change', () => syncVersioningBracketOptions(form));
  syncVersioningBracketOptions(form);

  applyChatGptUiMode(form, initialUiMode);
  showChatGptStep(form, currentStep);
  setChatGptValidationMessage(null);
  scrollChatGptResults(form);

  document.querySelectorAll<HTMLElement>('[data-chatgpt-ui-mode-button]').forEach(button => {
    button.addEventListener('click', () => {
      const mode = parseChatGptUiMode(button.dataset.chatgptUiModeButton);
      applyChatGptUiMode(form, mode);
      storageAvailable?.setItem(chatGptUiModeStorageKey, mode);
    });
  });

  form.querySelectorAll<HTMLElement>('[data-chatgpt-show-step]').forEach(button => {
    button.addEventListener('click', () => {
      const step = parseChatGptStep(button.dataset.chatgptShowStep);
      showChatGptStep(form, step);
      setChatGptValidationMessage(null);
    });
  });

  form.querySelectorAll<HTMLElement>('[data-chatgpt-next-step]').forEach(button => {
    button.addEventListener('click', () => {
      const step = parseChatGptStep(button.dataset.chatgptNextStep);
      showChatGptStep(form, step);
      setChatGptValidationMessage(null);
      form.querySelector<HTMLElement>(`[data-chatgpt-step="${step}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    });
  });

  form.addEventListener('submit', event => {
    const submitter = (event as SubmitEvent).submitter as HTMLElement | null;
    const step = parseChatGptStep(submitter?.dataset.chatgptSubmitStep ?? form.dataset.chatgptCurrentStep);
    const validationMessage = validateChatGptPacketsStep(form, step);
    if (!validationMessage) {
      setChatGptValidationMessage(null);
      showChatGptStep(form, step);
      return;
    }

    event.preventDefault();
    hideBusyIndicator();
    showChatGptStep(form, step);
    setChatGptValidationMessage(validationMessage);
  });
};

const parseChatGptComparisonStep = (value: string | undefined | null): number => {
  const parsedValue = parseInt(value ?? '1', 10);
  return Number.isNaN(parsedValue) || parsedValue < 1 || parsedValue > 3 ? 1 : parsedValue;
};

const setChatGptComparisonValidationMessage = (message: string | null): void => {
  const errorNode = document.querySelector<HTMLElement>('[data-chatgpt-comparison-validation-error]');
  if (!errorNode) {
    return;
  }

  if (!message) {
    errorNode.textContent = '';
    errorNode.classList.add('hidden');
    return;
  }

  errorNode.textContent = message;
  errorNode.classList.remove('hidden');
  errorNode.scrollIntoView({ behavior: 'smooth', block: 'center' });
};

const showChatGptComparisonStep = (form: HTMLFormElement, step: number): void => {
  form.dataset.chatgptComparisonCurrentStep = step.toString();
  const workflowInput = form.querySelector<HTMLInputElement>('[data-chatgpt-comparison-workflow-step]');
  if (workflowInput) {
    workflowInput.value = step.toString();
  }

  form.querySelectorAll<HTMLElement>('[data-chatgpt-comparison-step]').forEach(panel => {
    const panelStep = parseChatGptComparisonStep(panel.dataset.chatgptComparisonStep);
    panel.classList.toggle('hidden', panelStep !== step);
    panel.setAttribute('aria-hidden', panelStep === step ? 'false' : 'true');
  });

  form.querySelectorAll<HTMLElement>('[data-chatgpt-comparison-show-step]').forEach(button => {
    const buttonStep = parseChatGptComparisonStep(button.dataset.chatgptComparisonShowStep);
    button.classList.toggle('is-active', buttonStep === step);
    button.setAttribute('aria-selected', buttonStep === step ? 'true' : 'false');
    button.setAttribute('tabindex', buttonStep === step ? '0' : '-1');
  });
};

const scrollChatGptComparisonResults = (form: HTMLFormElement): void => {
  const step = parseChatGptComparisonStep(form.dataset.chatgptComparisonCurrentStep);
  const activePanel = form.querySelector<HTMLElement>(`[data-chatgpt-comparison-step="${step}"]`);
  const resultAnchor = activePanel?.querySelector<HTMLElement>('[data-chatgpt-comparison-result-anchor]');
  if (!resultAnchor) {
    return;
  }

  window.setTimeout(() => {
    resultAnchor.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }, 120);
};

const validateChatGptComparisonStep = (form: HTMLFormElement, step: number): string | null => {
  const deckASource = form.querySelector<HTMLTextAreaElement>('textarea[name="DeckASource"]')?.value.trim() ?? '';
  const deckBSource = form.querySelector<HTMLTextAreaElement>('textarea[name="DeckBSource"]')?.value.trim() ?? '';
  const deckABracket = form.querySelector<HTMLSelectElement>('select[name="DeckABracket"]')?.value.trim() ?? '';
  const deckBBracket = form.querySelector<HTMLSelectElement>('select[name="DeckBBracket"]')?.value.trim() ?? '';
  const comparisonResponseJson = form.querySelector<HTMLTextAreaElement>('textarea[name="ComparisonResponseJson"]')?.value.trim() ?? '';

  if (!deckASource) {
    return 'Enter Deck A URL or deck text before generating the comparison packet.';
  }

  if (!deckBSource) {
    return 'Enter Deck B URL or deck text before generating the comparison packet.';
  }

  if (!deckABracket) {
    return 'Choose a Commander bracket for Deck A before generating the comparison packet.';
  }

  if (!deckBBracket) {
    return 'Choose a Commander bracket for Deck B before generating the comparison packet.';
  }

  if (step >= 3 && !comparisonResponseJson) {
    return 'Paste the deck_comparison JSON returned from ChatGPT into Step 3 before rendering the summary.';
  }

  return null;
};

const attachChatGptComparisonWorkflow = (): void => {
  const form = document.querySelector<HTMLFormElement>('[data-chatgpt-comparison-form]');
  if (!form) {
    return;
  }

  const currentStep = parseChatGptComparisonStep(form.dataset.chatgptComparisonCurrentStep);
  showChatGptComparisonStep(form, currentStep);
  setChatGptComparisonValidationMessage(null);
  scrollChatGptComparisonResults(form);

  form.querySelectorAll<HTMLElement>('[data-chatgpt-comparison-show-step]').forEach(button => {
    button.addEventListener('click', () => {
      const step = parseChatGptComparisonStep(button.dataset.chatgptComparisonShowStep);
      showChatGptComparisonStep(form, step);
      setChatGptComparisonValidationMessage(null);
    });
  });

  form.querySelectorAll<HTMLElement>('[data-chatgpt-comparison-next-step]').forEach(button => {
    button.addEventListener('click', () => {
      const step = parseChatGptComparisonStep(button.dataset.chatgptComparisonNextStep);
      const validationMessage = validateChatGptComparisonStep(form, Math.min(step, 2));
      if (validationMessage) {
        setChatGptComparisonValidationMessage(validationMessage);
        return;
      }

      showChatGptComparisonStep(form, step);
      setChatGptComparisonValidationMessage(null);
      form.querySelector<HTMLElement>(`[data-chatgpt-comparison-step="${step}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    });
  });

  form.addEventListener('submit', event => {
    const submitter = (event as SubmitEvent).submitter as HTMLElement | null;
    const step = parseChatGptComparisonStep(submitter?.dataset.chatgptComparisonSubmitStep ?? form.dataset.chatgptComparisonCurrentStep);
    const validationMessage = validateChatGptComparisonStep(form, step);
    if (!validationMessage) {
      setChatGptComparisonValidationMessage(null);
      showChatGptComparisonStep(form, step);
      return;
    }

    event.preventDefault();
    hideBusyIndicator();
    showChatGptComparisonStep(form, step);
    setChatGptComparisonValidationMessage(validationMessage);
  });
};

const parseChatGptCedhStep = (value: string | undefined | null): number => {
  const parsedValue = parseInt(value ?? '1', 10);
  return Number.isNaN(parsedValue) || parsedValue < 1 || parsedValue > 3 ? 1 : parsedValue;
};

const parseChatGptCedhPage = (value: string | undefined | null): number => {
  const parsedValue = parseInt(value ?? '1', 10);
  return Number.isNaN(parsedValue) || parsedValue < 1 ? 1 : parsedValue;
};

const maxChatGptCedhReferences = 3;

const setChatGptCedhValidationMessage = (message: string | null): void => {
  const errorNode = document.querySelector<HTMLElement>('[data-chatgpt-cedh-validation-error]');
  if (!errorNode) {
    return;
  }

  if (!message) {
    errorNode.textContent = '';
    errorNode.classList.add('hidden');
    return;
  }

  errorNode.textContent = message;
  errorNode.classList.remove('hidden');
  errorNode.scrollIntoView({ behavior: 'smooth', block: 'center' });
};

const showChatGptCedhStep = (form: HTMLFormElement, step: number): void => {
  form.dataset.chatgptCedhCurrentStep = step.toString();
  const workflowInput = form.querySelector<HTMLInputElement>('[data-chatgpt-cedh-workflow-step]');
  if (workflowInput) {
    workflowInput.value = step.toString();
  }

  form.querySelectorAll<HTMLElement>('[data-chatgpt-cedh-step]').forEach(panel => {
    const panelStep = parseChatGptCedhStep(panel.dataset.chatgptCedhStep);
    panel.classList.toggle('hidden', panelStep !== step);
    panel.setAttribute('aria-hidden', panelStep === step ? 'false' : 'true');
  });

  form.querySelectorAll<HTMLElement>('[data-chatgpt-cedh-show-step]').forEach(button => {
    const buttonStep = parseChatGptCedhStep(button.dataset.chatgptCedhShowStep);
    button.classList.toggle('is-active', buttonStep === step);
    button.setAttribute('aria-selected', buttonStep === step ? 'true' : 'false');
    button.setAttribute('tabindex', buttonStep === step ? '0' : '-1');
  });
};

const scrollChatGptCedhResults = (form: HTMLFormElement): void => {
  const step = parseChatGptCedhStep(form.dataset.chatgptCedhCurrentStep);
  const activePanel = form.querySelector<HTMLElement>(`[data-chatgpt-cedh-step="${step}"]`);
  const resultAnchor = activePanel?.querySelector<HTMLElement>('[data-chatgpt-cedh-result-anchor]');
  if (!resultAnchor) {
    return;
  }

  window.setTimeout(() => {
    resultAnchor.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }, 120);
};

const validateChatGptCedhStep = (form: HTMLFormElement, step: number): string | null => {
  if (step === 1) {
    const deckSource = form.querySelector<HTMLTextAreaElement>('textarea[name="DeckSource"]')?.value.trim() ?? '';
    if (!deckSource) {
      return 'Paste your deck URL or deck text before fetching EDH Top 16 reference decks.';
    }
  }

  if (step === 2) {
    const checkedReferences = form.querySelectorAll<HTMLInputElement>('[data-chatgpt-cedh-reference-checkbox]:checked').length;
    if (checkedReferences < 1) {
      return 'Select at least 1 EDH Top 16 reference deck before generating the prompt.';
    }

    if (checkedReferences > maxChatGptCedhReferences) {
      return `Select no more than ${maxChatGptCedhReferences} EDH Top 16 reference decks before generating the prompt.`;
    }
  }

  if (step === 3) {
    const responseJson = form.querySelector<HTMLTextAreaElement>('textarea[name="MetaGapResponseJson"]')?.value.trim() ?? '';
    if (!responseJson) {
      return 'Paste the meta_gap JSON returned from ChatGPT into Step 3 before rendering the analysis.';
    }
  }

  return null;
};

const syncChatGptCedhCheckboxState = (form: HTMLFormElement): void => {
  const checkboxes = Array.from(form.querySelectorAll<HTMLInputElement>('[data-chatgpt-cedh-reference-checkbox]'));
  const checkedCount = checkboxes.filter(checkbox => checkbox.checked).length;
  checkboxes.forEach(checkbox => {
    checkbox.disabled = !checkbox.checked && checkedCount >= maxChatGptCedhReferences;
  });
};

const showChatGptCedhReferencePage = (form: HTMLFormElement, page: number): void => {
  const rowsWithPages = Array.from(form.querySelectorAll<HTMLElement>('[data-chatgpt-cedh-reference-row]')).map(row => ({
    row,
    page: parseChatGptCedhPage(row.dataset.chatgptCedhPage)
  }));
  if (rowsWithPages.length === 0) {
    return;
  }

  const maxPage = Math.max(...rowsWithPages.map(({ page: rowPage }) => rowPage));
  const nextPage = Math.min(Math.max(page, 1), maxPage);

  rowsWithPages.forEach(({ row, page: rowPage }) => {
    row.classList.toggle('hidden', rowPage !== nextPage);
  });

  form.dataset.chatgptCedhReferencePage = nextPage.toString();
  const status = form.querySelector<HTMLElement>('[data-chatgpt-cedh-page-status]');
  if (status) {
    status.textContent = `Page ${nextPage} of ${maxPage}`;
  }

  const prevButton = form.querySelector<HTMLButtonElement>('[data-chatgpt-cedh-page-nav="prev"]');
  const nextButton = form.querySelector<HTMLButtonElement>('[data-chatgpt-cedh-page-nav="next"]');
  if (prevButton) {
    prevButton.disabled = nextPage <= 1;
  }

  if (nextButton) {
    nextButton.disabled = nextPage >= maxPage;
  }
};

const attachChatGptCedhWorkflow = (): void => {
  const form = document.querySelector<HTMLFormElement>('[data-chatgpt-cedh-form]');
  if (!form) {
    return;
  }

  const currentStep = parseChatGptCedhStep(form.dataset.chatgptCedhCurrentStep);
  showChatGptCedhStep(form, currentStep);
  setChatGptCedhValidationMessage(null);
  syncChatGptCedhCheckboxState(form);
  showChatGptCedhReferencePage(form, parseChatGptCedhPage(form.dataset.chatgptCedhReferencePage));
  scrollChatGptCedhResults(form);

  form.querySelectorAll<HTMLInputElement>('[data-chatgpt-cedh-reference-checkbox]').forEach(checkbox => {
    checkbox.addEventListener('change', () => {
      syncChatGptCedhCheckboxState(form);
      setChatGptCedhValidationMessage(null);
    });
  });

  form.querySelectorAll<HTMLElement>('[data-chatgpt-cedh-show-step]').forEach(button => {
    button.addEventListener('click', () => {
      const step = parseChatGptCedhStep(button.dataset.chatgptCedhShowStep);
      showChatGptCedhStep(form, step);
      setChatGptCedhValidationMessage(null);
    });
  });

  form.querySelectorAll<HTMLElement>('[data-chatgpt-cedh-next-step]').forEach(button => {
    button.addEventListener('click', () => {
      const step = parseChatGptCedhStep(button.dataset.chatgptCedhNextStep);
      showChatGptCedhStep(form, step);
      setChatGptCedhValidationMessage(null);
      form.querySelector<HTMLElement>(`[data-chatgpt-cedh-step="${step}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    });
  });

  form.querySelectorAll<HTMLButtonElement>('[data-chatgpt-cedh-page-nav]').forEach(button => {
    button.addEventListener('click', () => {
      const currentPage = parseChatGptCedhPage(form.dataset.chatgptCedhReferencePage);
      const delta = button.dataset.chatgptCedhPageNav === 'next' ? 1 : -1;
      showChatGptCedhReferencePage(form, currentPage + delta);
    });
  });

  form.addEventListener('submit', event => {
    const submitter = (event as SubmitEvent).submitter as HTMLElement | null;
    const step = parseChatGptCedhStep(submitter?.dataset.chatgptCedhSubmitStep ?? form.dataset.chatgptCedhCurrentStep);
    const validationMessage = validateChatGptCedhStep(form, step);
    if (!validationMessage) {
      setChatGptCedhValidationMessage(null);
      showChatGptCedhStep(form, step);
      return;
    }

    event.preventDefault();
    hideBusyIndicator();
    showChatGptCedhStep(form, step);
    setChatGptCedhValidationMessage(validationMessage);
  });
};

interface Window {
  setAllPrintingChoices?: (value: string) => void;
  hideBusyIndicator?: () => void;
}

window.setAllPrintingChoices = setAllPrintingChoices;
window.hideBusyIndicator = hideBusyIndicator;

let deckSyncBootstrapped = false;

const bootstrapDeckSync = (): void => {
  if (deckSyncBootstrapped) {
    return;
  }

  deckSyncBootstrapped = true;
  initializeSyncInputModeUi();
  registerBusyIndicator();
  attachActionButtons();
  attachGenericPersistedForms();
  attachDeckSyncPersistence();
  attachChatGptPacketsWorkflow();
  attachChatGptComparisonWorkflow();
  attachChatGptCedhWorkflow();
  loadSetOptionsAsync();
  attachToolNav();
  attachConvertForm();
};

const attachToolNav = (): void => {
  const nav = document.querySelector<HTMLElement>('[data-tool-nav]');
  if (!nav) return;

  nav.querySelectorAll<HTMLButtonElement>('[data-tool-nav-trigger]').forEach(trigger => {
    trigger.addEventListener('click', () => {
      const group = trigger.closest<HTMLElement>('[data-tool-nav-group]');
      if (!group) return;
      const isOpen = group.classList.contains('is-open');
      nav.querySelectorAll<HTMLElement>('[data-tool-nav-group]').forEach(g => {
        g.classList.remove('is-open');
        g.querySelector<HTMLButtonElement>('[data-tool-nav-trigger]')?.setAttribute('aria-expanded', 'false');
      });
      if (!isOpen) {
        group.classList.add('is-open');
        trigger.setAttribute('aria-expanded', 'true');
      }
    });
  });

  document.addEventListener('click', event => {
    if (!nav.contains(event.target as Node)) {
      nav.querySelectorAll<HTMLElement>('[data-tool-nav-group]').forEach(g => {
        g.classList.remove('is-open');
        g.querySelector<HTMLButtonElement>('[data-tool-nav-trigger]')?.setAttribute('aria-expanded', 'false');
      });
    }
  });
};

const attachConvertForm = (): void => {
  const form = document.querySelector<HTMLFormElement>('form[data-cache-key="deck-convert"]');
  if (!form) return;

  const inputSourceSelect = form.querySelector<HTMLSelectElement>('select[name="InputSource"]');
  const sourceFormatSelect = form.querySelector<HTMLSelectElement>('[data-convert-source]');
  const urlPanel = form.querySelector<HTMLElement>('[data-convert-panel="url"]');
  const textPanel = form.querySelector<HTMLElement>('[data-convert-panel="text"]');
  const commanderPanel = form.querySelector<HTMLElement>('[data-convert-panel="commander"]');

  const syncConvertPanels = (): void => {
    const isUrl = inputSourceSelect?.value === 'PublicUrl';
    urlPanel?.classList.toggle('hidden', !isUrl);
    textPanel?.classList.toggle('hidden', isUrl);

    const isMoxfield = sourceFormatSelect?.value === 'Moxfield';
    commanderPanel?.classList.toggle('hidden', !isMoxfield);
  };

  inputSourceSelect?.addEventListener('change', syncConvertPanels);
  sourceFormatSelect?.addEventListener('change', syncConvertPanels);
  syncConvertPanels();

  const commanderInput = form.querySelector<HTMLInputElement>('input[data-commander-search]');
  if (commanderInput) {
    const endpoint = commanderInput.dataset.commanderSearch!;
    const datalist = document.getElementById('commander-suggestions') as HTMLDataListElement | null;
    let debounceTimer: number | undefined;

    commanderInput.addEventListener('input', () => {
      window.clearTimeout(debounceTimer);
      const query = commanderInput.value.trim();
      if (query.length < 2) {
        if (datalist) datalist.innerHTML = '';
        return;
      }

      debounceTimer = window.setTimeout(async () => {
        try {
          const response = await fetch(`${endpoint}?q=${encodeURIComponent(query)}`);
          if (!response.ok || !datalist) return;
          const names = await response.json() as string[];
          datalist.innerHTML = '';
          names.forEach(name => {
            const option = document.createElement('option');
            option.value = name;
            datalist.appendChild(option);
          });
        } catch {
          // ignore — typeahead is best-effort
        }
      }, 300);
    });
  }
};

document.addEventListener('DOMContentLoaded', bootstrapDeckSync);
if (document.readyState !== 'loading') {
  bootstrapDeckSync();
}
