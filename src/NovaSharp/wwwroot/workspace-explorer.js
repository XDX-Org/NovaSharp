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

globalThis.NovaWorkspace = Object.freeze({ focus, rememberFocus, restoreFocus, attachResizer, detachResizer });
