import { defineConfig, devices } from '@playwright/test';

// Overridable because 5199 is only a default: another application holding it makes every browser test
// fail at the web server rather than in a test, which reads as a product failure and is not one.
const port = Number(process.env.BLAZORSQLITE_TEST_PORT ?? 5199);

/**
 * Browsers already installed on the machine are used by default rather than Playwright's own builds.
 * Besides being faster, it is the only option in regions where Playwright's CDN is blocked - installing
 * its Chromium fails there with a 403, so a config that required it would make these tests unrunnable.
 *
 * Set BLAZORSQLITE_BROWSERS=all to add Firefox and WebKit. Those have no installed-browser equivalent
 * and do need `playwright install`, so they are opt-in - but they are also the browsers that matter
 * most for the storage matrix, since neither has JSPI and both must therefore work on Asyncify.
 */
const includeDownloadedBrowsers = process.env.BLAZORSQLITE_BROWSERS === 'all';

/**
 * The soak suite tags itself @soak and is excluded from an ordinary run: it kills workers a thousand
 * times over and drives eight tabs at once, which is minutes of wall clock rather than seconds. Run it
 * with `npm run soak`, or set BLAZORSQLITE_SOAK=1.
 */
const includeSoak = process.env.BLAZORSQLITE_SOAK === '1' || process.env.BLAZORSQLITE_SOAK === 'true';

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

  grepInvert: includeSoak ? undefined : /@soak/,

  // Loading and instantiating a WASM engine per test is slower than a typical DOM test. The soak tests
  // set their own timeout from the iteration count they were asked for.
  timeout: 60_000,

  reporter: process.env.CI ? 'github' : 'list',

  use: {
    baseURL: `http://localhost:${port}`,
    trace: 'retain-on-failure',
  },

  projects,

  webServer: {
    command: 'node server.js',
    env: { PORT: String(port) },
    url: `http://localhost:${port}/index.html`,
    reuseExistingServer: !process.env.CI,
    stdout: 'pipe',
  },
});
