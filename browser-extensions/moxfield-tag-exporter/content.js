(function () {
  'use strict';

  const PANEL_ID = 'mtg-deck-studio-moxfield-exporter';
  const STATUS_ID = 'mtg-deck-studio-moxfield-exporter-status';
  const COPY_ARCHIDEKT_ID = 'mtg-deck-studio-copy-archidekt';
  const COPY_MOXFIELD_ID = 'mtg-deck-studio-copy-moxfield';
  const DOWNLOAD_ID = 'mtg-deck-studio-download-both';
  const TOGGLE_ID = 'mtg-deck-studio-toggle';
  const state = {
    deckId: getDeckId(),
    deckPayload: null,
    exportBundle: null,
    loading: false,
    collapsed: false
  };

  if (!state.deckId || document.getElementById(PANEL_ID)) {
    return;
  }

  injectPanel();
  updateStatus('Ready.\nExports commander, mainboard, and sideboard with visible Moxfield tags only.');

  document.getElementById(COPY_ARCHIDEKT_ID)?.addEventListener('click', () => runExport('copy-archidekt'));
  document.getElementById(COPY_MOXFIELD_ID)?.addEventListener('click', () => runExport('copy-moxfield'));
  document.getElementById(DOWNLOAD_ID)?.addEventListener('click', () => runExport('download'));
  document.getElementById(TOGGLE_ID)?.addEventListener('click', togglePanel);

  async function runExport(action) {
    if (state.loading) {
      return;
    }

    setBusy(true);

    try {
      if (!state.exportBundle) {
        const payload = await loadDeckPayload();
        state.deckPayload = payload;
        state.exportBundle = buildExportBundle(payload);
      }

      if (action === 'copy-archidekt') {
        await copyText(state.exportBundle.archidektText);
        updateStatus(`Copied Archidekt text for ${state.exportBundle.cardCount} cards.`);
        return;
      }

      if (action === 'copy-moxfield') {
        await copyText(state.exportBundle.moxfieldText);
        updateStatus(`Copied Moxfield text for ${state.exportBundle.cardCount} cards.`);
        return;
      }

      if (action === 'download') {
        downloadTextFile(state.exportBundle.archidektFileName, state.exportBundle.archidektText);
        downloadTextFile(state.exportBundle.moxfieldFileName, state.exportBundle.moxfieldText);
        updateStatus(`Downloaded both exports for ${state.exportBundle.cardCount} cards.`);
      }
    } catch (error) {
      updateStatus(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  }

  async function loadDeckPayload() {
    const inlinePayload = tryExtractInlineDeckPayload(state.deckId);
    if (inlinePayload) {
      return inlinePayload;
    }

    const fetchedPayload = await fetchDeckPayload(state.deckId);
    if (!fetchedPayload) {
      throw new Error('Unable to find deck data on this page. Reload the page after opening the deck, then try again.');
    }

    return fetchedPayload;
  }

  async function fetchDeckPayload(deckId) {
    const response = await chrome.runtime.sendMessage({ type: 'moxfield-fetch-deck', deckId });

    if (!response?.ok) {
      throw new Error(response?.error ?? 'Unable to fetch deck data from Moxfield.');
    }

    return response.payload;
  }

  function buildExportBundle(deckPayload) {
    const deckName = sanitizeFilePart(deckPayload.name || `moxfield-${state.deckId}`);
    const authorTags = deckPayload.authorTags || {};
    const cards = [];

    appendZone(cards, deckPayload.commanders, 'Commander', authorTags);
    appendZone(cards, deckPayload.mainboard, null, authorTags);
    appendZone(cards, deckPayload.sideboard, 'Sideboard', authorTags);

    if (cards.length === 0) {
      throw new Error('No commander, mainboard, or sideboard cards were found.');
    }

    const archidektText = cards.map(formatArchidektLine).join('\n') + '\n';
    const moxfieldText = cards.map(formatMoxfieldLine).join('\n') + '\n';

    return {
      cardCount: cards.length,
      archidektText,
      moxfieldText,
      archidektFileName: `${deckName}-archidekt-tags.txt`,
      moxfieldFileName: `${deckName}-moxfield-tags.txt`
    };
  }

  function appendZone(target, zone, boardTag, authorTags) {
    if (!zone || typeof zone !== 'object') {
      return;
    }

    for (const [fallbackName, rawEntry] of Object.entries(zone)) {
      if (!rawEntry || typeof rawEntry !== 'object') {
        continue;
      }

      const card = rawEntry.card && typeof rawEntry.card === 'object' ? rawEntry.card : rawEntry;
      const cardName = card.name || fallbackName;
      const tags = normalizeTags(authorTags[cardName], boardTag);
      target.push({
        quantity: Number(rawEntry.quantity) || 1,
        name: cardName,
        setCode: typeof card.set === 'string' ? card.set.toLowerCase() : '',
        collectorNumber: typeof card.cn === 'string' ? card.cn : '',
        tags
      });
    }
  }

  function normalizeTags(rawTags, boardTag) {
    const tags = [];

    if (boardTag) {
      tags.push(boardTag);
    }

    if (Array.isArray(rawTags)) {
      for (const rawTag of rawTags) {
        if (typeof rawTag !== 'string') {
          continue;
        }

        const trimmed = rawTag.trim();
        if (trimmed) {
          tags.push(trimmed);
        }
      }
    }

    return Array.from(new Set(tags.map((tag) => tag.trim()).filter(Boolean)));
  }

  function formatArchidektLine(entry) {
    const printing = formatPrinting(entry);
    const tags = entry.tags.length > 0 ? ` [${entry.tags.join(',')}]` : '';
    return `${entry.quantity} ${entry.name}${printing}${tags}`;
  }

  function formatMoxfieldLine(entry) {
    const printing = formatPrinting(entry);
    const tags = entry.tags.length > 0
      ? ` ${entry.tags.map((tag) => `#${tag.replace(/\s+/g, '')}`).join(' ')}`
      : '';
    return `${entry.quantity} ${entry.name}${printing}${tags}`;
  }

  function formatPrinting(entry) {
    if (!entry.setCode || !entry.collectorNumber) {
      return '';
    }

    return ` (${entry.setCode}) ${entry.collectorNumber}`;
  }

  function tryExtractInlineDeckPayload(deckId) {
    const marker = `"publicId":"${deckId}"`;
    const candidates = Array.from(document.scripts)
      .filter((script) => !script.src && script.textContent && script.textContent.includes(marker));

    for (const script of candidates) {
      const payload = extractDeckPayloadFromText(script.textContent, marker);
      if (payload) {
        return payload;
      }
    }

    const htmlPayload = extractDeckPayloadFromText(document.documentElement.outerHTML, marker);
    return htmlPayload;
  }

  function extractDeckPayloadFromText(text, marker) {
    const markerIndex = text.indexOf(marker);
    if (markerIndex < 0) {
      return null;
    }

    const startIndex = findObjectStart(text, markerIndex);
    if (startIndex < 0) {
      return null;
    }

    const endIndex = findObjectEnd(text, startIndex);
    if (endIndex < 0) {
      return null;
    }

    const candidate = text.slice(startIndex, endIndex + 1);

    try {
      const payload = JSON.parse(candidate);
      return payload && typeof payload === 'object' && payload.publicId ? payload : null;
    } catch {
      return null;
    }
  }

  function findObjectStart(text, markerIndex) {
    let inString = false;
    let escaping = false;

    for (let index = markerIndex; index >= 0; index -= 1) {
      const character = text[index];

      if (escaping) {
        escaping = false;
        continue;
      }

      if (character === '\\') {
        escaping = true;
        continue;
      }

      if (character === '"') {
        inString = !inString;
        continue;
      }

      if (!inString && character === '{') {
        return index;
      }
    }

    return -1;
  }

  function findObjectEnd(text, startIndex) {
    let depth = 0;
    let inString = false;
    let escaping = false;

    for (let index = startIndex; index < text.length; index += 1) {
      const character = text[index];

      if (escaping) {
        escaping = false;
        continue;
      }

      if (character === '\\') {
        escaping = true;
        continue;
      }

      if (character === '"') {
        inString = !inString;
        continue;
      }

      if (inString) {
        continue;
      }

      if (character === '{') {
        depth += 1;
      } else if (character === '}') {
        depth -= 1;
        if (depth === 0) {
          return index;
        }
      }
    }

    return -1;
  }

  async function copyText(text) {
    await navigator.clipboard.writeText(text);
  }

  function downloadTextFile(fileName, text) {
    const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
    chrome.runtime.sendMessage({
      type: 'download-file',
      fileName,
      url
    }, () => {
      window.setTimeout(() => URL.revokeObjectURL(url), 10_000);
    });
  }

  function sanitizeFilePart(value) {
    return value
      .trim()
      .replace(/[<>:"/\\|?*\x00-\x1F]+/g, '-')
      .replace(/\s+/g, '-')
      .replace(/-+/g, '-')
      .replace(/^-|-$/g, '')
      .toLowerCase() || 'moxfield-deck';
  }

  function getDeckId() {
    const match = window.location.pathname.match(/^\/decks\/([^/]+)/i);
    return match ? match[1] : null;
  }

  function injectPanel() {
    const panel = document.createElement('section');
    panel.id = PANEL_ID;
    panel.setAttribute('data-collapsed', 'false');
    panel.innerHTML = `
      <div class="mtg-deck-studio-moxfield-exporter__header">
        <span class="mtg-deck-studio-moxfield-exporter__title">Moxfield Tag Exporter</span>
        <button id="${TOGGLE_ID}" class="mtg-deck-studio-moxfield-exporter__toggle" type="button" aria-expanded="true" title="Minimize exporter">−</button>
      </div>
      <div class="mtg-deck-studio-moxfield-exporter__body">
        <div id="${STATUS_ID}" class="mtg-deck-studio-moxfield-exporter__status"></div>
        <div class="mtg-deck-studio-moxfield-exporter__actions">
          <button id="${COPY_ARCHIDEKT_ID}" class="mtg-deck-studio-moxfield-exporter__button" type="button">Copy Archidekt Text</button>
          <button id="${COPY_MOXFIELD_ID}" class="mtg-deck-studio-moxfield-exporter__button" type="button">Copy Moxfield Text</button>
          <button id="${DOWNLOAD_ID}" class="mtg-deck-studio-moxfield-exporter__button" type="button">Download Both Files</button>
        </div>
        <div class="mtg-deck-studio-moxfield-exporter__footnote">Flattened export. Uses commander and sideboard tags plus visible Moxfield card tags.</div>
      </div>
    `;

    document.documentElement.appendChild(panel);
  }

  function togglePanel() {
    state.collapsed = !state.collapsed;
    const panel = document.getElementById(PANEL_ID);
    const toggle = document.getElementById(TOGGLE_ID);

    if (panel) {
      panel.setAttribute('data-collapsed', state.collapsed ? 'true' : 'false');
    }

    if (toggle instanceof HTMLButtonElement) {
      toggle.textContent = state.collapsed ? '+' : '−';
      toggle.setAttribute('aria-expanded', state.collapsed ? 'false' : 'true');
      toggle.title = state.collapsed ? 'Expand exporter' : 'Minimize exporter';
    }
  }

  function setBusy(isBusy) {
    state.loading = isBusy;
    for (const id of [COPY_ARCHIDEKT_ID, COPY_MOXFIELD_ID, DOWNLOAD_ID]) {
      const button = document.getElementById(id);
      if (button instanceof HTMLButtonElement) {
        button.disabled = isBusy;
      }
    }

    if (isBusy) {
      updateStatus('Preparing export…');
    }
  }

  function updateStatus(message) {
    const status = document.getElementById(STATUS_ID);
    if (status) {
      status.textContent = message;
    }
  }
})();
