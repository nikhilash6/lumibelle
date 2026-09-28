import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: './tests/browser', testMatch: '**/*.spec.js', timeout: 45000, fullyParallel: false, workers: 1,
  globalSetup: './tests/browser/build-host.js',
  use: { headless: true, channel: process.env.LUMIBELLE_BROWSER_CHANNEL || 'msedge', screenshot: 'only-on-failure' }
});
