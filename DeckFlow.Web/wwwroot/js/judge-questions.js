"use strict";
const debounceJudgeSearch = (fn, delay) => {
    let timer;
    return () => {
        if (timer !== undefined) {
            window.clearTimeout(timer);
        }
        timer = window.setTimeout(fn, delay);
    };
};
const createJudgeSuggestionPanel = (anchor) => {
    const panel = document.createElement('div');
    panel.className = 'autocomplete-panel hidden';
    panel.setAttribute('role', 'listbox');
    anchor.appendChild(panel);
    return panel;
};
const hideJudgeSuggestionPanel = (panel) => {
    panel.classList.add('hidden');
    panel.replaceChildren();
};
const attachJudgeCardLookahead = (input, panel, minChars) => {
    const fetchSuggestions = async () => {
        const query = input.value.trim();
        if (query.length < minChars) {
            hideJudgeSuggestionPanel(panel);
            return;
        }
        try {
            const response = await fetch(`/suggest-categories/card-search?query=${encodeURIComponent(query)}`);
            if (!response.ok) {
                hideJudgeSuggestionPanel(panel);
                return;
            }
            const names = await response.json();
            panel.replaceChildren();
            if (names.length === 0) {
                hideJudgeSuggestionPanel(panel);
                return;
            }
            names.forEach(name => {
                const option = document.createElement('button');
                option.type = 'button';
                option.className = 'autocomplete-option';
                option.textContent = name;
                option.addEventListener('mousedown', event => {
                    event.preventDefault();
                    input.value = name;
                    hideJudgeSuggestionPanel(panel);
                });
                panel.appendChild(option);
            });
            panel.classList.remove('hidden');
        }
        catch (_a) {
            hideJudgeSuggestionPanel(panel);
        }
    };
    const debounced = debounceJudgeSearch(fetchSuggestions, 250);
    input.addEventListener('input', debounced);
    input.addEventListener('focus', debounced);
    document.addEventListener('click', event => {
        if (!(event.target instanceof Node) || panel.contains(event.target) || input.contains(event.target)) {
            return;
        }
        hideJudgeSuggestionPanel(panel);
    });
};
const buildJudgePrompt = (question, cardName, cardDetails) => {
    const lines = [];
    lines.push('You are helping with a Magic: The Gathering rules question.');
    lines.push('');
    lines.push('IMPORTANT: Answer carefully and acknowledge uncertainty when present. MTG rules are precise; replacement effects, layers, and timing are easy to get wrong. If anything in the question is ambiguous, list the possible interpretations and answer each. Cite Comprehensive Rules section numbers when you can. End with a one-sentence "bottom line" answer.');
    lines.push('');
    if (cardName && cardDetails) {
        lines.push(`Reference card: ${cardName}`);
        lines.push('');
        lines.push(cardDetails);
        lines.push('');
    }
    else if (cardName) {
        lines.push(`Reference card: ${cardName}`);
        lines.push('');
    }
    lines.push('Question:');
    lines.push(question);
    return lines.join('\n');
};
const fetchCardDetails = async (cardName) => {
    var _a;
    if (!cardName) {
        return '';
    }
    const response = await fetch(`/card-lookup/single?name=${encodeURIComponent(cardName)}`);
    if (!response.ok) {
        throw new Error(`Could not find card "${cardName}" on Scryfall.`);
    }
    const payload = await response.json();
    return (_a = payload === null || payload === void 0 ? void 0 : payload.verifiedText) !== null && _a !== void 0 ? _a : '';
};
const initializeJudgeQuestions = () => {
    const cardInput = document.querySelector('[data-judge-card-input]');
    const questionInput = document.querySelector('[data-judge-question-input]');
    const generateButton = document.querySelector('[data-judge-generate]');
    const clearButton = document.querySelector('[data-judge-clear]');
    const errorBanner = document.querySelector('[data-judge-error]');
    const resultPanel = document.querySelector('[data-judge-result]');
    const promptOutput = document.querySelector('[data-judge-prompt-output]');
    if (!cardInput || !questionInput || !generateButton || !errorBanner || !resultPanel || !promptOutput) {
        return;
    }
    const anchor = cardInput.parentElement;
    if (anchor) {
        const panel = createJudgeSuggestionPanel(anchor);
        attachJudgeCardLookahead(cardInput, panel, 4);
    }
    const showError = (message) => {
        errorBanner.textContent = message;
        errorBanner.classList.remove('hidden');
        resultPanel.classList.add('hidden');
    };
    const clearError = () => {
        errorBanner.textContent = '';
        errorBanner.classList.add('hidden');
    };
    const showPrompt = (prompt) => {
        clearError();
        promptOutput.value = prompt;
        resultPanel.classList.remove('hidden');
    };
    generateButton.addEventListener('click', async () => {
        const question = questionInput.value.trim();
        const cardName = cardInput.value.trim();
        if (!question) {
            showError('Enter a question first.');
            return;
        }
        generateButton.disabled = true;
        clearError();
        try {
            let cardDetails = '';
            if (cardName) {
                try {
                    cardDetails = await fetchCardDetails(cardName);
                }
                catch (error) {
                    showError(error instanceof Error ? error.message : 'Could not fetch card details.');
                    return;
                }
            }
            showPrompt(buildJudgePrompt(question, cardName, cardDetails));
        }
        finally {
            generateButton.disabled = false;
        }
    });
    clearButton === null || clearButton === void 0 ? void 0 : clearButton.addEventListener('click', () => {
        questionInput.value = '';
        cardInput.value = '';
        promptOutput.value = '';
        resultPanel.classList.add('hidden');
        clearError();
        questionInput.focus();
    });
};
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initializeJudgeQuestions);
}
else {
    initializeJudgeQuestions();
}
