// Blazor handles selection and focus; suppress native scrolling only for navigation keys.
document.addEventListener('keydown', event => {
    const tab = event.target.closest?.('[role="tab"]');
    if (!tab) return;
    const list = tab.parentElement;
    const arrows = list?.classList.contains('ai-settings-tabs') ? ['ArrowLeft', 'ArrowRight']
        : list?.classList.contains('ai-provider-sidebar') ? ['ArrowUp', 'ArrowDown'] : null;
    if (arrows && [...arrows, 'Home', 'End'].includes(event.key)) event.preventDefault();
});

// Re-selecting the current settings page must not add a duplicate history entry.
document.addEventListener('click', event => {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    const link = event.target.closest?.('a[data-ai-settings-link]');
    if (link?.href === window.location.href) event.preventDefault();
}, true);

// Keep the selected section visible when the tab row becomes scrollable.
window.addEventListener('resize', () => {
    const list = document.querySelector('.ai-settings-tabs');
    const selected = list?.querySelector('[aria-selected="true"]');
    if (!selected) return;
    const row = list.getBoundingClientRect(), tab = selected.getBoundingClientRect();
    if (tab.left < row.left) list.scrollLeft -= row.left - tab.left;
    else if (tab.right > row.right) list.scrollLeft += tab.right - row.right;
});
