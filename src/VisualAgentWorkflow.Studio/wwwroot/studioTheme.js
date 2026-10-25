(() => {
    const key = 'visual-agent-workflow.theme';
    const themes = new Set(['dark', 'light', 'high-contrast']);
    const root = document.documentElement;
    let theme = 'dark';

    try {
        const saved = localStorage.getItem(key);
        if (themes.has(saved)) theme = saved;
    } catch { }

    root.dataset.vawTheme = theme;
    window.vawStudioTheme = {
        get: () => root.dataset.vawTheme,
        set: value => {
            if (!themes.has(value)) return;
            root.dataset.vawTheme = value;
            try { localStorage.setItem(key, value); } catch { }
        }
    };
})();
