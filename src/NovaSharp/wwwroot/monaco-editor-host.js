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

export async function createEditor(root, documentId, filePath, languageId, value, wordWrap,
    selectionStart, selectionEnd, scrollTop, scrollLeft, dotNet) {
    const monaco = await loadMonaco();
    let entry = models.get(documentId);
    if (!entry) {
        entry = { model: monaco.editor.createModel(value, languageId, modelUri(monaco, documentId, filePath)),
            views: 0, lastUsed: Date.now() };
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
    let applying = false;
    let viewTimer;
    const offset = position => entry.model.getOffsetAt(position);
    const notifyView = () => {
        clearTimeout(viewTimer);
        viewTimer = setTimeout(() => {
            const selection = editor.getSelection();
            if (selection) dotNet.invokeMethodAsync('ViewChanged', offset(selection.getStartPosition()),
                offset(selection.getEndPosition()), editor.getScrollTop(), editor.getScrollLeft());
        }, 120);
    };
    const disposables = [
        entry.model.onDidChangeContent(() => {
            if (applying) return;
            const selection = editor.getSelection();
            dotNet.invokeMethodAsync('ModelChanged', entry.model.getValue(),
                selection ? offset(selection.getStartPosition()) : 0, selection ? offset(selection.getEndPosition()) : 0);
        }),
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
    root.__novaMonaco = { monaco, documentId, entry, editor, resizeObserver, disposables,
        decorations: editor.createDecorationsCollection(), get applying() { return applying; },
        set applying(value) { applying = value; }, get viewTimer() { return viewTimer; } };
}

export function updateEditor(root, value, wordWrap, markers, breakpointLines, executionLine) {
    const current = state(root);
    current.editor.updateOptions({ wordWrap: wordWrap ? 'on' : 'off' });
    if (current.entry.model.getValue() !== value) {
        current.applying = true;
        current.entry.model.setValue(value);
        current.applying = false;
    }
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
    current.entry.views--;
    current.entry.lastUsed = Date.now();
    root.__novaMonaco = undefined;
    const unused = [...models.entries()].filter(([, entry]) => entry.views === 0)
        .sort((left, right) => right[1].lastUsed - left[1].lastUsed);
    for (const [documentId, entry] of unused.slice(32)) {
        entry.model.dispose();
        models.delete(documentId);
    }
}
