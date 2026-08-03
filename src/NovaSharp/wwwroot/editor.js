export function createEditor(root, wordWrap, dotNet, selectionStart, selectionEnd, scrollTop, scrollLeft) {
    const input = root.querySelector('.editor-input');
    const presentation = root.querySelector('.presentation');
    input.wrap = wordWrap ? 'soft' : 'off';
    input.setSelectionRange(selectionStart, selectionEnd);
    input.scrollTop = scrollTop;
    input.scrollLeft = scrollLeft;

    const sync = () => {
        presentation.scrollTop = input.scrollTop;
        presentation.scrollLeft = input.scrollLeft;
        dotNet.invokeMethodAsync('ScrollChanged', input.scrollTop, input.scrollLeft);
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
    const selectionChange = () => dotNet?.invokeMethodAsync('SelectionChanged', input.selectionStart, input.selectionEnd);
    input.addEventListener('scroll', sync);
    input.addEventListener('keydown', keydown);
    input.addEventListener('compositionstart', compositionStart);
    input.addEventListener('input', compositionInput);
    input.addEventListener('input', inputChanged);
    input.addEventListener('compositionend', compositionEnd);
    input.addEventListener('select', selectionChange);
    input.addEventListener('keyup', selectionChange);
    const pointerup = () => {
        selectionChange();
        dotNet.invokeMethodAsync('EditorCommand', 'hover', input.selectionStart);
    };
    input.addEventListener('pointerup', pointerup);
    root.__novaEditor = { input, dotNet, sync, keydown, compositionStart, compositionInput, inputChanged, compositionEnd, selectionChange, pointerup };
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
    editor.input.removeEventListener('pointerup', editor.pointerup);
    delete root.__novaEditor;
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
    const rowLimit = Math.ceil(root.clientHeight / 20) + 18;
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
    const waitFor = async predicate => {
        for (let attempt = 0; attempt < 100; attempt++) {
            if (predicate()) return true;
            await new Promise(resolve => setTimeout(resolve, 50));
        }
        return false;
    };
    try {
        input.value = 'using System; class C { void M() { Con } }';
        input.setSelectionRange(input.value.indexOf('Con }') + 3, input.value.indexOf('Con }') + 3);
        await dotNet.invokeMethodAsync('InputChanged', input.value, input.selectionStart, null);
        await dotNet.invokeMethodAsync('EditorCommand', 'completion', input.selectionStart);
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
            autoIndent, commentToggle, formattingApplied, loadingStateCleared: !root.querySelector('.language-state') };
    } catch (error) {
        return { error: String(error) };
    }
}
