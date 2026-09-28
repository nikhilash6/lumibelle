import { test, expect } from './fixtures.js';
import { toolsTab, imageOutput } from './workspace-tools.js';

async function setup(page, request) {
  const project = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${project.id}/images`)).json();
  await page.goto(`/projects/${project.id}/assets`);
  await toolsTab(page, 'Prompt');
  return { project, library };
}
// Tags and seed live in the Image settings disclosure; aspect, takes and Generate stay in the footer.
const settings = page => page.locator('.generation-panel .image-generation-settings');
async function openSettings(page) {
  if (await settings(page).getAttribute('open') === null) await settings(page).locator('summary').click();
  await expect(settings(page)).toHaveAttribute('open', '');
}
async function closeSettings(page) {
  if (await settings(page).getAttribute('open') !== null) await settings(page).locator('summary').click();
  await expect(settings(page)).not.toHaveAttribute('open', '');
}

test('footer output choices follow each asset draft across tool tabs and reach generation', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  const { project, library } = await setup(page, request);
  const footer = page.locator('.generation-panel .workspace-pane-footer');
  const tags = page.locator('.generation-panel').getByLabel('Tags for these takes', { exact: true });
  await expect(page.getByRole('tab', { name: 'Output', exact: true })).toHaveCount(0);
  await footer.getByRole('combobox', { name: 'Aspect', exact: true }).selectOption('16:9');
  await footer.getByRole('combobox', { name: 'Takes', exact: true }).selectOption('2');
  await openSettings(page);
  await tags.fill('portrait, warm light');
  await page.locator('.generation-panel').getByRole('button', { name: '+ face', exact: true }).click();
  await expect(tags).toHaveValue('portrait, warm light, face');
  await toolsTab(page, 'Inputs');
  await expect(footer.getByRole('combobox', { name: 'Aspect', exact: true })).toHaveValue('16:9');
  await page.locator('.asset-choice').filter({ hasText: library.assets[1].name }).click();
  await expect(page.locator('.asset-list-row.selected')).toHaveAttribute('data-asset-id', library.assets[1].id);
  await footer.getByRole('combobox', { name: 'Aspect', exact: true }).selectOption('4:3');
  await footer.getByRole('combobox', { name: 'Takes', exact: true }).selectOption('1');
  await page.locator('.asset-choice').filter({ hasText: library.assets[0].name }).click();
  await expect(page.locator('.asset-list-row.selected')).toHaveAttribute('data-asset-id', library.assets[0].id);
  await expect(footer.getByRole('combobox', { name: 'Aspect', exact: true })).toHaveValue('16:9');
  await expect(footer.getByRole('combobox', { name: 'Takes', exact: true })).toHaveValue('2');
  await expect(tags).toHaveValue('portrait, warm light, face');
  await toolsTab(page, 'Prompt');
  await page.getByLabel('Image prompt', { exact: true }).fill('A portrait in warm light.');
  await footer.getByRole('button', { name: 'Generate images', exact: true }).click();
  const review = page.locator('.image-review-dialog');
  await expect(review.getByRole('button', { name: 'View Take 2', exact: true })).toBeVisible();
  const state = (await (await request.get(`/fixtures/${project.id}`)).json()).assets;
  const generated = state.assets[0].images.filter(i => i.generation?.prompt === 'A portrait in warm light.');
  expect(generated).toHaveLength(2);
  for (const image of generated) {
    expect(image.generation.aspectRatio).toBe('16:9');
    expect(image.tags).toEqual(['portrait', 'warm light', 'face']);
  }
  await review.getByRole('button', { name: 'Close image review', exact: true }).click();
  await page.setViewportSize({ width: 1280, height: 720 });
  await expect(footer.getByRole('button', { name: 'Review latest images', exact: true })).toBeInViewport();
  await expect(footer.getByRole('button', { name: 'Generate images', exact: true })).toBeInViewport({ ratio: 1 });
});

test('footer controls and generation stay visible with expanded options on desktop and mobile', async ({ page, request }) => {
  await setup(page, request);
  const footer = page.locator('.generation-panel .workspace-pane-footer');
  const generate = footer.getByRole('button', { name: 'Generate images', exact: true });
  for (const [width, height] of [[1173, 1272], [1280, 720], [587, 636], [390, 844]]) {
    await page.setViewportSize({ width, height });
    await imageOutput(page);
    await expect(footer.getByRole('combobox', { name: 'Aspect', exact: true })).toBeInViewport();
    await expect(footer.getByRole('combobox', { name: 'Takes', exact: true })).toBeInViewport();
    await openSettings(page);
    await page.locator('.generation-panel').getByLabel('Tags for these takes', { exact: true }).fill('portrait');
    await page.locator('.generation-panel #fixed-seed').fill('42');
    await expect(generate).toBeInViewport({ ratio: 1 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `test-results/asset-output-expanded-${width}.png` });
    await closeSettings(page);
    await toolsTab(page, 'Inputs');
    const before = await generate.boundingBox();
    await toolsTab(page, 'Prompt');
    await page.getByLabel('Image prompt', { exact: true }).scrollIntoViewIfNeeded();
    const after = await generate.boundingBox();
    expect(Math.abs(after.y - before.y)).toBeLessThan(1);
    await expect(generate).toBeInViewport({ ratio: 1 });
    await page.screenshot({ path: `test-results/asset-output-footer-${width}.png` });
  }
});
