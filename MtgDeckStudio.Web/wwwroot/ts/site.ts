((): void => {
  'use strict';

  let backToTopInitialized = false;

  const attachBackToTop = (): void => {
    if (backToTopInitialized) {
      return;
    }

    backToTopInitialized = true;
    const button = document.getElementById('back-to-top-button');
    if (!(button instanceof HTMLButtonElement)) {
      return;
    }

    button.addEventListener('click', () => {
      window.scrollTo({
        top: 0,
        behavior: 'smooth'
      });
    });
  };

  document.addEventListener('DOMContentLoaded', attachBackToTop);
  if (document.readyState !== 'loading') {
    attachBackToTop();
  }
})();
