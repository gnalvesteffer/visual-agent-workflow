function options(root) { return [...root.querySelectorAll('[role="option"]')].filter(item => !item.hidden); }
function activate(root, option) {
    const input = root.querySelector('input');
    for (const item of root.querySelectorAll('[role="option"]')) item.classList.toggle('is-active', item === option);
    if (option) { input.setAttribute('aria-activedescendant', option.id); option.scrollIntoView({ block: 'nearest' }); }
    else input.removeAttribute('aria-activedescendant');
}
function position(root) {
    const panel = root.querySelector('[popover]');
    const trigger = root.querySelector('[popovertarget]').getBoundingClientRect();
    panel.style.left = `${Math.max(8, Math.min(trigger.left, innerWidth - panel.offsetWidth - 8))}px`;
    panel.style.top = `${trigger.bottom + 6}px`;
    panel.style.maxHeight = `${Math.max(100, innerHeight - trigger.bottom - 14)}px`;
}
export function refresh(root) {
    const opened = root.querySelector('[popover]').matches(':popover-open');
    root.querySelector('[popovertarget]').setAttribute('aria-expanded', String(opened));
    if (!opened) return;
    const query = root.querySelector('input').value.trim().toLocaleLowerCase();
    for (const item of root.querySelectorAll('[role="option"]')) item.hidden = !item.dataset.projectSearch.toLocaleLowerCase().includes(query);
    const items = options(root);
    root.querySelector('[role="status"]').textContent = items.length ? `${items.length} ${items.length === 1 ? 'project' : 'projects'}` : 'No matching projects. Try another name or folder.';
    position(root);
    activate(root, items.find(item => item.getAttribute('aria-selected') === 'true') ?? items[0]);
}
export function close(root, restoreFocus = false) {
    const panel = root.querySelector('[popover]');
    root.vawPickerNavigating = !restoreFocus;
    if (panel.matches(':popover-open')) panel.hidePopover();
    if (restoreFocus) root.querySelector('[popovertarget]').focus();
}
export function initialize(root) {
    const panel = root.querySelector('[popover]');
    const trigger = root.querySelector('[popovertarget]');
    const input = root.querySelector('input');
    const toggle = event => {
        const opened = event.newState === 'open';
        trigger.setAttribute('aria-expanded', String(opened));
        if (opened) {
            root.vawPickerNavigating = false;
            input.value = '';
            refresh(root);
            input.focus();
        } else if (!root.vawPickerNavigating && (panel.contains(document.activeElement) || document.activeElement === document.body)) trigger.focus();
    };
    const keyboard = event => {
        if (event.target !== input) return;
        const items = options(root);
        const index = items.findIndex(item => item.id === input.getAttribute('aria-activedescendant'));
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            if (items.length) activate(root, items[(index + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length]);
        } else if (event.key === 'Enter') {
            event.preventDefault();
            items[index]?.click();
        }
    };
    const pointer = event => {
        const option = event.target.closest('[role="option"]');
        if (option) { event.preventDefault(); activate(root, option); }
    };
    const click = event => { if (event.target.closest('a')) close(root); };
    const resize = () => { if (panel.matches(':popover-open')) position(root); };
    let focusTimer;
    const focusout = () => {
        window.clearTimeout(focusTimer);
        focusTimer = window.setTimeout(() => {
            if (panel.matches(':popover-open') && !root.contains(document.activeElement)) close(root);
        }, 0);
    };
    const filter = () => refresh(root);
    input.addEventListener('input', filter);
    panel.addEventListener('toggle', toggle);
    root.addEventListener('mousedown', pointer);
    root.addEventListener('keydown', keyboard);
    root.addEventListener('click', click);
    root.addEventListener('focusout', focusout);
    window.addEventListener('resize', resize);
    root.vawPickerDispose = () => {
        window.clearTimeout(focusTimer);
        input.removeEventListener('input', filter);
        panel.removeEventListener('toggle', toggle);
        root.removeEventListener('mousedown', pointer);
        root.removeEventListener('keydown', keyboard);
        root.removeEventListener('click', click);
        root.removeEventListener('focusout', focusout);
        window.removeEventListener('resize', resize);
    };
}
export function dispose(root) { root.vawPickerDispose?.(); }
