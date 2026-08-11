window.novaSharp = window.novaSharp || {};

window.novaSharp.initTerminal = function (host, dotNet, terminalId) {
    if (!host || host.dataset.terminalReady) return;
    host.dataset.terminalReady = "true";
    let pendingSize;
    let appliedSize;
    let resizing = false;
    const applyLatestSize = async () => {
        if (resizing) return;
        resizing = true;
        try {
            while (pendingSize) {
                const size = pendingSize;
                pendingSize = undefined;
                await dotNet?.invokeMethodAsync("ResizeTerminal", size.columns, size.rows);
                appliedSize = size;
            }
        } catch { }
        finally { resizing = false; }
    };
    const resize = () => {
        const terminal = host.querySelector(".xterm");
        if (!terminal) return;
        const style = getComputedStyle(terminal);
        const xterm = globalThis.XtermBlazor?._terminals?.get(terminalId)?.terminal;
        const cell = xterm?._core?._renderService?.dimensions?.css?.cell;
        const cellWidth = Math.max(1, cell?.width || parseFloat(style.fontSize) * .6);
        const cellHeight = Math.max(1, cell?.height || parseFloat(style.lineHeight) || 20);
        const viewport = terminal.querySelector(".xterm-viewport");
        const scrollbarWidth = viewport ? Math.max(0, viewport.offsetWidth - viewport.clientWidth) : 0;
        const columns = Math.max(2, Math.floor((host.clientWidth - scrollbarWidth) / cellWidth));
        const rows = Math.max(1, Math.floor(host.clientHeight / cellHeight));
        if (appliedSize?.columns === columns && appliedSize?.rows === rows) return;
        pendingSize = { columns, rows };
        void applyLatestSize();
    };
    host.addEventListener("keydown", event => event.stopPropagation());
    new ResizeObserver(resize).observe(host);
    resize();
    requestAnimationFrame(resize);
};

window.novaSharp.searchTerminal = function (terminalId, query) {
    const terminal = globalThis.XtermBlazor?._terminals?.get(terminalId)?.terminal;
    if (!terminal) return;
    terminal.clearSelection();
    if (!query) return;
    const buffer = terminal.buffer.active;
    const needle = query.toLocaleLowerCase();
    for (let row = buffer.length - 1; row >= 0; row--) {
        const text = buffer.getLine(row)?.translateToString(true) || "";
        const column = text.toLocaleLowerCase().indexOf(needle);
        if (column < 0) continue;
        terminal.select(column, row, query.length);
        terminal.scrollToLine(row);
        return;
    }
};

window.novaSharp.positionContextMenu = function (menu, x, y) {
    if (!menu) return;
    const margin = 8;
    const bounds = menu.getBoundingClientRect();
    const left = Math.max(margin, Math.min(x - bounds.width, window.innerWidth - bounds.width - margin));
    const top = Math.max(margin, Math.min(y, window.innerHeight - bounds.height - margin));
    menu.style.left = left + "px";
    menu.style.top = top + "px";
};

window.novaSharp.initAppContextMenu = function () {
    if (window.novaSharp.appContextMenuReady) return;
    window.novaSharp.appContextMenuReady = true;
    let menu;
    let target;

    const close = () => {
        menu?.remove();
        menu = null;
        target = null;
    };
    const selectedText = element => {
        if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement)
            return element.value.slice(element.selectionStart ?? 0, element.selectionEnd ?? 0);
        return window.getSelection()?.toString() ?? "";
    };
    const copyText = async text => {
        if (!text) return;
        try { await navigator.clipboard.writeText(text); }
        catch { document.execCommand("copy"); }
    };
    const replaceSelection = text => {
        if (!(target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) || target.readOnly || target.disabled) return;
        target.setRangeText(text, target.selectionStart ?? 0, target.selectionEnd ?? 0, "end");
        target.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText", data: text }));
        target.focus();
    };
    const act = async action => {
        const selection = selectedText(target);
        switch (action) {
            case "undo": target.focus(); document.execCommand("undo"); break;
            case "cut": await copyText(selection); replaceSelection(""); break;
            case "copy": await copyText(selection); break;
            case "paste":
                try { replaceSelection(await navigator.clipboard.readText()); } catch { }
                break;
            case "delete": replaceSelection(""); break;
            case "selectAll":
                target.focus();
                if (typeof target.select === "function") target.select();
                break;
        }
        close();
    };
    const addItem = (label, action, shortcut, disabled = false) => {
        const button = document.createElement("button");
        button.type = "button";
        button.disabled = disabled;
        button.innerHTML = `<span>${label}</span>${shortcut ? `<kbd>${shortcut}</kbd>` : ""}`;
        button.addEventListener("click", () => act(action));
        menu.appendChild(button);
    };
    const addBrowserOptionsHint = () => {
        menu.appendChild(document.createElement("hr"));
        const hint = document.createElement("div");
        hint.className = "context-menu-hint";
        hint.innerHTML = "<span>Browser options</span><kbd>Shift+Right-click</kbd>";
        menu.appendChild(hint);
    };

    document.addEventListener("contextmenu", event => {
        if (event.shiftKey) { close(); return; }
        if (event.target.closest(".explorer")) { event.preventDefault(); return; }
        if (event.target.closest(".document-tab")) return;
        if (event.target.closest(".menu-popup,.explorer-context-menu")) return;
        const editable = event.target.closest("textarea,input:not([type=button]):not([type=submit])");
        const selectionTarget = editable || event.target;
        const selection = selectedText(selectionTarget);
        event.preventDefault();
        close();
        target = selectionTarget;
        menu = document.createElement("div");
        menu.className = "app-context-menu";
        menu.setAttribute("role", "menu");
        document.body.appendChild(menu);

        if (editable) {
            const readOnly = editable.readOnly || editable.disabled;
            addItem("Undo", "undo", "Ctrl+Z", readOnly);
            menu.appendChild(document.createElement("hr"));
            addItem("Cut", "cut", "Ctrl+X", readOnly || !selection);
            addItem("Copy", "copy", "Ctrl+C", !selection);
            addItem("Paste", "paste", "Ctrl+V", readOnly);
            addItem("Delete", "delete", "Del", readOnly || !selection);
            menu.appendChild(document.createElement("hr"));
            addItem("Select all", "selectAll", "Ctrl+A");
        } else {
            if (selection) addItem("Copy", "copy", "Ctrl+C");
        }
        addBrowserOptionsHint();
        window.novaSharp.positionContextMenu(menu, event.clientX + menu.offsetWidth, event.clientY);
    });
    document.addEventListener("pointerdown", event => {
        if (menu && !event.target.closest(".app-context-menu")) close();
    }, true);
    document.addEventListener("keydown", event => { if (event.key === "Escape") close(); });
};

window.novaSharp.initPointerResize = function (handle, axis, bodyClass, begin, resize, complete) {
    if (!handle || handle.dataset.resizeReady) return;
    handle.dataset.resizeReady = 'true';
    handle.addEventListener("pointerdown", event => {
        if (event.button !== 0) return;
        event.preventDefault();
        try { handle.setPointerCapture(event.pointerId); } catch { }
        document.body.classList.add(bodyClass);
        const coordinate = value => axis === 'x' ? value.clientX : value.clientY;
        const start = coordinate(event);
        const state = begin();
        let latest = start;
        let animationFrame = 0;
        const apply = () => {
            animationFrame = 0;
            resize(latest - start, state);
        };
        const move = moveEvent => {
            latest = coordinate(moveEvent);
            if (!animationFrame) animationFrame = requestAnimationFrame(apply);
        };
        const stop = () => {
            if (animationFrame) {
                cancelAnimationFrame(animationFrame);
                apply();
            }
            document.body.classList.remove(bodyClass);
            complete(state);
            handle.removeEventListener("pointermove", move);
            handle.removeEventListener("pointerup", stop);
            handle.removeEventListener("pointercancel", stop);
        };

        handle.addEventListener("pointermove", move);
        handle.addEventListener("pointerup", stop);
        handle.addEventListener("pointercancel", stop);
    });
};

window.novaSharp.initExplorerResize = function (explorer, dotNet) {
    const handle = explorer?.querySelector('.explorer-resizer');
    window.novaSharp.initPointerResize(handle, 'x', 'resizing-explorer',
        () => ({ width: explorer.getBoundingClientRect().width }),
        (delta, state) => {
            const maximum = Math.min(800, window.innerWidth * 0.65);
            explorer.style.width = Math.max(180, Math.min(maximum, state.width - delta)) + 'px';
        },
        () => dotNet?.invokeMethodAsync('ExplorerResized', explorer.getBoundingClientRect().width));
};

window.novaSharp.initEditorSplitter = function (handle, dotNet, orientation) {
    const horizontal = orientation === 'horizontal';
    handle.dataset.axis = horizontal ? 'x' : 'y';
    window.novaSharp.initPointerResize(handle, horizontal ? 'x' : 'y', horizontal ? 'resizing-editor-x' : 'resizing-editor-y',
        () => {
            const parent = handle.parentElement;
            const bounds = parent.getBoundingClientRect();
            return {
                parent,
                extent: horizontal ? bounds.width : bounds.height,
                ratio: Number(handle.getAttribute('aria-valuenow')) / 100,
                latestRatio: Number(handle.getAttribute('aria-valuenow')) / 100
            };
        },
        (delta, state) => {
            const ratio = Math.max(0.1, Math.min(0.9, state.ratio + delta / Math.max(1, state.extent)));
            state.latestRatio = ratio;
            const first = ratio * 100;
            const second = (1 - ratio) * 100;
            const template = `minmax(160px,${first}fr) 5px minmax(160px,${second}fr)`;
            if (horizontal) state.parent.style.gridTemplateColumns = template;
            else state.parent.style.gridTemplateRows = template;
            handle.setAttribute('aria-valuenow', Math.round(first));
        },
        state => dotNet?.invokeMethodAsync('SplitterResized', state.latestRatio || state.ratio));
};

window.novaSharp.initEditorDragCleanup = function (workbench, dotNet) {
    if (!workbench || workbench.dataset.dragCleanupReady) return;
    workbench.dataset.dragCleanupReady = 'true';
    window.novaSharp.cancelEditorDrag = () => {
        window.novaSharp.tabDragActive = false;
        if (workbench.querySelector('.group-drop-zones')) dotNet?.invokeMethodAsync('CancelEditorDrag');
    };
    document.addEventListener('dragend', () => setTimeout(() => {
        if (workbench.querySelector('.group-drop-zones')) dotNet?.invokeMethodAsync('CancelEditorDrag');
    }, 0));
    document.addEventListener('drop', event => {
        if (event.target.closest?.('.drop-zone,.document-tab')) return;
        window.novaSharp.cancelEditorDrag();
    }, true);
    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || !workbench.querySelector('.group-drop-zones')) return;
        event.preventDefault();
        dotNet?.invokeMethodAsync('CancelEditorDrag');
    });
};

window.novaSharp.initPointerTabDrag = function (workbench, dotNet) {
    if (!workbench || workbench.dataset.pointerTabDragReady) return;
    workbench.dataset.pointerTabDragReady = 'true';
    workbench.addEventListener('pointerdown', event => {
        const tab = event.target.closest?.('.document-tab');
        if (!tab || event.button !== 0 || event.target.closest('button')) return;
        const startX = event.clientX, startY = event.clientY, pointerId = event.pointerId;
        let dragging = false, cancelled = false, target, ghost;
        tab.setPointerCapture(pointerId);
        const clearTarget = () => { target?.classList.remove('pointer-target'); target = null; };
        const clearVisuals = () => {
            clearTarget();
            ghost?.remove();
            ghost = null;
            document.body.classList.remove('pointer-tab-dragging');
        };
        const move = async moveEvent => {
            moveEvent.preventDefault();
            if (!dragging && Math.hypot(moveEvent.clientX - startX, moveEvent.clientY - startY) < 5) return;
            if (!dragging) {
                dragging = true;
                tab.addEventListener('click', click => {
                    click.preventDefault();
                    click.stopImmediatePropagation();
                }, { once: true, capture: true });
                document.body.classList.add('pointer-tab-dragging');
                const bounds = tab.getBoundingClientRect();
                ghost = tab.cloneNode(true);
                ghost.className = 'tab-drag-ghost';
                ghost.style.width = `${bounds.width}px`;
                document.body.appendChild(ghost);
                await dotNet.invokeMethodAsync('BeginPointerTabDrag', tab.dataset.tabId);
            }
            ghost.style.transform = `translate(${moveEvent.clientX + 12}px,${moveEvent.clientY + 12}px)`;
            clearTarget();
            const hit = document.elementFromPoint(moveEvent.clientX, moveEvent.clientY);
            target = hit?.closest?.('.drop-zone,.document-tab');
            target?.classList.add('pointer-target');
        };
        const finish = async finishEvent => {
            tab.removeEventListener('pointermove', move);
            tab.removeEventListener('pointerup', finish);
            tab.removeEventListener('pointercancel', cancel);
            window.removeEventListener('keydown', escape, true);
            if (!dragging || cancelled) return;
            const destination = target;
            clearVisuals();
            if (!destination) { await dotNet.invokeMethodAsync('CancelEditorDrag'); return; }
            const group = destination.closest('.editor-group');
            const direction = destination.classList.contains('drop-zone') ? destination.dataset.direction : null;
            const index = destination.classList.contains('document-tab') ? Number(destination.dataset.tabIndex) : null;
            await dotNet.invokeMethodAsync('DropPointerTab', group.dataset.groupId, index,
                direction === 'center' ? null : direction, finishEvent.ctrlKey || finishEvent.metaKey);
        };
        const cancel = async () => {
            cancelled = true;
            clearVisuals();
            tab.removeEventListener('pointermove', move);
            tab.removeEventListener('pointerup', finish);
            window.removeEventListener('keydown', escape, true);
            if (dragging) await dotNet.invokeMethodAsync('CancelEditorDrag');
        };
        const escape = event => { if (event.key === 'Escape') cancel(); };
        tab.addEventListener('pointermove', move);
        tab.addEventListener('pointerup', finish);
        tab.addEventListener('pointercancel', cancel, { once: true });
        window.addEventListener('keydown', escape, true);
    });
};

window.novaSharp.initExplorerFileDrag = function (workbench, dotNet) {
    if (!workbench || workbench.dataset.explorerFileDragReady) return;
    workbench.dataset.explorerFileDragReady = 'true';
    workbench.addEventListener('pointerdown', event => {
        const row = event.target.closest?.('.tree-row[data-file-path]');
        if (!row || event.button !== 0) return;
        const startX = event.clientX, startY = event.clientY;
        let dragging = false, cancelled = false, target, ghost;
        row.setPointerCapture(event.pointerId);
        const clear = () => {
            target?.classList.remove('pointer-target');
            ghost?.remove();
            document.body.classList.remove('pointer-tab-dragging');
            target = ghost = null;
        };
        const move = async moveEvent => {
            moveEvent.preventDefault();
            if (!dragging && Math.hypot(moveEvent.clientX - startX, moveEvent.clientY - startY) < 5) return;
            if (!dragging) {
                dragging = true;
                row.addEventListener('click', click => {
                    click.preventDefault();
                    click.stopImmediatePropagation();
                }, { once: true, capture: true });
                document.body.classList.add('pointer-tab-dragging');
                ghost = document.createElement('div');
                ghost.className = 'tab-drag-ghost explorer-file-drag-ghost';
                ghost.textContent = row.querySelector('.tree-name')?.textContent ?? '';
                document.body.appendChild(ghost);
                await dotNet.invokeMethodAsync('BeginExplorerFileDrag', row.dataset.filePath);
            }
            ghost.style.transform = `translate(${moveEvent.clientX + 12}px,${moveEvent.clientY + 12}px)`;
            target?.classList.remove('pointer-target');
            target = document.elementFromPoint(moveEvent.clientX, moveEvent.clientY)?.closest?.('.drop-zone');
            target?.classList.add('pointer-target');
        };
        const finish = async () => {
            row.removeEventListener('pointermove', move);
            row.removeEventListener('pointerup', finish);
            row.removeEventListener('pointercancel', cancel);
            window.removeEventListener('keydown', escape, true);
            if (!dragging || cancelled) return;
            const destination = target;
            clear();
            if (!destination) { await dotNet.invokeMethodAsync('CancelExplorerFileDrag'); return; }
            const direction = destination.dataset.direction;
            await dotNet.invokeMethodAsync('DropExplorerFile', destination.closest('.editor-group').dataset.groupId,
                direction === 'center' ? null : direction);
        };
        const cancel = async () => {
            cancelled = true;
            clear();
            window.removeEventListener('keydown', escape, true);
            if (dragging) await dotNet.invokeMethodAsync('CancelExplorerFileDrag');
        };
        const escape = event => { if (event.key === 'Escape') cancel(); };
        row.addEventListener('pointermove', move);
        row.addEventListener('pointerup', finish);
        row.addEventListener('pointercancel', cancel, { once: true });
        window.addEventListener('keydown', escape, true);
    });
};

window.novaSharp.focusEditorGroup = function (workbench, dotNet, direction) {
    const current = workbench?.querySelector('.editor-group.focused');
    if (!current) return;
    const source = current.getBoundingClientRect();
    const sourceX = source.left + source.width / 2;
    const sourceY = source.top + source.height / 2;
    const candidates = [...workbench.querySelectorAll('.editor-group')].filter(group => group !== current)
        .map(group => {
            const bounds = group.getBoundingClientRect();
            const x = bounds.left + bounds.width / 2;
            const y = bounds.top + bounds.height / 2;
            const primary = direction === 'left' ? sourceX - x : direction === 'right' ? x - sourceX
                : direction === 'up' ? sourceY - y : y - sourceY;
            const secondary = direction === 'left' || direction === 'right' ? Math.abs(y - sourceY) : Math.abs(x - sourceX);
            return { group, primary, secondary, rank: Number(group.dataset.focusRank) || 0 };
        })
        .filter(candidate => candidate.primary > 0)
        .sort((left, right) => left.primary - right.primary || left.secondary - right.secondary || right.rank - left.rank);
    if (candidates[0]) dotNet?.invokeMethodAsync('FocusEditorGroup', candidates[0].group.dataset.groupId);
};

window.novaSharp.runPhase3Smoke = async function (explorer, dotNet) {
    const wait = (milliseconds = 100) => new Promise(resolve => setTimeout(resolve, milliseconds));
    const key = (target, value, options = {}) => target.dispatchEvent(new KeyboardEvent("keydown", {
        key: value, bubbles: true, cancelable: true, ...options
    }));
    try {
        const tree = explorer?.querySelector('[role="tree"]');
        key(tree, "ArrowRight");
        await wait(250);
        key(tree, "ArrowDown");
        await wait(300);
        const keyboardNavigation = explorer.querySelector(".tree-row.selected .tree-name")?.textContent === "active.cs";

        const input = document.querySelector(".editor-input");
        input.value = "class DirtySelection;";
        input.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText" }));
        input.setSelectionRange(6, 20);
        input.dispatchEvent(new Event("select", { bubbles: true }));
        key(tree, "F2");
        await wait();
        const renameInput = explorer.querySelector('input[aria-label="New name"]');
        if (renameInput) {
            renameInput.value = "renamed.cs";
            renameInput.dispatchEvent(new Event("change", { bubbles: true }));
            await wait();
            renameInput.closest("form")?.requestSubmit();
        }
        await wait(250);
        const renamePreservedDirtySelection = input.value === "class DirtySelection;"
            && input.selectionStart === 6 && input.selectionEnd === 20;

        key(tree, "ContextMenu");
        await wait();
        const keyboardMenu = explorer.querySelector('[role="menu"]');
        const contextActionsRelevant = !!keyboardMenu
            && keyboardMenu.querySelector('button:nth-of-type(3)')?.disabled === false
            && [...keyboardMenu.querySelectorAll("button")].some(button => button.textContent.includes("Move"));
        key(tree, "Escape");
        await wait();
        const contextMenuDismissed = !explorer.querySelector('[role="menu"]');
        const renderedFile = [...tree.querySelectorAll('[role="treeitem"]')]
            .find(row => !row.hasAttribute("aria-expanded"));
        renderedFile?.dispatchEvent(new MouseEvent("contextmenu", {
            bubbles: true, cancelable: true, clientX: window.innerWidth - 1, clientY: window.innerHeight - 1
        }));
        for (let attempt = 0; attempt < 10; attempt++) {
            await wait(50);
            if (explorer.querySelector('[role="menu"]')?.style.left) break;
        }
        const edgeMenu = explorer.querySelector('[role="menu"]');
        const bounds = edgeMenu?.getBoundingClientRect();
        const contextMenuInsideViewport = !!bounds && bounds.left >= -1 && bounds.top >= -1
            && bounds.right <= window.innerWidth + 1 && bounds.bottom <= window.innerHeight + 1;
        key(tree, "Escape");
        await wait();
        const shiftContext = new MouseEvent("contextmenu", {
            bubbles: true, cancelable: true, shiftKey: true,
            clientX: window.innerWidth - 1, clientY: window.innerHeight - 1
        });
        const nativeContextBypass = renderedFile?.dispatchEvent(shiftContext) === true
            && !explorer.querySelector('[role="menu"]');
        const renderedRows = tree.querySelectorAll('[role="treeitem"]').length;
        const viewportHeight = tree.clientHeight || Math.max(0, explorer.clientHeight - 71);
        const rowLimit = Math.max(64, Math.ceil(viewportHeight / 20) + 18);
        await dotNet.invokeMethodAsync("CompletePhase3SmokeAsync", {
            treePresent: !!tree && renderedRows > 0,
            rowsBounded: renderedRows > 0 && renderedRows <= rowLimit,
            keyboardNavigation,
            contextActionsRelevant,
            contextMenuInsideViewport,
            contextMenuDismissed,
            nativeContextBypass,
            renamePreservedDirtySelection,
            renderedRows
        });
    } catch (error) {
        await dotNet.invokeMethodAsync("CompletePhase3SmokeAsync", {
            treePresent: false, rowsBounded: false, keyboardNavigation: false,
            contextActionsRelevant: false, contextMenuInsideViewport: false,
            contextMenuDismissed: false, nativeContextBypass: false,
            renamePreservedDirtySelection: false, renderedRows: 0,
            error: `${error?.name ?? "Error"}: ${error?.message ?? String(error)}`
        });
    }
};

window.novaSharp.initAppContextMenu();

document.addEventListener("dragstart", event => {
    const tab = event.target.closest?.(".document-tab");
    if (!tab || !event.dataTransfer) return;
    event.dataTransfer.effectAllowed = "move";
    window.novaSharp.tabDragActive = true;
    tab.addEventListener("dragend", () => window.novaSharp.cancelEditorDrag?.(), { once: true });
    event.dataTransfer.setData("application/x-novasharp-tab", "tab");
    event.dataTransfer.setData("text/plain", tab.innerText);
});

const hasNovaSharpTab = event => window.novaSharp.tabDragActive || [...(event.dataTransfer?.types ?? [])]
    .includes("application/x-novasharp-tab");
document.addEventListener("dragover", event => {
    if (!hasNovaSharpTab(event) || event.target.closest?.(".drop-zone,.document-tab")) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "none";
}, true);
document.addEventListener("drop", event => {
    if (!hasNovaSharpTab(event) || event.target.closest?.(".drop-zone,.document-tab")) return;
    event.preventDefault();
}, true);
document.addEventListener("dragend", () => { window.novaSharp.tabDragActive = false; }, true);
document.addEventListener("drop", () => { window.novaSharp.tabDragActive = false; });

window.novaSharp.tabIndexAtX = function (strip, x) {
    const tabs = [...strip.querySelectorAll(".document-tab")];
    if (!tabs.length) return 0;
    const index = tabs.findIndex(tab => x < tab.getBoundingClientRect().left + tab.offsetWidth / 2);
    return index < 0 ? tabs.length - 1 : index;
};

window.novaSharp.runPhase4Smoke = async function (workbench, dotNet) {
    const wait = (milliseconds = 100) => new Promise(resolve => setTimeout(resolve, milliseconds));
    try {
        const strip = workbench.querySelector('.document-tabs');
        let tabs = [...strip.querySelectorAll('.document-tab')];
        const tabsPresent = tabs.length > 2;
        const firstLabel = tabs[0]?.querySelector('.tab-label')?.textContent;
        await dotNet.invokeMethodAsync('BeginPointerTabDrag', tabs[0].dataset.tabId);
        const groupId = tabs[0].closest('.editor-group').dataset.groupId;
        await dotNet.invokeMethodAsync('DropPointerTab', groupId, 2, null, false);
        await wait();
        tabs = [...strip.querySelectorAll('.document-tab')];
        const pointerReordered = tabs[2]?.querySelector('.tab-label')?.textContent === firstLabel;
        const overflowScrollable = strip.scrollWidth > strip.clientWidth && getComputedStyle(strip).overflowX === 'auto';
        const accessibleLabels = tabs.every(tab => tab.getAttribute('role') === 'tab'
            && tab.getAttribute('aria-label')?.includes('saved') && tab.querySelector('.tab-close[aria-label]'));
        tabs[0]?.dispatchEvent(new MouseEvent('contextmenu', {
            bubbles: true, cancelable: true, clientX: 80, clientY: 40
        }));
        await wait();
        const menu = workbench.querySelector('.tab-context-menu');
        const labels = [...(menu?.querySelectorAll('[role="menuitem"]') ?? [])].map(item => item.textContent.trim());
        const contextCommandsPresent = ['Close others', 'Close to the right', 'Close saved', 'Close all']
            .every(label => labels.includes(label));
        workbench.querySelector('.context-menu-dismiss')?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
        await wait();
        const beforeClose = strip.querySelectorAll('.document-tab').length;
        strip.querySelector('.document-tab')?.dispatchEvent(new MouseEvent('mousedown', {
            bubbles: true, cancelable: true, button: 1
        }));
        await wait();
        const middleClickClosed = strip.querySelectorAll('.document-tab').length === beforeClose - 1;
        await dotNet.invokeMethodAsync('CompletePhase4SmokeAsync', {
            tabsPresent, pointerReordered, overflowScrollable, middleClickClosed, accessibleLabels,
            contextCommandsPresent
        });
    } catch (error) {
        await dotNet.invokeMethodAsync('CompletePhase4SmokeAsync', {
            tabsPresent: false, pointerReordered: false, overflowScrollable: false,
            middleClickClosed: false, accessibleLabels: false, contextCommandsPresent: false,
            error: `${error?.name ?? 'Error'}: ${error?.message ?? String(error)}`
        });
    }
};
window.novaSharp.schedulePhase4Smoke = function (workbench, dotNet) {
    setTimeout(() => window.novaSharp.runPhase4Smoke(workbench, dotNet), 0);
};

window.novaSharp.runPhase5Smoke = async function (workbench, dotNet) {
    const wait = (milliseconds = 150) => new Promise(resolve => setTimeout(resolve, milliseconds));
    try {
        let groups = [...workbench.querySelectorAll('.editor-group')];
        const groupsPresent = groups.length === 2 && !!workbench.querySelector('.editor-split');
        let inputs = groups.map(group => group.querySelector('.editor-input'));
        inputs[0].value = 'class SharedUpdate;';
        inputs[0].dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText' }));
        await wait(250);
        groups = [...workbench.querySelectorAll('.editor-group')];
        inputs = groups.map(group => group.querySelector('.editor-input'));
        const sharedEditsImmediate = inputs.every(input => input?.value === 'class SharedUpdate;');
        inputs[0].setSelectionRange(1, 4);
        inputs[0].dispatchEvent(new Event('select', { bubbles: true }));
        inputs[1].setSelectionRange(7, 13);
        inputs[1].dispatchEvent(new Event('select', { bubbles: true }));
        await wait();
        const independentSelections = inputs[0].selectionStart === 1 && inputs[0].selectionEnd === 4
            && inputs[1].selectionStart === 7 && inputs[1].selectionEnd === 13;
        const splitter = workbench.querySelector('.editor-splitter');
        const splitterAccessible = splitter?.getAttribute('aria-label') === 'Resize editor groups'
            && splitter.getAttribute('aria-orientation') === 'vertical'
            && splitter.dataset.axis === 'x';
        const initialRatio = Number(splitter.getAttribute('aria-valuenow'));
        splitter.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
        await wait();
        const splitterResized = Number(splitter.getAttribute('aria-valuenow')) > initialRatio;
        const splitterBounds = splitter.getBoundingClientRect();
        const pointerStart = splitterBounds.left + splitterBounds.width / 2 + 0.25;
        const pointerRatio = Number(splitter.getAttribute('aria-valuenow'));
        splitter.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, pointerId: 51, clientX: pointerStart }));
        workbench.style.maxWidth = 'calc(100% - 1px)';
        splitter.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, pointerId: 51, clientX: pointerStart + 20.5 }));
        splitter.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, pointerId: 51, clientX: pointerStart + 20.5 }));
        workbench.style.maxWidth = '';
        await wait();
        const fractionalPointerResize = Number(splitter.getAttribute('aria-valuenow')) > pointerRatio;
        const focusedBefore = workbench.querySelector('.editor-group.focused')?.dataset.groupId;
        window.novaSharp.focusEditorGroup(workbench, dotNet, 'right');
        await wait();
        const directionalFocus = !!focusedBefore
            && workbench.querySelector('.editor-group.focused')?.dataset.groupId !== focusedBefore;
        const originalWidth = workbench.style.width;
        workbench.style.width = '480px';
        await wait();
        const narrowLayoutOperable = [...workbench.querySelectorAll('.editor-group')]
            .every(group => group.getBoundingClientRect().width >= 159);
        workbench.style.width = originalWidth;
        let sourceTab = workbench.querySelector('.document-tab');
        await dotNet.invokeMethodAsync('BeginPointerTabDrag', sourceTab.dataset.tabId);
        await wait();
        let zones = [];
        for (let attempt = 0; attempt < 10 && zones.length < 10; attempt++) {
            await wait(100);
            zones = [...workbench.querySelectorAll('.drop-zone')];
        }
        const dropZonesPresent = zones.length >= 10 && zones.every(zone => zone.getAttribute('aria-label'));
        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
        await wait();
        const escapeCancelsDrag = !workbench.querySelector('.group-drop-zones');
        sourceTab = workbench.querySelector('.document-tab');
        await dotNet.invokeMethodAsync('BeginPointerTabDrag', sourceTab.dataset.tabId);
        await wait();
        const right = workbench.querySelectorAll('.editor-group')[1]?.querySelector('.drop-zone.right');
        if (!right) throw new Error('Drop zones did not render after drag start.');
        await dotNet.invokeMethodAsync('DropPointerTab', right.closest('.editor-group').dataset.groupId, null, 'right', true);
        await wait(250);
        const splittersMatchOrientation = [...workbench.querySelectorAll('.editor-splitter')].every(candidate =>
            candidate.dataset.axis === (candidate.getAttribute('aria-orientation') === 'vertical' ? 'x' : 'y'));
        const edgeDropSplit = workbench.querySelectorAll('.editor-group').length === 3 && splittersMatchOrientation;
        await dotNet.invokeMethodAsync('CompletePhase5SmokeAsync', {
            groupsPresent, sharedEditsImmediate, independentSelections, splitterAccessible,
            splitterResized, dropZonesPresent, edgeDropSplit, narrowLayoutOperable,
            escapeCancelsDrag, fractionalPointerResize, directionalFocus
        });
    } catch (error) {
        await dotNet.invokeMethodAsync('CompletePhase5SmokeAsync', {
            groupsPresent: false, sharedEditsImmediate: false, independentSelections: false,
            splitterAccessible: false, splitterResized: false, dropZonesPresent: false,
            edgeDropSplit: false, narrowLayoutOperable: false, escapeCancelsDrag: false,
            fractionalPointerResize: false, directionalFocus: false,
            error: `${error?.name ?? 'Error'}: ${error?.message ?? String(error)}`
        });
    }
};
window.novaSharp.schedulePhase5Smoke = function (workbench, dotNet) {
    setTimeout(() => window.novaSharp.runPhase5Smoke(workbench, dotNet), 0);
};
window.novaSharp.startSmokePolling = function (bridge, phase4, phase5) {
    if (!phase4 && !phase5) return;
    const poll = setInterval(() => {
        const workbench = document.querySelector('.workbench');
        if (!workbench) return;
        if (phase4 && workbench.querySelectorAll('.document-tab').length < 3) return;
        if (phase5 && (workbench.querySelectorAll('.editor-group').length < 2
            || workbench.querySelectorAll('.editor-input').length < 2 || !workbench.querySelector('.editor-splitter'))) return;
        clearInterval(poll);
        if (phase4) window.novaSharp.schedulePhase4Smoke(workbench, bridge);
        else window.novaSharp.schedulePhase5Smoke(workbench, bridge);
    }, 100);
};

window.novaSharp.initBottomPanelResize = function (workbench) {
    if (workbench.dataset.bottomPanelResize === 'true') return;
    workbench.dataset.bottomPanelResize = 'true';
    const storageKey = panel => `novaSharp.bottomPanelHeight.${panel.dataset.bottomPanel}`;
    const restore = panel => {
        if (panel.dataset.heightRestored === 'true') return;
        panel.dataset.heightRestored = 'true';
        try {
            const height = Number(localStorage.getItem(storageKey(panel)));
            if (Number.isFinite(height) && height >= 110) panel.style.height = `${height}px`;
        } catch { }
    };
    const restorePanels = () => workbench.querySelectorAll('[data-bottom-panel]').forEach(restore);
    const save = panel => {
        try { localStorage.setItem(storageKey(panel), String(Math.round(panel.getBoundingClientRect().height))); }
        catch { }
    };
    restorePanels();
    new MutationObserver(restorePanels).observe(workbench, { childList: true, subtree: true });
    workbench.addEventListener('pointerdown', event => {
        const handle = event.target.closest('.bottom-panel-resizer');
        if (!handle) return;
        const panel = handle.parentElement;
        const startY = event.clientY;
        const startHeight = panel.getBoundingClientRect().height;
        const editorHeight = panel.parentElement.getBoundingClientRect().height;
        handle.classList.add('dragging');
        handle.setPointerCapture(event.pointerId);
        const move = moveEvent => {
            const height = Math.max(110, Math.min(editorHeight * .75, startHeight + startY - moveEvent.clientY));
            panel.style.height = `${height}px`;
        };
        const finish = () => {
            save(panel);
            handle.classList.remove('dragging');
            handle.removeEventListener('pointermove', move);
            handle.removeEventListener('pointerup', finish);
            handle.removeEventListener('pointercancel', finish);
        };
        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', finish);
        handle.addEventListener('pointercancel', finish);
        event.preventDefault();
    });
    workbench.addEventListener('keydown', event => {
        const handle = event.target.closest('.bottom-panel-resizer');
        if (!handle || !['ArrowUp', 'ArrowDown'].includes(event.key)) return;
        const panel = handle.parentElement;
        const current = panel.getBoundingClientRect().height;
        const maximum = panel.parentElement.getBoundingClientRect().height * .75;
        panel.style.height = `${Math.max(110, Math.min(maximum, current + (event.key === 'ArrowUp' ? 20 : -20)))}px`;
        save(panel);
        event.preventDefault();
    });
};

window.novaSharp.runPhase6Smoke = async function (bridge) {
    try {
        for (let attempt = 0; attempt < 100 && !document.querySelector('.solution-explorer .project-tree'); attempt++)
            await new Promise(resolve => setTimeout(resolve, 100));
        const solutionTreePresent = !!document.querySelector('.solution-explorer .project-tree');
        const projectNodes = document.querySelectorAll('.solution-explorer .project-node').length;
        const project = [...document.querySelectorAll('.project-node > summary')]
            .find(summary => summary.title?.toLowerCase().endsWith('.csproj'));
        const projectFileEditable = !!project;
        project?.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 120, clientY: 100 }));
        for (let attempt = 0; attempt < 20 && !document.querySelector('.explorer-context-menu'); attempt++)
            await new Promise(resolve => setTimeout(resolve, 100));
        const menuText = document.querySelector('.explorer-context-menu')?.textContent ?? '';
        const contextMenuPresent = ['Open', 'New file', 'New folder', 'Rename', 'Move', 'Delete']
            .every(label => menuText.includes(label));
        const dragSourcePresent = !!document.querySelector('.solution-explorer .tree-row[data-file-path]');
        document.querySelector('.context-menu-dismiss')?.click();
        await new Promise(resolve => setTimeout(resolve, 200));
        await bridge.invokeMethodAsync('CompletePhase6SmokeAsync', solutionTreePresent, projectNodes,
            projectFileEditable, contextMenuPresent, dragSourcePresent, null);
    } catch (error) {
        await bridge.invokeMethodAsync('CompletePhase6SmokeAsync', false, 0, false, false, false,
            `${error?.name ?? 'Error'}: ${error?.message ?? String(error)}`);
    }
};

window.novaSharp.runPhase9Smoke = async function (workbench, bridge) {
    const waitFor = async (predicate, attempts = 200) => {
        for (let attempt = 0; attempt < attempts; attempt++) {
            if (predicate()) return true;
            await new Promise(resolve => setTimeout(resolve, 50));
        }
        return false;
    };
    try {
        workbench.dispatchEvent(new KeyboardEvent('keydown', { key: 'p', ctrlKey: true, bubbles: true }));
        const quickOpenVisible = await waitFor(() => !!workbench.querySelector('.quick-access'));
        const duplicateFilesVisible = await waitFor(() => [...workbench.querySelectorAll('.quick-access-results button span')]
            .filter(label => label.textContent === 'Shared.cs').length >= 2);
        workbench.querySelector('.quick-access-backdrop')?.click();
        workbench.querySelector('[aria-label="Search"]')?.click();
        const searchVisible = await waitFor(() => !!workbench.querySelector('.search-panel'));
        const resultCount = await bridge.invokeMethodAsync('RunPhase9SearchSmokeAsync', 'needle');
        const resultsStreamed = await waitFor(() => workbench.querySelectorAll('.search-results button').length >= 2);
        if (!resultsStreamed) throw new Error(`Search returned ${resultCount} results but did not render them.`);
        workbench.querySelector('.search-actions button:last-child')?.click();
        const replacePreview = await waitFor(() => !!workbench.querySelector('.edit-preview'));
        await bridge.invokeMethodAsync('CompletePhase9SmokeAsync', quickOpenVisible, duplicateFilesVisible,
            searchVisible, resultsStreamed, replacePreview, null);
    } catch (error) {
        await bridge.invokeMethodAsync('CompletePhase9SmokeAsync', false, false, false, false, false,
            `${error?.name ?? 'Error'}: ${error?.message ?? String(error)}`);
    }
};

window.novaSharp.runPhase11Smoke = async function (workbench, bridge) {
    const waitFor = async (predicate, attempts = 300) => {
        for (let attempt = 0; attempt < attempts; attempt++) {
            if (predicate()) return true;
            await new Promise(resolve => setTimeout(resolve, 50));
        }
        return false;
    };
    try {
        const ready = await waitFor(() => !!workbench.querySelector('.terminal-host'));
        const host = workbench.querySelector('.terminal-host');
        const terminalPresent = ready && !!host;
        const resizeValid = terminalPresent && host.clientWidth > 0 && host.clientHeight > 0;
        const inputRoundTrip = terminalPresent && await bridge.invokeMethodAsync('RunPhase11TerminalSmokeAsync');
        const processExited = await waitFor(() => workbench.querySelector('.terminal-state')?.textContent.includes('Exited (exit 7)'));
        await bridge.invokeMethodAsync('CompletePhase11SmokeAsync', terminalPresent, inputRoundTrip,
            resizeValid, processExited, null);
    } catch (error) {
        await bridge.invokeMethodAsync('CompletePhase11SmokeAsync', false, false, false, false,
            `${error?.name ?? 'Error'}: ${error?.message ?? String(error)}`);
    }
};
