"use strict";
(() => {
    'use strict';
    let backToTopInitialized = false;
    let themePickerInitialized = false;
    const themeStorageKey = 'mtg-deck-studio-theme';
    const themeCookieMaxAgeSeconds = 60 * 60 * 24 * 365;
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
    document.addEventListener('DOMContentLoaded', attachBackToTop);
    document.addEventListener('DOMContentLoaded', attachThemePicker);
    if (document.readyState !== 'loading') {
        attachBackToTop();
        attachThemePicker();
    }
})();
