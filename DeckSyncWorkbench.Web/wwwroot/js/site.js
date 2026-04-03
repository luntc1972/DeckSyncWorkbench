"use strict";
(() => {
    'use strict';
    let backToTopInitialized = false;
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
    document.addEventListener('DOMContentLoaded', attachBackToTop);
    if (document.readyState !== 'loading') {
        attachBackToTop();
    }
})();
