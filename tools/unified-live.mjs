import { chromium } from '@playwright/test';
import fs from 'node:fs/promises';
const project = 'e866c29c-a38f-4548-a741-60b44b7a5bed';
const output = 'artifacts/unified-live';
const selections = JSON.parse(await fs.readFile(`${output}/reference-selection.json`, 'utf8'));
console.log('Opening browser', process.argv.slice(2));
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const page = await browser.newPage({ viewport: { width: 1600, height: 1100 } });
page.setDefaultTimeout(30000);
page.on('pageerror', error => console.log('PAGE ERROR:', error.message));
try {
  for (const { shotId, shot } of selections.filter(s => !process.argv[3] || s.shot.title === process.argv[3])) {
    console.log('Opening shot', shot.title);
    await page.goto(`http://localhost:5183/projects/${project}/shots?shotId=${shotId}`);
    await page.locator('[data-prompt-ready=true]').waitFor();
    console.log('Editor ready', shot.title);
    const doc = JSON.parse(await fs.readFile(`App_Data/Projects/${project}/production.json`, 'utf8'));
    const setup = doc.compositions.find(c => c.shotId === shotId && !c.archived);
    if (process.argv[2] === 'prepare') {
      await page.locator('[data-workspace-group=tools]').getByRole('tab', { name: /^References/ }).click();
      if (!setup.inputs.images.length) {
        await page.getByRole('button', { name: 'Manage image references', exact: true }).click();
        const dialog = page.locator('.manual-reference-dialog');
        for (const binding of shot.images) {
          await dialog.locator(`[data-reference="${binding.assetId}/${binding.mediaId}"]`).click();
          if (binding.crop) {
            await dialog.getByText('Crop reference', { exact: true }).click();
            await dialog.getByLabel('Width', { exact: true }).fill(String(binding.crop.width));
            await dialog.getByLabel('Height', { exact: true }).fill(String(binding.crop.height));
            await dialog.getByLabel('Horizontal position').fill(String(binding.crop.x));
            await dialog.getByLabel('Vertical position').fill(String(binding.crop.y));
          }
        }
        await dialog.getByRole('button', { name: 'Apply changes', exact: true }).click();
        await dialog.waitFor({ state: 'hidden' });
      }
      for (const voice of shot.voices) {
        if (setup.inputs.voices.some(v => v.voiceId === voice.voiceId)) continue;
        await page.getByLabel('Add character voice').selectOption(voice.voiceId);
        await page.getByRole('button', { name: 'Edit voice', exact: true }).click();
        const dialog = page.locator('.shot-voice-dialog');
        await dialog.getByLabel('Speaker', { exact: true }).selectOption(voice.speaker);
        await dialog.getByLabel('Start', { exact: true }).fill(String(voice.start));
        await dialog.getByLabel('Seconds', { exact: true }).fill(String(voice.duration));
        await dialog.getByRole('button', { name: 'Apply', exact: true }).click();
        await dialog.waitFor({ state: 'hidden' });
      }
      await page.locator('[data-workspace-group=tools]').getByRole('tab', { name: 'Generation', exact: true }).click();
      await page.getByLabel('Generation preset', { exact: true }).selectOption(shot.generationPreset);
      await page.getByLabel('Resolution', { exact: true }).selectOption(shot.upscalePreview ? 'upscaled' : shot.nativeResolution ? 'True' : 'False');
      await page.getByLabel('Direction for AI').fill('Use the selected images for recognizable identity, clothing, and bedroom geography. Stage the authored action clearly within the exact duration. Preserve the exact dialogue and voice mapping where supplied.');
      await page.getByText('AI model and reasoning', { exact: true }).click();
      console.log(shot.title, 'MODEL', await page.getByLabel('Text model', { exact: true }).inputValue());
      if (!setup.prompt) {
        await page.getByRole('button', { name: 'Compose prompt', exact: true }).click();
        await page.getByRole('button', { name: 'Cancel composition', exact: true }).waitFor();
        console.log(shot.title, 'composition queued');
      }
    } else if (process.argv[2] === 'generate') {
      if (!setup.prompt) throw new Error(`${shot.title}: prompt not ready`);
      // Exercise a real manual change before generation; no separate acceptance step.
      console.log('Editing prompt before generation');
      const editor = page.getByRole('textbox', { name: 'H3 prompt', exact: true });
      if (!setup.prompt.includes('Do not add a musical score.')) {
        await editor.press('Control+Home');
        await editor.press('Control+End');
        await editor.press('Enter');
        await editor.pressSequentially('Do not add a musical score.');
      }
      console.log('Click generate');
      await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
      await Promise.race([page.getByRole('button', { name: 'Generate takes', exact: true }).waitFor({ state: 'hidden', timeout: 120000 }), page.getByRole('alert').first().waitFor().then(async () => { throw new Error(await page.getByRole('alert').first().innerText()); })]);
      console.log(shot.title, 'generation queued');
    }
    await page.screenshot({ path: `${output}/${shot.title.replaceAll(' ', '-')}-${process.argv[2]}.png` });
  }
} catch (error) {
  console.log('FAILED', error.message);
  await page.screenshot({ path: `${output}/error.png`, fullPage: true });
  await fs.writeFile(`${output}/page-error.txt`, await page.locator('body').innerText());
  throw error;
} finally { await browser.close(); }
