import { defineConfig } from '@playwright/test'

const artifactRoot = process.env.PLAYWRIGHT_ARTIFACTS_DIR ?? '/artifacts/e2e'

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.spec.ts',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  outputDir: `${artifactRoot}/test-results`,
  reporter: [['list'], ['html', { outputFolder: `${artifactRoot}/report`, open: 'never' }]],
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? 'http://web:8080',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'desktop-1440x900', use: { browserName: 'chromium', viewport: { width: 1440, height: 900 } } },
    { name: 'desktop-1920x1080', use: { browserName: 'chromium', viewport: { width: 1920, height: 1080 } } },
  ],
})
