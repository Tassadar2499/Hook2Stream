import { expect, test } from "@playwright/test";

test("container health endpoints are live, ready, and not cached", async ({
  request,
}) => {
  const live = await request.get("/health/live");
  expect(live.status()).toBe(200);
  expect(await live.json()).toEqual({ status: "ok" });
  expect(live.headers()["cache-control"]).toContain("no-store");

  const ready = await request.get("/health/ready");
  expect(ready.status()).toBe(200);
  expect(await ready.json()).toEqual({ status: "ready" });
  expect(ready.headers()["cache-control"]).toContain("no-store");
});

test("landing communicates the campaign result and pricing", async ({ page }) => {
  await page.goto("/");

  await expect(
    page.getByRole("heading", { name: /one song.*three weeks.*of shorts/i }),
  ).toBeVisible();
  await expect(page.getByText("18 videos · one ZIP")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Pay for the release." })).toBeVisible();
  await expect(page.getByText("$9.90")).toBeVisible();
  await expect(page.getByText(/^Upload one finished MP3 or WAV\./i)).toBeVisible();
  await expect(page.getByText(/no virality promise/i)).toBeVisible();
});

test("configured fast-test build exposes the local protected workspace", async ({ page }) => {
  await page.goto("/");

  await expect(page.getByRole("link", { name: "Dashboard" })).toHaveAttribute(
    "href",
    "/dashboard",
  );
  await expect(page.getByText("Local developer")).toHaveCount(1);
});

test("mobile landing has no horizontal overflow", async ({ page }) => {
  await page.goto("/");
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth > document.documentElement.clientWidth,
  );

  expect(overflow).toBe(false);
});
