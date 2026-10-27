(() => {
    const key = 'visual-agent-workflow.theme';
    const themes = new Set(['dark', 'light', 'vs-code-dark-plus', 'solarized-dark', 'nord', 'monokai', 'github-dark', 'github-light', 'dracula', 'neon', 'high-contrast']);
    const root = document.documentElement;
    let navigationTimer;

    const clearNavigationStatus = () => {
        window.clearTimeout(navigationTimer);
        document.querySelector('.vaw-navigation-status')?.setAttribute('hidden', '');
    };

    const read = () => {
        try {
            const saved = localStorage.getItem(key);
            return themes.has(saved) ? saved : 'dark';
        } catch { return 'dark'; }
    };
    const apply = () => { root.dataset.vawTheme = read(); };

    apply();
    window.vawStudioTheme = {
        get: read,
        navigationComplete: clearNavigationStatus,
        set: value => {
            if (!themes.has(value)) return;
            root.dataset.vawTheme = value;
            try { localStorage.setItem(key, value); } catch { }
        }
    };
    window.vawStudioIde = {
        selectWorkflow: id => {
            const select = document.querySelector('[data-vaw-workflow-selector]');
            if (!select) return 0;
            if (select.disabled) return -1;
            if (![...select.options].some(option => option.value === id)) return 0;
            select.value = id;
            select.dispatchEvent(new Event('change', { bubbles: true }));
            return 1;
        }
    };

    document.addEventListener('click', event => {
        if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        const link = event.target instanceof Element ? event.target.closest('a[data-vaw-route-link]') : null;
        if (!link || link.target || link.hasAttribute('download')) return;

        const destination = new URL(link.href, location.href);
        if (destination.origin !== location.origin ||
            (destination.pathname === location.pathname && destination.search === location.search)) return;

        window.clearTimeout(navigationTimer);
        navigationTimer = window.setTimeout(() => {
            document.querySelector('.vaw-navigation-status')?.removeAttribute('hidden');
        }, 120);
    }, true);
    window.addEventListener('pageshow', clearNavigationStatus);

    // Enhanced navigation patches the document and can discard root data attributes.
    document.addEventListener('DOMContentLoaded', () => {
        window.Blazor?.addEventListener('enhancedload', () => {
            apply();
            clearNavigationStatus();
        });
    }, { once: true });
})();
