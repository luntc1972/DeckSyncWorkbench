(function () {
  'use strict';

  const allowedOriginsStorageKey = 'deckflowAllowedOrigins';
  const defaultAllowedOrigins = [
    'http://localhost',
    'https://localhost',
    'http://127.0.0.1',
    'https://127.0.0.1'
  ];

  const textarea = document.getElementById('allowed-origins');
  const saveButton = document.getElementById('save-button');
  const addRecommendedButton = document.getElementById('add-recommended-button');
  const status = document.getElementById('status');
  const recommendedOriginPanel = document.getElementById('recommended-origin');
  const recommendedOriginValue = document.getElementById('recommended-origin-value');

  if (!(textarea instanceof HTMLTextAreaElement)
    || !(saveButton instanceof HTMLButtonElement)
    || !(addRecommendedButton instanceof HTMLButtonElement)
    || !(status instanceof HTMLElement)
    || !(recommendedOriginPanel instanceof HTMLElement)
    || !(recommendedOriginValue instanceof HTMLElement)) {
    return;
  }

  const requestedOrigin = readRequestedOrigin();

  initialize().catch((error) => {
    status.textContent = error instanceof Error ? error.message : String(error);
  });

  async function initialize() {
    const storedOrigins = await loadAllowedOrigins();
    textarea.value = storedOrigins.join('\n');

    if (requestedOrigin) {
      recommendedOriginPanel.hidden = false;
      recommendedOriginValue.textContent = requestedOrigin;
      addRecommendedButton.hidden = false;
      addRecommendedButton.addEventListener('click', () => {
        const nextOrigins = new Set(parseOrigins(textarea.value));
        nextOrigins.add(requestedOrigin);
        textarea.value = Array.from(nextOrigins).sort().join('\n');
      });
    }

    saveButton.addEventListener('click', saveOrigins);
  }

  async function loadAllowedOrigins() {
    const result = await chrome.storage.sync.get(allowedOriginsStorageKey);
    const configured = Array.isArray(result[allowedOriginsStorageKey]) ? result[allowedOriginsStorageKey] : [];
    const normalized = configured
      .map(normalizeOrigin)
      .filter(Boolean);

    return normalized.length > 0 ? Array.from(new Set(normalized)).sort() : defaultAllowedOrigins;
  }

  async function saveOrigins() {
    const origins = parseOrigins(textarea.value);
    if (origins.length === 0) {
      status.textContent = 'Enter at least one valid origin.';
      return;
    }

    await chrome.storage.sync.set({ [allowedOriginsStorageKey]: origins });
    status.textContent = 'Saved.';
    window.setTimeout(() => {
      if (status.textContent === 'Saved.') {
        status.textContent = '';
      }
    }, 1500);
  }

  function parseOrigins(input) {
    return Array.from(new Set(
      input
        .split(/\r?\n/)
        .map((value) => normalizeOrigin(value))
        .filter(Boolean)
    )).sort();
  }

  function normalizeOrigin(value) {
    try {
      return new URL(value.trim()).origin;
    } catch {
      return '';
    }
  }

  function readRequestedOrigin() {
    try {
      const origin = new URLSearchParams(window.location.search).get('origin');
      return origin ? normalizeOrigin(origin) : '';
    } catch {
      return '';
    }
  }
})();
