export function createEditor(root, wordWrap) {
    const input = root.querySelector('.editor-input');
    const presentation = root.querySelector('.presentation');
    input.wrap = wordWrap ? 'soft' : 'off';

    const sync = () => {
        presentation.scrollTop = input.scrollTop;
        presentation.scrollLeft = input.scrollLeft;
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
    input.addEventListener('scroll', sync);
    input.addEventListener('keydown', keydown);
    input.addEventListener('compositionstart', compositionStart);
    input.addEventListener('input', compositionInput);
    input.addEventListener('compositionend', compositionEnd);
    root.__novaEditor = { input, sync, keydown, compositionStart, compositionInput, compositionEnd };
}

export function disposeEditor(root) {
    const editor = root.__novaEditor;
    if (!editor) return;
    editor.input.removeEventListener('scroll', editor.sync);
    editor.input.removeEventListener('keydown', editor.keydown);
    editor.input.removeEventListener('compositionstart', editor.compositionStart);
    editor.input.removeEventListener('input', editor.compositionInput);
    editor.input.removeEventListener('compositionend', editor.compositionEnd);
    delete root.__novaEditor;
}
