import { expect, test } from "@playwright/test";

test("landing communicates the campaign result and pricing", async ({ page }) => {
  await page.goto("/");

  await expect(
    page.getByRole("heading", { name: /one song.*three weeks.*of shorts/i }),
  ).toBeVisible();
  await expect(page.getByText("18 videos · one ZIP")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Pay for the release." })).toBeVisible();
  await expect(page.getByText("$39")).toBeVisible();
  await expect(page.getByText(/no virality promise/i)).toBeVisible();
});

test("unconfigured local build leads to actionable Clerk setup", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("link", { name: "Configure Clerk" }).click();

  await expect(
    page.getByRole("heading", { name: "Connect Clerk first." }),
  ).toBeVisible();
  await expect(page.getByText(/no development auth bypass/i)).toBeVisible();
});

test("mobile landing has no horizontal overflow", async ({ page }) => {
  await page.goto("/");
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth > document.documentElement.clientWidth,
  );

  expect(overflow).toBe(false);
});
