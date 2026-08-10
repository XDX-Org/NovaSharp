export function createEditor(root, wordWrap, dotNet, selectionStart, selectionEnd, scrollTop, scrollLeft) {
    const input = root.querySelector('.editor-input');
    const presentation = root.querySelector('.presentation');
    input.wrap = wordWrap ? 'soft' : 'off';
    input.setSelectionRange(selectionStart, selectionEnd);
    input.scrollTop = scrollTop;
    input.scrollLeft = scrollLeft;

    let viewStateTimer;
    const persistViewState = () => {
        clearTimeout(viewStateTimer);
        viewStateTimer = undefined;
        dotNet.invokeMethodAsync('ViewportChanged', input.selectionStart, input.selectionEnd,
            input.scrollTop, input.scrollLeft);
    };
    const scheduleViewState = () => {
        clearTimeout(viewStateTimer);
        viewStateTimer = setTimeout(persistViewState, 160);
    };
    const sync = () => {
        presentation.scrollTop = input.scrollTop;
        presentation.scrollLeft = input.scrollLeft;
        scheduleViewState();
    };
    const keydown = event => {
        const popup = root.querySelector('.completion-popup');
        const selectedCompletion = popup?.querySelector('.selected');
        if (selectedCompletion?.dataset.commit?.includes(event.key) && event.key.length === 1) {
            event.preventDefault();
            dotNet.invokeMethodAsync('EditorCommand', 'completion-accept', input.selectionStart).then(() => {
                input.setRangeText(event.key, input.selectionStart, input.selectionEnd, 'end');
                input.dispatchEvent(new InputEvent('input', { bubbles: true, data: event.key, inputType: 'insertText' }));
            });
            return;
        }
        if (popup && ['ArrowDown', 'ArrowUp', 'Enter', 'Tab', 'Escape'].includes(event.key)) {
            event.preventDefault();
            const commands = { ArrowDown: 'completion-next', ArrowUp: 'completion-previous', Enter: 'completion-accept', Tab: 'completion-accept', Escape: 'escape' };
            dotNet.invokeMethodAsync('EditorCommand', commands[event.key], input.selectionStart);
            return;
        }
        if ((event.ctrlKey || event.metaKey) && event.key === ' ') {
            event.preventDefault();
            dotNet.invokeMethodAsync('EditorCommand', event.shiftKey ? 'signature' : 'completion', input.selectionStart);
            return;
        }
        if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === 'f') {
            event.preventDefault();
            dotNet.invokeMethodAsync('EditorCommand', 'format', input.selectionStart);
            return;
        }
        if (event.key === 'F12') {
            event.preventDefault();
            const command = event.ctrlKey && event.shiftKey ? 'type-definition'
                : event.altKey ? 'peek' : event.shiftKey ? 'references'
                : event.ctrlKey || event.metaKey ? 'implementation' : 'definition';
            dotNet.invokeMethodAsync('EditorCommand', command, input.selectionStart);
            return;
        }
        if (event.key === 'F2') {
            event.preventDefault();
            dotNet.invokeMethodAsync('EditorCommand', 'rename', input.selectionStart);
            return;
        }
        if ((event.ctrlKey || event.metaKey) && event.key === '.') {
            event.preventDefault();
            dotNet.invokeMethodAsync('EditorCommand', 'code-actions', input.selectionStart);
            return;
        }
        if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === 'o') {
            event.preventDefault();
            dotNet.invokeMethodAsync('EditorCommand', 'outline', input.selectionStart);
            return;
        }
        if (event.altKey && ['ArrowLeft', 'ArrowRight'].includes(event.key)) {
            event.preventDefault();
            dotNet.invokeMethodAsync('EditorCommand', event.key === 'ArrowLeft' ? 'back' : 'forward', input.selectionStart);
            return;
        }
        if ((event.ctrlKey || event.metaKey) && event.key === '/') {
            event.preventDefault();
            toggleLineComment(input);
            return;
        }
        if ((event.ctrlKey || event.metaKey) && ['s', 'o', 'f', 'z', 'y'].includes(event.key.toLowerCase())) {
            event.preventDefault();
            return;
        }
        if (event.key === 'Enter') {
            const before = input.value.slice(0, input.selectionStart);
            const indent = before.slice(before.lastIndexOf('\n') + 1).match(/^\s*/)?.[0] ?? '';
            const extra = before.trimEnd().endsWith('{') ? '    ' : '';
            event.preventDefault();
            input.setRangeText('\n' + indent + extra, input.selectionStart, input.selectionEnd, 'end');
            input.dispatchEvent(new InputEvent('input', { bubbles: true, data: '\n', inputType: 'insertLineBreak' }));
        } else if (event.key === 'Tab') {
            event.preventDefault();
            input.setRangeText('    ', input.selectionStart, input.selectionEnd, 'end');
            input.dispatchEvent(new Event('input', { bubbles: true }));
        } else if (!event.ctrlKey && !event.metaKey && ['{', '[', '(', '"', "'"].includes(event.key)) {
            const pairs = { '{': '}', '[': ']', '(': ')', '"': '"', "'": "'" };
            event.preventDefault();
            const start = input.selectionStart;
            const selected = input.value.slice(start, input.selectionEnd);
            input.setRangeText(event.key + selected + pairs[event.key], start, input.selectionEnd, 'end');
            input.setSelectionRange(start + 1, start + 1 + selected.length);
            input.dispatchEvent(new Event('input', { bubbles: true }));
        }
    };
    let composing = false;
    const compositionStart = () => composing = true;
    const compositionInput = event => { if (composing) event.stopPropagation(); };
    const compositionEnd = () => {
        composing = false;
        input.dispatchEvent(new Event('input', { bubbles: true }));
    };
    const inputChanged = event => {
        if (!composing) dotNet.invokeMethodAsync('InputChanged', input.value, input.selectionStart, event.data ?? null);
    };
    const selectionChange = scheduleViewState;
    const blur = () => {
        if (viewStateTimer !== undefined) persistViewState();
    };
    input.addEventListener('scroll', sync);
    input.addEventListener('keydown', keydown);
    input.addEventListener('compositionstart', compositionStart);
    input.addEventListener('input', compositionInput);
    input.addEventListener('input', inputChanged);
    input.addEventListener('compositionend', compositionEnd);
    input.addEventListener('select', selectionChange);
    input.addEventListener('keyup', selectionChange);
    input.addEventListener('blur', blur);
    let hoverTimer;
    let hoverCloseTimer;
    let hoverPosition = -1;
    const pointermove = event => {
        const position = editorPositionAtPoint(input, event.clientX, event.clientY);
        if (position === hoverPosition) return;
        hoverPosition = position;
        clearTimeout(hoverTimer);
        hoverTimer = setTimeout(() => dotNet.invokeMethodAsync('EditorCommand', 'hover', position), 250);
    };
    const closeHover = () => {
        clearTimeout(hoverTimer);
        hoverPosition = -1;
        dotNet.invokeMethodAsync('EditorCommand', 'hover-close', input.selectionStart);
    };
    const pointerleave = () => hoverCloseTimer = setTimeout(closeHover, 100);
    const rootPointerover = event => {
        if (event.target.closest?.('.hover-popup')) clearTimeout(hoverCloseTimer);
    };
    const rootPointerout = event => {
        const tooltip = event.target.closest?.('.hover-popup');
        if (tooltip && !tooltip.contains(event.relatedTarget)) hoverCloseTimer = setTimeout(closeHover, 100);
    };
    input.addEventListener('pointermove', pointermove);
    input.addEventListener('pointerleave', pointerleave);
    root.addEventListener('pointerover', rootPointerover);
    root.addEventListener('pointerout', rootPointerout);
    root.__novaEditor = { input, dotNet, sync, keydown, compositionStart, compositionInput, inputChanged, compositionEnd,
        selectionChange, blur, pointermove, pointerleave, rootPointerover, rootPointerout,
        get viewStateTimer() { return viewStateTimer; },
        get hoverTimer() { return hoverTimer; }, get hoverCloseTimer() { return hoverCloseTimer; } };
}

export function disposeEditor(root) {
    const editor = root.__novaEditor;
    if (!editor) return;
    editor.input.removeEventListener('scroll', editor.sync);
    editor.input.removeEventListener('keydown', editor.keydown);
    editor.input.removeEventListener('compositionstart', editor.compositionStart);
    editor.input.removeEventListener('input', editor.compositionInput);
    editor.input.removeEventListener('input', editor.inputChanged);
    editor.input.removeEventListener('compositionend', editor.compositionEnd);
    editor.input.removeEventListener('select', editor.selectionChange);
    editor.input.removeEventListener('keyup', editor.selectionChange);
    editor.input.removeEventListener('blur', editor.blur);
    clearTimeout(editor.viewStateTimer);
    clearTimeout(editor.hoverTimer);
    clearTimeout(editor.hoverCloseTimer);
    editor.input.removeEventListener('pointermove', editor.pointermove);
    editor.input.removeEventListener('pointerleave', editor.pointerleave);
    root.removeEventListener('pointerover', editor.rootPointerover);
    root.removeEventListener('pointerout', editor.rootPointerout);
    delete root.__novaEditor;
}

function editorPositionAtPoint(input, clientX, clientY) {
    const style = getComputedStyle(input);
    const rect = input.getBoundingClientRect();
    const lineHeight = Number.parseFloat(style.lineHeight) || 20;
    const lines = input.value.split(/\r\n|\r|\n/);
    const line = Math.max(0, Math.min(lines.length - 1,
        Math.floor((clientY - rect.top + input.scrollTop) / lineHeight)));
    const measure = document.createElement('canvas').getContext('2d');
    measure.font = style.font;
    const columnWidth = measure.measureText('M').width || 8;
    const column = Math.max(0, Math.min(lines[line].length,
        Math.round((clientX - rect.left + input.scrollLeft - Number.parseFloat(style.paddingLeft)) / columnWidth)));
    let position = column;
    for (let index = 0; index < line; index++) position += lines[index].length + 1;
    return position;
}

export function applyEditorEdit(root, start, length, text, newPosition) {
    const input = root.querySelector('.editor-input');
    input.setRangeText(text, start, start + length, 'end');
    if (newPosition != null) input.setSelectionRange(newPosition, newPosition);
    input.dispatchEvent(new InputEvent('input', { bubbles: true, data: text, inputType: 'insertReplacementText' }));
    input.focus();
}

export function replaceEditorText(root, text, selectionStart, selectionLength) {
    const input = root.querySelector('.editor-input');
    input.value = text;
    input.setSelectionRange(selectionStart, selectionStart + selectionLength);
    input.dispatchEvent(new InputEvent('input', { bubbles: true, data: null, inputType: 'insertReplacementText' }));
    input.focus();
}

export function getEditorAnchor(root, position) {
    const input = root.querySelector('.editor-input');
    const before = input.value.slice(0, position).split(/\r\n|\r|\n/);
    const style = getComputedStyle(input);
    const lineHeight = Number.parseFloat(style.lineHeight) || 20;
    const measure = document.createElement('canvas').getContext('2d');
    measure.font = style.font;
    const x = 66 + measure.measureText(before.at(-1) ?? '').width - input.scrollLeft;
    const y = before.length * lineHeight - input.scrollTop;
    return [Math.max(58, Math.min(x, root.clientWidth - 280)), Math.max(20, Math.min(y, root.clientHeight - 120))];
}

export function fitEditorPopup(root, selector) {
    const popup = root.querySelector(selector);
    if (!popup) return;
    const margin = 8;
    let left = Number.parseFloat(popup.style.left) || 0;
    let top = Number.parseFloat(popup.style.top) || 0;
    left = Math.max(margin, Math.min(left, root.clientWidth - popup.offsetWidth - margin));
    top = Math.max(margin, Math.min(top, root.clientHeight - popup.offsetHeight - margin));
    popup.style.left = `${left}px`;
    popup.style.top = `${top}px`;
}

function toggleLineComment(input) {
    const start = input.value.lastIndexOf('\n', input.selectionStart - 1) + 1;
    const endIndex = input.value.indexOf('\n', input.selectionEnd);
    const end = endIndex < 0 ? input.value.length : endIndex;
    const lines = input.value.slice(start, end).split('\n');
    const uncomment = lines.every(line => line.trimStart().startsWith('//'));
    const replacement = lines.map(line => uncomment ? line.replace(/^(\s*)\/\/ ?/, '$1') : line.replace(/^(\s*)/, '$1// ')).join('\n');
    input.setRangeText(replacement, start, end, 'select');
    input.dispatchEvent(new InputEvent('input', { bubbles: true, data: null, inputType: 'insertReplacementText' }));
}

export async function runSmokeChecks(root) {
    const input = root.querySelector('.editor-input');
    if (!input) return { inputPresent: false };
    const original = input.value;

    input.value = 'before selected after';
    input.setSelectionRange(7, 15);
    input.setRangeText('value', input.selectionStart, input.selectionEnd, 'end');
    const selectionReplacement = input.value === 'before value after' && input.selectionStart === 12;

    input.value = 'x';
    input.setSelectionRange(0, 1);
    input.dispatchEvent(new KeyboardEvent('keydown', { key: '{', bubbles: true, cancelable: true }));
    const bracketPairing = input.value === '{x}' && input.selectionStart === 1 && input.selectionEnd === 2;

    input.value = 'x';
    input.setSelectionRange(1, 1);
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
    const tabInsertion = input.value === 'x    ' && input.selectionStart === 5;

    let bubbledInputs = 0;
    const countInput = () => bubbledInputs++;
    root.addEventListener('input', countInput);
    input.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true, data: '' }));
    input.dispatchEvent(new InputEvent('input', { bubbles: true, data: '漢', inputType: 'insertCompositionText' }));
    input.dispatchEvent(new CompositionEvent('compositionend', { bubbles: true, data: '漢' }));
    root.removeEventListener('input', countInput);

    input.value = original;
    let renderedRows = 0;
    for (let attempt = 0; attempt < 20 && renderedRows === 0; attempt++) {
        await new Promise(resolve => setTimeout(resolve, 100));
        renderedRows = root.querySelectorAll('.source-line').length;
    }
    const rowLimit = Math.max(100, Math.ceil(root.clientHeight / 20) + 66);
    return {
        inputPresent: true,
        selectionReplacement,
        bracketPairing,
        tabInsertion,
        compositionCommittedOnce: bubbledInputs === 1,
        rowsBounded: renderedRows > 0 && renderedRows <= rowLimit,
        renderedRows
    };
}

export async function runPhase7Smoke(root) {
    const { input, dotNet } = root.__novaEditor;
    const waitFor = async (predicate, attempts = 100) => {
        for (let attempt = 0; attempt < attempts; attempt++) {
            if (await predicate()) return true;
            await new Promise(resolve => setTimeout(resolve, 50));
        }
        return false;
    };
    try {
        if (!await waitFor(() => dotNet.invokeMethodAsync('LanguageReady'), 300))
            throw new Error('C# services did not finish loading');
        input.value = 'using System; class C { void M() { Console. } }';
        const completionPosition = input.value.indexOf('. }') + 1;
        input.setSelectionRange(completionPosition, completionPosition);
        await dotNet.invokeMethodAsync('InputChanged', input.value, input.selectionStart, '.');
        const completionItems = await dotNet.invokeMethodAsync('CompletionItemCount');
        const completionDiagnostic = await dotNet.invokeMethodAsync('CompletionDiagnostic');
        const completionVisible = await waitFor(() => root.querySelectorAll('.completion-popup [role="option"]').length > 0);
        input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
        const completionKeyboardOwned = await waitFor(() => !root.querySelector('.completion-popup'));

        input.value = 'using System; class C { void M() { string.Concat("a", ); } }';
        const signaturePosition = input.value.indexOf(', )') + 2;
        input.setSelectionRange(signaturePosition, signaturePosition);
        await dotNet.invokeMethodAsync('InputChanged', input.value, signaturePosition, ',');
        const signatureVisible = await waitFor(() => !!root.querySelector('.signature-popup'));
        const hoverPosition = input.value.indexOf('string.Concat');
        input.setSelectionRange(hoverPosition, hoverPosition);
        await dotNet.invokeMethodAsync('EditorCommand', 'hover', hoverPosition);
        const hoverVisible = await waitFor(() => !!root.querySelector('.hover-popup'));
        const semanticTokensPresent = await waitFor(() => !!root.querySelector('.token-method'));

        input.value = 'class C {';
        input.setSelectionRange(input.value.length, input.value.length);
        input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
        const autoIndent = input.value.endsWith('\n    ');
        input.value = 'class C { }';
        input.setSelectionRange(0, input.value.length);
        input.dispatchEvent(new KeyboardEvent('keydown', { key: '/', ctrlKey: true, bubbles: true, cancelable: true }));
        const commentToggle = input.value.startsWith('// ');

        input.value = 'class C{void M(){}}';
        input.setSelectionRange(0, 0);
        await dotNet.invokeMethodAsync('InputChanged', input.value, 0, null);
        await dotNet.invokeMethodAsync('EditorCommand', 'format', 0);
        const formattingApplied = await waitFor(() => input.value.startsWith('class C {'));
        return { completionVisible, completionKeyboardOwned, signatureVisible, hoverVisible, semanticTokensPresent,
            autoIndent, commentToggle, formattingApplied, loadingStateCleared: !root.querySelector('.language-state'),
            error: completionVisible ? null : `${completionDiagnostic}; provider returned ${completionItems} completion items` };
    } catch (error) {
        return { error: String(error) };
    }
}

export async function runPhase8Smoke(root) {
    const { input, dotNet } = root.__novaEditor;
    const workbench = root.closest('.workbench');
    const waitFor = async (predicate, attempts = 200) => {
        for (let attempt = 0; attempt < attempts; attempt++) {
            if (await predicate()) return true;
            await new Promise(resolve => setTimeout(resolve, 50));
        }
        return false;
    };
    try {
        if (!await waitFor(() => dotNet.invokeMethodAsync('LanguageReady'), 300))
            throw new Error('C# services did not finish loading');
        input.value = 'class C{C value; Missing missing;}';
        input.setSelectionRange(0, 0);
        await dotNet.invokeMethodAsync('InputChanged', input.value, 0, null);
        const diagnosticSquiggle = await waitFor(() => !!root.querySelector('.diagnostic-error'));
        workbench.querySelector('[aria-label="Problems"]')?.click();
        const problemsPanel = await waitFor(() => !!workbench.querySelector('.problems-panel .problem'));
        const typePosition = input.value.indexOf('C value');
        input.setSelectionRange(typePosition, typePosition);
        await dotNet.invokeMethodAsync('EditorCommand', 'peek', typePosition);
        const definitionPeek = await waitFor(() => !!root.querySelector('.navigation-popup code'));
        await dotNet.invokeMethodAsync('EditorCommand', 'outline', typePosition);
        const outline = await waitFor(() => !!root.querySelector('.outline-popup button'));
        await dotNet.invokeMethodAsync('EditorCommand', 'code-actions', typePosition);
        const codeActions = await waitFor(() => !!root.querySelector('.code-actions-popup button'));
        return { diagnosticSquiggle, problemsPanel, definitionPeek, outline, codeActions };
    } catch (error) {
        return { diagnosticSquiggle: false, problemsPanel: false,
            definitionPeek: false, outline: false, codeActions: false, error: String(error) };
    }
}

export async function runPhase15Smoke(root) {
    const { input, dotNet } = root.__novaEditor;
    const waitFor = async (predicate, attempts = 200) => {
        for (let attempt = 0; attempt < attempts; attempt++) {
            if (await predicate()) return true;
            await new Promise(resolve => setTimeout(resolve, 50));
        }
        return false;
    };
    try {
        if (!await waitFor(() => dotNet.invokeMethodAsync('LanguageReady'), 300))
            throw new Error('Web language services did not finish loading');
        const languageSelected = root.dataset.language === 'razor';
        input.value = '<MyC';
        input.setSelectionRange(4, 4);
        await dotNet.invokeMethodAsync('InputChanged', input.value, 4, 'C');
        await dotNet.invokeMethodAsync('EditorCommand', 'completion', 4);
        const componentCompletion = await waitFor(() => [...root.querySelectorAll('.completion-popup button')]
            .some(item => item.textContent.includes('MyCard')));
        input.value = '<style>.hero { color: red; }</style>\n@code { public int Value { get; set; } }';
        await dotNet.invokeMethodAsync('InputChanged', input.value, input.value.length, null);
        const semanticTokens = await waitFor(() => !!root.querySelector('.token-keyword'));
        input.value = '<div><span></div>';
        await dotNet.invokeMethodAsync('InputChanged', input.value, input.value.length, null);
        const diagnostics = await waitFor(() => !!root.querySelector('.diagnostic-error'));
        input.value = '<div>\n<span>Text</span>\n</div>';
        await dotNet.invokeMethodAsync('InputChanged', input.value, 0, null);
        await dotNet.invokeMethodAsync('EditorCommand', 'format', 0);
        const formatting = await waitFor(() => input.value.includes('\n    <span>'));
        return { languageSelected, componentCompletion, semanticTokens, diagnostics, formatting };
    } catch (error) {
        return { languageSelected: false, componentCompletion: false, semanticTokens: false,
            diagnostics: false, formatting: false, error: String(error) };
    }
}
