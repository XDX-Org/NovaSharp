function rows() {
    return Array.from(document.querySelectorAll('.workspace-tree .tree-row'));
}

function focus(id, movement) {
    const all = rows();
    const current = all.find(row => row.closest('.tree-item')?.dataset.nodeId === id);
    if (!current) return;

    const index = all.indexOf(current);
    let target;
    if (movement === 'next') target = all[index + 1];
    else if (movement === 'previous') target = all[index - 1];
    else if (movement === 'first') target = all[0];
    else if (movement === 'last') target = all[all.length - 1];
    else if (movement === 'child') target = current.closest('.tree-item')?.querySelector(':scope > [role="group"] > .tree-item > .tree-row');
    else if (movement === 'parent') target = current.closest('[role="group"]')?.closest('.tree-item')?.querySelector(':scope > .tree-row');
    target?.focus();
}

globalThis.NovaWorkspace = Object.freeze({ focus });
