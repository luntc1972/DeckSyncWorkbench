const debounce = (fn: () => void, delay: number) => {
  let timer: number | undefined;
  return () => {
    if (timer !== undefined) {
      window.clearTimeout(timer);
    }
    timer = window.setTimeout(fn, delay);
  };
};

const renderSuggestions = (list: string[], datalist: HTMLDataListElement): void => {
  datalist.innerHTML = '';
  list.forEach(name => {
    const option = document.createElement('option');
    option.value = name;
    datalist.appendChild(option);
  });
};

const setCommanderSearchError = (message?: string): void => {
  const panel = document.querySelector<HTMLElement>('[data-api-panel="commander-search-error"]');
  const text = document.querySelector<HTMLElement>('[data-api-field="commander-search-error-text"]');
  if (!panel || !text) {
    return;
  }

  text.textContent = message ?? '';
  panel.classList.toggle('hidden', !message);
};

const attachCommanderSearch = (): void => {
  const input = document.getElementById('commander-search-input') as HTMLInputElement | null;
  const datalist = document.getElementById('commander-suggestions') as HTMLDataListElement | null;
  if (!input || !datalist) {
    return;
  }

  const fetchSuggestions = async (): Promise<void> => {
    const query = input.value.trim();
    if (query.length < 2) {
      datalist.innerHTML = '';
      setCommanderSearchError();
      return;
    }

    try {
      const response = await fetch(`/commander-categories/search?query=${encodeURIComponent(query)}`);
      if (!response.ok) {
        let payload: { message?: string; Message?: string } | null = null;
        try {
          payload = await response.json() as { message?: string; Message?: string };
        } catch {
          payload = null;
        }

        datalist.innerHTML = '';
        setCommanderSearchError(payload?.message ?? payload?.Message ?? 'Scryfall could not be reached right now. Try again shortly.');
        return;
      }
      const names: string[] = await response.json();
      renderSuggestions(names, datalist);
      setCommanderSearchError();
    } catch (error) {
      datalist.innerHTML = '';
      setCommanderSearchError(error instanceof Error ? error.message : 'Scryfall could not be reached right now. Try again shortly.');
      console.error('Failed to fetch commander suggestions', error);
    }
  };

  const debounced = debounce(fetchSuggestions, 350);
  input.addEventListener('input', debounced);
};

document.addEventListener('DOMContentLoaded', attachCommanderSearch);
