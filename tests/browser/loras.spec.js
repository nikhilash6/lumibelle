import { test, expect } from './fixtures.js';
import { imageOutput, toolsTab } from './workspace-tools.js';
test.use({ actionTimeout: 10000 });

test('register and stack LoRAs, switch workflows, reopen, and recover a missing file', async ({ page, request }) => {
  test.setTimeout(180000);
  await page.setViewportSize({ width: 1440, height: 1000 });
  const project = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${project.id}/images`)).json();
  const { slug } = await (await request.get(`/fixtures/${project.id}/project-route`)).json();
  const state = async () => (await (await request.get(`/fixtures/${project.id}`)).json()).assets;
  await page.goto(`/settings/ai?projectId=${project.id}&returnTo=assets&tab=loras`);
  await page.getByRole('button', { name: 'Refresh installed LoRAs', exact: true }).click();
  await expect(page.getByRole('tab', { name: 'LoRAs', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('tab', { name: 'LoRAs', exact: true }).focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('tab', { name: 'Connections', exact: true })).toBeFocused();
  await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('tab', { name: 'Text models', exact: true })).toBeFocused();
  await page.getByRole('tab', { name: 'Image models', exact: true }).click();
  await expect(page.locator('.lora-library')).toBeHidden();
  await page.getByRole('tab', { name: 'LoRAs', exact: true }).click();
  const definitions = [
    ['character.safetensors', 'Mouse identity', 'Krea2', 'juniper character', '.75', 'sfw, character'],
    ['styles/ink.safetensors', 'Ink illustration', 'Krea2', 'ink drawing', '1.2', 'sfw, nsfw'],
    ['klein/style.safetensors', 'Klein illustration', 'Flux2Klein9bKv', '', '.6', 'sfw, style'],
  ];
  for (const [file, name, workflow, trigger, strength, tags] of definitions) {
    await page.getByRole('button', { name: 'Add registration', exact: true }).click();
    await page.locator('#lora-file-search').fill(file.split('/').pop());
    await page.locator('#lora-file').selectOption(file);
    await page.locator('#lora-name').fill(name);
    await page.locator('#lora-workflow').selectOption(workflow);
    await page.locator('#lora-default-strength').fill(strength);
    await page.locator('#lora-trigger').fill(trigger);
    await page.locator('#lora-tags').fill(tags);
    await page.getByRole('button', { name: 'Save LoRA library', exact: true }).click();
    await expect(page.locator('.lora-registration')).toHaveCount(0);
  }
  await page.locator('#lora-library-tag').selectOption('tag:nsfw');
  await expect(page.locator('.lora-library .lora-row')).toHaveCount(1);
  await expect(page.locator('.lora-library .lora-row')).toContainText('Ink illustration');
  await page.locator('#lora-library-tag').selectOption('');
  await page.screenshot({ path: 'test-results/lora-library-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole('tab', { name: 'LoRAs', exact: true })).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: 'test-results/lora-library-mobile.png', fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.getByRole('link', { name: /Back to assets/ }).click();
  // Image LoRAs are drafted in their own dialog and take effect on Apply changes.
  const dialog = page.locator('.image-loras-dialog');
  const loraButton = page.locator('.image-loras-button');
  const picker = dialog.locator('#lora-add');
  const generate = name => page.getByRole('button', { name, exact: true });
  async function openLoras() {
    await toolsTab(page, 'Inputs');
    if (!await dialog.isVisible()) await loraButton.click();
    await expect(dialog.getByRole('textbox', { name: 'Add LoRA', exact: true })).toBeVisible();
  }
  async function applyLoras() { await dialog.getByRole('button', { name: 'Apply changes', exact: true }).click(); await expect(dialog).toBeHidden(); }
  // Escape would close the whole dialog, so dismiss the option list by clicking the dialog title.
  async function closeOptions() { await dialog.locator('.mud-dialog-title').click(); await expect(page.locator('.lora-options.mud-popover-open')).toHaveCount(0); }
  async function cancelLoras() { await dialog.getByRole('button', { name: 'Cancel', exact: true }).click(); await expect(dialog).toBeHidden(); }
  await openLoras();
  await dialog.getByRole('link', { name: 'Manage LoRAs', exact: true }).click();
  await expect(page).toHaveURL(/tab=loras/);
  expect(new URL(page.url()).searchParams.get('returnUrl')).toBe(`/projects/${slug}/assets`);
  await expect(page.getByRole('tab', { name: 'LoRAs', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('link', { name: /Back to assets/ }).click();
  await toolsTab(page, 'Prompt');
  await page.locator('#asset-image-workflow').selectOption('Krea2');
  await page.locator('#image-prompt').fill('A mouse in a garden');
  await openLoras();
  await expect(page.locator('.project-lora-visibility')).toHaveCount(0);
  const settingsUrl = await page.locator('.project-tabs').getByRole('link', { name: 'Settings', exact: true }).getAttribute('href');
  const projectSettings = await page.context().newPage();
  await projectSettings.goto(settingsUrl);
  await expect(projectSettings.locator('.project-tabs').getByRole('link', { name: 'Settings', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(projectSettings.getByRole('button', { name: 'Save project visibility', exact: true })).toBeEnabled();
  async function setVisibility(only, hidden) {
    await projectSettings.locator('#lora-only-tags').fill(only);
    await projectSettings.locator('#lora-hidden-tags').fill(hidden);
    await projectSettings.getByRole('button', { name: 'Save project visibility', exact: true }).click();
    await expect(projectSettings.locator('.project-lora-visibility [role=status]')).toHaveText('Project visibility saved.');
    await refreshPicker();
  }
  // Opening the picker list re-checks the library and the project's visibility.
  async function refreshPicker() {
    await openLoras();
    await picker.click();
    await expect(page.locator('.lora-options.mud-popover-open')).toBeVisible();
    await expect(page.getByText('Checking available LoRAs…', { exact: true })).toBeHidden();
    await closeOptions();
  }
  async function add(name, search = name) {
    await openLoras();
    await picker.fill(search);
    await expect(page.getByRole('option').filter({ hasText: name })).toBeVisible();
    await picker.press('ArrowDown');
    await picker.press('Enter');
    await expect(dialog.getByRole('checkbox', { name, exact: true })).toBeVisible();
    await expect(picker).toHaveValue('');
  }
  const strength = dialog.getByRole('spinbutton', { name: 'Strength for Mouse identity' });
  await setVisibility('', 'NSFW');
  await picker.fill('Ink');
  await expect(page.getByText('No matching LoRAs. Try another search or manage your library.')).toBeVisible();
  await closeOptions();
  await expect(dialog.locator('.lora-panel')).toContainText('1 hidden by project visibility');
  await setVisibility('', '');
  await expect(dialog.locator('.lora-selections')).toHaveCount(0);
  await add('Mouse identity', 'character');
  const selectedSummary = dialog.locator('.lora-selections > summary');
  await selectedSummary.focus(); await page.keyboard.press('Enter');
  await expect(strength).toBeHidden();
  await expect(picker).toBeVisible();
  await refreshPicker();
  await expect(strength).toBeHidden();
  await add('Ink illustration', 'styles/ink');
  await expect(strength).toBeVisible();
  await expect(selectedSummary).toContainText('Selected LoRAs (2)');
  // Triggers are staged in the dialog and reach the prompt only on Apply.
  await dialog.locator('.lora-row').filter({ hasText: 'Mouse identity' }).getByRole('button', { name: 'Add trigger on apply' }).click();
  await expect(dialog).toContainText('1 trigger(s) will be added to the prompt.');
  await expect(page.locator('#image-prompt')).toHaveValue('A mouse in a garden');
  await strength.fill('0.55');
  await dialog.getByRole('button', { name: 'Move Ink illustration up', exact: true }).focus(); await page.keyboard.press('Enter');
  await expect.poll(() => dialog.locator('.lora-panel .lora-row').evaluateAll(rows => rows.map(row => row.dataset.loraFile))).toEqual(['styles/ink.safetensors', 'character.safetensors']);
  await applyLoras();
  await expect(page.locator('#image-prompt')).toHaveValue('A mouse in a garden\njuniper character');
  await expect(loraButton).toHaveAttribute('aria-label', 'LoRAs, 2 active');
  // A project filter excluding an applied LoRA blocks generation until it is disabled.
  await setVisibility('sfw', 'nsfw');
  await expect(dialog.locator('.lora-panel .lora-row').filter({ hasText: 'Ink illustration' })).toContainText('Excluded by this project');
  await cancelLoras();
  await expect(generate('Generate images')).toBeDisabled();
  await openLoras();
  await dialog.locator('.lora-selections > summary').click();
  await dialog.getByRole('checkbox', { name: 'Ink illustration', exact: true }).uncheck();
  await applyLoras();
  await expect(generate('Generate images')).toBeEnabled();
  await setVisibility('sfw', '');
  await dialog.locator('.lora-selections > summary').click();
  await dialog.getByRole('checkbox', { name: 'Ink illustration', exact: true }).check();
  await applyLoras();
  await expect(page.locator('#image-prompt')).toHaveValue('A mouse in a garden\njuniper character');
  await generate('Generate images').click();
  await expect.poll(async () => (await state()).assets[0].images.length).toBe(2);
  await page.locator('.image-review-dialog').getByRole('button', { name: 'Close image review', exact: true }).click();
  let generated = (await state()).assets[0].images.at(-1).generation;
  expect(generated.loras.map(l => l.reference.fileName)).toEqual(['styles/ink.safetensors', 'character.safetensors']);
  expect(generated.loras.map(l => l.strength)).toEqual([1.2, .55]);
  // Each workflow keeps its own LoRAs.
  await toolsTab(page, 'Prompt');
  await page.locator('#asset-image-workflow').selectOption('Flux2Klein9bKv');
  await openLoras();
  await expect(dialog.locator('.lora-row')).toHaveCount(0);
  await add('Klein illustration');
  await applyLoras();
  await toolsTab(page, 'Prompt');
  await page.locator('#asset-image-workflow').selectOption('Krea2');
  await openLoras();
  await expect(dialog.locator('.lora-panel .lora-row')).toHaveCount(2);
  await cancelLoras();
  await page.locator('.asset-media-card[data-media-kind=Image] .media-select').first().click();
  await toolsTab(page, 'Prompt');
  await page.getByRole('textbox', { name: 'Edit instruction', exact: true }).fill('Keep the mouse and change the sky');
  await imageOutput(page);
  await page.locator('#candidate-count').selectOption('2');
  await generate('Generate edited images').click();
  await expect(page.locator('#lora-hidden-tags')).toHaveCount(0);
  const modal = page.locator('.image-review-dialog');
  await expect(modal).toBeVisible();
  await expect(modal.getByRole('button', { name: 'View Take 2', exact: true })).toBeVisible();
  await modal.locator('.review-image-details > summary').click();
  await expect(modal).toContainText('LoRA · Mouse identity');
  await modal.getByRole('button', { name: 'Close image review', exact: true }).click();
  await expect.poll(async () => (await state()).assets[0].images.length).toBe(4);
  await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await projectSettings.reload();
  await expect(projectSettings.locator('#lora-only-tags')).toHaveValue('sfw');
  await toolsTab(page, 'Prompt');
  await page.locator('#asset-image-workflow').selectOption('Krea2');
  await openLoras();
  await expect(dialog.locator('.lora-selections')).not.toHaveAttribute('open');
  await dialog.locator('.lora-selections > summary').click();
  await expect(strength).toHaveValue('0.55');
  await cancelLoras();
  expect((await state()).assets[1].loras).toEqual({});
  await request.post('/fixtures/lora-file?file=character.safetensors&missing=true');
  await refreshPicker(); await cancelLoras();
  await toolsTab(page, 'Prompt');
  await page.locator('#image-prompt').fill('Another mouse');
  await expect(generate('Generate edited images')).toBeDisabled();
  await expect(page.locator('.generation-panel')).toContainText('exact LoRA file is missing');
  await request.post('/fixtures/lora-file?file=character.safetensors&missing=false');
  await refreshPicker(); await cancelLoras();
  await expect(generate('Generate edited images')).toBeEnabled();
  await openLoras();
  await dialog.locator('.lora-selections > summary').click();
  await dialog.getByRole('button', { name: 'Remove Ink illustration', exact: true }).click();
  await picker.fill('nsfw');
  await expect(page.getByRole('option').filter({ hasText: 'Ink illustration' })).toBeVisible();
  await page.screenshot({ path: 'test-results/lora-assets-desktop.png', fullPage: true });
  await closeOptions();
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(strength).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await picker.click();
  await expect(page.getByRole('option').filter({ hasText: 'Ink illustration' })).toBeVisible();
  await page.screenshot({ path: 'test-results/lora-assets-mobile.png', fullPage: true });
  await closeOptions();
  await cancelLoras();
  await projectSettings.screenshot({ path: 'test-results/project-settings-desktop.png', fullPage: true });
  await projectSettings.setViewportSize({ width: 390, height: 844 });
  await expect(projectSettings.getByRole('button', { name: 'Save project visibility', exact: true })).toBeVisible();
  expect(await projectSettings.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await projectSettings.screenshot({ path: 'test-results/project-settings-mobile.png', fullPage: true });
  const secondProject = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${secondProject.id}/images`);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(`/projects/${secondProject.id}/assets`);
  await page.locator('.project-tabs').getByRole('link', { name: 'Settings', exact: true }).click();
  await expect(page.locator('#project-lora-heading')).toContainText('All LoRAs');
  await expect(page.locator('#lora-only-tags')).toHaveValue('');
  await projectSettings.close();
});
