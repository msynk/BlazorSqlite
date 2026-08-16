import { defineConfig, devices } from '@playwright/test';

const port = 5199;

/**
 * Browsers already installed on the machine are used by default rather than Playwright's own builds.
 * Besides being faster, it is the only option in regions where Playwright's CDN is blocked — installing
 * its Chromium fails there with a 403, so a config that required it would make these tests unrunnable.
 *
 * Set BLAZORSQLITE_BROWSERS=all to add Firefox and WebKit. Those have no installed-browser equivalent
 * and do need `playwright install`, so they are opt-in — but they are also the browsers that matter
 * most for the storage matrix, since neither has JSPI and both must therefore work on Asyncify.
 */
const includeDownloadedBrowsers = process.env.BLAZORSQLITE_BROWSERS === 'all';

const projects = [
  { name: 'chrome', use: { ...devices['Desktop Chrome'], channel: 'chrome' } },
  { name: 'msedge', use: { ...devices['Desktop Edge'], channel: 'msedge' } },
];

if (includeDownloadedBrowsers) {
  projects.push(
    { name: 'firefox', use: { ...devices['Desktop Firefox'] } },
    { name: 'webkit', use: { ...devices['Desktop Safari'] } });
}

export default defineConfig({
  testDir: './tests',

  // The engine and the worker are what is under test, so a failure here is a real defect rather than
  // flake; retrying would only hide it.
  retries: 0,
  fullyParallel: true,

  // Loading and instantiating a WASM engine per test is slower than a typical DOM test.
  timeout: 60_000,

  reporter: process.env.CI ? 'github' : 'list',

  use: {
    baseURL: `http://localhost:${port}`,
    trace: 'retain-on-failure',
  },

  projects,

  webServer: {
    command: 'node server.js',
    url: `http://localhost:${port}/index.html`,
    reuseExistingServer: !process.env.CI,
    stdout: 'pipe',
  },
});
