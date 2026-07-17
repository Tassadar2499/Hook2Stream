import { expect, test } from "@playwright/test";

test("AppHost local auth reaches the protected workspace", async ({ page }) => {
  test.skip(
    process.env.HOOK2STREAM_FULL_STACK !== "1",
    "Requires the running Aspire stack in Local Auth mode.",
  );

  await page.goto("/dashboard");

  const onboarding = page.getByRole("heading", { name: "Name your workspace." });
  const workspace = page.getByText("Workspace", { exact: true });
  await expect(onboarding.or(workspace)).toBeVisible();

  if (await onboarding.isVisible()) {
    await page.getByLabel("Workspace name").fill("Local development workspace");
    await page.getByLabel("Artist display name").fill("Local Developer");
    await page.getByLabel(/I accept the current draft Terms/i).check();
    await page.getByLabel(/I accept the current draft Privacy/i).check();
    await page.getByRole("button", { name: "Enter workspace" }).click();
    await expect(page).toHaveURL(/\/dashboard$/);
    await expect(workspace).toBeVisible();
  }

  await expect(page.getByText("Local developer").first()).toBeVisible();
  await expect(page.getByText("Could not load dashboard")).toHaveCount(0);
  await expect(page.getByText("Start with AppHost.")).toHaveCount(0);
});
