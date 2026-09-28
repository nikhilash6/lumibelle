import { test, expect } from './fixtures.js';

// Exercise saved scroll against a workspace whose content arrives after the normal settle window.
test('slow workspace loading preserves saved scroll until content arrives or navigation leaves', async ({ page }) => {
  await page.goto('/');
  await page.evaluate(async () => {
    const position = await import('/_content/Lumibelle.UI/workspace-position.js');
    const key = `loading-test-${crypto.randomUUID()}`;
    position.write(key, { scroll: { 'center:asset': { top: 650, left: 0 } }, tabs: {} });
    const root = document.createElement('section');
    root.setAttribute('aria-busy', 'true');
    root.style.cssText = 'position:fixed;inset:100px auto auto 20px;width:400px;background:white';
    root.innerHTML = '<div class="workspace-center"><div class="workspace-pane-body" style="flex:none;height:200px;overflow:auto">Loading assets…</div></div>';
    document.body.append(root);
    window.loadingFixture = { root, key, position, controller: position.attach(root, key, () => 'asset') };
  });
  await page.waitForTimeout(2000);
  expect(await page.evaluate(() => {
    const f = window.loadingFixture;
    f.root.dispatchEvent(new PointerEvent('pointerdown'));
    f.controller.dispose();
    return JSON.parse(localStorage.getItem(f.key)).scroll['center:asset'].top;
  })).toBe(650);
  await page.evaluate(() => {
    const f = window.loadingFixture;
    f.controller = f.position.attach(f.root, f.key, () => 'asset');
    f.root.querySelector('.workspace-pane-body').innerHTML = '<div style="height:2000px">Loaded references</div>';
    f.root.setAttribute('aria-busy', 'false');
  });
  await expect.poll(() => page.evaluate(() => window.loadingFixture.root.querySelector('.workspace-pane-body').scrollTop)).toBe(650);
  await page.evaluate(() => { const f = window.loadingFixture; f.controller.dispose(); f.root.remove(); localStorage.removeItem(f.key); });
});
