const models = new Map();
let loader;

function loadMonaco() {
    if (loader) return loader;
    loader = new Promise((resolve, reject) => {
        const finish = () => {
            window.MonacoEnvironment = { getWorkerUrl: () => `data:text/javascript;charset=utf-8,${encodeURIComponent(
                "self.MonacoEnvironment={baseUrl:'monaco/'};importScripts('monaco/vs/base/worker/workerMain.js');")}` };
            window.require.config({ paths: { vs: 'monaco/vs' } });
            window.require(['vs/editor/editor.main'], () => resolve(window.monaco), reject);
        };
        if (window.require?.config) { finish(); return; }
        const script = document.createElement('script');
        script.src = 'monaco/vs/loader.js';
        script.onload = finish;
        script.onerror = reject;
        document.head.appendChild(script);
    });
    return loader;
}

function modelUri(monaco, documentId, filePath) {
    return filePath ? monaco.Uri.file(filePath.replaceAll('\\', '/'))
        : monaco.Uri.parse(`novasharp://untitled/${documentId}`);
}

function state(root) {
    if (!root.__novaMonaco) throw new Error('Monaco editor is not initialized.');
    return root.__novaMonaco;
}

export async function createEditor(root, documentId, filePath, languageId, value, version, wordWrap,
    selectionStart, selectionEnd, scrollTop, scrollLeft, dotNet) {
    const monaco = await loadMonaco();
    let entry = models.get(documentId);
    if (!entry) {
        entry = { model: monaco.editor.createModel(value, languageId, modelUri(monaco, documentId, filePath)),
            version, clients: new Map(), owner: undefined, applying: false, views: 0, lastUsed: Date.now() };
        entry.modelListener = entry.model.onDidChangeContent(event => {
            if (entry.applying) return;
            const client = entry.clients.get(entry.owner) ?? [...entry.clients.values()].find(item => item.editor.hasTextFocus())
                ?? entry.clients.values().next().value;
            if (!client) return;
            const selection = client.editor.getSelection();
            const baseVersion = entry.version++;
            client.dotNet.invokeMethodAsync('ModelChanged', baseVersion, event.changes.map(change => ({
                start: change.rangeOffset, length: change.rangeLength, text: change.text
            })), selection ? entry.model.getOffsetAt(selection.getStartPosition()) : 0,
                selection ? entry.model.getOffsetAt(selection.getEndPosition()) : 0);
        });
        models.set(documentId, entry);
    }
    entry.views++;
    entry.lastUsed = Date.now();
    const editor = monaco.editor.create(root, {
        model: entry.model, wordWrap: wordWrap ? 'on' : 'off', glyphMargin: true,
        minimap: { enabled: false }, fontSize: 13, lineHeight: 20, tabSize: 4,
        fixedOverflowWidgets: true, accessibilitySupport: 'auto'
    });
    const resizeObserver = new ResizeObserver(() => editor.layout());
    resizeObserver.observe(root);
    let viewTimer;
    const viewId = crypto.randomUUID();
    const offset = position => entry.model.getOffsetAt(position);
    const notifyView = () => {
        clearTimeout(viewTimer);
        viewTimer = setTimeout(() => {
            const selection = editor.getSelection();
            if (selection) dotNet.invokeMethodAsync('ViewChanged', offset(selection.getStartPosition()),
                offset(selection.getEndPosition()), editor.getScrollTop(), editor.getScrollLeft());
        }, 120);
    };
    const client = { editor, dotNet };
    entry.clients.set(viewId, client);
    const disposables = [
        editor.onDidFocusEditorText(() => entry.owner = viewId),
        editor.onDidChangeCursorSelection(notifyView), editor.onDidScrollChange(notifyView),
        editor.onMouseDown(event => {
            if (event.target.type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && event.target.position)
                dotNet.invokeMethodAsync('ToggleBreakpoint', event.target.position.lineNumber);
        })
    ];
    const start = entry.model.getPositionAt(Math.min(selectionStart, entry.model.getValueLength()));
    const end = entry.model.getPositionAt(Math.min(selectionEnd, entry.model.getValueLength()));
    editor.setSelection(new monaco.Selection(start.lineNumber, start.column, end.lineNumber, end.column));
    editor.setScrollPosition({ scrollTop, scrollLeft });
    root.__novaMonaco = { monaco, documentId, viewId, entry, editor, resizeObserver, disposables,
        decorations: editor.createDecorationsCollection(), get viewTimer() { return viewTimer; } };
}

export function updateEditor(root, value, version, wordWrap, markers, breakpointLines, executionLine) {
    const current = state(root);
    current.editor.updateOptions({ wordWrap: wordWrap ? 'on' : 'off' });
    if (current.entry.model.getValue() !== value) {
        current.entry.applying = true;
        current.entry.model.setValue(value);
        current.entry.applying = false;
    }
    current.entry.version = version;
    const severity = current.monaco.MarkerSeverity;
    current.monaco.editor.setModelMarkers(current.entry.model, 'novasharp', markers.map(marker => ({
        ...marker, severity: severity[marker.severity] ?? severity.Info
    })));
    const decorations = [...breakpointLines].map(line => ({ range: new current.monaco.Range(line, 1, line, 1),
        options: { isWholeLine: true, glyphMarginClassName: 'monaco-breakpoint' } }));
    if (executionLine) decorations.push({ range: new current.monaco.Range(executionLine, 1, executionLine, 1),
        options: { isWholeLine: true, className: 'monaco-execution-line', glyphMarginClassName: 'monaco-execution-glyph' } });
    current.decorations.set(decorations);
}

export function disposeEditor(root) {
    const current = root.__novaMonaco;
    if (!current) return;
    clearTimeout(current.viewTimer);
    current.resizeObserver.disconnect();
    current.disposables.forEach(disposable => disposable.dispose());
    current.decorations.clear();
    current.editor.dispose();
    current.entry.clients.delete(current.viewId);
    if (current.entry.owner === current.viewId) current.entry.owner = current.entry.clients.keys().next().value;
    current.entry.views--;
    current.entry.lastUsed = Date.now();
    root.__novaMonaco = undefined;
    const unused = [...models.entries()].filter(([, entry]) => entry.views === 0)
        .sort((left, right) => right[1].lastUsed - left[1].lastUsed);
    for (const [documentId, entry] of unused.slice(32)) {
        entry.modelListener.dispose();
        entry.model.dispose();
        models.delete(documentId);
    }
}
