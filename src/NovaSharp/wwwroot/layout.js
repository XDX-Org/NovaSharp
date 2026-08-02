window.novaSharp = window.novaSharp || {};

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

window.novaSharp.initExplorerResize = function (explorer, dotNet) {
    const handle = explorer?.querySelector(".explorer-resizer");
    if (!handle || handle.dataset.resizeReady) return;
    handle.dataset.resizeReady = "true";

    handle.addEventListener("pointerdown", event => {
        if (event.button !== 0) return;
        event.preventDefault();
        handle.setPointerCapture(event.pointerId);
        document.body.classList.add("resizing-explorer");
        const startX = event.clientX;
        const startWidth = explorer.getBoundingClientRect().width;
        let latestX = startX;
        let animationFrame = 0;

        const applyWidth = () => {
            animationFrame = 0;
            const maximum = Math.min(800, window.innerWidth * 0.65);
            explorer.style.width = Math.max(180, Math.min(maximum, startWidth + startX - latestX)) + "px";
        };
        const move = moveEvent => {
            latestX = moveEvent.clientX;
            if (!animationFrame) animationFrame = requestAnimationFrame(applyWidth);
        };
        const stop = () => {
            if (animationFrame) {
                cancelAnimationFrame(animationFrame);
                applyWidth();
            }
            document.body.classList.remove("resizing-explorer");
            dotNet?.invokeMethodAsync("ExplorerResized", explorer.getBoundingClientRect().width);
            handle.removeEventListener("pointermove", move);
            handle.removeEventListener("pointerup", stop);
            handle.removeEventListener("pointercancel", stop);
        };

        handle.addEventListener("pointermove", move);
        handle.addEventListener("pointerup", stop);
        handle.addEventListener("pointercancel", stop);
    });
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
        const rowLimit = Math.max(64, Math.ceil(viewportHeight / 26) + 18);
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

document.addEventListener('keydown', event => {
    const splitter = event.target.closest?.('.editor-splitter');
    if (!splitter || !event.shiftKey || !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
    event.preventDefault();
    const decreasing = event.key === 'ArrowLeft' || event.key === 'ArrowUp';
    splitter.value = Math.max(10, Math.min(90, Number(splitter.value) + (decreasing ? -10 : 10)));
    splitter.dispatchEvent(new Event('input', { bubbles: true }));
});

document.addEventListener("dragstart", event => {
    const tab = event.target.closest?.(".document-tab");
    if (!tab || !event.dataTransfer) return;
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", tab.innerText);
});

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
        const dataTransfer = new DataTransfer();
        tabs[0]?.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer }));
        await wait(200);
        tabs = [...strip.querySelectorAll('.document-tab')];
        tabs[2]?.dispatchEvent(new DragEvent('dragenter', { bubbles: true, cancelable: true, dataTransfer }));
        await wait(200);
        tabs = [...strip.querySelectorAll('.document-tab')];
        tabs[2]?.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer }));
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
            && splitter.getAttribute('aria-orientation') === 'vertical';
        splitter.value = '65';
        splitter.dispatchEvent(new Event('input', { bubbles: true }));
        await wait();
        const splitterResized = workbench.querySelector('.editor-split')?.dataset.ratio === '0.65';
        const originalWidth = workbench.style.width;
        workbench.style.width = '480px';
        await wait();
        const narrowLayoutOperable = [...workbench.querySelectorAll('.editor-group')]
            .every(group => group.getBoundingClientRect().width >= 159);
        workbench.style.width = originalWidth;
        const sourceTab = workbench.querySelector('.document-tab');
        const dataTransfer = new DataTransfer();
        sourceTab.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer }));
        await wait();
        let zones = [];
        for (let attempt = 0; attempt < 10 && zones.length < 10; attempt++) {
            await wait(100);
            zones = [...workbench.querySelectorAll('.drop-zone')];
        }
        const dropZonesPresent = zones.length >= 10 && zones.every(zone => zone.getAttribute('aria-label'));
        const right = workbench.querySelectorAll('.editor-group')[1]?.querySelector('.drop-zone.right');
        if (!right) throw new Error('Drop zones did not render after drag start.');
        right.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer, ctrlKey: true }));
        await wait(250);
        const edgeDropSplit = workbench.querySelectorAll('.editor-group').length === 3;
        await dotNet.invokeMethodAsync('CompletePhase5SmokeAsync', {
            groupsPresent, sharedEditsImmediate, independentSelections, splitterAccessible,
            splitterResized, dropZonesPresent, edgeDropSplit, narrowLayoutOperable
        });
    } catch (error) {
        await dotNet.invokeMethodAsync('CompletePhase5SmokeAsync', {
            groupsPresent: false, sharedEditsImmediate: false, independentSelections: false,
            splitterAccessible: false, splitterResized: false, dropZonesPresent: false,
            edgeDropSplit: false, narrowLayoutOperable: false,
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
