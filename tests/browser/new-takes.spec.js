import { test, expect } from './fixtures.js';
import { toolsTab, closeShotSetup } from './workspace-tools.js';

test('takes stay new until they have been shown in the Takes view', async ({ page, request }) => {
  test.setTimeout(120000);
  await page.setViewportSize({ width: 1173, height: 1000 });
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  expect((await request.post(`/fixtures/${id}/take-generation-setup`)).ok()).toBe(true);
  const state = async () => (await request.get(`/fixtures/${id}/shots`)).json();
  const job = async () => (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.target.projectId === id && j.kind === 'Video');
  await page.goto(`/projects/${id}/shots`);
  await toolsTab(page, 'Generate');
  await closeShotSetup(page);
  await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
  await expect.poll(async () => (await state()).takes.length, { timeout: 45000 }).toBe(2);
  // The batch review marks what it shows as seen.
  await expect.poll(async () => (await job()).unread).toBe(false);
  await page.locator('.shot-review-dialog').getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.locator('.shot-new-takes')).toHaveCount(0);

  await request.post(`/fixtures/ai-jobs/${(await job()).id}/unread`);
  await expect(page.locator('.shot-new-takes')).toHaveText('· 2 new');
  const shotTab = page.locator('[data-workspace-group=center]').getByRole('tab', { name: 'Shot', exact: true });
  await shotTab.click();
  // A hidden Takes view must not count as seen; acknowledgement checks every second.
  await page.waitForTimeout(1500);
  expect((await job()).unread).toBe(true);
  await page.locator('[data-workspace-group=center]').getByRole('tab', { name: 'Takes', exact: true }).click();
  await expect(page.locator('.unified-take-card .new-take-badge')).toHaveCount(0);
  await expect(page.locator('.shot-new-takes')).toHaveCount(0);
  expect((await job()).unread).toBe(false);
});
