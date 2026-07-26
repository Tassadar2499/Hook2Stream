import { defineConfig, devices } from "@playwright/test";

const baseUrl = process.env.PLAYWRIGHT_BASE_URL ?? "http://127.0.0.1:3100";

export default defineConfig({
  testDir: "./tests/full-stack",
  fullyParallel: false,
  workers: 1,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  timeout: 20 * 60_000,
  expect: {
    timeout: 120_000,
  },
  reporter: process.env.CI
    ? [
        ["line"],
        ["html", { outputFolder: "playwright-report/full-stack", open: "never" }],
        ["junit", { outputFile: "test-results/full-stack/junit.xml" }],
      ]
    : "line",
  outputDir: "test-results/full-stack/artifacts",
  use: {
    baseURL: baseUrl,
    ...devices["Desktop Chrome"],
    actionTimeout: 30_000,
    navigationTimeout: 120_000,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
  },
  projects: [
    {
      name: "full-stack-chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
