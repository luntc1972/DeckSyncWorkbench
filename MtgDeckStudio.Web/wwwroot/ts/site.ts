((): void => {
  'use strict';

  let backToTopInitialized = false;
  let themePickerInitialized = false;
  const themeStorageKey = 'mtg-deck-studio-theme';
  const themeCookieMaxAgeSeconds = 60 * 60 * 24 * 365;

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

  document.addEventListener('DOMContentLoaded', attachBackToTop);
  document.addEventListener('DOMContentLoaded', attachThemePicker);
  if (document.readyState !== 'loading') {
    attachBackToTop();
    attachThemePicker();
  }
})();
