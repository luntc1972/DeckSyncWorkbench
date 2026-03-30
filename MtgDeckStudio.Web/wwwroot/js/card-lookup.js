"use strict";
const countNonEmptyLines = (value) => value
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(line => line.length > 0)
    .length;
const initializeCardLookupForm = () => {
    const form = document.querySelector('form[action="/card-lookup"]');
    if (!form) {
        return;
    }
    const textArea = form.querySelector('textarea[name="CardList"]');
    const counter = document.querySelector('[data-verify-lines-count]');
    const validationMessage = document.querySelector('[data-verify-lines-error]');
    const submitButtons = form.querySelectorAll('button[type="submit"]');
    const downloadButton = form.querySelector('button[formaction="/card-lookup/download"]');
    if (!textArea || !counter || !validationMessage) {
        return;
    }
    const updateUi = () => {
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
    downloadButton === null || downloadButton === void 0 ? void 0 : downloadButton.addEventListener('click', () => {
        window.setTimeout(() => {
            var _a;
            (_a = window.hideBusyIndicator) === null || _a === void 0 ? void 0 : _a.call(window);
        }, 300);
    });
    updateUi();
};
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initializeCardLookupForm);
}
else {
    initializeCardLookupForm();
}
