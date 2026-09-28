// MudDialog names a dialog after its whole title area, including the context line and close button.
// Name it by its heading and describe it by its summary instead.
export function label(heading, summary) {
    const dialog = heading?.closest('[role=dialog]');
    if (!dialog) return;
    dialog.setAttribute('aria-labelledby', heading.id);
    if (summary) dialog.setAttribute('aria-describedby', summary.id);
    else dialog.removeAttribute('aria-describedby');
}
