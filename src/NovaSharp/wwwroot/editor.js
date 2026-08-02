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
        if ((event.ctrlKey || event.metaKey) && ['s', 'o', 'f', 'z', 'y'].includes(event.key.toLowerCase())) {
            event.preventDefault();
            return;
        }
        if (event.key === 'Tab') {
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
    const selectionChange = () => dotNet?.invokeMethodAsync('SelectionChanged', input.selectionStart, input.selectionEnd);
    input.addEventListener('scroll', sync);
    input.addEventListener('keydown', keydown);
    input.addEventListener('compositionstart', compositionStart);
    input.addEventListener('input', compositionInput);
    input.addEventListener('compositionend', compositionEnd);
    input.addEventListener('select', selectionChange);
    input.addEventListener('keyup', selectionChange);
    input.addEventListener('pointerup', selectionChange);
    root.__novaEditor = { input, sync, keydown, compositionStart, compositionInput, compositionEnd, selectionChange };
}

export function disposeEditor(root) {
    const editor = root.__novaEditor;
    if (!editor) return;
    editor.input.removeEventListener('scroll', editor.sync);
    editor.input.removeEventListener('keydown', editor.keydown);
    editor.input.removeEventListener('compositionstart', editor.compositionStart);
    editor.input.removeEventListener('input', editor.compositionInput);
    editor.input.removeEventListener('compositionend', editor.compositionEnd);
    editor.input.removeEventListener('select', editor.selectionChange);
    editor.input.removeEventListener('keyup', editor.selectionChange);
    editor.input.removeEventListener('pointerup', editor.selectionChange);
    delete root.__novaEditor;
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
