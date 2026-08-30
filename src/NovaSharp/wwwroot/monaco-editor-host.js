// The single interop surface between NovaSharp and the packaged Monaco editor.
//
// Monaco owns live text, undo/redo, selection, composition, viewport rendering, and token colours. This module owns
// the editor's lifetime and the replication stream: creating the editor after its container is mounted, keeping one
// text model per canonical document identity, retaining the host's URI spelling for protocol messages, turning
// Monaco's change events into ordered edit batches, and disposing
// everything deterministically.
//
// Nothing here waits for .NET on the typing path. A change event appends to a queue and returns; a pump sends what is
// queued with at most one call in flight, and whatever Monaco raises while that call is outstanding travels in the
// next one. Whole document text crosses this boundary only on open, on a NovaSharp-driven replacement, and on the
// snapshot a resynchronization asks for, including the one needed when an immutable model URI changes.
//
// Loaded as an ES module. The bundle resolves its worker URL from import.meta.url, so a classic script tag would
// silently break worker creation.

import { monaco } from './monaco/monaco.js';

/** How long a probe waits for a worker to report a load failure before treating it as healthy. */
const WORKER_PROBE_TIMEOUT_MS = 250;

/** Maximum edit batches retained while the previous interop send is in flight. */
const REPLICATION_CAPACITY = 256;

const EDITOR_FONT_FAMILIES = Object.freeze({
    'default': '"JetBrains Mono", "Cascadia Code", "Droid Sans Mono", monospace',
    'fast-mono': '"Fast Mono", "JetBrains Mono", "Cascadia Code", "Droid Sans Mono", monospace',
});

// Where an edit batch came from. Mirrors NovaSharp.Editing.EditOrigins.
//
// Only the user's origin is ever sent in phase 2: the one change NovaSharp itself makes to a model is a reload, and
// that is applied under suppression because both sides already hold its text. The field stays in the protocol for the
// .NET-originated edits later phases replicate, such as formatting.
const ORIGIN_USER = 'user';

/**
 * Turns a normalized keybinding such as `CtrlCmd+Shift+KeyS` into the value Monaco binds.
 *
 * Pure lookup, no grammar: NovaSharp's command registry already normalized the string into Monaco's own vocabulary,
 * so anything this cannot resolve is a binding that would silently do nothing and is reported instead.
 *
 * @returns {number|null} null when a token is not a Monaco modifier or key
 */
function resolveKeybinding(keybinding) {
    const parts = String(keybinding).split('+');
    let value = 0;

    for (const modifier of parts.slice(0, -1)) {
        const resolved = monaco.KeyMod[modifier];
        if (typeof resolved !== 'number') {
            return null;
        }

        value |= resolved;
    }

    const key = monaco.KeyCode[parts[parts.length - 1]];
    return typeof key === 'number' ? value | key : null;
}

/**
 * Wraps the worker factory installed by the bundle so a real dedicated worker can be told from a main-thread
 * fallback. A fallback fails the phase, so it has to be observable rather than assumed.
 */
const workerFactory = (() => {
    const environment = globalThis.MonacoEnvironment;
    const create = environment?.getWorker?.bind(environment);
    const state = { observed: false };

    if (create) {
        globalThis.MonacoEnvironment = {
            ...environment,
            getWorker(...args) {
                const worker = create(...args);
                state.observed ||= worker instanceof Worker;
                return worker;
            },
        };
    }

    return { state, create };
})();

/**
 * Starts one editor worker through the packaged factory to prove the worker script resolves and runs under this
 * page's actual origin, then terminates it.
 *
 * @returns {Promise<boolean>} false when the factory is missing, returns a non-Worker, or the worker fails to load
 */
async function probeWorker() {
    if (!workerFactory.create) {
        return false;
    }

    let worker;
    try {
        worker = workerFactory.create('novasharp', 'editorWorkerService');
    } catch {
        return false;
    }

    if (!(worker instanceof Worker)) {
        return false;
    }

    try {
        return await new Promise(resolve => {
            const timer = setTimeout(() => resolve(true), WORKER_PROBE_TIMEOUT_MS);
            const settle = value => {
                clearTimeout(timer);
                resolve(value);
            };

            worker.addEventListener('error', () => settle(false), { once: true });
            worker.addEventListener('message', () => settle(true), { once: true });
        });
    } finally {
        worker.terminate();
    }
}

/** Reads the packaged Monaco version from the generated asset manifest. Cached after the first read. */
let monacoVersion;
async function readMonacoVersion() {
    if (monacoVersion !== undefined) {
        return monacoVersion;
    }

    try {
        const response = await fetch(new URL('./monaco/asset-manifest.json', import.meta.url));
        const manifest = await response.json();
        monacoVersion = manifest.monacoEditorVersion ?? 'unknown';
    } catch {
        monacoVersion = 'unknown';
    }

    return monacoVersion;
}

/**
 * Counts resources this page loaded from another origin. Every editor asset is packaged locally, so this must stay
 * at zero; a non-zero count means something reached the network at runtime.
 */
function countExternalRequests() {
    if (typeof performance?.getEntriesByType !== 'function') {
        return 0;
    }

    const origin = globalThis.location?.origin;
    return performance.getEntriesByType('resource').filter(entry => {
        let url;
        try {
            url = new URL(entry.name, globalThis.location?.href);
        } catch {
            return false;
        }

        if (url.protocol !== 'http:' && url.protocol !== 'https:') {
            return false;
        }

        return url.origin !== origin;
    }).length;
}

/**
 * Models are keyed by Monaco's normalized spelling of the host canonical URI and reference counted, so a document
 * shown in more than one editor is one model with shared text and undo history. The host spelling remains on the
 * document object for protocol messages. Phase 2 shows one at a time; the leases are what make split views in phase 5
 * a change of caller rather than a change of ownership.
 */
const leasesByUri = new Map();

function acquireModel(uriString, languageId, text, lineEnding) {
    const uri = monaco.Uri.parse(uriString);
    const key = uri.toString();

    const tracked = leasesByUri.get(key);
    if (tracked) {
        tracked.leases += 1;
        return tracked.model;
    }

    // An existing model for this URI is the same document, including any edits the user has not saved. Adopt it
    // rather than overwriting live text with what is currently on disk; reconciling the two is the reload command.
    let model = monaco.editor.getModel(uri);
    if (!model) {
        model = monaco.editor.createModel(text, languageId, uri);
        model.setEOL(toEndOfLineSequence(lineEnding));
    }

    leasesByUri.set(key, { model, leases: 1 });
    return model;
}

function releaseModel(model) {
    if (!model) {
        return;
    }

    const key = model.uri.toString();
    const entry = leasesByUri.get(key);
    if (!entry) {
        return;
    }

    entry.leases -= 1;
    if (entry.leases > 0) {
        return;
    }

    leasesByUri.delete(key);
    if (!model.isDisposed()) {
        model.dispose();
    }
}

/** Monaco represents a line feed or a carriage-return pair and nothing else; NovaSharp converts anything else. */
function toEndOfLineSequence(lineEnding) {
    return lineEnding === '\r\n'
        ? monaco.editor.EndOfLineSequence.CRLF
        : monaco.editor.EndOfLineSequence.LF;
}

/** Reads both of a model's version counters together, so they always describe the same state. */
function readSequence(model) {
    return model
        ? { sequence: model.getVersionId(), alternativeSequence: model.getAlternativeVersionId() }
        : { sequence: 0, alternativeSequence: 0 };
}

/**
 * Creates one editor inside an already-mounted, empty container and returns its handle.
 *
 * The handle's members are closures rather than prototype methods, so the object carries no `this` dependency across
 * the interop boundary.
 *
 * @param {HTMLElement} container the mounted, empty element the editor is created in
 * @param {object} bridge the .NET object edits and commands are sent to
 * @returns {object} the editor handle
 */
export function createEditor(container, bridge) {
    if (!(container instanceof HTMLElement)) {
        throw new TypeError('An editor container element is required.');
    }

    let editorFontFamily = EDITOR_FONT_FAMILIES.default;
    const createCodeEditor = host => monaco.editor.create(host, {
        // Layout is driven by the ResizeObserver below, so the editor is not polling on a timer.
        automaticLayout: false,
        theme: 'vs-dark',
        model: null,
        readOnly: false,
        ariaLabel: 'C# editor',
        accessibilitySupport: 'auto',
        fontFamily: editorFontFamily,
        fontSize: 14,
        lineHeight: 22,
        minimap: { enabled: false },
        renderWhitespace: 'selection',
        scrollbar: { horizontal: 'hidden' },
        scrollBeyondLastLine: false,
        tabSize: 4,
        wordBasedSuggestions: 'off',
    });
    let editor = createCodeEditor(container);

    let observer = new ResizeObserver(() => editor.layout());
    observer.observe(container);

    let currentModel = null;
    let currentDocument = null;
    const documents = new Map();
    let disposed = false;
    let workerVerified;
    let registeredCommands = [];
    let registeredCommandDescriptors = [];
    const languageContexts = new Map();
    const languageRegistrations = [];
    let languageRequestSequence = 0;
    let languageRequestCount = 0;
    const languageRequestLatencies = [];
    const secondaryViews = new Map();
    let diffEditor = null;
    let diffObserver = null;
    let originalModel = null;
    let comparisonSource = null;

    let maximumQueueDepth = 0;
    let overflowCount = 0;

    const ensureLive = () => {
        if (disposed) {
            throw new Error('This editor has been disposed.');
        }
    };

    const semanticLegend = {
        tokenTypes: ['namespace', 'class', 'struct', 'interface', 'enum', 'typeParameter', 'method', 'property', 'event', 'field', 'parameter', 'variable'],
        tokenModifiers: ['declaration', 'static', 'readonly'],
    };
    const completionKinds = {
        method: monaco.languages.CompletionItemKind.Method,
        function: monaco.languages.CompletionItemKind.Function,
        property: monaco.languages.CompletionItemKind.Property,
        variable: monaco.languages.CompletionItemKind.Variable,
        constant: monaco.languages.CompletionItemKind.Constant,
        field: monaco.languages.CompletionItemKind.Field,
        event: monaco.languages.CompletionItemKind.Event,
        class: monaco.languages.CompletionItemKind.Class,
        struct: monaco.languages.CompletionItemKind.Struct,
        interface: monaco.languages.CompletionItemKind.Interface,
        enum: monaco.languages.CompletionItemKind.Enum,
        enumMember: monaco.languages.CompletionItemKind.EnumMember,
        typeParameter: monaco.languages.CompletionItemKind.TypeParameter,
        module: monaco.languages.CompletionItemKind.Module,
        keyword: monaco.languages.CompletionItemKind.Keyword,
        snippet: monaco.languages.CompletionItemKind.Snippet,
        text: monaco.languages.CompletionItemKind.Text,
    };

    function languageRequest(model, position, options = {}) {
        const context = languageContexts.get(model.uri.toString());
        if (!context?.available || model.getLanguageId() !== 'csharp' || (options.suggestion && !context.suggestionsEnabled)) return null;
        return {
            requestId: `language-${++languageRequestSequence}`,
            documentUri: context.documentUri,
            projectContextId: context.projectContextId,
            sourceVersion: context.sourceVersion,
            sequence: model.getVersionId(),
            position: model.getOffsetAt(position),
            rangeStart: options.range ? model.getOffsetAt(options.range.getStartPosition()) : null,
            rangeEnd: options.range ? model.getOffsetAt(options.range.getEndPosition()) : null,
            triggerCharacter: options.triggerCharacter ?? null,
            isExplicit: options.isExplicit ?? false,
            priority: options.priority ?? 'foreground',
            suggestionsEnabled: context.suggestionsEnabled,
        };
    }

    function requestStillCurrent(model, request) {
        const context = languageContexts.get(model.uri.toString());
        return !disposed
            && !model.isDisposed()
            && context?.available
            && context.projectContextId === request.projectContextId
            && context.sourceVersion === request.sourceVersion
            && context.suggestionsEnabled === request.suggestionsEnabled
            && model.getVersionId() === request.sequence;
    }

    async function invokeLanguage(model, request, method, payload, cancellationToken) {
        if (!request || cancellationToken?.isCancellationRequested) return null;
        const started = performance.now();
        const cancellation = cancellationToken?.onCancellationRequested(() => {
            void bridge.invokeMethodAsync('CancelLanguageRequest', request.requestId).catch(() => {});
        });
        try {
            const result = await bridge.invokeMethodAsync(method, payload ?? request);
            return result
                && result.requestId === request.requestId
                && result.sourceVersion === request.sourceVersion
                && result.sequence === request.sequence
                && requestStillCurrent(model, request)
                ? result
                : null;
        } catch {
            return null;
        } finally {
            cancellation?.dispose();
            languageRequestCount += 1;
            languageRequestLatencies.push(performance.now() - started);
            if (languageRequestLatencies.length > 256) languageRequestLatencies.shift();
        }
    }

    function rangeFromOffsets(model, start, end) {
        return monaco.Range.fromPositions(model.getPositionAt(start), model.getPositionAt(end));
    }

    function toTextEdit(model, edit) {
        return { range: rangeFromOffsets(model, edit.start, edit.end), text: edit.text };
    }

    languageRegistrations.push(monaco.languages.setLanguageConfiguration('csharp', {
        comments: { lineComment: '//', blockComment: ['/*', '*/'] },
        brackets: [['{', '}'], ['[', ']'], ['(', ')']],
        autoClosingPairs: [
            { open: '{', close: '}' }, { open: '[', close: ']' }, { open: '(', close: ')' },
            { open: '"', close: '"', notIn: ['string', 'comment'] },
            { open: "'", close: "'", notIn: ['string', 'comment'] },
        ],
        surroundingPairs: [['{', '}'], ['[', ']'], ['(', ')'], ['"', '"'], ["'", "'"]],
        indentationRules: {
            increaseIndentPattern: /(?:\{|\[|\()\s*$/,
            decreaseIndentPattern: /^\s*(?:\}|\]|\))/,
        },
    }));

    languageRegistrations.push(monaco.languages.registerCompletionItemProvider('csharp', {
        triggerCharacters: ['.', ' ', '(', '[', '<', ':', '#'],
        async provideCompletionItems(model, position, context, token) {
            const request = languageRequest(model, position, {
                suggestion: true,
                triggerCharacter: context.triggerCharacter,
                isExplicit: context.triggerKind === monaco.languages.CompletionTriggerKind.Invoke,
            });
            const result = await invokeLanguage(model, request, 'GetCompletionsAsync', request, token);
            if (!result) return { suggestions: [] };
            const word = model.getWordUntilPosition(position);
            const range = new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
            return {
                incomplete: result.isIncomplete,
                suggestions: result.items.map(item => ({
                    label: item.label,
                    kind: completionKinds[item.kind] ?? monaco.languages.CompletionItemKind.Text,
                    detail: item.detail,
                    sortText: item.sortText,
                    filterText: item.filterText,
                    insertText: item.insertText,
                    preselect: item.preselect,
                    insertTextRules: item.isSnippet ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
                    commitCharacters: item.commitCharacters,
                    additionalTextEdits: item.additionalTextEdits.map(edit => toTextEdit(model, edit)),
                    range,
                    _nova: item.isSnippet ? null : { request, itemId: item.id },
                })),
            };
        },
        async resolveCompletionItem(item, token) {
            if (!item._nova) return item;
            const model = monaco.editor.getModel(monaco.Uri.parse(item._nova.request.documentUri));
            if (!model) return item;
            const details = await invokeLanguage(model, item._nova.request, 'ResolveCompletionAsync', {
                request: item._nova.request,
                itemId: item._nova.itemId,
                commitCharacter: null,
            }, token);
            if (!details) return item;
            item.detail = details.detail ?? item.detail;
            item.documentation = details.documentation ? { value: details.documentation } : undefined;
            item.insertText = details.insertText;
            if (details.textEdit) item.range = rangeFromOffsets(model, details.textEdit.start, details.textEdit.end);
            item.additionalTextEdits = details.additionalTextEdits.map(edit => toTextEdit(model, edit));
            return item;
        },
    }));

    languageRegistrations.push(monaco.languages.registerSignatureHelpProvider('csharp', {
        signatureHelpTriggerCharacters: ['(', ','],
        signatureHelpRetriggerCharacters: [')'],
        async provideSignatureHelp(model, position, token, context) {
            const request = languageRequest(model, position, { triggerCharacter: context.triggerCharacter });
            const result = await invokeLanguage(model, request, 'GetSignatureHelpAsync', request, token);
            if (!result) return null;
            return {
                value: {
                    signatures: result.signatures.map(signature => ({
                        label: signature.label,
                        documentation: signature.documentation,
                        parameters: signature.parameters,
                    })),
                    activeSignature: result.activeSignature,
                    activeParameter: result.activeParameter,
                },
                dispose() {},
            };
        },
    }));

    languageRegistrations.push(monaco.languages.registerHoverProvider('csharp', {
        async provideHover(model, position, token) {
            const request = languageRequest(model, position);
            const result = await invokeLanguage(model, request, 'GetHoverAsync', request, token);
            return result ? {
                range: rangeFromOffsets(model, result.start, result.end),
                contents: [
                    { value: `\`\`\`csharp\n${result.signature}\n\`\`\`` },
                    ...(result.documentation ? [{ value: result.documentation }] : []),
                    ...(result.origin ? [{ value: `_${result.origin}_` }] : []),
                ],
            } : null;
        },
    }));

    const format = async (model, range, token) => {
        const request = languageRequest(model, range.getStartPosition(), { range, priority: 'background' });
        const result = await invokeLanguage(model, request, 'FormatAsync', request, token);
        return result?.edits.map(edit => toTextEdit(model, edit)) ?? [];
    };
    languageRegistrations.push(monaco.languages.registerDocumentFormattingEditProvider('csharp', {
        provideDocumentFormattingEdits(model, _options, token) {
            return format(model, model.getFullModelRange(), token);
        },
    }));
    languageRegistrations.push(monaco.languages.registerDocumentRangeFormattingEditProvider('csharp', {
        provideDocumentRangeFormattingEdits(model, range, _options, token) {
            return format(model, range, token);
        },
    }));

    const semanticTokensForRange = async (model, range, token) => {
        const request = languageRequest(model, range.getStartPosition(), { range });
        const result = await invokeLanguage(model, request, 'GetSemanticTokensAsync', request, token);
        if (!result) return { data: new Uint32Array(), resultId: null };
        const data = [];
        let lastLine = 0;
        let lastCharacter = 0;
        for (const semantic of [...result.tokens].sort((left, right) => left.start - right.start)) {
            const position = model.getPositionAt(semantic.start);
            const line = position.lineNumber - 1;
            const character = position.column - 1;
            data.push(
                line - lastLine,
                line === lastLine ? character - lastCharacter : character,
                semantic.length,
                semanticLegend.tokenTypes.indexOf(semantic.type),
                semantic.modifiers.reduce((bits, modifier) => {
                    const index = semanticLegend.tokenModifiers.indexOf(modifier);
                    return index < 0 ? bits : bits | (1 << index);
                }, 0));
            lastLine = line;
            lastCharacter = character;
        }
        return { data: new Uint32Array(data), resultId: result.resultId };
    };
    languageRegistrations.push(monaco.languages.registerDocumentSemanticTokensProvider('csharp', {
        getLegend() { return semanticLegend; },
        provideDocumentSemanticTokens(model, _lastResultId, token) {
            return semanticTokensForRange(model, model.getFullModelRange(), token);
        },
        releaseDocumentSemanticTokens() {},
    }));
    languageRegistrations.push(monaco.languages.registerDocumentRangeSemanticTokensProvider('csharp', {
        getLegend() { return semanticLegend; },
        provideDocumentRangeSemanticTokens(model, range, token) {
            return semanticTokensForRange(model, range, token);
        },
    }));

    function scheduleResync(document) {
        document.queued = [];
        document.resyncing = true;
        document.resyncRequestPending = true;
        if (!document.sending) {
            void requestResync(document);
        }
    }

    /** Requests a snapshot without overlapping the edit send that may already be in flight. */
    async function requestResync(document) {
        if (document.sending || !document.resyncRequestPending || disposed) {
            return;
        }

        document.sending = true;
        document.resyncRequestPending = false;
        try {
            await bridge.invokeMethodAsync('RequestResync', document.canonicalUri);
        } catch {
            // The page is going away. A later open reconstructs the shadow from the file and a fresh model.
        } finally {
            document.sending = false;
        }
    }

    /** Sends what is queued, keeping at most one interop call in flight. */
    async function flush(document) {
        if (document.sending || document.queued.length === 0 || disposed || document.resyncing) {
            return;
        }

        document.sending = true;
        const batches = document.queued;
        document.queued = [];

        try {
            const accepted = await bridge.invokeMethodAsync('ReplicateEdits', batches);
            if (accepted === false) {
                // .NET dropped a batch and is fetching a snapshot instead. Anything still queued describes edits that
                // the snapshot will already contain, so sending it would only ask for a second recovery.
                document.queued = [];
                document.resyncing = true;
            }
        } catch {
            // The page is being torn down, or .NET is gone. Neither is worth interrupting the user's typing over, and
            // the shadow is rebuilt from a snapshot when the document is next opened.
            document.queued = [];
        } finally {
            document.sending = false;
            if (document.resyncRequestPending) {
                void requestResync(document);
            } else if (document.queued.length > 0 && !document.resyncing) {
                void flush(document);
            }
        }
    }

    function onContentChanged(document, event) {
        if (document.suppressed || disposed) {
            return;
        }

        if (event.isEolChange) {
            // A line-ending change rewrites every line in the model at once and no range edit describes it. The queue
            // is dropped and .NET rebuilds from a snapshot rather than being sent offsets into text it does not have.
            document.sentSequence = document.model.getVersionId();
            scheduleResync(document);
            return;
        }

        if (document.resyncing) {
            return;
        }

        // Monaco reports changes from the end of the document backwards so they can be applied without shifting each
        // other's offsets. NovaSharp's protocol is ascending, so they are reversed here, once, at the source.
        const edits = [];
        for (let i = event.changes.length - 1; i >= 0; i--) {
            const change = event.changes[i];
            edits.push({
                start: change.rangeOffset,
                end: change.rangeOffset + change.rangeLength,
                text: change.text,
            });
        }

        const batch = {
            documentUri: document.canonicalUri,
            baseSequence: document.sentSequence,
            resultSequence: event.versionId,
            alternativeSequence: document.model.getAlternativeVersionId(),
            origin: ORIGIN_USER,
            edits,
        };

        document.sentSequence = event.versionId;
        if (document.queued.length === REPLICATION_CAPACITY) {
            overflowCount += 1;
            scheduleResync(document);
            return;
        }

        document.queued.push(batch);
        maximumQueueDepth = Math.max(maximumQueueDepth, document.queued.length);
        void flush(document);
    }

    /** Stops any open comparison and gives the model back to the editor. */
    function stopCompare() {
        if (!diffEditor) {
            return;
        }

        diffObserver?.disconnect();
        diffObserver = null;

        // The model is detached before the view is disposed, so disposing the diff editor cannot take the live
        // document with it.
        diffEditor.setModel(null);
        diffEditor.dispose();
        diffEditor = null;

        originalModel?.dispose();
        originalModel = null;

        if (comparisonSource && !disposed) {
            comparisonSource.editor.setModel(comparisonSource.document.model);
            comparisonSource.editor.focus();
        }
        comparisonSource = null;
    }

    function portableViewState() {
        if (!currentDocument || !currentModel) {
            return null;
        }

        const selection = editor.getSelection();
        const position = editor.getPosition();
        return {
            lineNumber: position?.lineNumber ?? 1,
            column: position?.column ?? 1,
            selectionStartLineNumber: selection?.selectionStartLineNumber ?? position?.lineNumber ?? 1,
            selectionStartColumn: selection?.selectionStartColumn ?? position?.column ?? 1,
            positionLineNumber: selection?.positionLineNumber ?? position?.lineNumber ?? 1,
            positionColumn: selection?.positionColumn ?? position?.column ?? 1,
            scrollTop: editor.getScrollTop(),
            scrollLeft: editor.getScrollLeft(),
        };
    }

    function captureCurrentView() {
        if (!currentDocument) {
            return;
        }

        currentDocument.monacoViewState = editor.saveViewState();
        currentDocument.portableViewState = portableViewState();
    }

    function clampPosition(model, lineNumber, column) {
        const line = Math.max(1, Math.min(Number(lineNumber) || 1, model.getLineCount()));
        const maximumColumn = model.getLineMaxColumn(line);
        return { lineNumber: line, column: Math.max(1, Math.min(Number(column) || 1, maximumColumn)) };
    }

    function restorePortableView(document, state) {
        if (!state) {
            return;
        }

        const start = clampPosition(document.model, state.selectionStartLineNumber, state.selectionStartColumn);
        const end = clampPosition(document.model, state.positionLineNumber, state.positionColumn);
        editor.setSelection({
            selectionStartLineNumber: start.lineNumber,
            selectionStartColumn: start.column,
            positionLineNumber: end.lineNumber,
            positionColumn: end.column,
        });
        editor.setScrollPosition({
            scrollTop: Math.max(0, Number(state.scrollTop) || 0),
            scrollLeft: Math.max(0, Number(state.scrollLeft) || 0),
        });
    }

    function attach(document, restoredViewState = null) {
        if (currentDocument !== document) {
            captureCurrentView();
            currentDocument = document;
            currentModel = document.model;
            editor.setModel(document.model);
        }

        editor.updateOptions({ readOnly: document.readOnly });
        if (restoredViewState) {
            restorePortableView(document, restoredViewState);
        } else if (document.monacoViewState) {
            editor.restoreViewState(document.monacoViewState);
        }
    }

    function portableStateFor(editorView) {
        if (!editorView.currentDocument) return null;
        const selection = editorView.editor.getSelection();
        const position = editorView.editor.getPosition();
        return {
            lineNumber: position?.lineNumber ?? 1,
            column: position?.column ?? 1,
            selectionStartLineNumber: selection?.selectionStartLineNumber ?? position?.lineNumber ?? 1,
            selectionStartColumn: selection?.selectionStartColumn ?? position?.column ?? 1,
            positionLineNumber: selection?.positionLineNumber ?? position?.lineNumber ?? 1,
            positionColumn: selection?.positionColumn ?? position?.column ?? 1,
            scrollTop: editorView.editor.getScrollTop(),
            scrollLeft: editorView.editor.getScrollLeft(),
        };
    }

    function captureSecondaryView(editorView) {
        if (!editorView.currentDocument) return;
        const key = editorView.currentDocument.model.uri.toString();
        editorView.monacoStates.set(key, editorView.editor.saveViewState());
        editorView.portableStates.set(key, portableStateFor(editorView));
    }

    function restoreStateFor(editorView, document, state) {
        if (!state) return;
        const start = clampPosition(document.model, state.selectionStartLineNumber, state.selectionStartColumn);
        const end = clampPosition(document.model, state.positionLineNumber, state.positionColumn);
        editorView.editor.setSelection({
            selectionStartLineNumber: start.lineNumber,
            selectionStartColumn: start.column,
            positionLineNumber: end.lineNumber,
            positionColumn: end.column,
        });
        editorView.editor.setScrollPosition({
            scrollTop: Math.max(0, Number(state.scrollTop) || 0),
            scrollLeft: Math.max(0, Number(state.scrollLeft) || 0),
        });
    }

    function attachSecondaryView(editorView, document, restoredViewState = null, focus = true) {
        if (editorView.currentDocument !== document) {
            captureSecondaryView(editorView);
            editorView.currentDocument = document;
            editorView.editor.setModel(document.model);
        }
        editorView.editor.updateOptions({ readOnly: document.readOnly });
        const key = document.model.uri.toString();
        if (restoredViewState) restoreStateFor(editorView, document, restoredViewState);
        else if (editorView.monacoStates.has(key)) editorView.editor.restoreViewState(editorView.monacoStates.get(key));
        if (focus) editorView.editor.focus();
    }

    function addActions(targetEditor, descriptors) {
        const registrations = [];
        const unresolved = [];
        for (const descriptor of descriptors ?? []) {
            const keybindings = [];
            for (const keybinding of descriptor.keybindings ?? []) {
                const resolved = resolveKeybinding(keybinding);
                if (resolved === null) {
                    unresolved.push(`${descriptor.id}: ${keybinding}`);
                    continue;
                }
                keybindings.push(resolved);
            }
            registrations.push(targetEditor.addAction({
                id: descriptor.id,
                label: descriptor.title,
                keybindings,
                contextMenuGroupId: descriptor.showInPalette ? 'navigation' : undefined,
                run: () => void bridge.invokeMethodAsync('InvokeCommandAsync', descriptor.id),
            }));
        }
        return { registrations, unresolved };
    }

    function ensureDocument(uriString, languageId, text, lineEnding, readOnly) {
        const key = monaco.Uri.parse(uriString).toString();
        let document = documents.get(key);
        if (!document) {
            const model = acquireModel(uriString, languageId, text, lineEnding);
            document = {
                canonicalUri: uriString,
                model,
                readOnly: readOnly === true,
                sentSequence: model.getVersionId(),
                queued: [],
                sending: false,
                resyncing: false,
                resyncRequestPending: false,
                suppressed: false,
                monacoViewState: null,
                portableViewState: null,
                contentSubscription: null,
            };
            document.contentSubscription = model.onDidChangeContent(event => onContentChanged(document, event));
            documents.set(key, document);
        }
        document.canonicalUri = uriString;
        document.readOnly = readOnly === true;
        return document;
    }

    const handle = {
        /** Creates another editor instance over the same URI-keyed document map. */
        createView(viewId, viewContainer) {
            ensureLive();
            if (!viewId) return null;
            if (viewId === 'main' || secondaryViews.has(viewId)) return handle.remountView(viewId, viewContainer);
            if (!(viewContainer instanceof HTMLElement)) throw new TypeError('An editor view container is required.');
            const viewEditor = createCodeEditor(viewContainer);
            const view = {
                editor: viewEditor,
                observer: new ResizeObserver(() => viewEditor.layout()),
                currentDocument: null,
                monacoStates: new Map(),
                portableStates: new Map(),
                registrations: [],
                container: viewContainer,
            };
            view.observer.observe(viewContainer);
            view.registrations = addActions(viewEditor, registeredCommandDescriptors).registrations;
            secondaryViews.set(viewId, view);
            return null;
        },

        /** Recreates only an editor instance when its split-tree leaf receives a new Blazor container. */
        remountView(viewId, viewContainer) {
            ensureLive();
            if (!(viewContainer instanceof HTMLElement)) throw new TypeError('An editor view container is required.');
            if (viewId === 'main') {
                if (container === viewContainer) return null;
                const document = currentDocument;
                captureCurrentView();
                editor.setModel(null);
                for (const registration of registeredCommands) registration.dispose();
                observer.disconnect();
                editor.dispose();
                editor = createCodeEditor(viewContainer);
                observer = new ResizeObserver(() => editor.layout());
                observer.observe(viewContainer);
                registeredCommands = addActions(editor, registeredCommandDescriptors).registrations;
                container = viewContainer;
                currentDocument = null;
                currentModel = null;
                if (document) attach(document);
                return null;
            }
            const view = secondaryViews.get(viewId);
            if (!view) throw new Error(`Editor view is not mounted: ${viewId}`);
            if (view.container === viewContainer) return null;
            const document = view.currentDocument;
            captureSecondaryView(view);
            view.editor.setModel(null);
            for (const registration of view.registrations) registration.dispose();
            view.observer.disconnect();
            view.editor.dispose();
            view.editor = createCodeEditor(viewContainer);
            view.observer = new ResizeObserver(() => view.editor.layout());
            view.observer.observe(viewContainer);
            view.container = viewContainer;
            view.registrations = addActions(view.editor, registeredCommandDescriptors).registrations;
            view.currentDocument = null;
            if (document) attachSecondaryView(view, document, null, false);
            return null;
        },

        /** Attaches one shared model to a named view. */
        switchViewDocument(viewId, uriString, restoredViewState, focus = true) {
            ensureLive();
            const document = documents.get(monaco.Uri.parse(uriString).toString());
            if (!document) throw new Error(`Document is not open: ${uriString}`);
            if (viewId === 'main') {
                attach(document, restoredViewState);
                if (focus) editor.focus();
            } else {
                const view = secondaryViews.get(viewId);
                if (!view) throw new Error(`Editor view is not mounted: ${viewId}`);
                attachSecondaryView(view, document, restoredViewState, focus);
            }
            return readSequence(document.model);
        },

        /** Detaches one view without changing document/model ownership. */
        clearView(viewId) {
            ensureLive();
            if (viewId === 'main') return handle.clearDocument();
            const view = secondaryViews.get(viewId);
            if (!view) return null;
            captureSecondaryView(view);
            view.editor.setModel(null);
            view.currentDocument = null;
            return null;
        },

        /** Captures portable state from a named view. */
        viewStateForView(viewId, uriString) {
            ensureLive();
            if (viewId === 'main') return handle.viewState(uriString);
            const view = secondaryViews.get(viewId);
            if (!view) return null;
            const key = monaco.Uri.parse(uriString).toString();
            if (view.currentDocument?.model.uri.toString() === key) captureSecondaryView(view);
            return view.portableStates.get(key) ?? null;
        },

        /** Releases one secondary editor instance while retaining all document models. */
        removeView(viewId) {
            ensureLive();
            const view = secondaryViews.get(viewId);
            if (!view) return null;
            captureSecondaryView(view);
            for (const registration of view.registrations) registration.dispose();
            view.observer.disconnect();
            view.editor.setModel(null);
            view.editor.dispose();
            view.monacoStates.clear();
            view.portableStates.clear();
            secondaryViews.delete(viewId);
            return null;
        },

        /**
         * Replaces the editor's actions with the ones NovaSharp's command registry describes.
         *
         * The registry is authoritative: the editor keeps no list of its own, so a command added, retitled, or rebound
         * in .NET reaches the keybinding and Monaco's palette without a second edit here. Every binding that cannot be
         * resolved is returned rather than dropped, so a shortcut that would silently do nothing fails a test instead.
         *
         * @param {Array<{id: string, title: string, keybindings: string[], showInPalette: boolean}>} descriptors
         * @returns {string[]} the bindings that could not be resolved
         */
        registerCommands(descriptors) {
            ensureLive();

            for (const registration of registeredCommands) {
                registration.dispose();
            }

            for (const view of secondaryViews.values()) {
                for (const registration of view.registrations) registration.dispose();
                view.registrations = [];
            }
            registeredCommandDescriptors = descriptors ?? [];
            const primary = addActions(editor, registeredCommandDescriptors);
            registeredCommands = primary.registrations;
            const unresolved = [...primary.unresolved];
            for (const view of secondaryViews.values()) {
                const result = addActions(view.editor, registeredCommandDescriptors);
                view.registrations = result.registrations;
                unresolved.push(...result.unresolved);
            }
            return [...new Set(unresolved)];
        },

        /**
         * Shows the file on disk beside the editor's text.
         *
         * The live model becomes the diff's modified side rather than a copy of it, so the comparison shows the user's
         * unsaved text, stays editable, and keeps replicating while it is open. The main editor releases the model for
         * the duration; nothing about the document's identity or its undo history changes.
         */
        beginCompare(diffContainer, originalText) {
            return handle.beginCompareInView('main', diffContainer, originalText);
        },

        /** Shows a comparison for the document attached to a named editor view. */
        beginCompareInView(viewId, diffContainer, originalText) {
            ensureLive();
            if (!(diffContainer instanceof HTMLElement)) {
                throw new TypeError('A container element is required to compare in.');
            }

            const source = viewId === 'main'
                ? { editor, document: currentDocument }
                : secondaryViews.has(viewId)
                    ? { editor: secondaryViews.get(viewId).editor, document: secondaryViews.get(viewId).currentDocument }
                    : null;
            const model = source?.document?.model;
            if (!source || !model) {
                throw new Error('No document is open.');
            }

            stopCompare();

            // No URI: an anonymous model cannot collide with the document registry, and it is disposed with the view.
            originalModel = monaco.editor.createModel(originalText, model.getLanguageId());
            originalModel.setEOL(model.getEOL() === '\r\n'
                ? monaco.editor.EndOfLineSequence.CRLF
                : monaco.editor.EndOfLineSequence.LF);

            diffEditor = monaco.editor.createDiffEditor(diffContainer, {
                automaticLayout: false,
                theme: 'vs-dark',
                originalEditable: false,
                readOnly: false,
                renderSideBySide: true,
                ariaLabel: 'Comparison with the file on disk',
                fontFamily: editorFontFamily,
                scrollbar: { horizontal: 'hidden' },
            });

            source.editor.setModel(null);
            comparisonSource = source;
            diffEditor.setModel({ original: originalModel, modified: model });

            diffObserver = new ResizeObserver(() => diffEditor?.layout());
            diffObserver.observe(diffContainer);
            return null;
        },

        /** Stops comparing and gives the model back to the editor. */
        endCompare() {
            stopCompare();
            return null;
        },

        /** Whether a comparison is open. */
        isComparing() {
            return diffEditor !== null;
        },

        /**
         * Shows a document. Its text crosses the boundary here; Monaco owns it afterwards.
         *
         * Monaco renders nothing until a model is attached, so this is also the point the editor first appears. Focus
         * moves with it: opening a file is a request to edit it.
         */
        openDocument(uriString, languageId, text, lineEnding, readOnly) {
            ensureLive();
            const document = ensureDocument(uriString, languageId, text, lineEnding, readOnly);
            attach(document);
            editor.focus();
            return readSequence(document.model);
        },

        /** Opens text streamed as UTF-8 so a large initial document does not need a second JSON-sized interop buffer. */
        async openDocumentStream(uriString, languageId, textStream, lineEnding, readOnly) {
            const text = new TextDecoder().decode(await textStream.arrayBuffer());
            return handle.openDocument(uriString, languageId, text, lineEnding, readOnly);
        },

        /** Opens a model once and attaches it to the requested editor view. */
        async openDocumentStreamInView(viewId, uriString, languageId, textStream, lineEnding, readOnly, activate = true) {
            const text = new TextDecoder().decode(await textStream.arrayBuffer());
            ensureLive();
            const document = ensureDocument(uriString, languageId, text, lineEnding, readOnly);
            if (activate) handle.switchViewDocument(viewId, uriString, null, true);
            return readSequence(document.model);
        },

        /** Attaches an existing model without copying its text or recreating its undo history. */
        switchDocument(uriString, restoredViewState) {
            ensureLive();
            const document = documents.get(monaco.Uri.parse(uriString).toString());
            if (!document) {
                throw new Error(`Document is not open: ${uriString}`);
            }

            attach(document, restoredViewState);
            editor.focus();
            return readSequence(document.model);
        },

        /** Detaches the active view without releasing any document lease. */
        clearDocument() {
            ensureLive();
            stopCompare();
            captureCurrentView();
            editor.setModel(null);
            currentDocument = null;
            currentModel = null;
            return null;
        },

        /** Returns the portable cursor, selection, and scroll state for one view. */
        viewState(uriString) {
            ensureLive();
            const document = documents.get(monaco.Uri.parse(uriString).toString());
            if (!document) {
                return null;
            }

            if (document === currentDocument) {
                captureCurrentView();
            }

            return document.portableViewState;
        },

        /** Releases one model lease; all other open tabs and editor instances remain intact. */
        closeDocument(uriString) {
            ensureLive();
            const key = monaco.Uri.parse(uriString).toString();
            const document = documents.get(key);
            if (!document) {
                return null;
            }

            if (document === currentDocument) {
                stopCompare();
                editor.setModel(null);
                currentDocument = null;
                currentModel = null;
            }
            for (const view of secondaryViews.values()) {
                if (view.currentDocument !== document) continue;
                captureSecondaryView(view);
                view.editor.setModel(null);
                view.currentDocument = null;
                view.monacoStates.delete(key);
                view.portableStates.delete(key);
            }

            document.queued = [];
            document.contentSubscription?.dispose();
            documents.delete(key);
            releaseModel(document.model);
            return null;
        },

        /** Recreates a model under a new immutable Monaco URI while preserving live text and view state. */
        relocateDocument(oldUriString, newUriString, languageId) {
            ensureLive();
            const oldKey = monaco.Uri.parse(oldUriString).toString();
            const newKey = monaco.Uri.parse(newUriString).toString();
            const oldDocument = documents.get(oldKey);
            if (!oldDocument) {
                throw new Error(`Document is not open: ${oldUriString}`);
            }

            if (oldKey === newKey) {
                oldDocument.canonicalUri = newUriString;
                return {
                    text: oldDocument.model.getValue(),
                    ...readSequence(oldDocument.model),
                };
            }

            const wasCurrent = oldDocument === currentDocument;
            const secondaryAttachments = [...secondaryViews.values()].filter(view => view.currentDocument === oldDocument);
            for (const view of secondaryAttachments) captureSecondaryView(view);
            if (wasCurrent) {
                stopCompare();
            }
            captureCurrentView();
            const model = acquireModel(newUriString, languageId, oldDocument.model.getValue(), oldDocument.model.getEOL());
            const document = {
                ...oldDocument,
                canonicalUri: newUriString,
                model,
                sentSequence: model.getVersionId(),
                queued: [],
                sending: false,
                resyncing: false,
                resyncRequestPending: false,
                suppressed: false,
                contentSubscription: null,
            };
            document.contentSubscription = model.onDidChangeContent(event => onContentChanged(document, event));

            oldDocument.queued = [];
            oldDocument.contentSubscription?.dispose();
            documents.delete(oldKey);
            documents.set(newKey, document);
            if (wasCurrent) {
                currentDocument = null;
                currentModel = null;
                attach(document);
            }
            for (const view of secondaryAttachments) {
                const state = view.portableStates.get(oldKey) ?? null;
                view.monacoStates.delete(oldKey);
                view.portableStates.delete(oldKey);
                view.currentDocument = null;
                attachSecondaryView(view, document, state, false);
            }
            releaseModel(oldDocument.model);
            return { text: model.getValue(), ...readSequence(model) };
        },

        /**
         * Replaces the whole text of the open model, as a reload does.
         *
         * Applied as an edit operation between two undo stops, so the change is undoable and the editor keeps its
         * selection, folding, and scroll position. Replication is suppressed for the duration: the caller already has
         * the text it just supplied, and the returned sequence is what its shadow adopts, so sending edits describing
         * a state both sides already agree on would cost a round trip and buy nothing.
         */
        replaceDocument(uriOrText, textOrLineEnding, maybeLineEnding) {
            ensureLive();
            const targeted = maybeLineEnding !== undefined;
            const document = targeted
                ? documents.get(monaco.Uri.parse(uriOrText).toString())
                : currentDocument;
            const text = targeted ? textOrLineEnding : uriOrText;
            const lineEnding = targeted ? maybeLineEnding : textOrLineEnding;
            if (!document) {
                throw new Error('The document is not open.');
            }

            const model = document.model;
            document.suppressed = true;
            try {
                model.pushStackElement();
                model.setEOL(toEndOfLineSequence(lineEnding));
                model.pushEditOperations(
                    document === currentDocument ? editor.getSelections() ?? [] : [],
                    [{ range: model.getFullModelRange(), text, forceMoveMarkers: true }],
                    () => null);
                model.pushStackElement();
            } finally {
                document.suppressed = false;
                document.queued = [];
                document.resyncing = false;
                document.resyncRequestPending = false;
                document.sentSequence = model.getVersionId();
            }

            return readSequence(model);
        },

        /** Replaces text streamed as UTF-8; ordinary incremental edits never use this whole-document path. */
        async replaceDocumentStream(uriOrStream, streamOrLineEnding, maybeLineEnding) {
            const targeted = maybeLineEnding !== undefined;
            const stream = targeted ? streamOrLineEnding : uriOrStream;
            const text = new TextDecoder().decode(await stream.arrayBuffer());
            return targeted
                ? handle.replaceDocument(uriOrStream, text, maybeLineEnding)
                : handle.replaceDocument(text, streamOrLineEnding);
        },

        /** Reads the model's text and sequence together, for a resynchronization. */
        snapshot(uriString) {
            ensureLive();
            const document = uriString
                ? documents.get(monaco.Uri.parse(uriString).toString())
                : currentDocument;
            if (!document) {
                if (uriString) {
                    throw new Error(`Document is not open: ${uriString}`);
                }
                return { text: '', sequence: 0, alternativeSequence: 0 };
            }

            const model = document.model;

            // The queue is cleared with the same certainty the snapshot is taken: everything in it is text the
            // snapshot already contains.
            document.queued = [];
            document.resyncing = false;
            document.resyncRequestPending = false;
            document.sentSequence = model.getVersionId();

            return {
                text: model.getValue(),
                sequence: model.getVersionId(),
                alternativeSequence: model.getAlternativeVersionId(),
            };
        },

        /** Reads the model's sequence without its text, for a save barrier. */
        sequence(uriString) {
            ensureLive();
            const document = uriString
                ? documents.get(monaco.Uri.parse(uriString).toString())
                : currentDocument;
            if (uriString && !document) {
                throw new Error(`Document is not open: ${uriString}`);
            }
            return readSequence(document?.model);
        },

        /** Makes the editor refuse edits, for a file that cannot be written. */
        setReadOnly(uriOrReadOnly, maybeReadOnly) {
            ensureLive();
            const targeted = maybeReadOnly !== undefined;
            const document = targeted
                ? documents.get(monaco.Uri.parse(uriOrReadOnly).toString())
                : currentDocument;
            if (!document) {
                return null;
            }

            document.readOnly = (targeted ? maybeReadOnly : uriOrReadOnly) === true;
            if (document === currentDocument) {
                editor.updateOptions({ readOnly: document.readOnly });
            }
            for (const view of secondaryViews.values()) {
                if (view.currentDocument === document) view.editor.updateOptions({ readOnly: document.readOnly });
            }
            return null;
        },

        /** Applies one allow-listed local font to the editor and any active comparison. */
        async setEditorFont(fontId) {
            ensureLive();
            const family = EDITOR_FONT_FAMILIES[fontId];
            if (!family) {
                throw new RangeError(`Unknown editor font: ${fontId}`);
            }

            if (fontId === 'fast-mono') {
                await document.fonts?.load('14px "Fast Mono"');
                ensureLive();
            }

            editorFontFamily = family;
            editor.updateOptions({ fontFamily: family });
            for (const view of secondaryViews.values()) view.editor.updateOptions({ fontFamily: family });
            diffEditor?.updateOptions({ fontFamily: family });
            monaco.editor.remeasureFonts();
            return null;
        },

        setLanguageContext(uriString, projectContextId, sourceVersion, available, suggestionsEnabled) {
            ensureLive();
            languageContexts.set(monaco.Uri.parse(uriString).toString(), {
                documentUri: uriString,
                projectContextId: projectContextId ?? null,
                sourceVersion,
                available: Boolean(available),
                suggestionsEnabled: Boolean(suggestionsEnabled),
            });
            return null;
        },

        /** Reports what the host observes about itself, so the phase gates can be asserted rather than claimed. */
        async runtimeInfo() {
            ensureLive();
            workerVerified ??= workerFactory.state.observed || await probeWorker();

            return {
                monacoVersion: await readMonacoVersion(),
                dedicatedWorker: workerVerified || workerFactory.state.observed,
                modelCount: monaco.editor.getModels().length,
                viewCount: 1 + secondaryViews.size,
                documentLength: currentModel?.getValueLength() ?? 0,
                externalRequestCount: countExternalRequests(),
                replicationCapacity: REPLICATION_CAPACITY,
                replicationQueueDepth: [...documents.values()].reduce((sum, document) => sum + document.queued.length, 0),
                replicationMaximumQueueDepth: maximumQueueDepth,
                replicationOverflowCount: overflowCount,
                languageProviderCount: languageRegistrations.length,
                languageRequestCount,
                languageRequestP95Milliseconds: languageRequestLatencies.length === 0
                    ? 0
                    : [...languageRequestLatencies].sort((left, right) => left - right)[Math.ceil(languageRequestLatencies.length * 0.95) - 1],
            };
        },

        /** Releases the change listener, the observer, the editor, and this editor's model lease, in that order. */
        dispose() {
            if (disposed) {
                return;
            }

            // Ended before anything else is released: the comparison holds the live model, and disposing the editor
            // while the diff view still has it would dispose the document out from under it.
            stopCompare();

            for (const viewId of [...secondaryViews.keys()]) handle.removeView(viewId);
            disposed = true;
            for (const registration of registeredCommands) {
                registration.dispose();
            }

            registeredCommands = [];
            registeredCommandDescriptors = [];
            for (const registration of languageRegistrations) registration.dispose();
            languageRegistrations.length = 0;
            languageContexts.clear();
            observer.disconnect();
            editor.setModel(null);
            editor.dispose();
            for (const document of documents.values()) {
                document.queued = [];
                document.contentSubscription?.dispose();
                releaseModel(document.model);
            }
            documents.clear();
            currentDocument = null;
            currentModel = null;
        },
    };

    return handle;
}
