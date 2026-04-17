((): void => {
  'use strict';

  let backToTopInitialized = false;
  let themePickerInitialized = false;
  let archidektCacheJobInitialized = false;
  let archidektCacheJobPollHandle: number | null = null;
  let archidektCacheJobStartPending = false;
  let archidektCacheJobLocked = false;
  let archidektCacheJobResolveVersion = 0;
  let archidektCacheJobRecordMemory: ArchidektCacheJobRecord | null = null;
  const themeStorageKey = 'deckflow-theme';
  const themeCookieMaxAgeSeconds = 60 * 60 * 24 * 365;
  const archidektCacheJobStorageKey = 'deckflow-archidekt-cache-job';
  const archidektCacheJobDismissedKey = 'deckflow-archidekt-cache-job-dismissed';
  const archidektCacheJobPendingKey = 'deckflow-archidekt-cache-job-pending';
  const pageSnapshotStoragePrefix = 'decksync-page-snapshot-';
  const archidektCacheJobPollIntervalMs = 5000;

  type ArchidektCacheJobRecord = {
    jobId: string;
    statusUrl: string;
    state: string;
    noticeDismissed?: boolean;
    completionNotified?: boolean;
  };

  type ArchidektCacheJobResponse = {
    jobId: string;
    statusUrl?: string;
    state: string;
    durationSeconds: number;
    requestedUtc: string;
    startedUtc?: string | null;
    completedUtc?: string | null;
    decksProcessed: number;
    additionalDecksFound: number;
    errorMessage?: string | null;
    startedNewJob?: boolean;
  };

  type ArchidektCacheJobResponsePayload = ArchidektCacheJobResponse & {
    JobId?: string;
    StatusUrl?: string;
    State?: string;
    DurationSeconds?: number;
    RequestedUtc?: string;
    StartedUtc?: string | null;
    CompletedUtc?: string | null;
    DecksProcessed?: number;
    AdditionalDecksFound?: number;
    ErrorMessage?: string | null;
    StartedNewJob?: boolean;
    Message?: string;
  };

  const getSessionStorage = (): Storage | null => {
    try {
      const testKey = '__decksync_page_snapshot_test_key__';
      window.sessionStorage.setItem(testKey, '1');
      window.sessionStorage.removeItem(testKey);
      return window.sessionStorage;
    } catch {
      return null;
    }
  };

  const getPageSnapshotKey = (): string => `${pageSnapshotStoragePrefix}${window.location.pathname}`;

  const clearLegacyPageSnapshot = (): void => {
    const storage = getSessionStorage();
    if (!storage) {
      return;
    }

    try {
      storage.removeItem(getPageSnapshotKey());
    } catch {
      // Ignore storage failures and continue without page snapshot persistence.
    }
  };

  const clearLegacyPageSnapshotsOnLoad = (): void => {
    clearLegacyPageSnapshot();
  };

  const attachBackToTop = (): void => {
    if (backToTopInitialized) {
      return;
    }

    backToTopInitialized = true;
    const button = document.getElementById('back-to-top-button');
    if (!(button instanceof HTMLButtonElement)) {
      return;
    }

    const updateVisibility = (): void => {
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

  const attachThemePicker = (): void => {
    if (themePickerInitialized) {
      return;
    }

    themePickerInitialized = true;
    const themeLink = document.getElementById('theme-stylesheet');
    const themeSelect = document.getElementById('theme-picker');
    if (!(themeLink instanceof HTMLLinkElement) || !(themeSelect instanceof HTMLSelectElement)) {
      return;
    }

    const themeCookieName = themeLink.dataset.cookieName ?? themeStorageKey;

    const getStoredTheme = (): string | null => {
      try {
        return window.localStorage.getItem(themeStorageKey);
      } catch {
        return null;
      }
    };

    const getCookieTheme = (): string | null => {
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
      } catch {
        return null;
      }
    };

    const setStoredTheme = (value: string): void => {
      try {
        window.localStorage.setItem(themeStorageKey, value);
      } catch {
        // Ignore storage failures and keep the current session theme applied.
      }
    };

    const setCookieTheme = (value: string): void => {
      document.cookie = `${encodeURIComponent(themeCookieName)}=${encodeURIComponent(value)}; max-age=${themeCookieMaxAgeSeconds}; path=/; samesite=lax`;
    };

    const getThemeHref = (value: string): string | null => {
      const matchingOption = Array.from(themeSelect.options).find((option) => option.value === value);
      return matchingOption?.dataset.themeHref ?? null;
    };

    const applyTheme = (value: string, persistSelection: boolean): void => {
      const selectedValue = getThemeHref(value) ? value : themeSelect.options[0]?.value ?? 'site.css';
      const selectedHref = getThemeHref(selectedValue) ?? themeLink.dataset.defaultHref ?? themeLink.href;
      themeLink.href = selectedHref;
      themeSelect.value = selectedValue;

      if (persistSelection) {
        setStoredTheme(selectedValue);
        setCookieTheme(selectedValue);
      }
    };

    applyTheme(getStoredTheme() ?? getCookieTheme() ?? themeLink.dataset.defaultTheme ?? 'site.css', false);
    themeSelect.addEventListener('change', () => {
      applyTheme(themeSelect.value, true);
    });
  };

  const readArchidektCacheJobRecord = (): ArchidektCacheJobRecord | null => {
    if (archidektCacheJobRecordMemory) {
      return archidektCacheJobRecordMemory;
    }

    try {
      const payload = window.localStorage.getItem(archidektCacheJobStorageKey);
      if (!payload) {
        return null;
      }

      const record = JSON.parse(payload) as ArchidektCacheJobRecord;
      archidektCacheJobRecordMemory = record;
      return record;
    } catch {
      return null;
    }
  };

  const writeArchidektCacheJobRecord = (record: ArchidektCacheJobRecord | null): void => {
    archidektCacheJobRecordMemory = record;

    try {
      if (!record) {
        window.localStorage.removeItem(archidektCacheJobStorageKey);
        return;
      }

      window.localStorage.setItem(archidektCacheJobStorageKey, JSON.stringify(record));
    } catch {
      // Ignore storage failures and continue without cross-page persistence.
    }
  };

  const readDismissedJobId = (): string | null => {
    try {
      return window.localStorage.getItem(archidektCacheJobDismissedKey);
    } catch {
      return null;
    }
  };

  const readPendingStart = (): boolean => {
    try {
      return window.localStorage.getItem(archidektCacheJobPendingKey) === '1';
    } catch {
      return false;
    }
  };

  const writePendingStart = (pending: boolean): void => {
    archidektCacheJobStartPending = pending;
    try {
      if (pending) {
        window.localStorage.setItem(archidektCacheJobPendingKey, '1');
        return;
      }

      window.localStorage.removeItem(archidektCacheJobPendingKey);
    } catch {
      // Ignore storage failures and keep the pending state in memory only.
    }
  };

  const writeDismissedJobId = (jobId: string | null): void => {
    try {
      if (!jobId) {
        window.localStorage.removeItem(archidektCacheJobDismissedKey);
        return;
      }

      window.localStorage.setItem(archidektCacheJobDismissedKey, jobId);
    } catch {
      // Ignore storage failures and keep the notice transient.
    }
  };

  const getGlobalJobNotice = (): HTMLElement | null =>
    document.querySelector<HTMLElement>('[data-global-job-notice]');

  const setGlobalJobNotice = (message?: string | null, jobId?: string): void => {
    const notice = getGlobalJobNotice();
    const text = document.querySelector<HTMLElement>('[data-global-job-notice-text]');
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

  const setPageJobStatus = (message?: string | null): void => {
    const panel = document.querySelector<HTMLElement>('[data-archidekt-cache-status-panel]');
    const text = document.querySelector<HTMLElement>('[data-archidekt-cache-status-text]');
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

  const updateArchidektCacheButtons = (isRunning: boolean): void => {
    document.querySelectorAll<HTMLButtonElement>('[data-archidekt-cache-start]').forEach(button => {
      const disabled = isRunning || archidektCacheJobStartPending || archidektCacheJobLocked;
      button.disabled = disabled;
      button.setAttribute('aria-disabled', disabled ? 'true' : 'false');
      button.classList.toggle('disabled', disabled);
      button.textContent = disabled
        ? (archidektCacheJobStartPending && !isRunning ? 'Starting Archidekt Harvest...' : 'Archidekt Harvest Running...')
        : 'Run 5-Minute Archidekt Harvest';
    });
  };

  const buildJobMessage = (job: ArchidektCacheJobResponse): string => {
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

  const normalizeArchidektCacheJobResponse = (payload: ArchidektCacheJobResponsePayload | null): ArchidektCacheJobResponse | null => {
    if (!payload) {
      return null;
    }

    const jobId = payload.jobId ?? payload.JobId;
    const state = payload.state ?? payload.State;
    const durationSeconds = payload.durationSeconds ?? payload.DurationSeconds;
    const requestedUtc = payload.requestedUtc ?? payload.RequestedUtc;
    if (!jobId || !state || !Number.isFinite(durationSeconds) || !requestedUtc) {
      return null;
    }

    return {
      jobId,
      statusUrl: payload.statusUrl ?? payload.StatusUrl,
      state,
      durationSeconds,
      requestedUtc,
      startedUtc: payload.startedUtc ?? payload.StartedUtc ?? null,
      completedUtc: payload.completedUtc ?? payload.CompletedUtc ?? null,
      decksProcessed: payload.decksProcessed ?? payload.DecksProcessed ?? 0,
      additionalDecksFound: payload.additionalDecksFound ?? payload.AdditionalDecksFound ?? 0,
      errorMessage: payload.errorMessage ?? payload.ErrorMessage ?? null,
      startedNewJob: payload.startedNewJob ?? payload.StartedNewJob
    };
  };

  const notifyJobCompletion = (job: ArchidektCacheJobResponse): void => {
    if (!('Notification' in window) || document.visibilityState === 'visible') {
      return;
    }

    if (Notification.permission === 'granted') {
      new Notification('Archidekt category harvest complete', {
        body: buildJobMessage(job)
      });
    }
  };

  const applyJobStatus = (job: ArchidektCacheJobResponse, statusUrl: string): void => {
    const message = buildJobMessage(job);
    const completed = job.state === 'Succeeded' || job.state === 'Failed';
    writePendingStart(false);
    archidektCacheJobLocked = !completed;
    const prior = readArchidektCacheJobRecord();
    const completionNotified = prior?.jobId === job.jobId ? prior.completionNotified === true : false;
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

  const pollArchidektCacheJob = async (): Promise<void> => {
    const record = readArchidektCacheJobRecord();
    if (!record?.statusUrl) {
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

      const job = await response.json() as ArchidektCacheJobResponse;
      const completed = job.state === 'Succeeded' || job.state === 'Failed';
      applyJobStatus(job, record.statusUrl);
      if (completed) {
        return;
      }
    } catch {
      // Keep the previous state and try again on the next poll tick.
    }
  };

  const resolveActiveArchidektCacheJob = async (activeUrl: string, version: number = ++archidektCacheJobResolveVersion): Promise<boolean> => {
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

      const payload = await response.json() as ArchidektCacheJobResponse;
      const statusUrl = `/api/archidekt-cache-jobs/${payload.jobId}`;
      applyJobStatus(payload, statusUrl);
      return true;
    } catch {
      return false;
    }
  };

  const startArchidektCacheJobPolling = (): void => {
    if (archidektCacheJobPollHandle !== null) {
      return;
    }

    archidektCacheJobPollHandle = window.setInterval(() => {
      void pollArchidektCacheJob();
    }, archidektCacheJobPollIntervalMs);
  };

  const attachArchidektCacheJobUi = (): void => {
    if (archidektCacheJobInitialized) {
      return;
    }

    archidektCacheJobInitialized = true;

    const dismissButton = document.querySelector<HTMLButtonElement>('[data-global-job-notice-dismiss]');
    dismissButton?.addEventListener('click', () => {
      const record = readArchidektCacheJobRecord();
      writeDismissedJobId(record?.jobId ?? null);
      setGlobalJobNotice(null);
    });

    document.querySelectorAll<HTMLButtonElement>('[data-archidekt-cache-start]').forEach(button => {
      button.addEventListener('click', async () => {
        archidektCacheJobResolveVersion += 1;
        const existingRecord = readArchidektCacheJobRecord();
        if (archidektCacheJobStartPending
          || existingRecord?.state === 'Queued'
          || existingRecord?.state === 'Running')
        {
          archidektCacheJobLocked = true;
          updateArchidektCacheButtons(true);
          return;
        }

        const startUrl = button.dataset.startUrl;
        const statusBaseUrl = button.dataset.statusBaseUrl;
        const activeUrl = button.dataset.activeUrl;
        const durationSeconds = Number(button.dataset.durationSeconds ?? '300');
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

          let payload: ArchidektCacheJobResponsePayload | null = null;
          try {
            payload = await response.json() as ArchidektCacheJobResponsePayload;
          } catch {
            payload = null;
          }

            if (!response.ok) {
                const message = payload?.Message ?? payload?.Message ?? payload?.errorMessage ?? 'Unable to start the Archidekt category harvest.';
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
          const statusUrl = job?.statusUrl
            ?? response.headers.get('Location')
            ?? (job?.jobId ? `${statusBaseUrl}/${job.jobId}` : null);

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
        } catch (error) {
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
    const activeUrl = document.querySelector<HTMLButtonElement>('[data-archidekt-cache-start]')?.dataset.activeUrl;
    if (activeRecord?.jobId && activeUrl) {
      archidektCacheJobLocked = true;
      // Verify the stored job still exists on the server before locking the button.
      // If the server was restarted, resolveActive returns 404 and clears the stale record.
      updateArchidektCacheButtons(true);
      void resolveActiveArchidektCacheJob(activeUrl);
    } else if (activeRecord?.jobId) {
      archidektCacheJobLocked = activeRecord.state === 'Queued' || activeRecord.state === 'Running';
      updateArchidektCacheButtons(activeRecord.state === 'Queued' || activeRecord.state === 'Running');
      void pollArchidektCacheJob();
    } else if (archidektCacheJobStartPending && activeUrl) {
      archidektCacheJobLocked = true;
      updateArchidektCacheButtons(true);
      void resolveActiveArchidektCacheJob(activeUrl);
    } else {
      archidektCacheJobLocked = false;
      updateArchidektCacheButtons(false);
      if (activeUrl) {
        void resolveActiveArchidektCacheJob(activeUrl);
      }
    }
  };

  clearLegacyPageSnapshotsOnLoad();
  document.addEventListener('DOMContentLoaded', attachBackToTop);
  document.addEventListener('DOMContentLoaded', attachThemePicker);
  document.addEventListener('DOMContentLoaded', attachArchidektCacheJobUi);
  if (document.readyState !== 'loading') {
    attachBackToTop();
    attachThemePicker();
    attachArchidektCacheJobUi();
  }
})();
