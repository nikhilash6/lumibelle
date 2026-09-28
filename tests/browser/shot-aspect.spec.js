import { openShotSetup, closeShotSetup } from './workspace-tools.js';
import { test, expect } from './fixtures.js';
test.beforeEach(async ({ request }) => { await request.post('/fixtures/generation-setups/reset'); });

test('a shot aspect stays with its shot in every setup and can be shared with its scene', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  // Two shots in the same scene.
  await request.post(`/fixtures/${id}/production-shot`);
  await request.post(`/fixtures/${id}/production-shot`);
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  const dialog = page.locator('.shot-setup-dialog');
  const aspect = page.locator('.generation-output').getByLabel('Shot aspect', { exact: true });
  await expect(aspect).toHaveValue('');
  await expect(page.getByRole('button', { name: /in this scene/ })).toHaveCount(0);
  await aspect.selectOption('9:16');
  await page.getByText('Aspect options', { exact: true }).click();
  const share = page.getByRole('button', { name: 'Use 9:16 for 1 other shot in this scene', exact: true });
  await share.click();
  await expect(page.getByText('The other shot in this scene now matches.', { exact: true })).toBeVisible();
  await expect(share).toHaveCount(0);
  const content = async () => (await (await request.get(`/fixtures/${id}/production`)).json()).shotContent.map(c => c.aspectOverride ?? null);
  await expect.poll(content).toEqual(['9:16', '9:16']);

  // A new global setup generates the same shot at its own aspect.
  await openShotSetup(page, 'Preset');
  await dialog.locator('.setup-menu summary').click();
  await dialog.getByRole('button', { name: 'New setup', exact: true }).click();
  await expect(dialog.getByLabel('Named setup')).toContainText('New setup');
  await closeShotSetup(page);
  await expect(aspect).toHaveValue('9:16');
  const setups = await (await request.get('/fixtures/generation-setups')).json();
  expect(JSON.stringify(setups)).not.toContain('aspectOverride');

  await closeShotSetup(page);
  await page.locator('.shot-list-row').nth(1).click();
  await expect(page.locator('.shot-list-row').nth(1)).toHaveAttribute('aria-pressed', 'true');
  await expect(aspect).toBeEnabled();
  await expect(aspect).toHaveValue('9:16');
  await aspect.selectOption('');
  await page.getByText('Aspect options', { exact: true }).click();
  await expect(page.getByRole('button', { name: 'Make 1 other shot in this scene follow the project', exact: true })).toBeVisible();
  await expect.poll(content).toEqual(['9:16', null]);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});
