// Browser-level gates for the packaged Monaco editor host.
//
// These assert the parts of phases 1 and 2 that only a real browser can prove: the packaged bundle loads from the
// application's own origin, the editor worker starts as a real dedicated worker rather than falling back to the
// browser thread, nothing reaches the network at runtime, disposal actually releases the model, and — the phase-2
// gate — the edit batches Monaco produces reconstruct its text exactly in a shadow that only ever sees those batches.
//
// Run with:  node tests/editor-host/editor-host.test.mjs
// Requires a Chromium build for Playwright. See tests/editor-host/README.md.

import { createServer } from 'node:http';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const here = path.dirname(fileURLToPath(import.meta.url));
const wwwroot = path.resolve(here, '..', '..', 'src', 'NovaSharp', 'wwwroot');

const CONTENT_TYPES = {
    '.css': 'text/css; charset=utf-8',
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.ttf': 'font/ttf',
};

const HARNESS_PAGE = `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <link rel="stylesheet" href="/monaco/monaco.css">
    <style>html, body, #host { width: 100%; height: 100%; margin: 0; }</style>
</head>
<body>
    <div id="host"></div>
    <script type="module">
        // Stands in for the .NET bridge, and for the shadow behind it. It applies the batches it is sent exactly the
        // way DocumentReplica does — from the end backwards, validating that each batch continues from the last — so
        // a divergence between the editor and the shadow shows up here rather than as a corrupted save.
        const shadow = {
            text: '',
            sequence: 0,
            alternativeSequence: 0,
            batches: [],
            resyncs: 0,
            concurrentCalls: 0,
            maxConcurrentCalls: 0,
            replicationLatencies: [],
            holdReplication: false,
            releaseReplication: null,
            problems: [],
        };
        globalThis.shadow = shadow;

        function applyBatch(batch) {
            if (batch.baseSequence !== shadow.sequence) {
                shadow.problems.push(\`gap: base \${batch.baseSequence} at \${shadow.sequence}\`);
                return;
            }

            let previousEnd = 0;
            for (const edit of batch.edits) {
                if (edit.start < previousEnd || edit.end < edit.start || edit.end > shadow.text.length) {
                    shadow.problems.push(\`bad edit \${edit.start}-\${edit.end} in text of \${shadow.text.length}\`);
                    return;
                }

                previousEnd = edit.end;
            }

            for (let i = batch.edits.length - 1; i >= 0; i--) {
                const edit = batch.edits[i];
                shadow.text = shadow.text.slice(0, edit.start) + edit.text + shadow.text.slice(edit.end);
            }

            shadow.sequence = batch.resultSequence;
            shadow.alternativeSequence = batch.alternativeSequence;
            shadow.batches.push(batch);
        }

        globalThis.bridge = {
            async invokeMethodAsync(name, ...args) {
                const started = performance.now();
                shadow.concurrentCalls += 1;
                shadow.maxConcurrentCalls = Math.max(shadow.maxConcurrentCalls, shadow.concurrentCalls);
                try {
                    // A deliberate turn of the event loop, so a host that sent without waiting would be caught by the
                    // concurrency counter above rather than passing because interop happened to be instant.
                    await new Promise(resolve => setTimeout(resolve, 0));

                    if (name === 'ReplicateEdits') {
                        if (shadow.holdReplication) {
                            await new Promise(resolve => { shadow.releaseReplication = resolve; });
                        }

                        for (const batch of args[0]) {
                            applyBatch(batch);
                        }

                        shadow.replicationLatencies.push(performance.now() - started);
                        return true;
                    }

                    if (name === 'RequestResync') {
                        shadow.resyncs += 1;
                        return null;
                    }

                    if (name === 'InvokeCommandAsync') {
                        (globalThis.commands ??= []).push(args[0]);
                        return null;
                    }

                    return null;
                } finally {
                    shadow.concurrentCalls -= 1;
                }
            },
        };

        globalThis.adoptSnapshot = () => {
            const snapshot = globalThis.editor.snapshot();
            shadow.text = snapshot.text;
            shadow.sequence = snapshot.sequence;
            shadow.alternativeSequence = snapshot.alternativeSequence;
            shadow.resyncs += 1;
        };

        const module = await import('/monaco-editor-host.js');
        globalThis.createEditor = module.createEditor;
        globalThis.editor = module.createEditor(document.getElementById('host'), globalThis.bridge);
        globalThis.editorReady = true;
    </script>
</body>
</html>
`;

/** Serves wwwroot plus one harness page, so the editor runs under a real single origin. */
function startServer() {
    const server = createServer(async (request, response) => {
        const url = new URL(request.url, 'http://localhost');

        if (url.pathname === '/' || url.pathname === '/index.html') {
            response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
            response.end(HARNESS_PAGE);
            return;
        }

        // Resolve inside wwwroot only; a traversal attempt is a 403 rather than a file read.
        const resolved = path.resolve(wwwroot, `.${url.pathname}`);
        if (resolved !== wwwroot && !resolved.startsWith(wwwroot + path.sep)) {
            response.writeHead(403).end();
            return;
        }

        try {
            const body = await readFile(resolved);
            response.writeHead(200, { 'content-type': CONTENT_TYPES[path.extname(resolved)] ?? 'application/octet-stream' });
            response.end(body);
        } catch {
            response.writeHead(404).end();
        }
    });

    return new Promise(resolve => {
        server.listen(0, '127.0.0.1', () => resolve({ server, port: server.address().port }));
    });
}

const results = [];
let measuredPerformance;
let measuredLifecycle;
const paintP95Limit = positiveLimit('NOVASHARP_PAINT_P95_LIMIT', 16);

function positiveLimit(name, defaultValue) {
    const raw = process.env[name];
    if (raw === undefined) {
        return defaultValue;
    }

    const value = Number(raw);
    if (!Number.isFinite(value) || value <= 0) {
        throw new Error(`${name} must be a positive number.`);
    }

    return value;
}

function check(name, condition, detail = '') {
    results.push({ name, ok: Boolean(condition), detail });
    process.stdout.write(`  ${condition ? 'PASS' : 'FAIL'}  ${name}${condition || !detail ? '' : ` — ${detail}`}\n`);
}

const { server, port } = await startServer();
const origin = `http://127.0.0.1:${port}`;

// Honour an externally provided Chromium so the test can run against a build the environment already has, rather
// than requiring every machine to download its own.
const executablePath = process.env.NOVASHARP_CHROMIUM_PATH || undefined;
const browser = await chromium.launch(executablePath ? { executablePath } : {});

try {
    const context = await browser.newContext();
    const page = await context.newPage();

    const workers = [];
    page.on('worker', worker => workers.push(worker.url()));

    const offOriginRequests = [];
    page.on('request', request => {
        if (!request.url().startsWith(origin) && /^https?:/.test(request.url())) {
            offOriginRequests.push(request.url());
        }
    });

    const pageErrors = [];
    page.on('pageerror', error => pageErrors.push(error.message));

    await page.goto(origin, { waitUntil: 'load' });
    await page.waitForFunction(() => globalThis.editorReady === true, null, { timeout: 30_000 });

    check('the page raised no script errors', pageErrors.length === 0, pageErrors.join('; '));
    check('the container stays empty until a document is opened',
        await page.evaluate(() => document.getElementById('host').children.length) === 0);

    // Opening a document is the one place text crosses the boundary.
    const SOURCE = 'namespace Widget;\n\npublic sealed class Gadget\n{\n    public int Value { get; init; }\n}\n';
    await page.evaluate(source => {
        const sequence = globalThis.editor.openDocument('file:///workspace/Widget.cs', 'csharp', source, '\n', false);
        globalThis.shadow.text = source;
        globalThis.shadow.sequence = sequence.sequence;
        globalThis.shadow.alternativeSequence = sequence.alternativeSequence;
    }, SOURCE);

    check('the editor mounts from the packaged bundle', await page.locator('.monaco-editor').count() > 0);

    const info = await page.evaluate(() => globalThis.editor.runtimeInfo());

    check('a real dedicated worker started, not a main-thread fallback', info.dedicatedWorker === true);
    check('Playwright observed the worker', workers.length > 0, JSON.stringify(workers));
    check('the worker came from the application origin', workers.every(url => url.startsWith(origin)), JSON.stringify(workers));
    check('the packaged Monaco version is reported', /^\d+\.\d+\.\d+$/.test(info.monacoVersion), info.monacoVersion);
    check('exactly one model is open', info.modelCount === 1, String(info.modelCount));
    check('the page reported no off-origin resource loads', info.externalRequestCount === 0, String(info.externalRequestCount));
    check('no request left the application origin', offOriginRequests.length === 0, offOriginRequests.join('; '));

    // C# lexical colouring comes from the packaged language definition, not from a NovaSharp overlay.
    const keywordColours = await page.evaluate(() => Array.from(document.querySelectorAll('.view-line span[class*="mtk"]'))
        .map(node => node.className)
        .filter((value, index, all) => all.indexOf(value) === index));
    check('C# lexical tokens are painted by Monaco', keywordColours.length > 1, JSON.stringify(keywordColours));

    const modelText = () => page.evaluate(() => globalThis.NovaMonaco.editor.getModels()[0].getValue());
    const shadow = () => page.evaluate(() => ({ ...globalThis.shadow, batches: globalThis.shadow.batches.length }));
    const settle = () => page.waitForFunction(() => globalThis.shadow.concurrentCalls === 0);

    // Typing goes through Monaco's own input path; there is no .NET round trip in the keystroke-to-paint path.
    // Opening the document focused the editor, so the keyboard reaches it without the test knowing Monaco's internals.
    await page.keyboard.type('// edited ');
    // A character outside the basic multilingual plane: two UTF-16 units that must stay one character.
    await page.keyboard.insertText('𝄞');
    await settle();

    const edited = await modelText();
    check('typing reaches the model', edited.includes('// edited '), JSON.stringify(edited.slice(0, 24)));
    check('a surrogate pair survives as one character', edited.includes('\u{1D11E}'));

    let replicated = await shadow();
    check('every keystroke is replicated as its own batch', replicated.batches >= 11, String(replicated.batches));
    check('the shadow reconstructs the typed text exactly', replicated.text === edited);
    check('no batch was rejected by the shadow', replicated.problems.length === 0, replicated.problems.join('; '));
    check('at most one replication call was in flight', replicated.maxConcurrentCalls <= 1, String(replicated.maxConcurrentCalls));

    // Composition: an input method builds a character over several events before committing one. Monaco owns the
    // composition entirely, and only the committed text may reach the shadow — a batch per intermediate keystroke
    // would replicate text the user never entered.
    const cdp = await context.newCDPSession(page);
    const batchesBeforeComposition = (await shadow()).batches;
    await cdp.send('Input.imeSetComposition', { text: 'ｎ', selectionStart: 1, selectionEnd: 1 });
    await cdp.send('Input.imeSetComposition', { text: 'に', selectionStart: 1, selectionEnd: 1 });
    await cdp.send('Input.insertText', { text: '日' });
    await settle();

    replicated = await shadow();
    check('composed text reaches the model', (await modelText()).includes('日'));
    check('the shadow matches the model after composition', replicated.text === await modelText(), replicated.problems.join('; '));
    check('composition does not replicate more batches than changes',
        replicated.batches - batchesBeforeComposition <= 4,
        String(replicated.batches - batchesBeforeComposition));

    // A paste that replaces a selection, and a multi-cursor edit: several ranges changed by one operation, which is
    // where an ascending-order or offset-shift mistake shows up.
    await page.evaluate(() => {
        const model = globalThis.NovaMonaco.editor.getModels()[0];
        const first = model.getPositionAt(0);
        const second = model.getPositionAt(20);
        globalThis.editor.snapshot; // keep the handle referenced
        model.pushEditOperations([], [
            { range: new globalThis.NovaMonaco.Range(first.lineNumber, first.column, first.lineNumber, first.column + 2), text: 'AB' },
            { range: new globalThis.NovaMonaco.Range(second.lineNumber, second.column, second.lineNumber, second.column), text: 'CD' },
        ], () => null);
    });
    await settle();

    replicated = await shadow();
    check('a multi-range edit replicates as one ordered batch', replicated.text === await modelText());
    check('multi-range offsets are ascending and non-overlapping', replicated.problems.length === 0, replicated.problems.join('; '));

    // Undo is Monaco's, not a NovaSharp reimplementation, and its alternative version returns to the earlier value —
    // which is what lets dirty state clear when the user undoes back to what was saved.
    const beforeUndo = await page.evaluate(() => globalThis.editor.sequence());
    await page.keyboard.press('ControlOrMeta+z');
    await page.keyboard.press('ControlOrMeta+z');
    await settle();

    const afterUndo = await page.evaluate(() => globalThis.editor.sequence());
    replicated = await shadow();
    check('undo is owned by Monaco', !(await modelText()).includes('// edited'), JSON.stringify((await modelText()).slice(0, 24)));
    check('undo still moves the version identifier forward', afterUndo.sequence > beforeUndo.sequence);
    check('undo returns the alternative version to an earlier value',
        afterUndo.alternativeSequence < beforeUndo.alternativeSequence,
        `${beforeUndo.alternativeSequence} -> ${afterUndo.alternativeSequence}`);
    check('the shadow follows an undo like any other edit', replicated.text === await modelText());

    // A line-ending change rewrites every line at once and no range edit describes it, so it must ask for a
    // resynchronization rather than send offsets into text the shadow does not have.
    const resyncsBefore = (await shadow()).resyncs;
    await page.evaluate(() => globalThis.NovaMonaco.editor.getModels()[0].setEOL(globalThis.NovaMonaco.editor.EndOfLineSequence.CRLF));
    await settle();
    replicated = await shadow();
    check('a line-ending change asks for a resynchronization', replicated.resyncs > resyncsBefore, String(replicated.resyncs));

    await page.evaluate(() => globalThis.adoptSnapshot());
    replicated = await shadow();
    check('the snapshot puts the shadow back in step', replicated.text === await modelText());

    // A NovaSharp-driven replacement keeps the model, its undo history, and the shadow in step without a round trip.
    const batchesBeforeReload = (await shadow()).batches;
    const reloaded = await page.evaluate(() => {
        const sequence = globalThis.editor.replaceDocument('class Reloaded;\n', '\n');
        globalThis.shadow.text = 'class Reloaded;\n';
        globalThis.shadow.sequence = sequence.sequence;
        globalThis.shadow.alternativeSequence = sequence.alternativeSequence;
        return sequence;
    });
    await settle();
    replicated = await shadow();

    check('a replacement changes the model', (await modelText()) === 'class Reloaded;\n');
    check('a replacement sends no edit batches', replicated.batches === batchesBeforeReload, String(replicated.batches - batchesBeforeReload));
    check('a replacement reports the sequence the caller adopts', reloaded.sequence > 0 && reloaded.alternativeSequence > 0);
    check('a replacement is undoable', await page.evaluate(async () => {
        globalThis.NovaMonaco.editor.getEditors()[0].trigger('test', 'undo', null);
        return globalThis.NovaMonaco.editor.getModels()[0].getValue() !== 'class Reloaded;\n';
    }));

    await page.evaluate(() => globalThis.adoptSnapshot());

    // Typing after a replacement continues from the sequence the replacement reported, with no gap.
    await page.evaluate(() => globalThis.editor.replaceDocument('class Reloaded;\n', '\n'));
    await page.evaluate(() => globalThis.adoptSnapshot());
    await page.evaluate(() => globalThis.NovaMonaco.editor.getEditors()[0].focus());
    await page.keyboard.type('// after');
    await settle();
    replicated = await shadow();
    check('replication resumes cleanly after a replacement', replicated.text === await modelText(), replicated.problems.join('; '));

    // Find, long lines, and scrolling exercise Monaco's own interaction and viewport paths. NovaSharp contributes no
    // second renderer or find UI.
    const LARGE_SOURCE = `${Array.from({ length: 2_000 }, (_, index) => `// line ${index}`).join('\n')}\n${'x'.repeat(100_000)} needle\n`;
    await page.evaluate(source => globalThis.editor.replaceDocument(source, '\n'), LARGE_SOURCE);
    await page.evaluate(() => globalThis.adoptSnapshot());
    await page.evaluate(() => {
        const editor = globalThis.NovaMonaco.editor.getEditors()[0];
        editor.setPosition({ lineNumber: 2_001, column: 90_000 });
        editor.revealPositionInCenter({ lineNumber: 2_001, column: 90_000 });
        editor.focus();
    });
    check('a 100,000-character line retains navigation at a distant column', await page.evaluate(() => {
        const editor = globalThis.NovaMonaco.editor.getEditors()[0];
        return editor.getPosition().lineNumber === 2_001 && editor.getPosition().column === 90_000;
    }));
    check('a 2,000-line document scrolls vertically',
        await page.evaluate(() => globalThis.NovaMonaco.editor.getEditors()[0].getScrollTop()) > 0);

    await page.evaluate(async () => {
        const editor = globalThis.NovaMonaco.editor.getEditors()[0];
        editor.setSelection(new globalThis.NovaMonaco.Range(1, 1, 1, 1));
        await editor.getAction('actions.find').run();
    });
    await page.waitForFunction(() => document.activeElement?.getAttribute('aria-label')?.startsWith('Find'));
    await page.keyboard.type('needle');
    await page.keyboard.press('Enter');
    await page.keyboard.press('Escape');
    check('Monaco find selects the matching text', await page.evaluate(() => {
        const editor = globalThis.NovaMonaco.editor.getEditors()[0];
        return editor.getModel().getValueInRange(editor.getSelection()) === 'needle';
    }));

    // Measure the documented 2,000-line fixture for 60 seconds while a bounded independent worker is active. Earlier
    // interaction gates deliberately use a 100,000-character line; carrying that pathological line into this budget
    // would measure a different fixture. The worker yields between short analysis bursts, as NovaSharp's bounded
    // background workers must, instead of monopolizing a runner core.
    const performanceCharacterCount = 1_200;
    await page.evaluate(async source => {
        globalThis.editor.replaceDocument(source, '\n');
        globalThis.adoptSnapshot();
        globalThis.shadow.replicationLatencies.length = 0;
        const editor = globalThis.NovaMonaco.editor.getEditors()[0];
        editor.setPosition({ lineNumber: 1_000, column: 10 });
        editor.revealPositionInCenter({ lineNumber: 1_000, column: 10 });
        const paints = [];
        const longTasks = [];
        const subscription = editor.getModel().onDidChangeContent(() => {
            const changedAt = performance.now();
            requestAnimationFrame(() => paints.push(performance.now() - changedAt));
        });
        const observer = new PerformanceObserver(entries => {
            longTasks.push(...entries.getEntries().map(entry => entry.duration));
        });
        observer.observe({ type: 'longtask', buffered: false });
        const worker = new Worker(URL.createObjectURL(new Blob([
            `const end = performance.now() + 65000;
             function analyze() {
                 const burstEnd = performance.now() + 8;
                 while (performance.now() < burstEnd) { Math.sqrt(Math.random()); }
                 if (performance.now() < end) { setTimeout(analyze, 32); }
             }
             analyze();`,
        ], { type: 'text/javascript' })));
        globalThis.performanceRun = { paints, longTasks, subscription, observer, worker };
        editor.focus();
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    }, Array.from({ length: 2_000 }, (_, index) => `// line ${index}: ordinary editor performance fixture`).join('\n'));
    await page.keyboard.type('a'.repeat(performanceCharacterCount), { delay: 50 });
    await settle();
    await page.waitForFunction(expected => globalThis.performanceRun.paints.length >= expected, performanceCharacterCount);
    measuredPerformance = await page.evaluate(() => {
        const run = globalThis.performanceRun;
        run.subscription.dispose();
        run.observer.disconnect();
        run.worker.terminate();
        const percentile = (values, value) => {
            const ordered = [...values].sort((left, right) => left - right);
            return ordered[Math.min(ordered.length - 1, Math.ceil(ordered.length * value) - 1)] ?? 0;
        };
        const replication = globalThis.shadow.replicationLatencies;
        return {
            paintP95: percentile(run.paints, 0.95),
            paintP99: percentile(run.paints, 0.99),
            longestTask: Math.max(0, ...run.longTasks),
            replicationP95: percentile(replication, 0.95),
            replicationP99: percentile(replication, 0.99),
        };
    });
    check(`keystroke-to-paint p95 stays within ${paintP95Limit} ms`, measuredPerformance.paintP95 <= paintP95Limit, `${measuredPerformance.paintP95.toFixed(2)} ms`);
    check('keystroke-to-paint p99 stays within 33 ms', measuredPerformance.paintP99 <= 33, `${measuredPerformance.paintP99.toFixed(2)} ms`);
    check('the browser thread has no task longer than 50 ms', measuredPerformance.longestTask <= 50, `${measuredPerformance.longestTask.toFixed(2)} ms`);
    check('edit replication p95 stays within 50 ms', measuredPerformance.replicationP95 <= 50, `${measuredPerformance.replicationP95.toFixed(2)} ms`);
    check('edit replication p99 stays within 150 ms', measuredPerformance.replicationP99 <= 150, `${measuredPerformance.replicationP99.toFixed(2)} ms`);
    const ordinaryQueue = await page.evaluate(() => globalThis.editor.runtimeInfo());
    check('the replication queue stays below 25% of capacity under load',
        ordinaryQueue.replicationMaximumQueueDepth <= ordinaryQueue.replicationCapacity * 0.25,
        `${ordinaryQueue.replicationMaximumQueueDepth}/${ordinaryQueue.replicationCapacity}`);

    // Stop the receiver, generate more than one full queue, then release it. Overflow must retain a fixed bound and
    // recover once through a snapshot; it must not create an unbounded browser backlog or overlap interop sends.
    const resyncsBeforeOverflow = (await shadow()).resyncs;
    await page.evaluate(() => {
        globalThis.shadow.holdReplication = true;
        const model = globalThis.NovaMonaco.editor.getModels()[0];
        for (let index = 0; index < 300; index++) {
            model.applyEdits([{ range: model.getFullModelRange().collapseToEnd(), text: 'z' }]);
        }
    });
    const saturated = await page.evaluate(() => globalThis.editor.runtimeInfo());
    check('the browser replication queue is bounded',
        saturated.replicationMaximumQueueDepth === saturated.replicationCapacity,
        `${saturated.replicationMaximumQueueDepth}/${saturated.replicationCapacity}`);
    check('queue overflow is observable', saturated.replicationOverflowCount === 1, String(saturated.replicationOverflowCount));
    await page.evaluate(() => {
        globalThis.shadow.holdReplication = false;
        globalThis.shadow.releaseReplication?.();
    });
    await page.waitForFunction(expected => globalThis.shadow.resyncs > expected, resyncsBeforeOverflow);
    await page.evaluate(() => globalThis.adoptSnapshot());
    replicated = await shadow();
    check('overflow recovers through one full snapshot', replicated.text === await modelText(), replicated.problems.join('; '));
    check('overflow never overlaps interop sends', replicated.maxConcurrentCalls <= 1, String(replicated.maxConcurrentCalls));

    // A read-only document refuses edits, which is how a file that cannot be written is presented.
    await page.evaluate(() => globalThis.editor.setReadOnly(true));
    const readOnlyBefore = await modelText();
    await page.keyboard.type('should not appear');
    await settle();
    check('a read-only editor refuses edits', (await modelText()) === readOnlyBefore);
    await page.evaluate(() => globalThis.editor.setReadOnly(false));

    // The command registry is authoritative: the editor binds the descriptors it is handed and keeps no list of its
    // own, so this is where a binding that would silently do nothing has to surface.
    const unresolved = await page.evaluate(() => globalThis.editor.registerCommands([
        { id: 'novasharp.document.save', title: 'Save', keybindings: ['CtrlCmd+KeyS'], showInPalette: true },
        { id: 'novasharp.document.saveAs', title: 'Save As…', keybindings: ['CtrlCmd+Shift+KeyS'], showInPalette: true },
        { id: 'novasharp.document.reload', title: 'Reload From Disk', keybindings: [], showInPalette: true },
        { id: 'test.unbindable', title: 'Unbindable', keybindings: ['Ctrl+KeyQ'], showInPalette: false },
    ]));

    check('a keybinding Monaco cannot resolve is reported rather than dropped',
        unresolved.length === 1 && unresolved[0].startsWith('test.unbindable'),
        JSON.stringify(unresolved));

    await page.evaluate(() => globalThis.NovaMonaco.editor.getEditors()[0].focus());
    await page.keyboard.press('ControlOrMeta+s');
    let saveCommandSeen = true;
    try {
        await page.waitForFunction(
            () => (globalThis.commands ?? []).includes('novasharp.document.save'),
            null,
            { timeout: 5000 });
    } catch {
        saveCommandSeen = false;
    }

    const invokedCommands = await page.evaluate(() => globalThis.commands ?? []);
    check('a registered shortcut invokes its command identifier', saveCommandSeen, JSON.stringify(invokedCommands));

    // Registering again replaces the previous actions rather than stacking a second copy of each.
    const secondRegistration = await page.evaluate(() => globalThis.editor.registerCommands([
        { id: 'novasharp.document.save', title: 'Save', keybindings: ['CtrlCmd+KeyS'], showInPalette: true },
    ]));
    check('re-registering replaces the previous actions', secondRegistration.length === 0, JSON.stringify(secondRegistration));

    // Comparing borrows the live model, so what is shown is the user's unsaved text rather than a copy of it.
    const beforeCompare = await modelText();
    await page.evaluate(() => {
        const host = document.createElement('div');
        host.id = 'diff';
        host.style.cssText = 'position:absolute;inset:0;';
        document.body.appendChild(host);
        globalThis.editor.beginCompare(host, 'class OnDisk;\n');
    });

    check('a comparison opens', await page.evaluate(() => globalThis.editor.isComparing()));
    check('the comparison shows both sides',
        await page.locator('.monaco-diff-editor').count() > 0);
    check('the comparison uses the live model rather than a copy',
        await page.evaluate(() => globalThis.NovaMonaco.editor.getModels()
            .some(model => model.uri.scheme === 'file' && !model.isDisposed())));

    // Editing continues while comparing, and still replicates.
    await page.evaluate(() => {
        const model = globalThis.NovaMonaco.editor.getModels().find(m => m.uri.scheme === 'file');
        model.pushEditOperations([], [{ range: model.getFullModelRange().collapseToStart(), text: '// while comparing\n' }], () => null);
    });
    await settle();
    replicated = await shadow();
    check('edits made while comparing still replicate', replicated.text === await modelText(), replicated.problems.join('; '));

    await page.evaluate(() => globalThis.editor.endCompare());
    check('ending the comparison closes the diff view',
        !(await page.evaluate(() => globalThis.editor.isComparing()))
        && await page.locator('.monaco-diff-editor').count() === 0);
    check('ending the comparison disposes only the side it created',
        await page.evaluate(() => globalThis.editor.runtimeInfo()).then(info => info.modelCount === 1));
    check('the document survives the comparison',
        (await modelText()).includes('// while comparing') && (await modelText()).includes(beforeCompare.slice(0, 10)));

    // Reopening the same document keeps one model rather than creating a second identity.
    await page.evaluate(() => globalThis.editor.openDocument('file:///workspace/Widget.cs', 'csharp', 'ignored', '\n', false));
    const afterReopen = await page.evaluate(() => globalThis.editor.runtimeInfo());
    check('reopening the same URI reuses one model', afterReopen.modelCount === 1, String(afterReopen.modelCount));

    // Switching documents releases the previous model deterministically.
    await page.evaluate(() => globalThis.editor.openDocument('file:///workspace/Other.cs', 'csharp', 'class Other;\n', '\n', false));
    const afterSwitch = await page.evaluate(() => globalThis.editor.runtimeInfo());
    check('switching documents releases the previous model', afterSwitch.modelCount === 1, String(afterSwitch.modelCount));

    await page.evaluate(() => globalThis.editor.dispose());
    const remaining = await page.evaluate(() => globalThis.NovaMonaco.editor.getModels().length);
    check('disposal releases every model', remaining === 0, String(remaining));
    check('disposal removes the editor from the page', await page.locator('.monaco-editor').count() === 0);

    const disposedCallFailed = await page.evaluate(() => {
        try {
            globalThis.editor.openDocument('file:///workspace/Widget.cs', 'csharp', 'x', '\n', false);
            return false;
        } catch {
            return true;
        }
    });
    check('a disposed editor rejects further use', disposedCallFailed);

    // Warm Monaco before taking a heap baseline, then repeat the complete create/open/dispose lifecycle 100 times.
    const runLifecycleCycles = count => page.evaluate(cycles => {
        for (let index = 0; index < cycles; index++) {
            const host = document.createElement('div');
            host.style.cssText = 'position:absolute;inset:0;';
            document.body.appendChild(host);
            const editor = globalThis.createEditor(host, globalThis.bridge);
            editor.openDocument(`file:///workspace/cycle-${index}.cs`, 'csharp', 'class Cycle;\n', '\n', false);
            editor.dispose();
            host.remove();
        }
    }, count);
    await runLifecycleCycles(10);
    await cdp.send('HeapProfiler.collectGarbage');
    const heapUsed = async () => (await cdp.send('Runtime.getHeapUsage')).usedSize;
    const heapBeforeCycles = await heapUsed();
    await runLifecycleCycles(100);
    await cdp.send('HeapProfiler.collectGarbage');
    const heapAfterCycles = await heapUsed();
    measuredLifecycle = { heapBeforeCycles, heapAfterCycles, cycles: 100 };
    check('100 open/close cycles leave zero live models',
        await page.evaluate(() => globalThis.NovaMonaco.editor.getModels().length) === 0);
    check('100 open/close cycles retain no more than 10% heap',
        heapAfterCycles <= heapBeforeCycles * 1.10,
        `${Math.round(heapBeforeCycles / 1024 / 1024)} MB -> ${Math.round(heapAfterCycles / 1024 / 1024)} MB`);
} finally {
    await browser.close();
    server.close();
}

const failed = results.filter(result => !result.ok);
process.stdout.write(`\n${results.length - failed.length} passed, ${failed.length} failed\n`);

if (process.env.NOVASHARP_BROWSER_METRICS) {
    const metricsPath = path.resolve(process.env.NOVASHARP_BROWSER_METRICS);
    await mkdir(path.dirname(metricsPath), { recursive: true });
    await writeFile(metricsPath, `${JSON.stringify({
        fixtureName: process.env.NOVASHARP_FIXTURE_NAME ?? 'unrecorded',
        platform: process.platform,
        architecture: process.arch,
        nodeVersion: process.version,
        limits: { paintP95Milliseconds: paintP95Limit },
        performance: measuredPerformance,
        lifecycle: measuredLifecycle,
        assertions: results,
    }, null, 2)}\n`);
}

process.exit(failed.length === 0 ? 0 : 1);
