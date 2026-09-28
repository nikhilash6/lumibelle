import { test, expect } from './fixtures.js';
import { toolsTab } from './workspace-tools.js';

const dialog = page => page.locator('.image-move-dialog');
async function setup(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  const initial = await (await request.post(`/fixtures/${id}/images`)).json();
  await page.goto(`/projects/${id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  return { id, initial, library: async () => (await (await request.get(`/fixtures/${id}`)).json()).assets };
}

test('move selection creates a normal reference asset, preserves images, and supports keyboard cancellation on mobile', async ({ page, request }) => {
  const { id, initial, library } = await setup(page, request);
  expect((await request.post(`/fixtures/${id}/approved`)).ok()).toBe(true);
  expect((await request.post(`/fixtures/${id}/reference-setups`)).ok()).toBe(true);
  await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  const before = await library(); const source = before.assets[0];
  await page.getByRole('button',{name:'Select multiple',exact:true}).click();
  const choices = page.getByRole('checkbox', { name: 'Select image', exact: true });
  await expect(choices).toHaveCount(source.images.length);
  for (let i = 0; i < source.images.length; i++) {
    await choices.nth(i).check(); await expect(page.locator('.look-bulk')).toContainText(`${i + 1} selected`);
  }
  await page.getByRole('button', { name: 'Move selected to asset…', exact: true }).click();
  await expect(dialog(page).getByLabel('Destination asset')).toBeFocused();
  await page.setViewportSize({ width: 390, height: 844 });
  for (let i = 0; i < 7; i++) { await page.keyboard.press('Tab'); await expect.poll(() => dialog(page).evaluate(el => el.contains(document.activeElement))).toBe(true); }
  await page.keyboard.press('Escape'); await expect(dialog(page)).not.toBeVisible();
  expect((await library()).assets[0].images.map(i => i.id)).toEqual(source.images.map(i => i.id));
  await expect(page.getByRole('button', { name: 'Move selected to asset…', exact: true })).toBeFocused();
  await page.getByRole('button', { name: 'Move selected to asset…', exact: true }).click();
  await dialog(page).getByLabel('Destination asset').selectOption('new');
  await dialog(page).getByLabel('New asset name').fill('Selected frames with a deliberately long name for a continuity collection');
  await expect(dialog(page).getByRole('button', { name: 'Move and edit', exact: true })).toHaveCount(0);
  expect(await dialog(page).evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
  await page.screenshot({ path: 'test-results/image-move-mobile.png' });
  await dialog(page).getByRole('button', { name: 'Move', exact: true }).click();
  await expect(dialog(page)).not.toBeVisible();
  await expect(page.locator('.asset-list-row.selected')).toContainText('Selected frames');
  const saved = await library(); const target = saved.assets.at(-1);
  expect(target.category).toBe(3); expect(target.images.map(i => i.id)).toEqual(source.images.map(i => i.id));
  expect(saved.assets[0].images).toHaveLength(0); expect(target.images.every(i => i.lookId === null)).toBe(true);
  await expect(page.locator('.reference-card')).toHaveCount(source.images.length);
  for (const image of target.images) expect((await request.get(`/media/projects/${id}/assets/${target.id}/images/${image.id}`)).ok()).toBe(true);
  await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await page.locator('[data-toggle-pane="left"]').click();
  await page.locator('.asset-choice').filter({ hasText: 'Selected frames' }).click();
  const closeLibrary = page.locator('.workspace-left [data-close-pane]');
  if (await closeLibrary.isVisible()) await closeLibrary.click();
  await page.getByRole('button', { name: 'Image actions', exact: true }).first().click();
  await page.getByRole('menuitem', { name: 'Move to asset…', exact: true }).click();
  await dialog(page).getByLabel('Destination asset').selectOption(initial.assets[0].id);
  await dialog(page).getByLabel('Destination look').selectOption(source.looks[0].id);
  await dialog(page).getByRole('button', { name: 'Move and edit', exact: true }).click();
  await expect(dialog(page)).not.toBeVisible();
  await expect(page.getByLabel('Edit instruction')).toBeFocused();
  await expect(page.getByLabel('Save results to look',{exact:true})).toHaveCount(0);
});

test('moved edited take reopens with its source, generates in the new asset, and survives Trash', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  const { id, initial, library } = await setup(page, request);
  const source = initial.assets[0], target = initial.assets[1];
  await page.locator('.asset-media-card[data-media-kind=Image] .media-select').click();
  await toolsTab(page, 'Prompt'); await page.getByLabel('Edit instruction').fill('Make the coat blue.');
  await page.getByRole('button', { name: 'Generate edited images', exact: true }).click();
  const review = page.locator('.image-review-dialog');
  await expect(review).toBeVisible(); await review.getByRole('button', { name: 'Close image review' }).click();
  const generated = (await library()).assets[0].images.find(i => i.generation?.edit);
  const card = page.locator('.reference-card').filter({ has: page.locator(`img[src$="/${generated.id}"]`) });
  await card.getByRole('button', { name: 'Image actions', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Move to asset…', exact: true }).click();
  await dialog(page).getByLabel('Destination asset').selectOption(target.id);
  await dialog(page).getByRole('button', { name: 'Move', exact: true }).click();
  await expect(page.locator('.asset-list-row.selected')).toContainText(target.name);
  const moved = (await library()).assets[1].images.find(i => i.id === generated.id);
  expect(moved.generation).toEqual(generated.generation);
  await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await page.locator('.asset-choice').filter({ hasText: target.name }).click();
  await card.getByRole('button',{name:/^Preview image/}).click();
  await expect(review.locator('.review-image-select strong')).toHaveText(['Source', 'Take']);
  await review.getByRole('button', { name: 'Compare with Source', exact: true }).click();
  await expect(review.locator('.review-wipe')).toBeVisible();
  await review.getByRole('button', { name: 'Close image review' }).click();
  await card.locator('.media-select').click();
  await toolsTab(page, 'Prompt'); await page.getByLabel('Edit instruction').fill('Add silver buttons.');
  await page.getByRole('button', { name: 'Generate edited images', exact: true }).click();
  await expect(review).toBeVisible(); await review.getByRole('button', { name: 'Close image review' }).click();
  const after = await library();
  expect(after.assets[0].images.map(i => i.id)).toEqual([source.images[0].id]);
  expect(after.assets[1].images.find(i => i.generation?.edit?.sourceImageId === generated.id).generation.edit.sourceAssetId).toBe(target.id);
  await card.getByRole('button', { name: 'Image actions', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Move to Trash', exact: true }).click();
  await expect.poll(async () => (await library()).trash.some(t => t.image.id === generated.id)).toBe(true);
  await page.getByRole('link', { name: 'Trash', exact: true }).click();
  await page.locator('.trash-toolbar select').selectOption(id);
  const deleted = (await library()).trash.find(t => t.image.id === generated.id);
  const trashed = page.locator('.trash-card').filter({ has: page.locator(`img[src$="/${deleted.id}"]`) });
  await trashed.getByRole('button', { name: 'Restore', exact: true }).click();
  await expect.poll(async () => (await library()).assets[1].images.some(i => i.id === generated.id)).toBe(true);
});
