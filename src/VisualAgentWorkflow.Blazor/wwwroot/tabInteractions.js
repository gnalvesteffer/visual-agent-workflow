let installed = false;
export function install() {
    if (installed) return;
    installed = true;
    document.addEventListener('mousedown', event => {
        if (event.button === 1 && event.target.closest('[data-vaw-closable-tab]')) event.preventDefault();
    });
}
export function showDiscardDialog(dialog) {
    showModalDialog(dialog);
}
export function showModalDialog(dialog) { if (!dialog.open) dialog.showModal(); }
export function closeModalDialog(dialog) { if (dialog.open) dialog.close(); }

// Restores keyboard focus to a Studio inspector control after a keyed list edit re-created or moved it.
export function focusShapeControl(key) {
    document.querySelector(`[data-shape-control="${CSS.escape(key)}"]`)?.focus({ preventScroll: true });
    return true;
}

// After a rejected edit the inspector controls are recreated so they show the stored value; these two calls keep keyboard focus on the same control.
export function activeAriaLabel() {
    return document.activeElement?.getAttribute?.('aria-label') ?? null;
}

export function focusByAriaLabel(label) {
    document.querySelector(`.vaw-properties [aria-label="${CSS.escape(label)}"]`)?.focus({ preventScroll: true });
    return true;
}
