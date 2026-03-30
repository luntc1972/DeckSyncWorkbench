const API_ENDPOINTS = [
  'https://api.moxfield.com/v2/decks/all/',
  'https://api2.moxfield.com/v3/decks/all/'
];

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === 'moxfield-fetch-deck' && typeof message.deckId === 'string') {
    fetchDeckPayload(message.deckId)
      .then((result) => sendResponse(result))
      .catch((error) => sendResponse({ ok: false, error: error instanceof Error ? error.message : String(error) }));

    return true;
  }

  if (message?.type === 'download-file' && typeof message.fileName === 'string' && typeof message.url === 'string') {
    chrome.downloads.download({
      url: message.url,
      filename: message.fileName,
      saveAs: true
    }, (downloadId) => {
      if (chrome.runtime.lastError) {
        sendResponse({ ok: false, error: chrome.runtime.lastError.message });
        return;
      }

      sendResponse({ ok: true, downloadId });
    });

    return true;
  }

  return false;
});

async function fetchDeckPayload(deckId) {
  const errors = [];

  for (const baseUrl of API_ENDPOINTS) {
    const url = `${baseUrl}${encodeURIComponent(deckId)}`;

    try {
      const response = await fetch(url, {
        method: 'GET',
        headers: {
          'Accept': 'application/json'
        }
      });

      if (!response.ok) {
        errors.push(`${url} returned ${response.status}`);
        continue;
      }

      const payload = await response.json();
      if (payload && typeof payload === 'object' && (payload.mainboard || payload.commanders || payload.main)) {
        return { ok: true, payload, sourceUrl: url };
      }

      errors.push(`${url} returned JSON without deck data`);
    } catch (error) {
      errors.push(`${url} failed: ${error instanceof Error ? error.message : String(error)}`);
    }
  }

  return {
    ok: false,
    error: errors.length > 0
      ? `Unable to fetch deck JSON from Moxfield. ${errors.join('; ')}`
      : 'Unable to fetch deck JSON from Moxfield.'
  };
}
