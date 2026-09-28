import { test, expect } from './fixtures.js';

for (const narrow of [false, true]) test(`outline reveals section headings and opening text (${narrow ? 'phone' : 'desktop'})`, async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  const script = await (await request.post(`/fixtures/${id}/revision-script`)).json();
  const scenes = script.blocks.filter(block => block.kind === 'Scene');
  const acts = script.blocks.filter(block => block.kind === 'Act');
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1272 });
  await page.goto(`/projects/${id}/script`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  const editor = page.getByRole('textbox', { name: 'Screenplay', exact: true });
  await expect(editor).toBeVisible();

  // Navigate down, back up to an act using the keyboard, to the last scene, and back to the beginning.
  for (const target of [scenes[3], acts[1], scenes.at(-1), scenes[0]]) {
    if (narrow) await page.getByRole('button', { name: 'Show Outline', exact: true }).click();
    const button = page.locator(`[data-outline-id="${target.id}"] .outline-title`);
    if (target.kind === 'Act') { await button.focus(); await button.press('Enter'); }
    else await button.click();
    await expect(editor).toBeFocused();
    if (narrow) await expect(page.locator('.workspace-left')).not.toBeVisible();
    const heading = editor.locator(`[data-block-id="${target.id}"]`);
    await expect.poll(() => heading.evaluate(node => {
      const pane = node.closest('.workspace-pane-body');
      const offset = node.getBoundingClientRect().top - pane.getBoundingClientRect().top;
      const atEnd = Math.abs(pane.scrollHeight - pane.clientHeight - pane.scrollTop) <= 2;
      return offset >= 0 && (offset <= 26 || atEnd);
    })).toBe(true);
    // The next paragraph must be visible as well; a heading alone at the bottom is insufficient.
    await expect(heading.locator('xpath=following-sibling::p[1]')).toBeInViewport({ ratio: 1 });
    expect(await page.evaluate(() => window.scrollY)).toBe(0);
    expect(await editor.evaluate(el => getSelection()?.anchorNode?.parentElement?.closest('[data-block-id]')?.dataset.blockId)).toBe(target.id);
    if (target.id === scenes.at(-1).id) await page.screenshot({ path: `artifacts/script-outline-${narrow ? 'phone' : 'desktop'}.png` });
  }
  expect((await (await request.get(`/fixtures/${id}`)).json()).script.blocks).toEqual(script.blocks);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});
