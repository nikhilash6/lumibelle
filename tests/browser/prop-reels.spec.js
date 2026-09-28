import { test, expect } from './fixtures.js';
const library = async (request, id) => (await (await request.get(`/fixtures/${id}`)).json()).assets;
async function tools(page) {
  const toggle = page.locator('[data-toggle-pane=right]');
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expect(async () => { if (!await page.locator('.workspace-right').isVisible()) await toggle.click({ timeout: 1000 }); }).toPass({timeout:10000});
}
for (const narrow of [false, true]) test(`prop camera presets orbit, turn and hold the object (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(90000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`); await request.post(`/fixtures/${id}/prop-reel-owner`);
  const asset = (await library(request, id)).assets[0];
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/assets?assetId=${asset.id}&view=reels`);
  await expect(page.getByLabel('Filter media', { exact: true })).toHaveValue('Reels');
  await expect(page.locator('.asset-import-menu button', { hasText: 'Import reel' })).toHaveCount(1);
  await tools(page);
  const editor = page.locator('.reel-tools');
  await expect(editor.locator('[data-prompt-ready]')).toHaveAttribute('data-prompt-ready', 'true');
  await expect(editor.getByRole('combobox', { name: 'Aspect', exact: true })).toHaveValue('1:1');
  await expect(editor.getByLabel('Requested seconds', { exact: true })).toHaveValue('15');
  await expect(editor.getByText('Silent visual reference · no dialogue or audio inputs.')).toBeVisible();
  await expect(editor.getByLabel('Voice mode')).toHaveCount(0);
  await expect(editor.getByLabel('Save to look')).toHaveCount(0);
  await editor.getByRole('button', { name: 'Reel assistance', exact: true }).click();
  const assist = page.getByRole('dialog', { name: 'Reel assistance', exact: true });
  await expect(assist.getByLabel('Camera preset', { exact: true })).toHaveValue('PropOrbit');
  const presets = [
    ['PropHalfOrbit', 'Half orbit', 8, true], ['PropTurntable', 'Turntable', 12, true], ['PropHeldViews', 'Held angles', 12, false],
    ['PropRise', 'Rise to top view', 6, false], ['PropDetail', 'Detail pass', 6, false], ['PropCustom', 'Custom move', 10, false],
    ['PropOrbit', '360° orbit', 15, true]
  ];
  const framings = { PropOrbit: 21, PropHalfOrbit: 22, PropTurntable: 23, PropHeldViews: 24, PropRise: 25, PropDetail: 26, PropCustom: 27 };
  for (const [value, label, seconds, direction] of presets) {
    await assist.getByLabel('Camera preset', { exact: true }).selectOption({ label });
    await expect(assist.getByLabel('Camera duration (seconds)', { exact: true })).toHaveValue(String(seconds));
    await expect(assist.getByLabel('Camera direction', { exact: true })).toHaveCount(direction ? 1 : 0);
    await expect(assist.getByText(/Use keyframes to reference all angles/)).toHaveCount(seconds >= 10 ? 1 : 0);
    await expect.poll(async () => (await library(request, id)).reelDrafts[0]?.framing).toBe(framings[value]);
    expect((await library(request, id)).reelDrafts[0].presetVersion).toBe('prop-reel-v1');
  }
  await expect(assist.locator('.reel-camera-summary')).toContainText('Circle the stationary prop');
  await assist.getByText('Camera timing', { exact: true }).click();
  await expect(assist.locator('.reel-framing-plan')).toContainText('orbits right around the stationary prop');
  await expect(assist.locator('.reel-framing-plan')).toContainText('The prop itself never rotates');
  await assist.getByLabel('Camera direction').selectOption('Left');
  await expect(assist.locator('.reel-framing-plan')).toContainText('orbits left around the stationary prop');
  await assist.getByLabel('Camera preset').selectOption('PropTurntable');
  await expect(assist.locator('.reel-framing-plan')).toContainText('concealed turntable');
  await assist.getByLabel('Camera preset').selectOption('PropHeldViews');
  await expect(assist.locator('.reel-framing-plan')).toContainText('[Shot 4]');
  await expect(assist.locator('.reel-framing-plan')).toContainText('the rear view');
  await assist.getByLabel('Camera preset').selectOption('PropCustom');
  await expect(assist.getByText('Describe the camera move in Instructions: starting view, path around the prop and end view.', { exact: true })).toBeVisible();
  await page.screenshot({ path: `artifacts/prop-presets-${narrow ? 'narrow' : 'desktop'}.png` });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('prop reel composes, generates and is offered to Shots', async ({ page, request }) => {
  test.setTimeout(150000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images?patterned=true`); await request.post(`/fixtures/${id}/prop-reel-owner`);
  const asset = (await library(request, id)).assets[0];
  await page.setViewportSize({ width: 1173, height: 1000 });
  await page.goto(`/projects/${id}/assets?assetId=${asset.id}&view=reels`); await tools(page);
  const editor = page.locator('.reel-tools');
  await expect(editor.locator('[data-prompt-ready]')).toHaveAttribute('data-prompt-ready', 'true');
  await editor.getByRole('button', { name: 'Manage reel references' }).click();
  const pictures = page.locator('.reel-pictures-dialog');
  await pictures.locator(`[data-reference="${asset.id}/${asset.images[0].id}"]`).click();
  await pictures.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await editor.getByLabel('Reel name', { exact: true }).fill('Armchair orbit');
  const prompt = editor.getByRole('textbox', { name: 'H3 prompt', exact: true });
  await editor.getByRole('button', { name: 'Reel assistance', exact: true }).click();
  const assist = page.locator('.ai-assist-dialog');
  await assist.locator('.model-chip').click();
  await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await page.getByRole('button', { name: 'Set as project default', exact: true }).click();
  await assist.getByRole('button', { name: 'Compose pair', exact: true }).click();
  await expect(assist).not.toBeVisible();
  await expect(prompt).toContainText('prop reference reel');
  const composed = (await (await request.get('/fixtures/reel-compositions')).json()).find(c => c.context.prop?.assetId === asset.id);
  expect(composed.context.prop.visualNotes).toBe(asset.description);
  expect(composed.context.presetViews).toContain('orbits right around the stationary prop');
  const draft = (await library(request, id)).reelDrafts[0];
  expect(draft.presetVersion).toBe('prop-reel-v1'); expect(draft.voiceMode).toBe(2);
  expect(draft.useGuidance).toContain('Visual prop reference'); expect(draft.prompt).not.toContain('<Audio');
  await editor.getByRole('button', { name: 'Generate reel', exact: true }).click();
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(1);
  const reel = (await library(request, id)).reels[0];
  expect(reel.generation.snapshot.profile).toBe('prop-reel-v1'); expect(reel.assetId).toBe(asset.id);
  await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/production-shot`);
  await page.goto(`/projects/${id}/shots`); await tools(page);
  await page.getByRole('button', { name: 'Manage references', exact: true }).click();
  const picker = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Manage references', exact: true }) });
  const card = picker.locator(`[data-reel-id="${reel.id}"]`);
  await expect(card).toContainText('Prop reel');
  await card.locator('.add-reel').click();
  await expect(picker.getByLabel('Visuals', { exact: true })).toHaveValue('Keyframes');
  await picker.getByLabel('Visuals', { exact: true }).selectOption('FullReel');
  await picker.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(picker).not.toBeVisible();
  const setup = (await (await request.get(`/fixtures/${id}/production`)).json()).compositions[0];
  expect(setup.inputs.videos[0].media.id).toBe(reel.media.id);
  expect(setup.inputs.videos[0].ownerCategory).toBe(2);
  expect(setup.inputs.videos[0].useSoundtrack).toBe(false);
});

test('a short window leaves the reel recipe room to scroll between its fixed header and Generate footer', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`); await request.post(`/fixtures/${id}/prop-reel-owner`);
  const asset = (await library(request, id)).assets[0];
  await page.setViewportSize({ width: 1150, height: 745 });
  await page.goto(`/projects/${id}/assets?assetId=${asset.id}&view=reels`);
  await tools(page);
  const editor = page.locator('.reel-tools');
  await expect(editor.locator('[data-prompt-ready]')).toHaveAttribute('data-prompt-ready', 'true');
  // The headers and footer squeezed the recipe to about 160 pixels; the compact footer and headers keep it usable.
  expect(await editor.locator('.workspace-pane-body').evaluate(b => b.clientHeight)).toBeGreaterThanOrEqual(240);
  await expect(editor.locator('.workspace-pane-body').getByText(/^Use Assist to choose a camera preset/)).toHaveCount(1);
  await expect(editor.locator('.workspace-pane-footer').getByText('Generate needs a prompt and use guidance.', { exact: true })).toBeVisible();
  await expect(editor.getByRole('button', { name: 'Generate reel', exact: true })).toBeInViewport();
});
