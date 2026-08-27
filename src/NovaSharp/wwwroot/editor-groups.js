const splitters = new WeakMap();
const dragSurfaces = new WeakMap();
const droppedWorkspaceFiles = new WeakMap();
const workspaceFileType = 'application/x-novasharp-workspace-file';

function hasWorkspaceFile(dataTransfer) {
    return Array.from(dataTransfer?.types ?? []).includes(workspaceFileType)
        || Boolean(globalThis.NovaWorkspace?.draggedFile());
}

function detachDragSurface(element) {
    const state = dragSurfaces.get(element);
    if (!state) return;
    element.removeEventListener('mousedown', state.onMouseDown);
    element.removeEventListener('dragstart', state.onDragStart);
    element.removeEventListener('dragenter', state.onDragEnter);
    element.removeEventListener('dragover', state.onDragOver);
    element.removeEventListener('dragleave', state.onDragLeave);
    element.removeEventListener('dragend', state.onDragEnd);
    element.removeEventListener('drop', state.onDrop);
    droppedWorkspaceFiles.delete(element);
    dragSurfaces.delete(element);
}

function takeDroppedWorkspaceFile(element) {
    const path = droppedWorkspaceFiles.get(element);
    droppedWorkspaceFiles.delete(element);
    return path ?? null;
}

function attachDragSurface(element) {
    detachDragSurface(element);
    if (!(element instanceof HTMLElement)) return;
    let tabDragging = false;
    const onMouseDown = event => {
        if (event.button !== 1) return;
        const tab = event.target.closest?.('.document-tab');
        if (tab && element.contains(tab)) event.preventDefault();
    };
    const onDragStart = event => {
        const tab = event.target.closest?.('.document-tab[draggable="true"]');
        if (!tab || !element.contains(tab)) return;
        tabDragging = true;
        element.classList.add('dragging');
        if (!event.dataTransfer) return;
        event.dataTransfer.effectAllowed = 'copyMove';
        event.dataTransfer.setData('text/plain', tab.dataset.viewId ?? 'editor-view');
    };
    const onDragEnter = event => {
        if (hasWorkspaceFile(event.dataTransfer)) element.classList.add('dragging');
    };
    const onDragOver = event => {
        const workspaceFile = hasWorkspaceFile(event.dataTransfer);
        if (!tabDragging && !workspaceFile) return;
        if (workspaceFile) element.classList.add('dragging');
        const target = event.target.closest?.('.group-drop-zone, .tabs-strip, .document-tab[draggable="true"]');
        if (!target || !element.contains(target)) return;
        event.preventDefault();
        if (event.dataTransfer) {
            event.dataTransfer.dropEffect = workspaceFile || event.ctrlKey || event.altKey ? 'copy' : 'move';
        }
    };
    const onDragLeave = event => {
        const related = event.relatedTarget;
        if (!tabDragging && (!(related instanceof Node) || !element.contains(related))) {
            element.classList.remove('dragging');
        }
    };
    const onDragEnd = () => {
        tabDragging = false;
        element.classList.remove('dragging');
    };
    const onDrop = event => {
        const path = event.dataTransfer?.getData(workspaceFileType)
            || globalThis.NovaWorkspace?.draggedFile();
        if (path) droppedWorkspaceFiles.set(element, path);
        onDragEnd();
    };
    element.addEventListener('mousedown', onMouseDown);
    element.addEventListener('dragstart', onDragStart);
    element.addEventListener('dragenter', onDragEnter);
    element.addEventListener('dragover', onDragOver);
    element.addEventListener('dragleave', onDragLeave);
    element.addEventListener('dragend', onDragEnd);
    element.addEventListener('drop', onDrop);
    dragSurfaces.set(element, { onMouseDown, onDragStart, onDragEnter, onDragOver, onDragLeave, onDragEnd, onDrop });
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

globalThis.NovaEditorGroups = {
    attachDragSurface,
    detachDragSurface,
    takeDroppedWorkspaceFile,
    attachSplitter,
    detachSplitter,
};
