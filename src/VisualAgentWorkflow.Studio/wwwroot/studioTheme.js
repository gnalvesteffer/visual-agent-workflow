(() => {
    const key = 'visual-agent-workflow.theme';
    const themes = new Set(['dark', 'light', 'vs-code-dark-plus', 'solarized-dark', 'nord', 'monokai', 'github-dark', 'github-light', 'dracula', 'high-contrast']);
    const root = document.documentElement;

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
        set: value => {
            if (!themes.has(value)) return;
            root.dataset.vawTheme = value;
            try { localStorage.setItem(key, value); } catch { }
        }
    };

    // Enhanced navigation patches the document and can discard root data attributes.
    document.addEventListener('DOMContentLoaded', () => {
        window.Blazor?.addEventListener('enhancedload', apply);
    }, { once: true });
})();
