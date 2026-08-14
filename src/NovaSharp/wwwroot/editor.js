export function fitLanguagePopups(root) {
    for (const popup of root.querySelectorAll('.language-popup')) {
        if (!pinPopup(root, popup)) fitPopup(root, popup);
    }
}

export function scrollSelectedCompletionIntoView(root) {
    const popup = root.querySelector('.completion-popup');
    const selected = popup?.querySelector('.selected');
    if (!selected) return;
    if (selected.offsetTop < popup.scrollTop) popup.scrollTop = selected.offsetTop;
    else if (selected.offsetTop + selected.offsetHeight > popup.scrollTop + popup.clientHeight)
        popup.scrollTop = selected.offsetTop + selected.offsetHeight - popup.clientHeight;
}

function fitPopup(root, popup) {
    const margin = 8;
    const left = Math.max(margin, Math.min(Number.parseFloat(popup.style.left) || 0,
        root.clientWidth - popup.offsetWidth - margin));
    const top = Math.max(margin, Math.min(Number.parseFloat(popup.style.top) || 0,
        root.clientHeight - popup.offsetHeight - margin));
    popup.style.left = `${left}px`;
    popup.style.top = `${top}px`;
}

function pinPopup(root, popup) {
    if (!popup.matches('.completion-popup, .hover-popup')) return false;
    const workbench = root.closest('.workbench');
    const placement = ['topleft', 'topcenter', 'topright', 'leftcenter', 'rightcenter',
        'bottomleft', 'bottomcenter', 'bottomright']
        .find(value => workbench?.classList.contains(`popup-placement-${value}`));
    if (!placement) {
        for (const property of ['position', 'top', 'right', 'bottom', 'left', 'max-width', 'max-height', 'transform'])
            popup.style.removeProperty(property);
        return false;
    }
    const area = workbench.querySelector('.editor-area');
    const surface = area?.querySelector(':scope > .editor-split, :scope > .editor-group') ?? area;
    if (!surface) return false;
    const rect = surface.getBoundingClientRect();
    const margin = 8;
    const verticallyCentered = placement === 'leftcenter' || placement === 'rightcenter';
    const horizontallyCentered = placement === 'topcenter' || placement === 'bottomcenter';
    popup.style.setProperty('position', 'fixed', 'important');
    popup.style.setProperty('max-width', `${Math.max(260, rect.width - margin * 2)}px`, 'important');
    popup.style.setProperty('max-height', `${Math.max(80, rect.height - margin * 2)}px`, 'important');
    popup.style.setProperty('top', placement.startsWith('top') ? `${rect.top + margin}px`
        : verticallyCentered ? `${rect.top + rect.height / 2}px` : 'auto', 'important');
    popup.style.setProperty('bottom', placement.startsWith('bottom') ? `${innerHeight - rect.bottom + margin}px` : 'auto', 'important');
    popup.style.setProperty('left', placement.endsWith('left') || placement === 'leftcenter' ? `${rect.left + margin}px`
        : horizontallyCentered ? `${rect.left + rect.width / 2}px` : 'auto', 'important');
    popup.style.setProperty('right', placement.endsWith('right') || placement === 'rightcenter'
        ? `${innerWidth - rect.right + margin}px` : 'auto', 'important');
    popup.style.setProperty('transform', verticallyCentered ? 'translateY(-50%)'
        : horizontallyCentered ? 'translateX(-50%)' : 'none', 'important');
    return true;
}
