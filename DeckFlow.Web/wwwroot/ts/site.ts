((): void => {
  'use strict';

  let backToTopInitialized = false;
  let themePickerInitialized = false;
  const themeStorageKey = 'deckflow-theme';
  const themeCookieMaxAgeSeconds = 60 * 60 * 24 * 365;
  const pageSnapshotStoragePrefix = 'decksync-page-snapshot-';

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

    const updateBackToTopVisibility = (): void => {
      const canScroll = document.documentElement.scrollHeight > window.innerHeight;
      const shouldShow = canScroll && window.scrollY >= 300;

      button.classList.toggle('is-visible', shouldShow);
      button.setAttribute('aria-hidden', shouldShow ? 'false' : 'true');
      button.tabIndex = shouldShow ? 0 : -1;
    };

    let themeResetTimer: number | undefined;
    const releaseThemeLock = (): void => {
      button.classList.remove('is-theme-locked');
      if (themeResetTimer !== undefined) {
        window.clearTimeout(themeResetTimer);
        themeResetTimer = undefined;
      }
    };

    button.addEventListener('click', () => {
      button.classList.add('is-theme-locked');
      button.blur();
      if (themeResetTimer !== undefined) {
        window.clearTimeout(themeResetTimer);
      }
      themeResetTimer = window.setTimeout(releaseThemeLock, 500);
      window.scrollTo({
        top: 0,
        behavior: 'smooth'
      });
    });

    window.addEventListener('scroll', updateBackToTopVisibility, { passive: true });
    window.addEventListener('resize', updateBackToTopVisibility);
    updateBackToTopVisibility();
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

    // Keep mobile browser chrome in step with the active theme. The static
    // <meta name="theme-color"> in _Layout is only the Classic default (and the
    // no-JS fallback); read the live --bg after each theme stylesheet parses.
    const themeColorMeta = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
    const syncThemeColor = (): void => {
      if (!themeColorMeta) {
        return;
      }
      const bg = getComputedStyle(document.documentElement).getPropertyValue('--bg').trim();
      if (bg) {
        themeColorMeta.content = bg;
      }
    };
    themeLink.addEventListener('load', syncThemeColor);

    applyTheme(getStoredTheme() ?? getCookieTheme() ?? themeLink.dataset.defaultTheme ?? 'site.css', false);
    syncThemeColor();
    themeSelect.addEventListener('change', () => {
      applyTheme(themeSelect.value, true);
    });
  };

  // Wires the top-nav group dropdowns (Analyze, Build, Reference, Categories).
  // Lives in site.ts so every page — including the landing hub that doesn't load
  // deck-sync.js — gets working dropdowns.
  const attachToolNav = (): void => {
    const nav = document.querySelector<HTMLElement>('[data-tool-nav]');
    if (!nav) return;

    const menuToggle = nav.querySelector<HTMLButtonElement>('[data-tool-nav-menu-toggle]');
    menuToggle?.addEventListener('click', () => {
      const isMenuOpen = nav.classList.toggle('is-menu-open');
      menuToggle.setAttribute('aria-expanded', isMenuOpen ? 'true' : 'false');
      if (!isMenuOpen) {
        closeAllGroups();
      }
    });

    const closeAllGroups = (): void => {
      nav.querySelectorAll<HTMLElement>('[data-tool-nav-group]').forEach(group => {
        group.classList.remove('is-open');
        group.querySelector<HTMLButtonElement>('[data-tool-nav-trigger]')?.setAttribute('aria-expanded', 'false');
      });
    };

    const closeMenu = (): void => {
      nav.classList.remove('is-menu-open');
      menuToggle?.setAttribute('aria-expanded', 'false');
      closeAllGroups();
    };

    nav.querySelectorAll<HTMLButtonElement>('[data-tool-nav-trigger]').forEach(trigger => {
      trigger.addEventListener('click', () => {
        const group = trigger.closest<HTMLElement>('[data-tool-nav-group]');
        if (!group) return;
        const isOpen = group.classList.contains('is-open');
        closeAllGroups();
        if (!isOpen) {
          group.classList.add('is-open');
          trigger.setAttribute('aria-expanded', 'true');
        }
      });
    });

    nav.querySelectorAll<HTMLAnchorElement>('.tool-nav__link').forEach(link => {
      link.addEventListener('click', closeMenu);
    });

    document.addEventListener('click', event => {
      if (!nav.contains(event.target as Node)) {
        closeMenu();
      }
    });

    document.addEventListener('keydown', event => {
      if (event.key === 'Escape') {
        closeMenu();
      }
    });
  };

  const collapsePrimerGroupsOnMobile = (): void => {
    if (!window.matchMedia('(max-width: 600px)').matches) {
      return;
    }

    const groups = Array.from(document.querySelectorAll<HTMLDetailsElement>('details.primer-group'));
    if (groups.length === 0) {
      return;
    }

    groups.forEach((group, index) => {
      if (index === 0) {
        group.setAttribute('open', '');
        return;
      }

      group.removeAttribute('open');
    });
  };

  const collapseKbFiltersOnMobile = (): void => {
    if (!window.matchMedia('(max-width: 600px)').matches) {
      return;
    }

    const filters = document.querySelector<HTMLDetailsElement>('details.kb-filters');
    if (filters === null) {
      return;
    }

    filters.removeAttribute('open');
  };

  const collapseJudgeOptionalRefOnMobile = (): void => {
    if (!window.matchMedia('(max-width: 600px)').matches) {
      return;
    }

    const optionalRef = document.querySelector<HTMLDetailsElement>('details.judge-optional-ref');
    if (optionalRef === null) {
      return;
    }

    optionalRef.removeAttribute('open');
  };

  const attachContentKbCardNavigation = (): void => {
    document.addEventListener('click', (event) => {
      const target = event.target;
      if (!(target instanceof Element)) {
        return;
      }

      if (target.closest('a, button, input')) {
        return;
      }

      const card = target.closest<HTMLElement>('[data-kb-entry]');
      if (card === null) {
        return;
      }

      const titleLink = card.querySelector<HTMLAnchorElement>('.hub-card__title a[href]');
      const href = titleLink?.href;
      if (!href) {
        return;
      }

      window.location.assign(href);
    });
  };

  // Workflow step tabs (_WorkflowStepTabs.cshtml) mark not-yet-reachable steps with
  // aria-disabled rather than the real `disabled` attribute, so they stay focusable and arrow
  // traversal of the tablist does not dead-end at the first incomplete step. That leaves them
  // clickable, so swallow activation here — in the capture phase, before the per-page handlers in
  // deck-sync.ts and primer-selection.ts get it. One listener covers all five tablist pages.
  document.addEventListener('click', event => {
    const target = event.target;
    if (!(target instanceof Element)) {
      return;
    }

    if (!target.closest('[role="tab"][aria-disabled="true"]')) {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
  }, true);

  clearLegacyPageSnapshotsOnLoad();
  document.addEventListener('DOMContentLoaded', attachBackToTop);
  document.addEventListener('DOMContentLoaded', attachThemePicker);
  document.addEventListener('DOMContentLoaded', attachToolNav);
  document.addEventListener('DOMContentLoaded', collapsePrimerGroupsOnMobile);
  document.addEventListener('DOMContentLoaded', collapseKbFiltersOnMobile);
  document.addEventListener('DOMContentLoaded', collapseJudgeOptionalRefOnMobile);
  document.addEventListener('DOMContentLoaded', attachContentKbCardNavigation);
  if (document.readyState !== 'loading') {
    attachBackToTop();
    attachThemePicker();
    attachToolNav();
    collapsePrimerGroupsOnMobile();
    collapseKbFiltersOnMobile();
    collapseJudgeOptionalRefOnMobile();
    attachContentKbCardNavigation();
  }
})();
