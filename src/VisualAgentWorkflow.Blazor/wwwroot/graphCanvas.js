// Browser-local gesture previews. Only completed edits cross the Blazor boundary.
const canvases = new WeakMap();
const limit = 32768;
const clamp = value => Math.max(0, Math.min(limit, Math.round(value / 8) * 8));
const number = value => Number.parseFloat(value) || 0;
const position = node => ({ x: number(node.style.left), y: number(node.style.top) });

export function attach(root, callback) {
    if (canvases.has(root)) return canvases.get(root).handle;
    const state = { root, callback, frame: 0, drag: null, nodes: new Map(), paths: [], sections: [], camera: { x: 0, y: 0 }, active: true };
    state.viewport = root.querySelector('.vaw-canvas-viewport');
    state.world = root.querySelector('.vaw-canvas-world');
    state.observer = new ResizeObserver(() => schedule(state));
    state.scroll = () => updateGrid(state);
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
        if (event.target.closest('[data-fullscreen-toggle]')) { toggleFullscreen(state); return; }
        const path = event.target.closest('[data-wire]');
        if (path) invoke(state, 'SelectWire', path.dataset.wire);
    };
    state.key = event => {
        const menu = root.querySelector('.vaw-context-menu:popover-open');
        if (menu?.contains(event.target) && ['ArrowDown','ArrowUp'].includes(event.key)) {
            const options = [...menu.querySelectorAll('button:not(:disabled)')];
            if (options.length) {
                const current = options.indexOf(document.activeElement);
                options[(current + (event.key === 'ArrowDown' ? 1 : -1) + options.length) % options.length].focus();
                event.preventDefault();
            }
            return;
        }
        if (menu && event.key === 'Escape') { event.preventDefault(); menu.hidePopover(); return; }
        const typing = event.target.closest('input,textarea,select,[contenteditable="true"]');
        if (!typing && root.dataset.contextMenu === 'true' && (event.key === 'ContextMenu' || event.key === 'F10' && event.shiftKey || event.code === 'Space' && event.target === state.viewport)) {
            event.preventDefault();
            const rect=state.viewport.getBoundingClientRect();
            const x=rect.left+rect.width/2,y=rect.top+rect.height/2,point=local(state,{clientX:x,clientY:y});
            state.contextTrigger=event.target;
            invoke(state,'OpenContextMenu',null,null,0,clamp(point.x),clamp(point.y),Math.max(8,Math.min(x,innerWidth-320)),Math.max(8,Math.min(y,innerHeight-440)));
            return;
        }
        if (!typing && event.key === 'Home' && event.target === state.viewport) { event.preventDefault(); root.querySelector('[data-graph-fit]')?.click(); return; }
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase()==='s') {
            const save=root.querySelector('[data-graph-save]:not(:disabled)');
            if (save) { event.preventDefault(); if (typing) event.target.blur(); save.click(); }
            return;
        }
        if (!typing && (event.ctrlKey || event.metaKey) && ['z','y'].includes(event.key.toLowerCase())) {
            const redo = event.key.toLowerCase() === 'y' || event.shiftKey;
            const action = root.querySelector(redo ? '[data-graph-redo]:not(:disabled)' : '[data-graph-undo]:not(:disabled)');
            if (action) { event.preventDefault(); menu?.hidePopover(); action.click(); }
            return;
        }
        if (!typing && event.key === 'Delete' && root.dataset.contextMenu === 'true') {
            event.preventDefault(); invoke(state,'DeleteSelection'); return;
        }
        if (document.fullscreenElement === root && event.key === 'Escape') {
            event.preventDefault();
            document.exitFullscreen().catch(() => {});
            return;
        }
        if (document.fullscreenElement === root && event.key === 'Tab') {
            const controls = [...root.querySelectorAll('button, input, select, textarea, summary, a[href], [tabindex]')]
                .filter(element => element.tabIndex >= 0 && !element.matches(':disabled') && element.getClientRects().length);
            const first = controls[0], last = controls.at(-1);
            if (event.shiftKey && (document.activeElement === first || document.activeElement === root)) { event.preventDefault(); last?.focus(); }
            if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
        }
        if (event.target.closest('[data-drag-node], [data-drag-section], [data-drag-wire]') && event.key.startsWith('Arrow'))
            event.preventDefault();
    };
    state.context = event => {
        if (root.dataset.contextMenu !== 'true' || !state.viewport.contains(event.target)) return;
        event.preventDefault();
        const node = event.target.closest('li[data-node-id]'), wire = event.target.closest('path[data-wire]');
        const point = local(state,event);
        state.contextTrigger = node?.querySelector('[data-drag-node]') || state.viewport;
        invoke(state,'OpenContextMenu',node?.dataset.nodeId || null,wire?.dataset.wire || null,Number(wire?.dataset.segment || 0),
            clamp(point.x),clamp(point.y),Math.max(8,Math.min(event.clientX,innerWidth-320)),Math.max(8,Math.min(event.clientY,innerHeight-440)));
    };
    state.dragStart = event => {
        const entry = event.target.closest('[data-node-key]');
        if (!entry || entry.disabled || root.dataset.contextMenu !== 'true') return;
        event.dataTransfer.setData('application/x-vaw-node',entry.dataset.nodeKey);
        event.dataTransfer.effectAllowed = 'copy';
    };
    state.dragOver = event => {
        if (root.dataset.contextMenu !== 'true' || !state.viewport.contains(event.target) || ![...event.dataTransfer.types].includes('application/x-vaw-node')) return;
        event.preventDefault(); event.dataTransfer.dropEffect='copy';
        state.viewport.dataset.nodeDrop='true';
    };
    state.dragLeave = event => { if (!state.viewport.contains(event.relatedTarget)) delete state.viewport.dataset.nodeDrop; };
    state.drop = event => {
        delete state.viewport.dataset.nodeDrop;
        const key=event.dataTransfer.getData('application/x-vaw-node');
        if (!key || root.dataset.contextMenu !== 'true' || !state.viewport.contains(event.target)) return;
        event.preventDefault();
        const point=local(state,event), node=event.target.closest('li[data-node-id]'), wire=event.target.closest('path[data-wire]');
        invoke(state,'CreateDroppedNode',key,node?.dataset.nodeId || null,wire?.dataset.wire || null,clamp(point.x),clamp(point.y));
    };
    root.addEventListener('contextmenu',state.context);
    root.addEventListener('dragstart',state.dragStart);
    root.addEventListener('dragover',state.dragOver);
    root.addEventListener('dragleave',state.dragLeave);
    root.addEventListener('drop',state.drop);
    root.addEventListener('pointerdown', state.down);
    root.addEventListener('pointermove', state.move);
    root.addEventListener('pointerup', state.up);
    root.addEventListener('pointercancel', state.cancel);
    root.addEventListener('lostpointercapture', state.cancel);
    root.addEventListener('dblclick', state.double);
    root.addEventListener('click', state.click);
    root.addEventListener('keydown', state.key);
    state.viewport.addEventListener('scroll', state.scroll, { passive: true });
    state.fullscreenChanged = () => {
        const active = document.fullscreenElement === root;
        root.dataset.fullscreen = String(active);
        const button = root.querySelector('[data-fullscreen-toggle]');
        if (button) { button.textContent = active ? 'Exit fullscreen graph' : 'Fullscreen graph'; button.setAttribute('aria-pressed', String(active)); }
        if (!active && state.wasFullscreen && button?.isConnected) button.focus({ preventScroll: true });
        state.wasFullscreen = active;
        schedule(state);
    };
    document.addEventListener('fullscreenchange', state.fullscreenChanged);
    state.handle = { refresh: () => sync(root), fit: () => fit(state), dispose: () => detach(root) };
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
    state.observer.observe(state.viewport);
    state.fullscreenChanged();
    const menu = root.querySelector('.vaw-context-menu');
    if (menu !== state.menu) {
        if (state.menu && state.menuToggle) state.menu.removeEventListener('toggle',state.menuToggle);
        state.menu = menu;
        if (menu) {
            const id=menu.dataset.contextId;
            state.menuToggle = event => {
                if (event.newState==='closed') {
                    invoke(state,'CloseContextMenu',id);
                    if (state.contextTrigger?.isConnected) state.contextTrigger.focus({preventScroll:true});
                }
            };
            menu.addEventListener('toggle',state.menuToggle);
            menu.showPopover();
            menu.querySelector('input')?.focus({preventScroll:true});
        }
    }
    draw(state);
    if (state.pendingFit) {
        state.viewport.scrollTo(
            state.pendingFit.x * scale(state) - state.viewport.clientWidth / 2,
            state.pendingFit.y * scale(state) - state.viewport.clientHeight / 2);
        state.pendingFit = null;
    }
}

async function toggleFullscreen(state) {
    const error = state.root.querySelector('[data-fullscreen-error]');
    try {
        if (document.fullscreenElement === state.root) await document.exitFullscreen();
        else await state.root.requestFullscreen();
        error.hidden = true;
    } catch { error.hidden = false; }
}

function fit(state) {
    const nodes = [...state.nodes.values()];
    if (!nodes.length) return scale(state);
    const left = Math.max(0, Math.min(...nodes.map(node => position(node).x)) - 24);
    const top = Math.max(0, Math.min(...nodes.map(node => position(node).y)) - 24);
    const right = Math.max(...nodes.map(node => position(node).x + node.offsetWidth)) + 24;
    const bottom = Math.max(...nodes.map(node => position(node).y + node.offsetHeight)) + 24;
    const zoom = Math.max(.25, Math.min(1.5,
        (state.viewport.clientWidth - 64) / (right - left),
        (state.viewport.clientHeight - 96) / (bottom - top)));
    const centerX = (left + right) / 2, centerY = (top + bottom) / 2;
    state.camera = {
        x: Math.max(0, state.viewport.clientWidth / zoom / 2 - centerX),
        y: Math.max(0, state.viewport.clientHeight / zoom / 2 - centerY)
    };
    state.pendingFit = { x: state.camera.x + centerX, y: state.camera.y + centerY };
    return zoom;
}

function schedule(state) {
    if (!state.active || state.frame) return;
    state.frame = requestAnimationFrame(() => { state.frame = 0; draw(state); });
}

function scale(state) { return new DOMMatrixReadOnly(getComputedStyle(state.world).transform).a || 1; }
function updateGrid(state) {
    const zoom = scale(state), spacing = 16 * zoom;
    const modulo = value => ((value % spacing) + spacing) % spacing;
    state.viewport.style.backgroundSize = `${spacing}px ${spacing}px`;
    state.viewport.style.backgroundPosition = `${modulo(state.camera.x * zoom - state.viewport.scrollLeft)}px ${modulo(state.camera.y * zoom - state.viewport.scrollTop)}px`;
}
function local(state, event) {
    const rect = state.world.getBoundingClientRect(), zoom = scale(state);
    return { x: (event.clientX - rect.left) / zoom, y: (event.clientY - rect.top) / zoom };
}

function draw(state) {
    if (!state.active || !state.world.isConnected || state.viewport.clientWidth === 0) return;
    const zoom = scale(state);
    state.world.style.transform = `scale(${zoom}) translate(${state.camera.x}px, ${state.camera.y}px)`;
    updateGrid(state);
    const world = state.world.getBoundingClientRect();
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
    extent.style.width = `${(width + state.camera.x) * zoom}px`;
    extent.style.height = `${(height + state.camera.y) * zoom}px`;
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
    const pin = event.target.closest('.vaw-output-ports [data-socket]');
    if (pin && state.root.dataset.contextMenu === 'true' && event.button === 0) {
        const box=pin.getBoundingClientRect(),start=local(state,{clientX:box.left+box.width/2,clientY:box.top+box.height/2});
        state.drag={kind:'pin',pointer:event.pointerId,screenX:event.clientX,screenY:event.clientY,target:state.viewport,
            id:pin.closest('[data-node-id]').dataset.nodeId,port:pin.dataset.socket,start,moved:false,items:[]};
        state.root.querySelector('[data-pin-hint]').hidden=true;
        state.viewport.setPointerCapture(event.pointerId); event.preventDefault(); return;
    }
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
    if (drag.kind === 'pin') {
        const preview=state.root.querySelector('[data-pin-preview]');
        preview.removeAttribute('hidden');
        preview.setAttribute('d',curve({position:drag.start,angle:0},{position:local(state,event),angle:0}));
    } else if (drag.kind === 'pan') {
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
    if (drag.kind === 'pin') {
        state.root.querySelector('[data-pin-preview]').setAttribute('hidden','');
        if (!cancelled && drag.moved) {
            const target=document.elementFromPoint(event.clientX,event.clientY);
            if (target?.closest('li[data-node-id]')) state.root.querySelector('[data-pin-hint]').hidden=false;
            else {
                const edge=state.paths.find(path=>path.dataset.source===drag.id&&path.dataset.sourcePort===drag.port);
                const point=local(state,event);
                invoke(state,'OpenContextMenu',null,edge?.dataset.wire || null,0,clamp(point.x),clamp(point.y),
                    Math.max(8,Math.min(event.clientX,innerWidth-320)),Math.max(8,Math.min(event.clientY,innerHeight-440)));
            }
        }
        schedule(state); return;
    }
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
    if (state.menu && state.menuToggle) state.menu.removeEventListener('toggle',state.menuToggle);
    for (const [name,handler] of [['contextmenu',state.context],['dragstart',state.dragStart],['dragover',state.dragOver],['dragleave',state.dragLeave],['drop',state.drop]])
        root.removeEventListener(name,handler);
    document.removeEventListener('fullscreenchange', state.fullscreenChanged);
    if (document.fullscreenElement === root) document.exitFullscreen().catch(() => {});
    state.observer.disconnect(); cancelAnimationFrame(state.frame);
    state.viewport.removeEventListener('scroll', state.scroll);
    for (const [name, handler] of [['pointerdown', state.down], ['pointermove', state.move], ['pointerup', state.up],
        ['pointercancel', state.cancel], ['lostpointercapture', state.cancel], ['dblclick', state.double], ['click', state.click], ['keydown', state.key]])
        root.removeEventListener(name, handler);
    canvases.delete(root);
}
