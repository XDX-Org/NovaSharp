// The single interop surface between NovaSharp and the packaged Monaco editor.
//
// Monaco owns live text, undo/redo, selection, composition, viewport rendering, and token colours. This module owns
// the editor's lifetime and the replication stream: creating the editor after its container is mounted, keeping one
// text model per canonical document URI, turning Monaco's change events into ordered edit batches, and disposing
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
 * Models are keyed by canonical document URI and reference counted, so a document shown in more than one editor is
 * one model with shared text and undo history. Phase 2 shows one at a time; the leases are what make split views in
 * phase 5 a change of caller rather than a change of ownership.
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
    const editor = monaco.editor.create(container, {
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
    });

    const observer = new ResizeObserver(() => editor.layout());
    observer.observe(container);

    let currentModel = null;
    let currentDocument = null;
    const documents = new Map();
    let disposed = false;
    let workerVerified;
    let registeredCommands = [];
    let diffEditor = null;
    let diffObserver = null;
    let originalModel = null;

    let maximumQueueDepth = 0;
    let overflowCount = 0;

    const ensureLive = () => {
        if (disposed) {
            throw new Error('This editor has been disposed.');
        }
    };

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
            await bridge.invokeMethodAsync('RequestResync', document.model.uri.toString());
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
            documentUri: document.model.uri.toString(),
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

        if (currentModel && !disposed) {
            editor.setModel(currentModel);
            editor.focus();
        }
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

    const handle = {
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

            registeredCommands = [];
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

                registeredCommands.push(editor.addAction({
                    id: descriptor.id,
                    label: descriptor.title,
                    keybindings,
                    contextMenuGroupId: descriptor.showInPalette ? 'navigation' : undefined,
                    // The action does not decide anything. It names the command, and .NET's registry resolves
                    // enablement and behaviour, so a keybinding and a button cannot diverge.
                    run: () => void bridge.invokeMethodAsync('InvokeCommandAsync', descriptor.id),
                }));
            }

            return unresolved;
        },

        /**
         * Shows the file on disk beside the editor's text.
         *
         * The live model becomes the diff's modified side rather than a copy of it, so the comparison shows the user's
         * unsaved text, stays editable, and keeps replicating while it is open. The main editor releases the model for
         * the duration; nothing about the document's identity or its undo history changes.
         */
        beginCompare(diffContainer, originalText) {
            ensureLive();
            if (!(diffContainer instanceof HTMLElement)) {
                throw new TypeError('A container element is required to compare in.');
            }

            const model = currentModel;
            if (!model) {
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

            editor.setModel(null);
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

            const key = monaco.Uri.parse(uriString).toString();
            let document = documents.get(key);
            if (!document) {
                const model = acquireModel(uriString, languageId, text, lineEnding);
                document = {
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

            document.readOnly = readOnly === true;
            attach(document);
            editor.focus();
            return readSequence(document.model);
        },

        /** Opens text streamed as UTF-8 so a large initial document does not need a second JSON-sized interop buffer. */
        async openDocumentStream(uriString, languageId, textStream, lineEnding, readOnly) {
            const text = new TextDecoder().decode(await textStream.arrayBuffer());
            return handle.openDocument(uriString, languageId, text, lineEnding, readOnly);
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
                return {
                    text: oldDocument.model.getValue(),
                    ...readSequence(oldDocument.model),
                };
            }

            const wasCurrent = oldDocument === currentDocument;
            if (wasCurrent) {
                stopCompare();
            }
            captureCurrentView();
            const model = acquireModel(newUriString, languageId, oldDocument.model.getValue(), oldDocument.model.getEOL());
            const document = {
                ...oldDocument,
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
            diffEditor?.updateOptions({ fontFamily: family });
            monaco.editor.remeasureFonts();
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
                documentLength: currentModel?.getValueLength() ?? 0,
                externalRequestCount: countExternalRequests(),
                replicationCapacity: REPLICATION_CAPACITY,
                replicationQueueDepth: [...documents.values()].reduce((sum, document) => sum + document.queued.length, 0),
                replicationMaximumQueueDepth: maximumQueueDepth,
                replicationOverflowCount: overflowCount,
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

            disposed = true;
            for (const registration of registeredCommands) {
                registration.dispose();
            }

            registeredCommands = [];
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
