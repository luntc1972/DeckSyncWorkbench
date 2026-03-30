const debounceCardSearch = (fn: () => void, delay: number) => {
  let timer: number | undefined;
  return () => {
    if (timer !== undefined) {
      window.clearTimeout(timer);
    }

    timer = window.setTimeout(fn, delay);
  };
};

const setCardSearchError = (message?: string): void => {
  const panel = document.querySelector<HTMLElement>('[data-api-panel="card-search-error"]');
  const text = document.querySelector<HTMLElement>('[data-api-field="card-search-error-text"]');
  if (!panel || !text) {
    return;
  }

  text.textContent = message ?? '';
  panel.classList.toggle('hidden', !message);
};

const renderCardSuggestions = (list: string[], datalist: HTMLDataListElement): void => {
  datalist.innerHTML = '';
  list.forEach(name => {
    const option = document.createElement('option');
    option.value = name;
    datalist.appendChild(option);
  });
};

const attachCardSearch = (): void => {
  const input = document.querySelector<HTMLInputElement>('input[name="CardName"]');
  const datalist = document.getElementById('card-suggestions') as HTMLDataListElement | null;
  if (!input || !datalist) {
    return;
  }

  const fetchSuggestions = async (): Promise<void> => {
    const query = input.value.trim();
    if (query.length < 2) {
      datalist.innerHTML = '';
      setCardSearchError();
      return;
    }

    try {
      const response = await fetch(`/suggest-categories/card-search?query=${encodeURIComponent(query)}`);
      if (!response.ok) {
        let payload: { message?: string; Message?: string } | null = null;
        try {
          payload = await response.json() as { message?: string; Message?: string };
        } catch {
          payload = null;
        }

        datalist.innerHTML = '';
        setCardSearchError(payload?.message ?? payload?.Message ?? 'Scryfall could not be reached right now. Try again shortly.');
        return;
      }

      const names: string[] = await response.json();
      renderCardSuggestions(names, datalist);
      setCardSearchError();
    } catch (error) {
      datalist.innerHTML = '';
      setCardSearchError(error instanceof Error ? error.message : 'Scryfall could not be reached right now. Try again shortly.');
      console.error('Failed to fetch card suggestions', error);
    }
  };

  const debounced = debounceCardSearch(fetchSuggestions, 250);
  input.addEventListener('input', debounced);
};

document.addEventListener('DOMContentLoaded', attachCardSearch);
