// Inline dialogs render their focus-trap sentinels outside DialogContent.
// Listen on the actual dialog so Escape also works after wrapping keyboard focus.
export function attach(button) {
    const dialog = button.closest('.mud-dialog');
    const keydown = event => {
        if (event.key !== 'Escape' || event.target.closest('.mud-dialog') !== dialog || document.fullscreenElement) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        button.click();
    };
    dialog?.addEventListener('keydown', keydown, true);
    return { dispose() { dialog?.removeEventListener('keydown', keydown, true); } };
}
