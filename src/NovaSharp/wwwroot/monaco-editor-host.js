const models = new Map();
let loader;

function loadMonaco() {
    if (loader) return loader;
    loader = new Promise((resolve, reject) => {
        const finish = () => {
            window.MonacoEnvironment = { getWorkerUrl: () => `data:text/javascript;charset=utf-8,${encodeURIComponent(
                "self.MonacoEnvironment={baseUrl:'monaco/'};importScripts('monaco/vs/base/worker/workerMain.js');")}` };
            window.require.config({ paths: { vs: 'monaco/vs' } });
            window.require(['vs/editor/editor.main', 'vs/basic-languages/monaco.contribution'],
                () => resolve(window.monaco), reject);
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

function semanticClass(classification) {
    const value = classification.toLowerCase().replaceAll(/[^a-z0-9]/g, '');
    const mappings = [['comment', 'comment'], ['string', 'string'], ['attributevalue', 'string'], ['number', 'number'],
        ['keyword', 'keyword'], ['directive', 'keyword'], ['transition', 'razortransition'],
        ['componentattribute', 'componentattribute'], ['component', 'component'], ['attribute', 'htmlattribute'],
        ['element', 'htmltag'], ['taghelper', 'htmltag'], ['method', 'method'], ['function', 'method'],
        ['property', 'property'], ['field', 'field'], ['constant', 'constant'], ['variable', 'variable'],
        ['local', 'variable'], ['typeparameter', 'typeparameter'], ['parameter', 'parameter'],
        ['enummember', 'enummember'], ['event', 'event'], ['namespace', 'namespace'], ['record', 'record'],
        ['interface', 'interface'], ['struct', 'struct'], ['class', 'class'], ['type', 'type'], ['label', 'label'],
        ['operator', 'operator'], ['regexp', 'regex'], ['regex', 'regex'], ['decorator', 'decorator'], ['macro', 'macro']];
    return mappings.find(([part]) => value.includes(part))?.[1] ?? 'semantic';
}

export async function createEditor(root, documentId, filePath, languageId, value, version, options,
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
        model: entry.model, wordWrap: options.wordWrap ? 'on' : 'off', glyphMargin: true,
        minimap: { enabled: false }, fontSize: 13, lineHeight: 20, tabSize: 4,
        fontLigatures: options.ligatures, quickSuggestions: options.suggestionsEnabled,
        suggestOnTriggerCharacters: options.suggestionsEnabled,
        guides: { bracketPairs: options.braceGuides, indentation: options.braceGuides },
        cursorBlinking: options.reducedMotion ? 'solid' : 'blink',
        fixedOverflowWidgets: true, accessibilitySupport: 'auto'
    });
    const resizeObserver = new ResizeObserver(() => editor.layout());
    resizeObserver.observe(root);
    let viewTimer;
    let hoverTimer;
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
        editor.onKeyDown(event => {
            const key = event.browserEvent.key;
            const modifier = event.ctrlKey || event.metaKey;
            if (modifier && ['z', 'y'].includes(key.toLowerCase())) { event.stopPropagation(); return; }
            const popup = root.closest('.code-editor')?.querySelector('.completion-popup');
            const selectedCompletion = popup?.querySelector('.selected');
            if (selectedCompletion?.dataset.commit?.includes(key) && key.length === 1) {
                event.preventDefault();
                event.stopPropagation();
                const position = editor.getPosition();
                dotNet.invokeMethodAsync('Command', 'completion-accept', position ? offset(position) : 0)
                    .then(() => editor.trigger('completion', 'type', { text: key }));
                return;
            }
            let command;
            if (popup && ['ArrowDown', 'ArrowUp', 'Enter', 'Tab', 'Escape'].includes(key))
                command = { ArrowDown: 'completion-next', ArrowUp: 'completion-previous', Enter: 'completion-accept',
                    Tab: 'completion-accept', Escape: 'escape' }[key];
            else if (modifier && key === ' ') command = event.shiftKey ? 'signature' : 'completion';
            else if (modifier && event.shiftKey && key.toLowerCase() === 'f') command = 'format';
            else if (key === 'F12') command = event.ctrlKey && event.shiftKey ? 'type-definition'
                : event.altKey ? 'peek' : event.shiftKey ? 'references'
                    : modifier ? 'implementation' : 'definition';
            else if (key === 'F2') command = 'rename';
            else if (modifier && key === '.') command = 'code-actions';
            else if (modifier && event.shiftKey && key.toLowerCase() === 'o') command = 'outline';
            else if (event.altKey && key === 'ArrowLeft') command = 'back';
            else if (event.altKey && key === 'ArrowRight') command = 'forward';
            if (!command) return;
            event.preventDefault();
            event.stopPropagation();
            const position = editor.getPosition();
            dotNet.invokeMethodAsync('Command', command, position ? offset(position) : 0);
        }),
        editor.onMouseMove(event => {
            clearTimeout(hoverTimer);
            if (!event.target.position) return;
            const position = offset(event.target.position);
            hoverTimer = setTimeout(() => dotNet.invokeMethodAsync('Command', 'hover', position), 250);
        }),
        editor.onMouseLeave(() => {
            clearTimeout(hoverTimer);
            const position = editor.getPosition();
            dotNet.invokeMethodAsync('Command', 'hover-close', position ? offset(position) : 0);
        }),
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
        decorations: editor.createDecorationsCollection(), get viewTimer() { return viewTimer; },
        get hoverTimer() { return hoverTimer; } };
}

export function updateEditor(root, value, version, options, markers, breakpointLines, executionLine, semanticSpans) {
    const current = state(root);
    current.monaco.editor.setTheme(options.highContrast ? 'hc-black' : options.lightTheme ? 'vs' : 'vs-dark');
    current.editor.updateOptions({ wordWrap: options.wordWrap ? 'on' : 'off', fontLigatures: options.ligatures,
        quickSuggestions: options.suggestionsEnabled, suggestOnTriggerCharacters: options.suggestionsEnabled,
        guides: { bracketPairs: options.braceGuides, indentation: options.braceGuides },
        cursorBlinking: options.reducedMotion ? 'solid' : 'blink' });
    current.entry.model.updateOptions({ tabSize: options.tabSize });
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
    for (const span of semanticSpans) {
        const start = current.entry.model.getPositionAt(span.start);
        const end = current.entry.model.getPositionAt(span.start + span.length);
        decorations.push({ range: new current.monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column),
            options: { inlineClassName: `token-${semanticClass(span.classification)}` } });
    }
    current.decorations.set(decorations);
}

export function applyEditorEdit(root, start, length, text, newPosition) {
    const current = state(root);
    const model = current.entry.model;
    const begin = model.getPositionAt(start);
    const end = model.getPositionAt(start + length);
    current.editor.executeEdits('novasharp', [{ range: new current.monaco.Range(begin.lineNumber, begin.column,
        end.lineNumber, end.column), text, forceMoveMarkers: true }]);
    const caret = model.getPositionAt(newPosition ?? start + text.length);
    current.editor.setPosition(caret);
    current.editor.focus();
}

export function replaceEditorText(root, text, selectionStart, selectionLength) {
    const current = state(root);
    current.editor.executeEdits('novasharp', [{ range: current.entry.model.getFullModelRange(), text }]);
    setEditorSelection(root, selectionStart, selectionStart + selectionLength);
}

export function setEditorSelection(root, selectionStart, selectionEnd) {
    const current = state(root);
    const start = current.entry.model.getPositionAt(selectionStart);
    const end = current.entry.model.getPositionAt(selectionEnd);
    current.editor.setSelection(new current.monaco.Selection(start.lineNumber, start.column, end.lineNumber, end.column));
    current.editor.revealPositionInCenterIfOutsideViewport(end);
}

export function getEditorAnchor(root, position) {
    const current = state(root);
    const location = current.editor.getScrolledVisiblePosition(current.entry.model.getPositionAt(position));
    return location ? [location.left, location.top + location.height] : [72, 24];
}

export async function runSmokeChecks(root) {
    const current = state(root);
    const editor = current.editor;
    const model = current.entry.model;
    const original = model.getValue();
    await new Promise(resolve => setTimeout(resolve, 250));
    const firstLength = Math.min(1, original.length);
    editor.executeEdits('smoke', [{ range: new current.monaco.Range(1, 1, 1, firstLength + 1), text: 'X' }]);
    const selectionReplacement = model.getValue().startsWith(`X${original.slice(firstLength)}`);
    editor.trigger('smoke', 'undo');

    editor.setPosition({ lineNumber: 1, column: 1 });
    editor.trigger('smoke', 'type', { text: '{' });
    const bracketPairing = editor.getOption(current.monaco.editor.EditorOption.autoClosingBrackets) !== 'never';
    editor.trigger('smoke', 'undo');

    editor.setPosition({ lineNumber: 1, column: 1 });
    editor.trigger('smoke', 'type', { text: '\t' });
    const tabInsertion = model.getValue().startsWith('\t');
    editor.trigger('smoke', 'undo');

    let compositionChanges = 0;
    const compositionListener = model.onDidChangeContent(() => compositionChanges++);
    editor.trigger('smoke', 'type', { text: 'Ω' });
    compositionListener.dispose();
    const compositionCommittedOnce = compositionChanges === 1;
    editor.trigger('smoke', 'undo');
    await new Promise(resolve => setTimeout(resolve, 100));
    const renderedRows = root.querySelectorAll('.view-line').length;
    const rowLimit = Math.ceil(root.clientHeight / 20) + 64;
    return { inputPresent: !!root.querySelector('.monaco-editor'), selectionReplacement, bracketPairing,
        tabInsertion, compositionCommittedOnce, rowsBounded: renderedRows > 0 && renderedRows <= rowLimit, renderedRows };
}

export function disposeEditor(root) {
    const current = root.__novaMonaco;
    if (!current) return;
    clearTimeout(current.viewTimer);
    clearTimeout(current.hoverTimer);
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
