import { defineConfig, devices } from "@playwright/test";

const externalBaseUrl = process.env.PLAYWRIGHT_BASE_URL;
const localBaseUrl = "http://127.0.0.1:3101";
const baseUrl = externalBaseUrl ?? localBaseUrl;
const localAuthToken =
  process.env.HOOK2STREAM_E2E_AUTH_TOKEN ??
  "hook2stream-fast-ui-local-auth-token-20260725";

export default defineConfig({
  testDir: "./tests",
  testIgnore: ["**/full-stack/**"],
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI
    ? [
        ["line"],
        ["html", { outputFolder: "playwright-report/fast", open: "never" }],
        ["junit", { outputFile: "test-results/fast/junit.xml" }],
      ]
    : "line",
  outputDir: "test-results/fast/artifacts",
  use: {
    baseURL: baseUrl,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
    {
      name: "firefox",
      use: { ...devices["Desktop Firefox"] },
    },
    {
      name: "webkit",
      use: { ...devices["Desktop Safari"] },
    },
    {
      name: "mobile-chromium",
      use: { ...devices["Pixel 7"] },
    },
  ],
  webServer: externalBaseUrl
    ? undefined
    : {
        command: "npm run dev -- --hostname 127.0.0.1 --port 3101",
        url: localBaseUrl,
        env: {
          NEXT_PUBLIC_API_BASE_URL: localBaseUrl,
          NEXT_PUBLIC_AUTH_MODE: "local",
          NEXT_PUBLIC_LOCAL_AUTH_TOKEN: localAuthToken,
        },
        reuseExistingServer: !process.env.CI,
        timeout: 120_000,
        gracefulShutdown: { signal: "SIGTERM", timeout: 5_000 },
      },
});
