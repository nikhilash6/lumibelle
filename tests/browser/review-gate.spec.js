import { test, expect } from './fixtures.js';

test('automatic review requires the originating visible page and never stacks or defers dialogs', async ({ page }) => {
  await page.goto('/');
  const result = await page.evaluate(async () => {
    const gate = await import('/_content/Lumibelle.UI/ai-jobs.js');
    const origin = document.createElement('button'); document.body.append(origin);
    const ownTab = gate.tabId();
    Object.defineProperty(document, 'hasFocus', { value: () => false });
    const tryAuto = (id, tab = ownTab) => gate.tryReview(id, tab, origin, true);
    const wrongTab = tryAuto('other-tab', crypto.randomUUID());

    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
    const hidden = tryAuto('hidden');
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
    const returned = tryAuto('hidden');

    const dialog = document.createElement('dialog'); document.body.append(dialog); dialog.showModal();
    const anotherDialog = tryAuto('dialog');
    dialog.close(); dialog.remove();
    const afterDialog = tryAuto('dialog');

    const visible = tryAuto('visible');
    const concurrent = tryAuto('concurrent');
    gate.closeReview('visible');
    const closed = tryAuto('visible');
    const manual = gate.tryReview('visible', ownTab, origin, false);
    gate.closeReview('visible');
    origin.remove();
    const navigated = tryAuto('navigated');
    return { wrongTab, hidden, returned, anotherDialog, afterDialog, visible, concurrent, closed, manual, navigated };
  });
  expect(result).toEqual({ wrongTab: false, hidden: false, returned: false, anotherDialog: false, afterDialog: false,
    visible: true, concurrent: false, closed: false, manual: true, navigated: false });
});
