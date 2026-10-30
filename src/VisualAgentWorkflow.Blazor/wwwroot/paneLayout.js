const STORAGE_KEY = 'visual-agent-workflow.layout.v1';
const DEFAULTS = Object.freeze({ library: 224, inspector: 304, bottom: 192 });
const LIMITS = Object.freeze({
    library: [176, 360],
    inspector: [240, 480],
    bottom: [128, 520]
});
const sessions = new WeakMap();
const ROOT_ATTRIBUTE = 'data-vaw-pane-layout-root';

function owns(root, element) {
    return element.closest(`[${ROOT_ATTRIBUTE}]`) === root;
}

function clamp(kind, value, root) {
    const [minimum, maximum] = LIMITS[kind];
    // Keep the user's persisted preference stable across the initial render. The
    // designer can be temporarily narrow while Blazor restores the workbench;
    // clamping against that transient width would silently lose the preference.
    // Responsive CSS hides horizontal panes when there is not enough room.
    return Math.round(Math.min(maximum, Math.max(minimum, Number(value) || DEFAULTS[kind])));
}

function read() {
    try {
        const stored = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? 'null');
        if (!stored || stored.version !== 1 || typeof stored.layout !== 'object') return { ...DEFAULTS };
        return { ...DEFAULTS, ...stored.layout };
    } catch {
        return { ...DEFAULTS };
    }
}

function write(layout) {
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify({ version: 1, layout })); } catch { /* Private browsing and quota failures are harmless. */ }
}

function writePreference(kind, value) {
    write({ ...read(), [kind]: value });
}

function apply(root, layout) {
    const values = {
        library: clamp('library', layout.library, root),
        inspector: clamp('inspector', layout.inspector, root),
        bottom: clamp('bottom', layout.bottom, root)
    };
    const workspace = [...root.querySelectorAll('.vaw-canvas-workspace')].find(element => owns(root, element));
    workspace?.style.setProperty('--vaw-library-width', `${values.library}px`);
    workspace?.style.setProperty('--vaw-inspector-width', `${values.inspector}px`);
    const tray = [...root.querySelectorAll('.vaw-bottom-tray')].find(element => owns(root, element));
    tray?.style.setProperty('--vaw-bottom-height', `${values.bottom}px`);
    for (const handle of root.querySelectorAll('[data-pane-resizer]')) {
        if (!owns(root, handle)) continue;
        const kind = handle.dataset.paneResizer;
        if (kind && values[kind] !== undefined) {
            const value = String(values[kind]);
            // The root observer watches this attribute because Blazor may replace
            // the resizer handles. Avoid writing the same value back: otherwise
            // each observer callback creates another mutation and can freeze the
            // workflow editor on initial navigation.
            if (handle.getAttribute('aria-valuenow') !== value)
                handle.setAttribute('aria-valuenow', value);
        }
    }
    return values;
}

export function attach(root) {
    if (!root) return { dispose() {} };
    const existing = sessions.get(root);
    if (existing) return existing;
    root.setAttribute(ROOT_ATTRIBUTE, '');
    let layout = apply(root, read());
    let active = null;
    const observer = new MutationObserver(() => apply(root, layout));
    observer.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['aria-valuenow'] });
    const update = (kind, value, persist = true) => {
        layout = { ...layout, [kind]: clamp(kind, value, root) };
        apply(root, layout);
        if (persist) writePreference(kind, layout[kind]);
    };
    const pointerMove = event => {
        if (!active) return;
        event.preventDefault();
        const delta = active.kind === 'bottom'
            ? active.startY - event.clientY
            : active.kind === 'inspector'
                ? active.startX - event.clientX
                : event.clientX - active.startX;
        update(active.kind, active.startValue + delta, false);
    };
    const pointerUp = () => {
        if (!active) return;
        writePreference(active.kind, layout[active.kind]);
        active = null;
        document.removeEventListener('pointermove', pointerMove, true);
        document.removeEventListener('pointerup', pointerUp, true);
        document.removeEventListener('pointercancel', pointerUp, true);
    };
    const pointerDown = event => {
        const handle = event.target instanceof Element ? event.target.closest('[data-pane-resizer]') : null;
        if (!handle || !root.contains(handle) || !owns(root, handle)) return;
        const kind = handle.dataset.paneResizer;
        if (!LIMITS[kind]) return;
        event.preventDefault();
        event.stopPropagation();
        active = { kind, startX: event.clientX, startY: event.clientY, startValue: layout[kind] };
        handle.setPointerCapture?.(event.pointerId);
        document.addEventListener('pointermove', pointerMove, true);
        document.addEventListener('pointerup', pointerUp, true);
        document.addEventListener('pointercancel', pointerUp, true);
    };
    const keyDown = event => {
        const handle = event.target instanceof Element ? event.target.closest('[data-pane-resizer]') : null;
        if (!handle || !root.contains(handle) || !owns(root, handle)) return;
        const kind = handle.dataset.paneResizer;
        if (!LIMITS[kind]) return;
        const [minimum, maximum] = LIMITS[kind];
        const step = event.shiftKey ? 64 : 16;
        let value = layout[kind];
        if (event.key === 'Home') value = minimum;
        else if (event.key === 'End') value = maximum;
        else if (kind === 'bottom' && event.key === 'ArrowUp' || kind !== 'bottom' && event.key === 'ArrowRight') value += step;
        else if (kind === 'bottom' && event.key === 'ArrowDown' || kind !== 'bottom' && event.key === 'ArrowLeft') value -= step;
        else return;
        event.preventDefault();
        update(kind, value);
    };
    root.addEventListener('pointerdown', pointerDown, true);
    root.addEventListener('keydown', keyDown, true);
    const reset = () => { layout = apply(root, DEFAULTS); };
    window.addEventListener('vaw-layout-reset', reset);
    const session = { dispose() {
        pointerUp();
        observer.disconnect();
        root.removeEventListener('pointerdown', pointerDown, true);
        root.removeEventListener('keydown', keyDown, true);
        window.removeEventListener('vaw-layout-reset', reset);
        root.removeAttribute(ROOT_ATTRIBUTE);
        sessions.delete(root);
    }};
    sessions.set(root, session);
    return session;
}
