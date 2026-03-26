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

  const moxfieldIsSource = directionSelect.value === 'DeckSyncWorkbench';
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

const serializeFormFields = (form: HTMLFormElement) => {
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

const restoreFormFields = (form: HTMLFormElement, data: Record<string, string>) => {
  form.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>('[name]').forEach(element => {
    const value = data[element.name];
    if (value === undefined) {
      return;
    }

    if (element instanceof HTMLInputElement && (element.type === 'checkbox' || element.type === 'radio')) {
      element.checked = element.value === value;
      return;
    }

    element.value = value;
  });
};

const persistFormState = (form: HTMLFormElement): void => {
  const key = form.getAttribute('data-cache-key');
  if (!key || !storageAvailable) {
    return;
  }

  const state = serializeFormFields(form);
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
    const state = JSON.parse(json) as Record<string, string>;
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
  attachDeckSyncPersistence();
};

document.addEventListener('DOMContentLoaded', bootstrapDeckSync);
if (document.readyState !== 'loading') {
  bootstrapDeckSync();
}
