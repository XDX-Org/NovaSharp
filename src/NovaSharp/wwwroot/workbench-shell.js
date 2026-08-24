const doubleShiftWindowMilliseconds = 500;

let bridge;
let bindings = [];
let paletteCommandId;
let lastShiftRelease = 0;
let shiftWasAlone = false;
let resizeObserver;
let resizeFrame;

function invoke(commandId) {
    return bridge?.invokeMethodAsync('InvokeShellCommandAsync', commandId);
}

function matchesBinding(event, binding) {
    const parts = binding.split('+');
    const key = parts.at(-1);
    const ctrlCmd = parts.includes('CtrlCmd');
    const shift = parts.includes('Shift');
    const alt = parts.includes('Alt');
    return event.code === key
        && (event.ctrlKey || event.metaKey) === ctrlCmd
        && event.shiftKey === shift
        && event.altKey === alt;
}

function onKeyDown(event) {
    if (event.key === 'Shift' && !event.repeat) {
        shiftWasAlone = true;
        return;
    }
    if (event.key !== 'Shift') shiftWasAlone = false;
    const binding = bindings.find(candidate => matchesBinding(event, candidate.keybinding));
    if (binding) {
        event.preventDefault();
        event.stopImmediatePropagation();
        void invoke(binding.commandId);
    }
}

function onKeyUp(event) {
    if (event.key !== 'Shift' || !shiftWasAlone) return;
    const now = performance.now();
    if (now - lastShiftRelease <= doubleShiftWindowMilliseconds) {
        event.preventDefault();
        lastShiftRelease = 0;
        void invoke(paletteCommandId);
    } else {
        lastShiftRelease = now;
    }
    shiftWasAlone = false;
}

function onPointerDown(event) {
    if (!document.querySelector('.command-menu-trigger[aria-expanded="true"]')
        || event.target.closest('.command-menu')) return;
    void bridge?.invokeMethodAsync('DismissCommandMenusAsync');
}

function applyResponsiveState(workbench) {
    const width = workbench.getBoundingClientRect().width;
    workbench.classList.toggle('narrow', width < 720);
}

function onResize(entries) {
    if (resizeFrame) return;
    resizeFrame = requestAnimationFrame(() => {
        resizeFrame = 0;
        for (const entry of entries) applyResponsiveState(entry.target);
    });
}

function initialize(nextBridge, descriptors, nextPaletteCommandId) {
    dispose();
    bridge = nextBridge;
    paletteCommandId = nextPaletteCommandId;
    bindings = descriptors.flatMap(descriptor => descriptor.keybindings.map(keybinding => ({
        commandId: descriptor.id,
        keybinding,
    })));
    document.addEventListener('keydown', onKeyDown, true);
    document.addEventListener('keyup', onKeyUp, true);
    document.addEventListener('pointerdown', onPointerDown, true);
    const workbench = document.querySelector('.workbench');
    if (workbench) {
        applyResponsiveState(workbench);
        resizeObserver = new ResizeObserver(onResize);
        resizeObserver.observe(workbench);
    }
}

function dispose() {
    document.removeEventListener('keydown', onKeyDown, true);
    document.removeEventListener('keyup', onKeyUp, true);
    document.removeEventListener('pointerdown', onPointerDown, true);
    resizeObserver?.disconnect();
    resizeObserver = undefined;
    if (resizeFrame) cancelAnimationFrame(resizeFrame);
    resizeFrame = 0;
    bridge = undefined;
    bindings = [];
    paletteCommandId = undefined;
    lastShiftRelease = 0;
    shiftWasAlone = false;
}

globalThis.NovaWorkbench = { initialize, dispose };
