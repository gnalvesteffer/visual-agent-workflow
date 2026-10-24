// Browser-local gesture previews. Only completed edits cross the Blazor boundary.
const canvases = new WeakMap();
const limit = 32768;
const clamp = value => Math.max(0, Math.min(limit, Math.round(value / 8) * 8));
const number = value => Number.parseFloat(value) || 0;
const position = node => ({ x: number(node.style.left), y: number(node.style.top) });

export function attach(root, callback) {
    if (canvases.has(root)) return canvases.get(root).handle;
    const state = { root, callback, frame: 0, drag: null, nodes: new Map(), paths: [], sections: [], active: true };
    state.viewport = root.querySelector('.vaw-canvas-viewport');
    state.world = root.querySelector('.vaw-canvas-world');
    state.observer = new ResizeObserver(() => schedule(state));
    state.down = event => pointerDown(state, event);
    state.move = event => pointerMove(state, event);
    state.up = event => pointerEnd(state, event, false);
    state.cancel = event => pointerEnd(state, event, true);
    state.double = event => {
        const path = event.target.closest('[data-wire]');
        if (!path || root.dataset.disabled === 'true') return;
        event.preventDefault();
        const point = local(state, event);
        invoke(state, 'AddWaypoint', path.dataset.wire, Number(path.dataset.segment), clamp(point.x), clamp(point.y));
    };
    state.click = event => {
        const path = event.target.closest('[data-wire]');
        if (path) invoke(state, 'SelectWire', path.dataset.wire);
    };
    state.key = event => {
        if (event.target.closest('[data-drag-node], [data-drag-section], [data-drag-wire]') && event.key.startsWith('Arrow'))
            event.preventDefault();
    };
    root.addEventListener('pointerdown', state.down);
    root.addEventListener('pointermove', state.move);
    root.addEventListener('pointerup', state.up);
    root.addEventListener('pointercancel', state.cancel);
    root.addEventListener('lostpointercapture', state.cancel);
    root.addEventListener('dblclick', state.double);
    root.addEventListener('click', state.click);
    root.addEventListener('keydown', state.key);
    state.handle = { refresh: () => sync(root), dispose: () => detach(root) };
    canvases.set(root, state);
    sync(root);
    return state.handle;
}

export function sync(root) {
    const state = canvases.get(root);
    if (!state) return;
    state.nodes = new Map([...root.querySelectorAll('li[data-node-id]')].map(node => [node.dataset.nodeId, node]));
    state.paths = [...root.querySelectorAll('path[data-wire]')];
    state.sections = [...root.querySelectorAll('[data-section]')];
    if (!state.drag) restoreCanonical(state);
    state.faulted = false;
    root.querySelector('[data-canvas-error]').hidden = true;
    state.observer.disconnect();
    for (const node of state.nodes.values()) state.observer.observe(node);
    draw(state);
}

function schedule(state) {
    if (!state.active || state.frame) return;
    state.frame = requestAnimationFrame(() => { state.frame = 0; draw(state); });
}

function scale(state) { return new DOMMatrixReadOnly(getComputedStyle(state.world).transform).a || 1; }
function local(state, event) {
    const rect = state.world.getBoundingClientRect(), zoom = scale(state);
    return { x: (event.clientX - rect.left) / zoom, y: (event.clientY - rect.top) / zoom };
}

function draw(state) {
    if (!state.active || !state.world.isConnected) return;
    const zoom = scale(state), world = state.world.getBoundingClientRect();
    const rects = new Map();
    for (const [id, node] of state.nodes) {
        const rect = node.getBoundingClientRect();
        const sockets = {};
        for (const socket of node.querySelectorAll('[data-socket]')) {
            const pin = socket.getBoundingClientRect();
            sockets[socket.dataset.socket] = { x: (pin.left + pin.width / 2 - world.left) / zoom,
                y: (pin.top + pin.height / 2 - world.top) / zoom };
        }
        rects.set(id, { x: (rect.left - world.left) / zoom, y: (rect.top - world.top) / zoom,
            width: rect.width / zoom, height: rect.height / zoom, sockets });
    }
    for (const path of state.paths) {
        const from = rects.get(path.dataset.source)?.sockets[path.dataset.sourcePort ?? "out"];
        const to = rects.get(path.dataset.target)?.sockets[path.dataset.targetPort ?? "in"];
        if (!from || !to) { path.setAttribute('d', ''); continue; }
        const points = JSON.parse(path.dataset.points);
        if (state.drag?.kind === 'wire' && state.drag.id === path.dataset.wire)
            points[state.drag.index].position = state.drag.point;
        const anchors = [{ position: from, angle: 0 }, ...points, { position: to, angle: 0 }];
        for (let i = 1; i < anchors.length - 1; i++) {
            if (anchors[i].angle === null || anchors[i].angle === undefined) {
                const previous = anchors[i - 1].position, next = anchors[i + 1].position;
                anchors[i].angle = Math.atan2(next.y - previous.y, next.x - previous.x) * 180 / Math.PI;
            }
        }
        const index = Number(path.dataset.segment), a = anchors[index], b = anchors[index + 1];
        path.setAttribute('d', path.dataset.style === 'orthogonal' ? orthogonal(a, b) : curve(a, b));
    }
    for (const box of state.sections) {
        const members = JSON.parse(box.dataset.members).map(id => rects.get(id)).filter(Boolean);
        if (!members.length) continue;
        const left = Math.max(0, Math.min(...members.map(node => node.x)) - 24);
        const top = Math.max(0, Math.min(...members.map(node => node.y)) - 64);
        const right = Math.max(...members.map(node => node.x + node.width)) + 24;
        const bottom = Math.max(...members.map(node => node.y + node.height)) + 24;
        Object.assign(box.style, { left: `${left}px`, top: `${top}px`, width: `${right - left}px`, height: `${bottom - top}px` });
    }
    // Large comments or agent controls may make cards taller than their initial estimate.
    const right = Math.max(1200, ...[...rects.values()].map(rect => rect.x + rect.width + 100));
    const bottom = Math.max(800, ...[...rects.values()].map(rect => rect.y + rect.height + 100));
    const maxPointX = Math.max(0, ...state.paths.flatMap(path => JSON.parse(path.dataset.points).map(p => p.position.x + 100)));
    const maxPointY = Math.max(0, ...state.paths.flatMap(path => JSON.parse(path.dataset.points).map(p => p.position.y + 100)));
    const width = Math.max(right, maxPointX), height = Math.max(bottom, maxPointY);
    state.world.style.width = `${width}px`; state.world.style.height = `${height}px`;
    const extent = state.world.parentElement;
    extent.style.width = `${width * zoom}px`; extent.style.height = `${height * zoom}px`;
}

function tangent(anchor, previous, next) {
    if (anchor.angle !== null && anchor.angle !== undefined) {
        const radians = anchor.angle * Math.PI / 180;
        return { x: Math.cos(radians), y: Math.sin(radians) };
    }
    const dx = next.position.x - previous.position.x, dy = next.position.y - previous.position.y;
    const length = Math.hypot(dx, dy) || 1;
    return { x: dx / length, y: dy / length };
}
function curve(a, b) {
    const length = Math.min(180, Math.max(40, Math.hypot(b.position.x - a.position.x, b.position.y - a.position.y) * 0.4));
    const start = tangent(a, a, b), end = tangent(b, a, b);
    return `M ${a.position.x} ${a.position.y} C ${a.position.x + start.x * length} ${a.position.y + start.y * length}, ${b.position.x - end.x * length} ${b.position.y - end.y * length}, ${b.position.x} ${b.position.y}`;
}
function orthogonal(a, b) {
    const axis = angle => {
        const radians = Math.round((angle || 0) / 90) * Math.PI / 2;
        return { x: Math.round(Math.cos(radians)), y: Math.round(Math.sin(radians)) };
    };
    const start = axis(a.angle), end = axis(b.angle);
    const p = { x: a.position.x + start.x * 32, y: a.position.y + start.y * 32 };
    const q = { x: b.position.x - end.x * 32, y: b.position.y - end.y * 32 };
    const middle = start.x !== 0
        ? `L ${(p.x + q.x) / 2} ${p.y} L ${(p.x + q.x) / 2} ${q.y}`
        : `L ${p.x} ${(p.y + q.y) / 2} L ${q.x} ${(p.y + q.y) / 2}`;
    return `M ${a.position.x} ${a.position.y} L ${p.x} ${p.y} ${middle} L ${q.x} ${q.y} L ${b.position.x} ${b.position.y}`;
}

function pointerDown(state, event) {
    if (state.drag || state.faulted || (event.button !== 0 && event.button !== 1)) return;
    const handle = event.target.closest('[data-drag-node], [data-drag-section], [data-drag-wire]');
    if (handle && state.root.dataset.disabled === 'true') return;
    const background = !event.target.closest('li[data-node-id], button, input, select, textarea, summary, path');
    if (!handle && (!background || !state.viewport.contains(event.target))) return;
    const drag = { pointer: event.pointerId, start: local(state, event), screenX: event.clientX, screenY: event.clientY,
        target: handle || state.viewport, moved: false, items: [] };
    if (handle?.dataset.dragNode) {
        drag.kind = 'node'; drag.id = handle.dataset.dragNode;
        const node = state.nodes.get(drag.id); drag.items = [{ node, start: position(node) }];
    } else if (handle?.dataset.dragSection) {
        drag.kind = 'section'; drag.id = handle.dataset.dragSection;
        const box = state.sections.find(section => section.dataset.section === drag.id);
        drag.items = JSON.parse(box.dataset.members).map(id => state.nodes.get(id)).filter(Boolean).map(node => ({ node, start: position(node) }));
    } else if (handle?.dataset.dragWire) {
        drag.kind = 'wire'; drag.id = handle.dataset.dragWire; drag.index = Number(handle.dataset.point);
        drag.original = position(handle); drag.point = drag.original;
    } else {
        drag.kind = 'pan'; drag.scrollX = state.viewport.scrollLeft; drag.scrollY = state.viewport.scrollTop;
    }
    state.drag = drag;
    drag.target.setPointerCapture(event.pointerId);
    if (drag.kind === 'pan') event.preventDefault();
}

function pointerMove(state, event) {
    const drag = state.drag;
    if (!drag || event.pointerId !== drag.pointer) return;
    const dx = (event.clientX - drag.screenX) / scale(state), dy = (event.clientY - drag.screenY) / scale(state);
    if (Math.hypot(dx, dy) < 2 && !drag.moved) return;
    drag.moved = true;
    if (drag.kind === 'pan') {
        state.viewport.scrollLeft = drag.scrollX - (event.clientX - drag.screenX);
        state.viewport.scrollTop = drag.scrollY - (event.clientY - drag.screenY);
    } else if (drag.kind === 'wire') {
        drag.point = { x: Math.max(0, Math.min(limit, drag.original.x + Math.round(dx / 8) * 8)),
            y: Math.max(0, Math.min(limit, drag.original.y + Math.round(dy / 8) * 8)) };
        Object.assign(drag.target.style, { left: `${drag.point.x}px`, top: `${drag.point.y}px` });
    } else {
        const minX = Math.min(...drag.items.map(item => item.start.x)), maxX = Math.max(...drag.items.map(item => item.start.x));
        const minY = Math.min(...drag.items.map(item => item.start.y)), maxY = Math.max(...drag.items.map(item => item.start.y));
        drag.dx = Math.max(-minX, Math.min(limit - maxX, Math.round(dx / 8) * 8));
        drag.dy = Math.max(-minY, Math.min(limit - maxY, Math.round(dy / 8) * 8));
        for (const item of drag.items) Object.assign(item.node.style, { left: `${item.start.x + drag.dx}px`, top: `${item.start.y + drag.dy}px` });
    }
    event.preventDefault(); schedule(state);
}

function pointerEnd(state, event, cancelled) {
    const drag = state.drag;
    if (!drag || event.pointerId !== drag.pointer) return;
    state.drag = null;
    if (drag.target.hasPointerCapture(event.pointerId)) drag.target.releasePointerCapture(event.pointerId);
    if (cancelled) {
        for (const item of drag.items) Object.assign(item.node.style, { left: `${item.start.x}px`, top: `${item.start.y}px` });
        if (drag.kind === 'wire') Object.assign(drag.target.style, { left: `${drag.original.x}px`, top: `${drag.original.y}px` });
    } else if (drag.moved) {
        if (drag.kind === 'node') { const p = position(drag.items[0].node); invoke(state, 'MoveNode', drag.id, p.x, p.y); }
        if (drag.kind === 'section') invoke(state, 'MoveSection', drag.id, drag.dx, drag.dy);
        if (drag.kind === 'wire') invoke(state, 'MoveWaypoint', drag.id, drag.index, drag.point.x, drag.point.y);
    }
    schedule(state);
}

function invoke(state, method, ...args) {
    if (!state.active) return;
    state.callback.invokeMethodAsync(method, ...args).catch(() => {
        if (state.active) {
            state.faulted = true;
            state.root.dataset.ready = 'false';
            restoreCanonical(state);
            state.root.querySelector('[data-canvas-error]').hidden = false;
            schedule(state);
        }
    });
}

function restoreCanonical(state) {
    for (const element of state.root.querySelectorAll('[data-x][data-y]'))
        Object.assign(element.style, { left: `${number(element.dataset.x)}px`, top: `${number(element.dataset.y)}px` });
}

export function detach(root) {
    const state = canvases.get(root);
    if (!state) return;
    state.active = false;
    state.observer.disconnect(); cancelAnimationFrame(state.frame);
    for (const [name, handler] of [['pointerdown', state.down], ['pointermove', state.move], ['pointerup', state.up],
        ['pointercancel', state.cancel], ['lostpointercapture', state.cancel], ['dblclick', state.double], ['click', state.click], ['keydown', state.key]])
        root.removeEventListener(name, handler);
    canvases.delete(root);
}
