const countNonEmptyLines = (value: string): number =>
  value
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(line => line.length > 0)
    .length;

const initializeCardLookupForm = (): void => {
  const form = document.querySelector<HTMLFormElement>('form[action="/card-lookup"]');
  if (!form) {
    return;
  }

  const textArea = form.querySelector<HTMLTextAreaElement>('textarea[name="CardList"]');
  const counter = document.querySelector<HTMLElement>('[data-verify-lines-count]');
  const validationMessage = document.querySelector<HTMLElement>('[data-verify-lines-error]');
  const submitButtons = form.querySelectorAll<HTMLButtonElement>('button[type="submit"]');
  const downloadButton = form.querySelector<HTMLButtonElement>('button[formaction="/card-lookup/download"]');
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

  textArea.addEventListener('input', updateUi);
  downloadButton?.addEventListener('click', () => {
    window.setTimeout(() => {
      window.hideBusyIndicator?.();
    }, 300);
  });
  updateUi();
};

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', initializeCardLookupForm);
} else {
  initializeCardLookupForm();
}
