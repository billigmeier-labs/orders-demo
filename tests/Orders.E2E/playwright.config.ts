import { defineConfig, devices } from '@playwright/test';
import { settings } from './support/settings';

// One console, one scenario lock: every spec runs in a single worker, in file order.
export default defineConfig({
  testDir: './specs',
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  // Fake mode: the simulation moves every few seconds. Live mode: deploys and alarms take minutes,
  // so a whole scenario gets hours and each step polls for up to 25 minutes (see support/run.ts).
  timeout: settings.live ? 3 * 60 * 60 * 1000 : 3 * 60 * 1000,
  expect: { timeout: settings.live ? 60_000 : 15_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: settings.baseURL,
    extraHTTPHeaders: settings.token ? { 'X-Verification-Token': settings.token } : {},
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    actionTimeout: settings.live ? 60_000 : 15_000,
    navigationTimeout: settings.live ? 60_000 : 15_000,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
