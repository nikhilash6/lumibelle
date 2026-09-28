import { scriptAssist, closeComposer } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

const activity = page => page.getByRole('dialog', { name: 'AI activity', exact: true });
async function openActivity(page) { await page.locator('.ai-activity-trigger').click(); await expect(activity(page)).toBeVisible(); }
async function closeActivity(page) { await activity(page).getByRole('button', { name: 'Close AI activity' }).click(); await expect(activity(page)).not.toBeVisible(); }
async function create(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await page.goto(`/projects/${id}/script`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expect(page.getByRole('textbox', { name: 'Screenplay', exact: true })).toBeVisible();
  await scriptAssist(page);
  await expect(page.getByRole('button', { name: 'Draft script', exact: true })).toBeEnabled();
  await closeComposer(page);
  return id;
}
async function jobFor(request, project) { return (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.target.projectId === project); }
async function done(request, project) { await expect.poll(async () => (await jobFor(request, project))?.state, { timeout: 20000 }).toBe('Completed'); }

test('a fenced screenplay with commentary is reviewable and only its blocks are applied', async ({ page, request }) => {
  const id = await create(page, request);
  await scriptAssist(page);
  await page.locator('#script-instructions').fill('FENCED_COMMENTARY: A mouse arrives for breakfast.');
  await page.getByRole('button', { name: 'Draft script', exact: true }).click();
  await done(request, id);
  const dialog = page.getByRole('dialog', { name: 'Review script proposal' });
  await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
  await expect(dialog.getByRole('alert')).toHaveCount(0);
  await expect(page.locator('.script-canvas [data-kind=Scene]')).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Scene]')).toHaveCount(2);
  await expect(page.locator('.script-canvas')).not.toContainText('Here is the screenplay');
  const state = await (await request.get(`/fixtures/${id}`)).json();
  expect(state.history.runs[0].output).toMatch(/^\*\*Here is the screenplay:\*\*\n\n```json\n/);
  expect(state.history.runs[0].applied).toBe(true);
});

test('invalid screenplay JSON can be corrected, reviewed after reload, and applied without another model call', async ({ page, request }) => {
  const id = await create(page, request);
  await scriptAssist(page);
  await page.locator('#script-instructions').fill('INVALID: A kettle whistles.');
  await page.getByRole('button', { name: 'Draft script', exact: true }).click();
  await expect.poll(async () => (await jobFor(request, id))?.state).toBe('NeedsAttention');
  await page.getByRole('button', { name: /^(Review changes|View response|Needs attention)$/ }).click();
  const dialog = page.getByRole('dialog', { name: 'Review script proposal' });
  await expect(dialog).toContainText('Invalid JSON at line 1, column');
  const original = (await (await request.get(`/fixtures/${id}`)).json()).history.runs[0];
  await dialog.getByRole('button', { name: 'Paste / edit JSON', exact: true }).click();
  const editor = dialog.getByRole('textbox', { name: 'Screenplay JSON', exact: true });
  await expect(editor).toHaveValue(original.output);
  await page.keyboard.press('Tab');
  expect(await dialog.evaluate(el => el.contains(document.activeElement))).toBe(true);
  await editor.fill('[{"kind":"Action","sp,ans":[]}]');
  await dialog.getByRole('button', { name: 'Review corrected JSON', exact: true }).click();
  await expect(dialog.locator('#script-json-error')).toContainText('Block 1: missing or invalid "spans"');
  await page.screenshot({ path: 'artifacts/validation/script-json-correction-desktop.png' });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(dialog.getByRole('button', { name: 'Review corrected JSON', exact: true })).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'artifacts/validation/script-json-correction-mobile.png' });
  const json = JSON.stringify([{ kind: 'Scene', spans: [{ text: 'INT. KITCHEN — DAY' }] }, { kind: 'Action', spans: [{ text: 'A kettle whistles.' }] }], null, 2);
  await editor.fill(json);
  await dialog.getByRole('button', { name: 'Review corrected JSON', exact: true }).click();
  await expect(dialog.locator('.script-review-meta')).toContainText('Pasted JSON');
  await expect(dialog.getByRole('alert')).toHaveCount(0);
  await page.keyboard.press('Tab');
  expect(await dialog.evaluate(el => el.contains(document.activeElement))).toBe(true);
  await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
  await expect(page.locator('.script-canvas [data-kind=Scene]')).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await page.reload();
  await scriptAssist(page);
  await page.getByRole('button', { name: /^(Review changes|View response|Needs attention)$/ }).click();
  await expect(dialog.locator('.script-review-meta')).toContainText('Pasted JSON');
  await expect(dialog.locator('.proposed-page')).toContainText('A kettle whistles.');
  await dialog.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Scene]')).toHaveCount(1);
  await expect(dialog).not.toBeVisible();
  const history = (await (await request.get(`/fixtures/${id}`)).json()).history.runs;
  expect(history).toHaveLength(2);
  expect(history.find(r => r.id === original.id)).toEqual(original);
  const correction = history.find(r => r.correctedFromRunId === original.id);
  expect(correction.applied).toBe(true); expect(correction.output).toBe(json); expect(correction.jobId).toBeNull();
  const jobs = (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id);
  expect(jobs).toHaveLength(1);
});

test('a quiet provider has a ticking timer, then streamed text updates the open review', async ({ page, request }) => {
  const id = await create(page, request);
  await scriptAssist(page);
  await page.locator('#script-instructions').fill('SLOW SILENT_START: A mouse arrives at dinner.');
  await page.getByRole('button', { name: 'Draft script', exact: true }).click();
  await page.getByRole('button', { name: /View request/ }).click();
  const dialog = page.getByRole('dialog', { name: 'Review script proposal' });
  await expect(dialog).toContainText('Some models take time');
  await expect(dialog.locator('.generation-progress-meta')).not.toHaveText('0s elapsed');
  await expect(dialog.locator('.generation-progress-label')).toContainText('Receiving response');
  await expect(dialog.locator('.generation-progress-meta')).toContainText('characters received');
  await dialog.getByText(/Response so far/).click();
  const initial = (await dialog.locator('.raw-response').textContent()).length;
  await expect.poll(async () => (await dialog.locator('.raw-response').textContent()).length).toBeGreaterThan(initial);
  await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toHaveCount(0);
  await page.screenshot({ path: 'artifacts/validation/script-live-response.png' });
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await page.getByRole('button', { name: /View request/ }).click();
  await expect(dialog.locator('.generation-progress-meta')).not.toHaveText('0s elapsed');
  await done(request, id);
  await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toBeVisible();
  await expect.poll(async () => (await jobFor(request, id)).unread).toBe(false);
  await expect(dialog.locator('.generation-progress-meta')).toHaveCount(0);
  await expect(page.locator('.script-canvas [data-kind=Scene]')).toHaveCount(0);
});

test('a completed discussion displays the saved reply as readable text without changing the script', async ({ page, request }) => {
  const id = await create(page, request);
  await scriptAssist(page);
  await page.locator('#script-operation').selectOption('Discuss');
  await scriptAssist(page);
  await page.locator('#script-instructions').fill('Suggest a brief reason for a surprise visitor.');
  await page.getByRole('button', { name: 'Discuss idea', exact: true }).click();
  await done(request, id);
  const dialog = page.getByRole('dialog', { name: 'Review script proposal' });
  await expect(dialog.locator('.markdown-view')).toHaveText('A simple summoning mishap could turn into a warm dinner scene.');
  await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toHaveCount(0);
  await expect(page.locator('.script-canvas')).not.toContainText('summoning mishap');
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
});

test('queued script restores its context, continues after navigation, and reviews the exact saved result', async ({ page, request }) => {
  const id = await create(page, request);
  await openActivity(page); await activity(page).getByRole('button', { name: 'Pause queue OpenRouter', exact: true }).click(); await closeActivity(page);
  try {
  await scriptAssist(page);
  await page.locator('#script-instructions').fill('SLOW: A mouse arrives for dinner.');
  await page.getByRole('button', { name: 'Draft script', exact: true }).click();
  await expect(page.locator('.script-assistant .request-action-button').getByText(/Queued/)).toBeVisible();
  await expect(page.locator('.script-assistant .request-time')).toContainText('waiting');
  await page.reload();
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await scriptAssist(page);
  await expect(page.locator('#script-instructions')).toHaveValue('SLOW: A mouse arrives for dinner.');
  await expect(page.locator('.script-assistant .assist-composer-model .model-chip')).toBeDisabled();
  await closeComposer(page);
  await page.locator('.project-tabs').getByRole('link', { name: 'Assets', exact: true }).click();
  await openActivity(page); await activity(page).getByRole('button', { name: 'Resume queue OpenRouter', exact: true }).click();
  await done(request, id); await expect(page.locator('.script-review-dialog')).not.toBeVisible();
  const job = await jobFor(request, id); expect(job.unread).toBe(true);
  await activity(page).getByRole('tab', { name: 'History', exact: true }).click();
  await activity(page).getByLabel('Project', { exact: true }).selectOption(id);
  await activity(page).locator(`[data-job-id="${job.id}"]`).getByRole('link', { name: 'Review', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/projects/${(await (await request.get(`/fixtures/${id}/project-route`)).json()).slug}/script\\?jobId=${job.id}`));
  await expect(page.getByRole('dialog', { name: 'Review script proposal' })).toBeVisible();
  await page.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Scene]')).toHaveCount(2);
  const state = await (await request.get(`/fixtures/${id}`)).json();
  expect(state.history.runs).toHaveLength(1); expect(state.history.runs[0].jobId).toBe(job.id);
  } finally {
    // Reset shared scheduling even if an assertion fails before the normal resume.
    await page.goto(`/projects/${id}/script`);
    await openActivity(page);
    const resume = activity(page).getByRole('button', { name: 'Resume queue OpenRouter', exact: true });
    if (await resume.isVisible()) await resume.click();
    await closeActivity(page);
  }
});

test('another open dialog suppresses automatic review permanently until the author asks to review', async ({ page, request }) => {
  const id = await create(page, request);
  await scriptAssist(page);
  await page.locator('#script-instructions').fill('SLOW: A mouse arrives at dinner.');
  await page.getByRole('button', { name: 'Draft script', exact: true }).click();
  await openActivity(page); await done(request, id);
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await closeActivity(page); await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.getByRole('button', { name: /^(Review changes|View response|Needs attention)$/ }).click();
  await expect(page.getByRole('dialog', { name: 'Review script proposal' })).toBeVisible();
  await page.getByRole('button', { name: 'Close', exact: true }).click();
});

test('closing request keeps it running and mobile activity traps focus, restores focus, and fits the screen', async ({ page, request }) => {
  const id = await create(page, request);
  await scriptAssist(page);
  await page.locator('#script-instructions').fill('SLOW: A small mouse bows.');
  await page.getByRole('button', { name: 'Draft script', exact: true }).click();
  await page.getByRole('button', { name: /View request/ }).click();
  await expect(page.getByRole('dialog', { name: 'Review script proposal' })).toBeVisible();
  await page.getByRole('button', { name: 'Close', exact: true }).click();
  await done(request, id); expect((await jobFor(request, id)).cancelRequested).toBe(false);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 });
  await openActivity(page); await activity(page).getByRole('tab', { name: 'Active', exact: true }).focus();
  await page.keyboard.press('End'); await expect(activity(page).getByRole('tab', { name: 'History', exact: true })).toBeFocused();
  for (let i = 0; i < 15; i++) { await page.keyboard.press('Tab'); await expect.poll(() => activity(page).evaluate(el => el.contains(document.activeElement))).toBe(true); }
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'artifacts/validation/plan005-activity-mobile.png' });
  await page.keyboard.press('Escape'); await expect(activity(page)).not.toBeVisible(); await expect(page.locator('.ai-activity-trigger')).toBeFocused();
});
