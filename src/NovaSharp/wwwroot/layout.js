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

    document.addEventListener("contextmenu", event => {
        if (event.target.closest(".explorer,.menu-popup,.explorer-context-menu")) return;
        const editable = event.target.closest("textarea,input:not([type=button]):not([type=submit])");
        const selectionTarget = editable || event.target;
        const selection = selectedText(selectionTarget);
        if (!editable && !selection) return;

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
            addItem("Copy", "copy", "Ctrl+C");
        }
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
        const rows = [...tree.querySelectorAll('[role="treeitem"]')];
        const active = rows.find(row => row.querySelector(".tree-name")?.textContent === "active.cs");
        active?.click();
        await wait();
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
            renamePreservedDirtySelection,
            renderedRows
        });
    } catch (error) {
        await dotNet.invokeMethodAsync("CompletePhase3SmokeAsync", {
            treePresent: false, rowsBounded: false, keyboardNavigation: false,
            contextActionsRelevant: false, contextMenuInsideViewport: false,
            contextMenuDismissed: false, renamePreservedDirtySelection: false, renderedRows: 0,
            error: `${error?.name ?? "Error"}: ${error?.message ?? String(error)}`
        });
    }
};

window.novaSharp.initAppContextMenu();
