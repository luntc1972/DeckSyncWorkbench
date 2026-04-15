"use strict";
(() => {
    'use strict';
    let backToTopInitialized = false;
    let themePickerInitialized = false;
    let archidektCacheJobInitialized = false;
    let archidektCacheJobPollHandle = null;
    let archidektCacheJobStartPending = false;
    const themeStorageKey = 'mtg-deck-studio-theme';
    const themeCookieMaxAgeSeconds = 60 * 60 * 24 * 365;
    const archidektCacheJobStorageKey = 'mtg-deck-studio-archidekt-cache-job';
    const archidektCacheJobDismissedKey = 'mtg-deck-studio-archidekt-cache-job-dismissed';
    const archidektCacheJobPendingKey = 'mtg-deck-studio-archidekt-cache-job-pending';
    const archidektCacheJobPollIntervalMs = 5000;
    const attachBackToTop = () => {
        if (backToTopInitialized) {
            return;
        }
        backToTopInitialized = true;
        const button = document.getElementById('back-to-top-button');
        if (!(button instanceof HTMLButtonElement)) {
            return;
        }
        const updateVisibility = () => {
            const shouldShow = window.scrollY > 120;
            button.classList.toggle('is-visible', shouldShow);
            button.setAttribute('aria-hidden', shouldShow ? 'false' : 'true');
            button.tabIndex = shouldShow ? 0 : -1;
        };
        button.addEventListener('click', () => {
            window.scrollTo({
                top: 0,
                behavior: 'smooth'
            });
        });
        window.addEventListener('scroll', updateVisibility, { passive: true });
        updateVisibility();
    };
    const attachThemePicker = () => {
        var _a, _b, _c, _d;
        if (themePickerInitialized) {
            return;
        }
        themePickerInitialized = true;
        const themeLink = document.getElementById('theme-stylesheet');
        const themeSelect = document.getElementById('theme-picker');
        if (!(themeLink instanceof HTMLLinkElement) || !(themeSelect instanceof HTMLSelectElement)) {
            return;
        }
        const themeCookieName = (_a = themeLink.dataset.cookieName) !== null && _a !== void 0 ? _a : themeStorageKey;
        const getStoredTheme = () => {
            try {
                return window.localStorage.getItem(themeStorageKey);
            }
            catch (_a) {
                return null;
            }
        };
        const getCookieTheme = () => {
            const cookiePrefix = `${encodeURIComponent(themeCookieName)}=`;
            const cookieValue = document.cookie
                .split(';')
                .map((value) => value.trim())
                .find((value) => value.startsWith(cookiePrefix));
            if (!cookieValue) {
                return null;
            }
            try {
                return decodeURIComponent(cookieValue.substring(cookiePrefix.length));
            }
            catch (_a) {
                return null;
            }
        };
        const setStoredTheme = (value) => {
            try {
                window.localStorage.setItem(themeStorageKey, value);
            }
            catch (_a) {
                // Ignore storage failures and keep the current session theme applied.
            }
        };
        const setCookieTheme = (value) => {
            document.cookie = `${encodeURIComponent(themeCookieName)}=${encodeURIComponent(value)}; max-age=${themeCookieMaxAgeSeconds}; path=/; samesite=lax`;
        };
        const getThemeHref = (value) => {
            var _a;
            const matchingOption = Array.from(themeSelect.options).find((option) => option.value === value);
            return (_a = matchingOption === null || matchingOption === void 0 ? void 0 : matchingOption.dataset.themeHref) !== null && _a !== void 0 ? _a : null;
        };
        const applyTheme = (value, persistSelection) => {
            var _a, _b, _c, _d;
            const selectedValue = getThemeHref(value) ? value : (_b = (_a = themeSelect.options[0]) === null || _a === void 0 ? void 0 : _a.value) !== null && _b !== void 0 ? _b : 'site.css';
            const selectedHref = (_d = (_c = getThemeHref(selectedValue)) !== null && _c !== void 0 ? _c : themeLink.dataset.defaultHref) !== null && _d !== void 0 ? _d : themeLink.href;
            themeLink.href = selectedHref;
            themeSelect.value = selectedValue;
            if (persistSelection) {
                setStoredTheme(selectedValue);
                setCookieTheme(selectedValue);
            }
        };
        applyTheme((_d = (_c = (_b = getStoredTheme()) !== null && _b !== void 0 ? _b : getCookieTheme()) !== null && _c !== void 0 ? _c : themeLink.dataset.defaultTheme) !== null && _d !== void 0 ? _d : 'site.css', false);
        themeSelect.addEventListener('change', () => {
            applyTheme(themeSelect.value, true);
        });
    };
    const readArchidektCacheJobRecord = () => {
        try {
            const payload = window.localStorage.getItem(archidektCacheJobStorageKey);
            if (!payload) {
                return null;
            }
            return JSON.parse(payload);
        }
        catch (_a) {
            return null;
        }
    };
    const writeArchidektCacheJobRecord = (record) => {
        try {
            if (!record) {
                window.localStorage.removeItem(archidektCacheJobStorageKey);
                return;
            }
            window.localStorage.setItem(archidektCacheJobStorageKey, JSON.stringify(record));
        }
        catch (_a) {
            // Ignore storage failures and continue without cross-page persistence.
        }
    };
    const readDismissedJobId = () => {
        try {
            return window.localStorage.getItem(archidektCacheJobDismissedKey);
        }
        catch (_a) {
            return null;
        }
    };
    const readPendingStart = () => {
        try {
            return window.localStorage.getItem(archidektCacheJobPendingKey) === '1';
        }
        catch (_a) {
            return false;
        }
    };
    const writePendingStart = (pending) => {
        archidektCacheJobStartPending = pending;
        try {
            if (pending) {
                window.localStorage.setItem(archidektCacheJobPendingKey, '1');
                return;
            }
            window.localStorage.removeItem(archidektCacheJobPendingKey);
        }
        catch (_a) {
            // Ignore storage failures and keep the pending state in memory only.
        }
    };
    const writeDismissedJobId = (jobId) => {
        try {
            if (!jobId) {
                window.localStorage.removeItem(archidektCacheJobDismissedKey);
                return;
            }
            window.localStorage.setItem(archidektCacheJobDismissedKey, jobId);
        }
        catch (_a) {
            // Ignore storage failures and keep the notice transient.
        }
    };
    const getGlobalJobNotice = () => document.querySelector('[data-global-job-notice]');
    const setGlobalJobNotice = (message, jobId) => {
        const notice = getGlobalJobNotice();
        const text = document.querySelector('[data-global-job-notice-text]');
        if (!notice || !text) {
            return;
        }
        if (!message || (jobId && readDismissedJobId() === jobId)) {
            notice.classList.add('hidden');
            text.textContent = '';
            return;
        }
        text.textContent = message;
        notice.classList.remove('hidden');
    };
    const setPageJobStatus = (message) => {
        const panel = document.querySelector('[data-archidekt-cache-status-panel]');
        const text = document.querySelector('[data-archidekt-cache-status-text]');
        if (!panel || !text) {
            return;
        }
        if (!message) {
            panel.classList.add('hidden');
            text.textContent = '';
            return;
        }
        text.textContent = message;
        panel.classList.remove('hidden');
    };
    const updateArchidektCacheButtons = (isRunning) => {
        var _a;
        console.debug('[harvest]', 'updateArchidektCacheButtons', { isRunning, pending: archidektCacheJobStartPending, stack: (_a = new Error().stack) === null || _a === void 0 ? void 0 : _a.split('\n').slice(1, 4).map(s => s.trim()).join(' < ') });
        document.querySelectorAll('[data-archidekt-cache-start]').forEach(button => {
            const disabled = isRunning || archidektCacheJobStartPending;
            button.disabled = disabled;
            button.setAttribute('aria-disabled', disabled ? 'true' : 'false');
            button.classList.toggle('disabled', disabled);
            button.textContent = disabled
                ? (archidektCacheJobStartPending && !isRunning ? 'Starting Archidekt Harvest...' : 'Archidekt Harvest Running...')
                : 'Run 5-Minute Archidekt Harvest';
        });
    };
    const buildJobMessage = (job) => {
        switch (job.state) {
            case 'Queued':
                return 'Archidekt category harvest is queued and will start shortly.';
            case 'Running':
                return `Archidekt category harvest is running in the background for up to ${Math.round(job.durationSeconds / 60)} minutes.`;
            case 'Succeeded':
                return `Archidekt category harvest completed. Processed ${job.decksProcessed} deck(s) and added ${job.additionalDecksFound} new cached deck(s).`;
            case 'Failed':
                return `Archidekt category harvest failed${job.errorMessage ? `: ${job.errorMessage}` : '.'}`;
            default:
                return 'Archidekt category harvest status updated.';
        }
    };
    const notifyJobCompletion = (job) => {
        if (!('Notification' in window) || document.visibilityState === 'visible') {
            return;
        }
        if (Notification.permission === 'granted') {
            new Notification('Archidekt category harvest complete', {
                body: buildJobMessage(job)
            });
        }
    };
    const applyJobStatus = (job, statusUrl) => {
        const message = buildJobMessage(job);
        const completed = job.state === 'Succeeded' || job.state === 'Failed';
        writePendingStart(false);
        const prior = readArchidektCacheJobRecord();
        const completionNotified = (prior === null || prior === void 0 ? void 0 : prior.jobId) === job.jobId ? prior.completionNotified === true : false;
        writeArchidektCacheJobRecord(completed ? null : {
            jobId: job.jobId,
            statusUrl,
            state: job.state,
            completionNotified
        });
        setPageJobStatus(message);
        setGlobalJobNotice(message, job.jobId);
        updateArchidektCacheButtons(!completed);
        if (completed && !completionNotified) {
            notifyJobCompletion(job);
        }
        if (completed) {
            writeDismissedJobId(null);
            if (archidektCacheJobPollHandle !== null) {
                window.clearInterval(archidektCacheJobPollHandle);
                archidektCacheJobPollHandle = null;
            }
            return;
        }
        startArchidektCacheJobPolling();
    };
    const pollArchidektCacheJob = async () => {
        const record = readArchidektCacheJobRecord();
        if (!(record === null || record === void 0 ? void 0 : record.statusUrl)) {
            updateArchidektCacheButtons(false);
            return;
        }
        try {
            const response = await fetch(record.statusUrl, {
                method: 'GET',
                headers: {
                    'Accept': 'application/json'
                }
            });
            if (!response.ok) {
                if (response.status === 404) {
                    writeArchidektCacheJobRecord(null);
                    updateArchidektCacheButtons(false);
                    setPageJobStatus(null);
                }
                return;
            }
            const job = await response.json();
            const completed = job.state === 'Succeeded' || job.state === 'Failed';
            applyJobStatus(job, record.statusUrl);
            if (completed) {
                return;
            }
        }
        catch (_a) {
            // Keep the previous state and try again on the next poll tick.
        }
    };
    const resolveActiveArchidektCacheJob = async (activeUrl) => {
        try {
            const response = await fetch(activeUrl, {
                method: 'GET',
                headers: {
                    'Accept': 'application/json'
                }
            });
            if (response.status === 404) {
                writePendingStart(false);
                writeArchidektCacheJobRecord(null);
                setPageJobStatus(null);
                updateArchidektCacheButtons(false);
                return false;
            }
            if (!response.ok) {
                return false;
            }
            const payload = await response.json();
            const statusUrl = `/api/archidekt-cache-jobs/${payload.jobId}`;
            applyJobStatus(payload, statusUrl);
            return true;
        }
        catch (_a) {
            return false;
        }
    };
    const startArchidektCacheJobPolling = () => {
        if (archidektCacheJobPollHandle !== null) {
            return;
        }
        archidektCacheJobPollHandle = window.setInterval(() => {
            void pollArchidektCacheJob();
        }, archidektCacheJobPollIntervalMs);
    };
    const attachArchidektCacheJobUi = () => {
        var _a;
        if (archidektCacheJobInitialized) {
            return;
        }
        archidektCacheJobInitialized = true;
        const dismissButton = document.querySelector('[data-global-job-notice-dismiss]');
        dismissButton === null || dismissButton === void 0 ? void 0 : dismissButton.addEventListener('click', () => {
            var _a;
            const record = readArchidektCacheJobRecord();
            writeDismissedJobId((_a = record === null || record === void 0 ? void 0 : record.jobId) !== null && _a !== void 0 ? _a : null);
            setGlobalJobNotice(null);
        });
        document.querySelectorAll('[data-archidekt-cache-start]').forEach(button => {
            button.addEventListener('click', async () => {
                var _a, _b, _c, _d, _e;
                const existingRecord = readArchidektCacheJobRecord();
                if (archidektCacheJobStartPending
                    || (existingRecord === null || existingRecord === void 0 ? void 0 : existingRecord.state) === 'Queued'
                    || (existingRecord === null || existingRecord === void 0 ? void 0 : existingRecord.state) === 'Running') {
                    updateArchidektCacheButtons(true);
                    return;
                }
                const startUrl = button.dataset.startUrl;
                const statusBaseUrl = button.dataset.statusBaseUrl;
                const activeUrl = button.dataset.activeUrl;
                const durationSeconds = Number((_a = button.dataset.durationSeconds) !== null && _a !== void 0 ? _a : '300');
                if (!startUrl || !statusBaseUrl || !activeUrl || !Number.isFinite(durationSeconds) || durationSeconds <= 0) {
                    return;
                }
                if ('Notification' in window && Notification.permission === 'default') {
                    void Notification.requestPermission();
                }
                writePendingStart(true);
                updateArchidektCacheButtons(true);
                setPageJobStatus('Starting Archidekt category harvest...');
                setGlobalJobNotice('Starting Archidekt category harvest...');
                try {
                    const response = await fetch(startUrl, {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            'Accept': 'application/json'
                        },
                        body: JSON.stringify({ durationSeconds })
                    });
                    let payload = null;
                    try {
                        payload = await response.json();
                    }
                    catch (_f) {
                        payload = null;
                    }
                    if (!response.ok) {
                        const message = (_d = (_c = (_b = payload === null || payload === void 0 ? void 0 : payload.message) !== null && _b !== void 0 ? _b : payload === null || payload === void 0 ? void 0 : payload.Message) !== null && _c !== void 0 ? _c : payload === null || payload === void 0 ? void 0 : payload.errorMessage) !== null && _d !== void 0 ? _d : 'Unable to start the Archidekt category harvest.';
                        const activeJobFound = await resolveActiveArchidektCacheJob(activeUrl);
                        if (!activeJobFound) {
                            writePendingStart(false);
                            setPageJobStatus(message);
                            setGlobalJobNotice(message);
                            updateArchidektCacheButtons(false);
                        }
                        return;
                    }
                    if (!payload) {
                        throw new Error('Archidekt category harvest returned an empty response.');
                    }
                    const statusUrl = (_e = payload.statusUrl) !== null && _e !== void 0 ? _e : `${statusBaseUrl}/${payload.jobId}`;
                    writeDismissedJobId(null);
                    applyJobStatus(payload, statusUrl);
                }
                catch (error) {
                    const activeJobFound = await resolveActiveArchidektCacheJob(activeUrl);
                    if (!activeJobFound) {
                        writePendingStart(false);
                        const message = error instanceof Error ? error.message : 'Unable to start the Archidekt category harvest.';
                        setPageJobStatus(message);
                        setGlobalJobNotice(message);
                        updateArchidektCacheButtons(false);
                    }
                }
            });
        });
        archidektCacheJobStartPending = readPendingStart();
        const activeRecord = readArchidektCacheJobRecord();
        const activeUrl = (_a = document.querySelector('[data-archidekt-cache-start]')) === null || _a === void 0 ? void 0 : _a.dataset.activeUrl;
        if ((activeRecord === null || activeRecord === void 0 ? void 0 : activeRecord.jobId) && activeUrl) {
            // Verify the stored job still exists on the server before locking the button.
            // If the server was restarted, resolveActive returns 404 and clears the stale record.
            updateArchidektCacheButtons(true);
            void resolveActiveArchidektCacheJob(activeUrl);
        }
        else if (activeRecord === null || activeRecord === void 0 ? void 0 : activeRecord.jobId) {
            updateArchidektCacheButtons(activeRecord.state === 'Queued' || activeRecord.state === 'Running');
            void pollArchidektCacheJob();
        }
        else if (archidektCacheJobStartPending && activeUrl) {
            updateArchidektCacheButtons(true);
            void resolveActiveArchidektCacheJob(activeUrl);
        }
        else {
            updateArchidektCacheButtons(false);
            if (activeUrl) {
                void resolveActiveArchidektCacheJob(activeUrl);
            }
        }
    };
    document.addEventListener('DOMContentLoaded', attachBackToTop);
    document.addEventListener('DOMContentLoaded', attachThemePicker);
    document.addEventListener('DOMContentLoaded', attachArchidektCacheJobUi);
    if (document.readyState !== 'loading') {
        attachBackToTop();
        attachThemePicker();
        attachArchidektCacheJobUi();
    }
})();
