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
        workflowSelectionListenerInstalled: false,
        subscribeWorkflowSelection: dotNetReference => {
            window.vawStudioIde.workflowSelectionReference = dotNetReference;
            if (window.vawStudioIde.workflowSelectionListenerInstalled) return;
            window.vawStudioIde.workflowSelectionListenerInstalled = true;
            document.addEventListener('change', event => {
                const select = event.target;
                if (select instanceof HTMLSelectElement && select.matches('[data-vaw-workflow-selector]')) {
                    window.vawStudioIde.applySelectedWorkflow(select.value);
                    window.vawStudioIde.workflowSelectionReference?.invokeMethodAsync('WorkflowSelectionChanged', select.value).catch(() => {});
                }
            });
        },
        getSelectedWorkflow: () => document.querySelector('[data-vaw-workflow-selector]')?.value ?? null,
        applySelectedWorkflow: id => {
            document.querySelectorAll('.vaw-explorer-file[data-workflow-id]').forEach(file => {
                const selected = file.dataset.workflowId === id;
                file.classList.toggle('is-selected', selected);
                if (selected) file.setAttribute('aria-current', 'page');
                else file.removeAttribute('aria-current');
            });
        },
        createWorkflow: () => {
            const button = document.querySelector('[data-vaw-new-workflow]');
            if (!button) return 0;
            if (button.disabled) return -1;
            button.click();
            return 1;
        },
        layoutExplorerMenu: (menu, x, y, focus) => {
            const box = menu.getBoundingClientRect();
            menu.style.left = `${Math.max(8, Math.min(x, innerWidth - box.width - 8))}px`;
            menu.style.top = `${Math.max(8, Math.min(y, innerHeight - box.height - 8))}px`;
            if (focus) (menu.querySelector('[role="menuitem"]:not(:disabled), input:not(:disabled)') ?? menu).focus({ preventScroll: true });
            if (!menu.vawMenuKeyboard) {
                menu.vawMenuKeyboard = true;
                menu.addEventListener('keydown', event => {
                    if (event.target.matches('input, textarea') || !['ArrowUp', 'ArrowDown'].includes(event.key)) return;
                    const items = [...menu.querySelectorAll('[role="menuitem"]:not(:disabled)')];
                    if (!items.length) return;
                    event.preventDefault();
                    const index = items.indexOf(document.activeElement);
                    items[(index + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length].focus();
                });
            }
        },
        selectWorkflow: id => {
            const select = document.querySelector('[data-vaw-workflow-selector]');
            if (!select) return 0;
            if (select.matches(':disabled')) return -1;
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
        if (link.closest('.vaw-host-settings-menu')) link.closest('details')?.removeAttribute('open');

        const destination = new URL(link.href, location.href);
        if (destination.origin !== location.origin ||
            (destination.pathname === location.pathname && destination.search === location.search)) return;

        window.clearTimeout(navigationTimer);
        navigationTimer = window.setTimeout(() => {
            document.querySelector('.vaw-navigation-status')?.removeAttribute('hidden');
        }, 120);
    }, true);
    document.addEventListener('pointerdown', event => {
        const openSettings = document.querySelector('.vaw-host-settings[open]');
        if (openSettings && event.target instanceof Node && !openSettings.contains(event.target)) openSettings.removeAttribute('open');
    }, true);
    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape') return;
        const openSettings = document.querySelector('.vaw-host-settings[open]');
        if (!openSettings) return;
        event.preventDefault();
        openSettings.removeAttribute('open');
        openSettings.querySelector(':scope > summary')?.focus();
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
