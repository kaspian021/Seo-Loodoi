import { defineConfig, devices } from '@playwright/test'
import { fileURLToPath } from 'node:url'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0, // a flaky first run must not become an invisible green retry
  forbidOnly: Boolean(process.env.CI),
  timeout: 180_000,
  expect: { timeout: 20_000 },
  outputDir: './test-results/e2e',
  reporter: [['list'], ['junit', { outputFile: 'e2e-results.xml' }]],
  use: {
    baseURL: 'http://127.0.0.1:4173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'dotnet run --project tests/SeoLoodoi.E2E.Host --configuration Release --no-build --no-launch-profile',
      cwd: fileURLToPath(new URL('../../', import.meta.url)),
      url: 'http://127.0.0.1:5080/health/ready',
      env: { SEO_LOODOI_E2E: '1' },
      timeout: 120_000,
      reuseExistingServer: false,
      stdout: 'pipe',
      stderr: 'pipe',
    },
    {
      command: 'npm run preview -- --host 0.0.0.0 --port 4173 --strictPort',
      url: 'http://127.0.0.1:4173',
      timeout: 30_000,
      reuseExistingServer: false,
    },
  ],
})
