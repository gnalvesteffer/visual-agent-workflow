let installed = false;
export function install() {
    if (installed) return;
    installed = true;
    document.addEventListener('mousedown', event => {
        if (event.button === 1 && event.target.closest('[data-vaw-closable-tab]')) event.preventDefault();
    });
}
export function showDiscardDialog(dialog) {
    if (!dialog.open) dialog.showModal();
}
