import { openShotSetup } from './workspace-tools.js';
import { test, expect } from './fixtures.js';
test.beforeEach(async ({ request }) => { await request.post('/fixtures/generation-setups/reset'); });

const pane = page => page.locator('#shot-setup-prompt-panel');
const dialog = page => page.locator('.ai-assist-dialog:visible').last();
async function failed(page, request, recovery, missingSound = false, numberWords = false, freeform = false) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  const response = await request.post(`/fixtures/${id}/failed-composition?recovery=${recovery}&missingSound=${missingSound}&numberWords=${numberWords}&freeform=${freeform}`);
  expect(response.ok()).toBeTruthy();
  const job = await response.json();
  await page.goto(`/projects/${id}/shots?jobId=${job.id}&view=Prompt`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await openShotSetup(page);
  return { id, jobId: job.id };
}

for (const narrow of [false, true]) test(`recover a non-template saved response unchanged (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  const { id } = await failed(page, request, 'GenerateAgain', true);
  await expect(pane(page)).toContainText('Missing H3 sections: overall_soundscape, non_diegetic_music.');
  await expect(pane(page).locator('.composition-sound-recovery')).toHaveCount(0);
  const recover = pane(page).getByRole('button', { name: 'Recover response as draft', exact: true });
  await recover.focus(); await expect(recover).toBeInViewport(); await recover.press('Enter');
  const prompt = page.getByLabel('H3 prompt', { exact: true });
  await expect(prompt).toContainText('detailed_description:');
  await expect(prompt).not.toContainText('overall_soundscape:');
  await expect(prompt).not.toContainText('non_diegetic_music:');
  await expect(recover).not.toBeVisible();
  expect(await jobs(request, id)).toHaveLength(1);
  // One Undo restores the prior empty draft and its retained response.
  await page.locator('.shot-setup-dialog').getByRole('button', { name: 'Undo', exact: true }).click();
  await expect(prompt).toHaveText(''); await expect(recover).toBeVisible();
  await recover.click(); await expect(prompt).toContainText('detailed_description:');
  await page.reload(); await expect(prompt).toContainText('detailed_description:');
  await expect(prompt).not.toContainText('overall_soundscape:');
  expect(await jobs(request, id)).toHaveLength(1);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});
async function jobs(request, id) {
  return (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id);
}

for (const narrow of [false, true]) test(`manually accept freeform text after a failed request (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  const { id } = await failed(page, request, 'GenerateAgain', false, false, true);
  const accept = page.locator('.shot-setup-dialog').getByRole('button', { name: 'Mark reviewed', exact: true });
  await expect(accept).toBeDisabled();
  await pane(page).getByRole('button', { name: 'Recover response as draft', exact: true }).click();
  const prompt = page.getByLabel('H3 prompt', { exact: true });
  const text = 'A quiet morning. Riley enters, smiles, and says hello.\nKeep the camera still.';
  await expect(prompt).toHaveText(text, { useInnerText: true });
  await expect(pane(page)).toContainText('this does not block saving or generation');
  await expect(accept).toBeEnabled();
  await accept.click();
  await expect(accept).toBeDisabled();
  await expect(pane(page).getByRole('button', { name: 'Needs attention', exact: true })).toHaveCount(0);
  await expect(prompt).toHaveText(text, { useInnerText: true });
  expect(await jobs(request, id)).toHaveLength(1);
  await page.reload();
  await expect(prompt).toHaveText(text, { useInnerText: true });
  await expect(accept).toBeDisabled();
  await expect(pane(page)).toContainText('Review note:');
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('accepting a manual edit clears the failed request from the prompt toolbar', async ({ page, request }) => {
  const { id } = await failed(page, request, 'GenerateAgain');
  const prompt = page.getByLabel('H3 prompt', { exact: true });
  await prompt.fill('Keep this exact free-form prompt. No section headings.');
  await expect(pane(page).getByRole('button', { name: 'Needs attention', exact: true })).toBeVisible();
  const accept = page.locator('.shot-setup-dialog').getByRole('button', { name: 'Mark reviewed', exact: true });
  await expect(accept).toBeEnabled();
  await accept.click();
  await expect(pane(page).getByRole('button', { name: 'Needs attention', exact: true })).toHaveCount(0);
  await expect(pane(page)).not.toContainText('This composition request stopped.');
  await expect(accept).toBeDisabled();
  await page.reload();
  await expect(prompt).toHaveText('Keep this exact free-form prompt. No section headings.');
  await expect(pane(page).getByRole('button', { name: 'Needs attention', exact: true })).toHaveCount(0);
  expect(await jobs(request, id)).toHaveLength(1);
});

for (const narrow of [false, true]) test(`recover a falsely rejected written duration unchanged (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  const { id } = await failed(page, request, 'GenerateAgain', false, true);
  await expect(pane(page)).toContainText('Recover the saved response unchanged');
  await expect(pane(page).getByRole('alert')).toHaveCount(0);
  await expect(pane(page).locator('.composition-sound-recovery')).toHaveCount(0);
  const recover = pane(page).getByRole('button', { name: 'Recover response as draft', exact: true });
  await recover.focus(); await expect(recover).toBeInViewport(); await recover.press('Enter');
  const prompt = page.getByLabel('H3 prompt', { exact: true });
  await expect(prompt).toContainText('eight seconds');
  await expect(page.getByText('Recovered the saved response unchanged.', { exact: false })).toBeVisible();
  expect(await jobs(request, id)).toHaveLength(1);
  await page.getByRole('button', { name: 'Undo', exact: true }).first().click();
  await expect(prompt).toHaveText(''); await expect(recover).toBeVisible();
  await recover.click(); await expect(prompt).toContainText('eight seconds');
  await page.reload(); await expect(prompt).toContainText('eight seconds');
  expect(await jobs(request, id)).toHaveLength(1);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('failed composition opens a fresh draft without retrying or losing direction', async ({ page, request }) => {
  const { id } = await failed(page, request, 'GenerateAgain');
  await expect(pane(page).getByRole('button', { name: 'Retry composition request', exact: true })).toHaveCount(0);
  await expect(pane(page)).toContainText('This composition request stopped.');
  await pane(page).getByRole('button', { name: 'New composition request', exact: true }).click();
  await expect(dialog(page).getByLabel('Direction for AI', { exact: true })).toHaveValue('Keep my camera direction.');
  expect(await jobs(request, id)).toHaveLength(1);
  await dialog(page).getByRole('button', { name: 'Close', exact: true }).click();
  await pane(page).getByRole('button', { name: 'Needs attention', exact: true }).click();
  await expect(dialog(page).getByRole('heading', { name: 'Text request', exact: true })).toBeVisible();
  await expect(dialog(page).getByRole('button', { name: 'Retry captured request', exact: true })).toHaveCount(0);
  await dialog(page).getByRole('button', { name: 'New request', exact: true }).click();
  await expect(dialog(page).getByLabel('Direction for AI', { exact: true })).toHaveValue('Keep my camera direction.');
  expect(await jobs(request, id)).toHaveLength(1);
  // Only the explicit submit starts another request.
  await dialog(page).getByRole('button', { name: 'Compose prompt', exact: true }).click();
  await expect(page.getByLabel('H3 prompt', { exact: true })).not.toHaveText('');
  await expect.poll(async () => (await jobs(request, id)).length).toBe(2);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  await page.reload();
  await expect(page.getByLabel('H3 prompt', { exact: true })).not.toHaveText('');
});

for (const recovery of ['RetryOutput', 'CheckStatus']) {
  test(`${recovery} keeps captured recovery available without creating another job`, async ({ page, request }) => {
    const { id } = await failed(page, request, recovery);
    await expect(pane(page).getByRole('button', { name: 'Retry composition request', exact: true })).toBeEnabled();
    await pane(page).getByRole('button', { name: 'Needs attention', exact: true }).click();
    await expect(dialog(page).getByRole('button', { name: 'Retry captured request', exact: true })).toBeEnabled();
    await dialog(page).getByRole('button', { name: 'Close', exact: true }).click();
    await pane(page).getByRole('button', { name: 'Retry composition request', exact: true }).click();
    // With no saved output, recovery ends honestly and still needs an explicit fresh request.
    await expect(pane(page).getByRole('button', { name: 'New composition request', exact: true })).toBeVisible();
    expect(await jobs(request, id)).toHaveLength(1);
    await expect(page.getByLabel('H3 prompt', { exact: true })).toHaveText('');
    await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  });
}
