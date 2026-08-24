const splitters = new WeakMap();
const dragSurfaces = new WeakMap();

function detachDragSurface(element) {
    const state = dragSurfaces.get(element);
    if (!state) return;
    element.removeEventListener('dragstart', state.onDragStart);
    element.removeEventListener('dragend', state.onDragEnd);
    element.removeEventListener('drop', state.onDragEnd);
    dragSurfaces.delete(element);
}

function attachDragSurface(element) {
    detachDragSurface(element);
    if (!(element instanceof HTMLElement)) return;
    const onDragStart = event => {
        const tab = event.target.closest?.('.document-tab[draggable="true"]');
        if (!tab || !element.contains(tab)) return;
        element.classList.add('dragging');
        if (!event.dataTransfer) return;
        event.dataTransfer.effectAllowed = 'copyMove';
        event.dataTransfer.setData('text/plain', tab.dataset.viewId ?? 'editor-view');
    };
    const onDragEnd = () => element.classList.remove('dragging');
    element.addEventListener('dragstart', onDragStart);
    element.addEventListener('dragend', onDragEnd);
    element.addEventListener('drop', onDragEnd);
    dragSurfaces.set(element, { onDragStart, onDragEnd });
}

function detachSplitter(element) {
    const state = splitters.get(element);
    if (!state) return;
    element.removeEventListener('pointerdown', state.onPointerDown);
    state.cancel?.();
    splitters.delete(element);
}

function attachSplitter(element, bridge, splitId, orientation, initialRatio) {
    detachSplitter(element);
    if (!(element instanceof HTMLElement)) return;
    const state = { cancel: null };
    state.onPointerDown = event => {
        if (event.button !== 0) return;
        const split = element.parentElement;
        if (!split) return;
        event.preventDefault();
        element.setPointerCapture(event.pointerId);
        let ratio = Number(initialRatio) || 0.5;
        let frame = 0;
        const update = pointerEvent => {
            const bounds = split.getBoundingClientRect();
            const raw = orientation === 'horizontal'
                ? (pointerEvent.clientX - bounds.left) / bounds.width
                : (pointerEvent.clientY - bounds.top) / bounds.height;
            ratio = Math.max(0.1, Math.min(0.9, raw));
            if (frame) return;
            frame = requestAnimationFrame(() => {
                frame = 0;
                split.style.setProperty('--split-first', `${ratio * 100}%`);
            });
        };
        const finish = async pointerEvent => {
            update(pointerEvent);
            cleanup();
            split.style.setProperty('--split-first', `${ratio * 100}%`);
            await bridge.invokeMethodAsync('CommitSplitRatioAsync', splitId, ratio);
        };
        const cleanup = () => {
            if (frame) cancelAnimationFrame(frame);
            frame = 0;
            element.removeEventListener('pointermove', update);
            element.removeEventListener('pointerup', finish);
            element.removeEventListener('pointercancel', cleanup);
            state.cancel = null;
        };
        state.cancel = cleanup;
        element.addEventListener('pointermove', update);
        element.addEventListener('pointerup', finish);
        element.addEventListener('pointercancel', cleanup);
    };
    element.addEventListener('pointerdown', state.onPointerDown);
    splitters.set(element, state);
}

globalThis.NovaEditorGroups = { attachDragSurface, detachDragSurface, attachSplitter, detachSplitter };
