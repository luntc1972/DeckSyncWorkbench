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

  const moxfieldIsSource = directionSelect.value === 'MoxfieldToArchidekt';
  const moxfieldStatus = document.querySelector<HTMLElement>('[data-sync-role="moxfield-status"]');
  const archidektStatus = document.querySelector<HTMLElement>('[data-sync-role="archidekt-status"]');
  const moxfieldHint = document.querySelector<HTMLElement>('[data-sync-role="moxfield-hint"]');
  const archidektHint = document.querySelector<HTMLElement>('[data-sync-role="archidekt-hint"]');

  if (moxfieldStatus) {
    moxfieldStatus.textContent = moxfieldIsSource ? 'Source deck' : 'Target deck';
  }

  if (archidektStatus) {
    archidektStatus.textContent = moxfieldIsSource ? 'Target deck' : 'Source deck';
  }

  if (moxfieldHint) {
    moxfieldHint.textContent = `Use this when the Moxfield deck is ${moxfieldIsSource ? 'the source' : 'the target'}.`;
  }

  if (archidektHint) {
    archidektHint.textContent = `Use this when the Archidekt deck is ${moxfieldIsSource ? 'the target' : 'the source'}.`;
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

const attachActionButtons = (): void => {
  document.querySelectorAll<HTMLElement>('[data-copy-target]').forEach(button => {
    button.addEventListener('click', async () => {
      const targetId = button.dataset.copyTarget;
      if (!targetId) {
        return;
      }

      await copyElementValue(targetId);
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
    storageAvailable.removeItem(tabNavigationKey);
  } else {
    storageAvailable.removeItem(`${formStateStoragePrefix}${key}`);
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
    form.reset();
    storageAvailable.removeItem(`${formStateStoragePrefix}${key}`);
    clearDeckSyncUi();
    updateSyncInputModeUi();
    updateSyncDirectionUi();
  });

  document.querySelectorAll<HTMLAnchorElement>('.tab-bar .tab-link').forEach(link => {
    link.addEventListener('click', () => {
      persistFormState(form);
      storageAvailable.setItem(tabNavigationKey, '1');
    });
  });
};

const parseChatGptStep = (value: string | undefined | null): number => {
  const parsedValue = parseInt(value ?? '1', 10);
  return Number.isNaN(parsedValue) || parsedValue < 1 || parsedValue > 4 ? 1 : parsedValue;
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
  });

  form.querySelectorAll<HTMLElement>('[data-chatgpt-show-step]').forEach(button => {
    const buttonStep = parseChatGptStep(button.dataset.chatgptShowStep);
    button.classList.toggle('is-active', buttonStep === step);
    button.setAttribute('aria-pressed', buttonStep === step ? 'true' : 'false');
  });
};

const validateChatGptPacketsStep = (form: HTMLFormElement, step: number): string | null => {
  const deckSource = form.querySelector<HTMLTextAreaElement>('textarea[name="DeckSource"]')?.value.trim() ?? '';
  const probeResponseJson = form.querySelector<HTMLTextAreaElement>('textarea[name="ProbeResponseJson"]')?.value.trim() ?? '';
  const deckProfileJson = form.querySelector<HTMLTextAreaElement>('textarea[name="DeckProfileJson"]')?.value.trim() ?? '';
  const targetCommanderBracket = form.querySelector<HTMLSelectElement>('select[name="TargetCommanderBracket"]')?.value.trim() ?? '';
  const cardSpecificQuestionCardName = form.querySelector<HTMLInputElement>('input[name="CardSpecificQuestionCardName"]')?.value.trim() ?? '';
  const setPacketText = form.querySelector<HTMLTextAreaElement>('textarea[name="SetPacketText"]')?.value.trim() ?? '';
  const selectedSetCodes = Array.from(
    form.querySelectorAll<HTMLOptionElement>('select[name="SelectedSetCodes"] option:checked')
  );
  const selectedCardSpecificQuestions = form.querySelectorAll<HTMLInputElement>(
    'input[name="SelectedAnalysisQuestions"][value="card-worth-it"]:checked, input[name="SelectedAnalysisQuestions"][value="better-alternatives"]:checked'
  ).length;

  if (!deckSource) {
    return 'Paste a deck URL or deck export before generating ChatGPT packets.';
  }

  if (step >= 2 && !probeResponseJson) {
    return 'Paste the JSON returned from ChatGPT into Probe response JSON before generating the analysis packet.';
  }

  if (step >= 3 && !targetCommanderBracket) {
    return 'Choose the target Commander bracket before generating the analysis packet.';
  }

  if (step >= 3 && form.querySelectorAll<HTMLInputElement>('input[name="SelectedAnalysisQuestions"]:checked').length === 0) {
    return 'Select at least one analysis question before generating the analysis packet.';
  }

  if (step >= 3 && selectedCardSpecificQuestions > 0 && !cardSpecificQuestionCardName) {
    return 'Enter a card name for the selected card-specific analysis questions.';
  }

  if (step >= 4) {
    if (!deckProfileJson) {
      return 'Paste the deck_profile JSON returned from ChatGPT into Deck profile JSON before generating the set-upgrade packet.';
    }

    if (!setPacketText && selectedSetCodes.length === 0) {
      return 'Select at least one set or paste a condensed set packet override before generating the set-upgrade packet.';
    }
  }

  return null;
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

const attachQuestionBucketSelection = (form: HTMLFormElement): void => {
  form.querySelectorAll<HTMLInputElement>('[data-question-bucket]').forEach(bucketCheckbox => {
    bucketCheckbox.addEventListener('change', () => {
      const bucketId = bucketCheckbox.dataset.questionBucket ?? '';
      form.querySelectorAll<HTMLInputElement>(`input[data-question-option="${bucketId}"]`).forEach(questionCheckbox => {
        questionCheckbox.checked = bucketCheckbox.checked;
      });

      syncQuestionBucketState(form);
    });
  });

  form.querySelectorAll<HTMLInputElement>('input[data-question-option]').forEach(questionCheckbox => {
    questionCheckbox.addEventListener('change', () => {
      syncQuestionBucketState(form);
    });
  });

  syncQuestionBucketState(form);
};

const attachChatGptPacketsWorkflow = (): void => {
  const form = document.querySelector<HTMLFormElement>('[data-chatgpt-packets-form]');
  if (!form) {
    return;
  }

  const currentStep = parseChatGptStep(form.dataset.chatgptCurrentStep);
  attachQuestionBucketSelection(form);
  showChatGptStep(form, currentStep);
  setChatGptValidationMessage(null);
  scrollChatGptResults(form);

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
  attachDeckSyncPersistence();
  attachChatGptPacketsWorkflow();
};

document.addEventListener('DOMContentLoaded', bootstrapDeckSync);
if (document.readyState !== 'loading') {
  bootstrapDeckSync();
}
