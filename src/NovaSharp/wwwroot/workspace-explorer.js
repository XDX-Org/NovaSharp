function rows() {
    return Array.from(document.querySelectorAll('.workspace-tree .tree-row'));
}

function focus(id, movement) {
    const all = rows();
    const current = all.find(row => row.closest('.tree-item')?.dataset.nodeId === id);
    if (!current) return;

    const index = all.indexOf(current);
    let target;
    if (movement === 'next') target = all[index + 1];
    else if (movement === 'previous') target = all[index - 1];
    else if (movement === 'first') target = all[0];
    else if (movement === 'last') target = all[all.length - 1];
    else if (movement === 'child') target = current.closest('.tree-item')?.querySelector(':scope > [role="group"] > .tree-item > .tree-row');
    else if (movement === 'parent') target = current.closest('[role="group"]')?.closest('.tree-item')?.querySelector(':scope > .tree-row');
    target?.focus();
}

let rememberedElement;
let detachCurrentResizer;

function rememberFocus() {
    const explorer = document.querySelector('.explorer');
    rememberedElement = explorer?.contains(document.activeElement) ? document.activeElement : undefined;
}

function restoreFocus() {
    if (rememberedElement?.isConnected) rememberedElement.focus();
}

const workspaceItemType = 'application/x-novasharp-workspace-item';
const workspaceFileType = 'application/x-novasharp-workspace-file';
const dragSurfaces = new WeakMap();
let draggedItemPath;
let draggedFilePath;

function draggedItem() {
    return draggedItemPath;
}

function draggedFile() {
    return draggedFilePath;
}

function detachDragSurface(element) {
    const state = dragSurfaces.get(element);
    if (!state) return;
    element.removeEventListener('dragstart', state.onDragStart);
    element.removeEventListener('dragover', state.onDragOver);
    element.removeEventListener('dragend', state.onDragEnd);
    element.removeEventListener('drop', state.onDragEnd);
    draggedItemPath = undefined;
    draggedFilePath = undefined;
    element.classList.remove('workspace-dragging');
    dragSurfaces.delete(element);
}

function attachDragSurface(element) {
    detachDragSurface(element);
    if (!(element instanceof HTMLElement)) return;
    let dragging = false;
    const onDragStart = event => {
        const row = event.target.closest?.('.tree-row[draggable="true"][data-workspace-path]');
        if (!row || !element.contains(row)) return;
        dragging = true;
        element.classList.add('workspace-dragging');
        const path = row.dataset.workspacePath;
        draggedItemPath = path;
        draggedFilePath = row.dataset.workspaceKind === 'file' ? path : undefined;
        if (!event.dataTransfer) return;
        event.dataTransfer.effectAllowed = 'copyMove';
        event.dataTransfer.setData(workspaceItemType, path);
        if (row.dataset.workspaceKind === 'file') event.dataTransfer.setData(workspaceFileType, path);
    };
    const onDragOver = event => {
        if (!dragging) return;
        const target = event.target.closest?.('.tree-row[data-workspace-drop-target="true"]');
        if (!target || !element.contains(target)) return;
        event.preventDefault();
        if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
    };
    const onDragEnd = () => {
        dragging = false;
        draggedItemPath = undefined;
        draggedFilePath = undefined;
        element.classList.remove('workspace-dragging');
    };
    element.addEventListener('dragstart', onDragStart);
    element.addEventListener('dragover', onDragOver);
    element.addEventListener('dragend', onDragEnd);
    element.addEventListener('drop', onDragEnd);
    dragSurfaces.set(element, { onDragStart, onDragOver, onDragEnd });
}

function attachResizer(handle, bridge) {
    detachResizer();
    const explorer = handle.closest('.explorer');
    let frame;
    let latestWidth;
    let startX;
    let startWidth;

    const apply = () => {
        frame = undefined;
        explorer.style.width = `${latestWidth}px`;
        handle.setAttribute('aria-valuenow', String(latestWidth));
    };
    const move = event => {
        latestWidth = Math.max(0, Math.round(startWidth + startX - event.clientX));
        if (!frame) frame = requestAnimationFrame(apply);
    };
    const finish = async event => {
        move(event);
        if (frame) { cancelAnimationFrame(frame); apply(); }
        handle.releasePointerCapture(event.pointerId);
        handle.removeEventListener('pointermove', move);
        handle.removeEventListener('pointerup', finish);
        handle.removeEventListener('pointercancel', finish);
        await bridge.invokeMethodAsync('CommitSidebarWidthAsync', latestWidth);
    };
    const start = event => {
        startX = event.clientX;
        startWidth = explorer.getBoundingClientRect().width;
        latestWidth = startWidth;
        handle.setPointerCapture(event.pointerId);
        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', finish);
        handle.addEventListener('pointercancel', finish);
    };
    handle.addEventListener('pointerdown', start);
    detachCurrentResizer = () => {
        if (frame) cancelAnimationFrame(frame);
        handle.removeEventListener('pointerdown', start);
        handle.removeEventListener('pointermove', move);
        handle.removeEventListener('pointerup', finish);
        handle.removeEventListener('pointercancel', finish);
    };
}

function detachResizer() {
    detachCurrentResizer?.();
    detachCurrentResizer = undefined;
}

globalThis.NovaWorkspace = Object.freeze({
    focus,
    rememberFocus,
    restoreFocus,
    draggedItem,
    draggedFile,
    attachDragSurface,
    detachDragSurface,
    attachResizer,
    detachResizer,
});
