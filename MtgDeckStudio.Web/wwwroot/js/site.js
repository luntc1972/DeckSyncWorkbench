"use strict";
(() => {
    'use strict';
    let backToTopInitialized = false;
    let themePickerInitialized = false;
    let archidektCacheJobInitialized = false;
    let archidektCacheJobPollHandle = null;
    let archidektCacheJobStartPending = false;
    let archidektCacheJobLocked = false;
    let archidektCacheJobResolveVersion = 0;
    let archidektCacheJobRecordMemory = null;
    let suppressPageSnapshotPersistence = false;
    const themeStorageKey = 'mtg-deck-studio-theme';
    const themeCookieMaxAgeSeconds = 60 * 60 * 24 * 365;
    const archidektCacheJobStorageKey = 'mtg-deck-studio-archidekt-cache-job';
    const archidektCacheJobDismissedKey = 'mtg-deck-studio-archidekt-cache-job-dismissed';
    const archidektCacheJobPendingKey = 'mtg-deck-studio-archidekt-cache-job-pending';
    const pageSnapshotStoragePrefix = 'decksync-page-snapshot-';
    const tabNavigationKey = 'decksync-tab-navigation';
    const archidektCacheJobPollIntervalMs = 5000;
    const getSessionStorage = () => {
        try {
            const testKey = '__decksync_page_snapshot_test_key__';
            window.sessionStorage.setItem(testKey, '1');
            window.sessionStorage.removeItem(testKey);
            return window.sessionStorage;
        }
        catch (_a) {
            return null;
        }
    };
    const getPageSnapshotKey = () => `${pageSnapshotStoragePrefix}${window.location.pathname}`;
    const canPersistCurrentPage = () => document.querySelector('form[data-cache-key]') !== null;
    const persistCurrentPageSnapshot = () => {
        if (suppressPageSnapshotPersistence || !canPersistCurrentPage()) {
            return;
        }
        const storage = getSessionStorage();
        const main = document.querySelector('main.content-shell');
        if (!storage || !main) {
            return;
        }
        try {
            storage.setItem(getPageSnapshotKey(), main.innerHTML);
        }
        catch (_a) {
            // Ignore storage failures and continue without page snapshot persistence.
        }
    };
    const clearCurrentPageSnapshot = () => {
        const storage = getSessionStorage();
        if (!storage) {
            return;
        }
        try {
            storage.removeItem(getPageSnapshotKey());
        }
        catch (_a) {
            // Ignore storage failures and continue without page snapshot persistence.
        }
    };
    const restoreCurrentPageSnapshot = () => {
        const storage = getSessionStorage();
        const main = document.querySelector('main.content-shell');
        if (!storage || !main || storage.getItem(tabNavigationKey) !== '1' || !canPersistCurrentPage()) {
            return;
        }
        const snapshot = storage.getItem(getPageSnapshotKey());
        if (!snapshot) {
            return;
        }
        main.innerHTML = snapshot;
    };
    const attachPageSnapshotPersistence = () => {
        if (!canPersistCurrentPage()) {
            return;
        }
        document.querySelectorAll('.tool-nav__link').forEach(link => {
            link.addEventListener('click', () => {
                const storage = getSessionStorage();
                persistCurrentPageSnapshot();
                storage === null || storage === void 0 ? void 0 : storage.setItem(tabNavigationKey, '1');
            });
        });
        document.querySelectorAll('[data-clear-cache]').forEach(button => {
            button.addEventListener('click', () => {
                suppressPageSnapshotPersistence = true;
                clearCurrentPageSnapshot();
            });
        });
        window.addEventListener('pagehide', () => {
            if (suppressPageSnapshotPersistence) {
                return;
            }
            persistCurrentPageSnapshot();
        });
    };
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
        if (archidektCacheJobRecordMemory) {
            return archidektCacheJobRecordMemory;
        }
        try {
            const payload = window.localStorage.getItem(archidektCacheJobStorageKey);
            if (!payload) {
                return null;
            }
            const record = JSON.parse(payload);
            archidektCacheJobRecordMemory = record;
            return record;
        }
        catch (_a) {
            return null;
        }
    };
    const writeArchidektCacheJobRecord = (record) => {
        archidektCacheJobRecordMemory = record;
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
        document.querySelectorAll('[data-archidekt-cache-start]').forEach(button => {
            const disabled = isRunning || archidektCacheJobStartPending || archidektCacheJobLocked;
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
    const normalizeArchidektCacheJobResponse = (payload) => {
        var _a, _b, _c, _d, _e, _f, _g, _h, _j, _k, _l;
        if (!payload) {
            return null;
        }
        const jobId = (_a = payload.jobId) !== null && _a !== void 0 ? _a : payload.JobId;
        const state = (_b = payload.state) !== null && _b !== void 0 ? _b : payload.State;
        const durationSeconds = (_c = payload.durationSeconds) !== null && _c !== void 0 ? _c : payload.DurationSeconds;
        const requestedUtc = (_d = payload.requestedUtc) !== null && _d !== void 0 ? _d : payload.RequestedUtc;
        if (!jobId || !state || !Number.isFinite(durationSeconds) || !requestedUtc) {
            return null;
        }
        return {
            jobId,
            statusUrl: (_e = payload.statusUrl) !== null && _e !== void 0 ? _e : payload.StatusUrl,
            state,
            durationSeconds,
            requestedUtc,
            startedUtc: (_g = (_f = payload.startedUtc) !== null && _f !== void 0 ? _f : payload.StartedUtc) !== null && _g !== void 0 ? _g : null,
            completedUtc: (_j = (_h = payload.completedUtc) !== null && _h !== void 0 ? _h : payload.CompletedUtc) !== null && _j !== void 0 ? _j : null,
            decksProcessed: (_k = payload.decksProcessed) !== null && _k !== void 0 ? _k : (_l = payload.DecksProcessed) !== null && _l !== void 0 ? _l : 0,
            additionalDecksFound: payload.additionalDecksFound ?? payload.AdditionalDecksFound ?? 0,
            errorMessage: payload.errorMessage ?? payload.ErrorMessage ?? null,
            startedNewJob: payload.startedNewJob ?? payload.StartedNewJob
        };
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
        archidektCacheJobLocked = !completed;
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
            archidektCacheJobLocked = false;
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
                    archidektCacheJobLocked = false;
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
    const resolveActiveArchidektCacheJob = async (activeUrl, version = ++archidektCacheJobResolveVersion) => {
        try {
            const response = await fetch(activeUrl, {
                method: 'GET',
                headers: {
                    'Accept': 'application/json'
                }
            });
            if (version !== archidektCacheJobResolveVersion) {
                return false;
            }
            if (response.status === 404) {
                writePendingStart(false);
                writeArchidektCacheJobRecord(null);
                archidektCacheJobLocked = false;
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
                archidektCacheJobResolveVersion += 1;
                const existingRecord = readArchidektCacheJobRecord();
                if (archidektCacheJobStartPending
                    || (existingRecord === null || existingRecord === void 0 ? void 0 : existingRecord.state) === 'Queued'
                    || (existingRecord === null || existingRecord === void 0 ? void 0 : existingRecord.state) === 'Running') {
                    archidektCacheJobLocked = true;
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
                archidektCacheJobLocked = true;
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
                            archidektCacheJobLocked = false;
                            setPageJobStatus(message);
                            setGlobalJobNotice(message);
                            updateArchidektCacheButtons(false);
                        }
                        return;
                    }
                    if (!payload) {
                        throw new Error('Archidekt category harvest returned an empty response.');
                    }
                    const job = normalizeArchidektCacheJobResponse(payload);
                    const statusUrl = (_c = (_b = job === null || job === void 0 ? void 0 : job.statusUrl) !== null && _b !== void 0 ? _b : response.headers.get('Location')) !== null && _c !== void 0 ? _c : ((job === null || job === void 0 ? void 0 : job.jobId) ? `${statusBaseUrl}/${job.jobId}` : null);
                    writePendingStart(false);
                    archidektCacheJobLocked = true;
                    if (!job || !statusUrl) {
                        setPageJobStatus('Archidekt category harvest is queued and will start shortly.');
                        setGlobalJobNotice('Archidekt category harvest is queued and will start shortly.');
                        updateArchidektCacheButtons(true);
                        void resolveActiveArchidektCacheJob(activeUrl);
                        return;
                    }
                    writeDismissedJobId(null);
                    applyJobStatus(job, statusUrl);
                }
                catch (error) {
                    const activeJobFound = await resolveActiveArchidektCacheJob(activeUrl);
                    if (!activeJobFound) {
                        writePendingStart(false);
                        archidektCacheJobLocked = false;
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
            archidektCacheJobLocked = true;
            // Verify the stored job still exists on the server before locking the button.
            // If the server was restarted, resolveActive returns 404 and clears the stale record.
            updateArchidektCacheButtons(true);
            void resolveActiveArchidektCacheJob(activeUrl);
        }
        else if (activeRecord === null || activeRecord === void 0 ? void 0 : activeRecord.jobId) {
            archidektCacheJobLocked = activeRecord.state === 'Queued' || activeRecord.state === 'Running';
            updateArchidektCacheButtons(activeRecord.state === 'Queued' || activeRecord.state === 'Running');
            void pollArchidektCacheJob();
        }
        else if (archidektCacheJobStartPending && activeUrl) {
            archidektCacheJobLocked = true;
            updateArchidektCacheButtons(true);
            void resolveActiveArchidektCacheJob(activeUrl);
        }
        else {
            archidektCacheJobLocked = false;
            updateArchidektCacheButtons(false);
            if (activeUrl) {
                void resolveActiveArchidektCacheJob(activeUrl);
            }
        }
    };
    restoreCurrentPageSnapshot();
    attachPageSnapshotPersistence();
    document.addEventListener('DOMContentLoaded', attachBackToTop);
    document.addEventListener('DOMContentLoaded', attachThemePicker);
    document.addEventListener('DOMContentLoaded', attachArchidektCacheJobUi);
    if (document.readyState !== 'loading') {
        attachBackToTop();
        attachThemePicker();
        attachArchidektCacheJobUi();
    }
})();
