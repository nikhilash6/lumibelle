import { test, expect } from './fixtures.js';
import { toolsTab, closeShotSetup } from './workspace-tools.js';

test('each take card downloads its own saved MP4', async ({ page, request }) => {
  test.setTimeout(120000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  expect((await request.post(`/fixtures/${id}/take-generation-setup`)).ok()).toBe(true);
  const shots = async () => (await (await request.get(`/fixtures/${id}/shots`)).json());
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await toolsTab(page, 'Generate');
  await closeShotSetup(page);
  await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
  await expect.poll(async () => (await shots()).takes.length, { timeout: 45000 }).toBe(2);
  await page.locator('.shot-review-dialog').getByRole('button', { name: 'Close', exact: true }).click();
  await page.locator('[data-workspace-group=center]').getByRole('tab', { name: 'Takes' }).click();
  const cards = page.locator('.unified-take-card');
  await expect(cards).toHaveCount(2);
  const links = cards.getByRole('link', { name: 'Download MP4', exact: true });
  await expect(links).toHaveCount(2);
  const downloadedIds = new Set();
  for (const link of await links.all()) {
    const path = await link.getAttribute('href');
    const takeId = path?.split('/').at(-1);
    expect((await shots()).takes.some(take => take.id === takeId)).toBe(true);
    downloadedIds.add(takeId);
    await expect(link).toHaveAttribute('download', `take-${takeId.replaceAll('-', '')}.mp4`);
    const media = await request.get(path);
    expect(media.status()).toBe(200);
    expect(media.headers()['content-type']).toMatch(/^video\/mp4/);
    expect((await media.body()).length).toBeGreaterThan(100);
    const event = page.waitForEvent('download');
    await link.click();
    const download = await event;
    expect(download.suggestedFilename()).toBe(`take-${takeId.replaceAll('-', '')}.mp4`);
  }
  expect(downloadedIds.size).toBe(2);

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(links.first()).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});
