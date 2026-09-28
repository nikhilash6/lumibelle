// Native popovers provide top-layer placement, outside dismissal and expanded state.
// Hover/focus previews can be pinned with a click, including on touch screens.
(() => {
    let dismissedWithEscape = false, active = null, pinned = false, openTimer, closeTimer;
    const triggerFor = element => element instanceof Element ? element.closest('.help-hint-trigger') : null;
    const partFor = element => element instanceof Element ? element.closest('.help-hint-trigger, .help-hint-content') : null;
    const clearTimers = () => { clearTimeout(openTimer); clearTimeout(closeTimer); };
    const show = (trigger, pin = false) => {
        clearTimers();
        const popup = document.getElementById(trigger.getAttribute('popovertarget'));
        if (!popup || !trigger.isConnected || trigger.closest('[hidden], [inert]')) return;
        if (active?.popup !== popup) pinned = false;
        active = { trigger, popup }; pinned ||= pin;
        if (!popup.matches(':popover-open')) popup.showPopover({ source: trigger });
    };
    const hide = () => {
        clearTimers();
        const popup = active?.popup;
        active = null; pinned = false;
        if (popup?.matches(':popover-open')) popup.hidePopover();
    };
    const closePreview = () => {
        clearTimeout(openTimer); clearTimeout(closeTimer);
        closeTimer = setTimeout(() => {
            if (!active || pinned || active.trigger.matches(':hover') || active.popup.matches(':hover') || active.trigger === document.activeElement) return;
            hide();
        }, 180);
    };
    document.addEventListener('pointerover', event => {
        if (event.pointerType !== 'mouse') return;
        const part = partFor(event.target);
        if (!part || part.contains(event.relatedTarget)) return;
        clearTimers();
        if (part.classList.contains('help-hint-trigger')) {
            openTimer = setTimeout(() => { if (part.matches(':hover')) show(part); }, 250);
        }
    });
    document.addEventListener('pointerout', event => {
        if (event.pointerType !== 'mouse') return;
        const part = partFor(event.target);
        if (part && !part.contains(event.relatedTarget)) closePreview();
    });
    document.addEventListener('focusin', event => {
        const trigger = triggerFor(event.target);
        if (trigger?.matches(':focus-visible')) show(trigger);
    });
    document.addEventListener('focusout', event => {
        if (active?.trigger === event.target) { pinned = false; closePreview(); }
    });
    document.addEventListener('click', event => {
        const trigger = triggerFor(event.target);
        if (!trigger) return;
        event.preventDefault(); // A hover preview should be pinned, not immediately toggled shut.
        if (active?.trigger === trigger && active.popup.matches(':popover-open') && pinned) hide();
        else show(trigger, true);
    });
    document.addEventListener('toggle', event => {
        if (event.target === active?.popup && event.newState === 'closed') {
            clearTimers(); active = null; pinned = false;
        }
    }, true);
    // Escape dismisses help before a containing Mud dialog or mobile drawer sees it.
    window.addEventListener('keydown', event => {
        if (event.key !== 'Escape') return;
        const open = document.querySelector('.help-hint-content:popover-open');
        if (!open && !dismissedWithEscape) return;
        hide();
        dismissedWithEscape = true;
        event.preventDefault();
        event.stopImmediatePropagation();
    }, true);
    window.addEventListener('keyup', event => {
        if (event.key !== 'Escape' || !dismissedWithEscape) return;
        dismissedWithEscape = false;
        event.preventDefault();
        event.stopImmediatePropagation();
    }, true);
    window.addEventListener('blur', () => { dismissedWithEscape = false; });
})();
