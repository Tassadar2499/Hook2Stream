import { execFileSync } from "node:child_process";
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test } from "@playwright/test";

const mp3Fixture = process.env.HOOK2STREAM_E2E_MP3;
const wavFixture = process.env.HOOK2STREAM_E2E_WAV;
const apiBaseUrl =
  process.env.HOOK2STREAM_E2E_API_BASE_URL ?? "http://127.0.0.1:5100";
const authToken =
  process.env.HOOK2STREAM_E2E_AUTH_TOKEN ??
  "hook2stream-e2e-local-auth-token-20260725-fixed";

test.describe.serial("real PostgreSQL, MinIO and worker journey", () => {
  test.beforeEach(() => {
    test.skip(
      process.env.HOOK2STREAM_FULL_STACK !== "1",
      "Run through ci/run-full-stack-e2e.sh.",
    );
    expect(mp3Fixture, "HOOK2STREAM_E2E_MP3").toBeTruthy();
    expect(wavFixture, "HOOK2STREAM_E2E_WAV").toBeTruthy();
  });

  test("MP3 reaches preview, fixture checkout, 18 videos and ZIP", async ({
    page,
    request,
  }, testInfo) => {
    await ensureWorkspace(page);
    await page.goto("/releases/new");

    await page
      .getByLabel(/I confirm I have the rights to process this audio/i)
      .check();
    const ingestAccepted = page.waitForResponse(
      (response) =>
        response.request().method() === "POST" &&
        /\/api\/v1\/uploads\/[0-9a-f-]+\/complete$/.test(
          new URL(response.url()).pathname,
        ),
    );
    await page
      .locator('input[type="file"][accept*=".mp3"]')
      .first()
      .setInputFiles(mp3Fixture!);
    const ingestJobId = (
      (await (await ingestAccepted).json()) as { jobId: string }
    ).jobId;
    await expect(page).toHaveURL(/\/releases\/[0-9a-f-]+$/, {
      timeout: 180_000,
    });

    const projectUrl = new URL(page.url());
    const projectId = projectUrl.pathname.split("/").at(-1)!;
    await expect(
      page.getByText("Audio-first release", { exact: true }),
    ).toBeVisible();

    const detailsForm = page
      .getByRole("heading", { name: "Confirm the details." })
      .locator("xpath=ancestor::form");
    await detailsForm.getByLabel("Artist").fill("Playwright Artist");
    await detailsForm.getByLabel("Track title").fill("End to End Signal");
    await detailsForm.getByLabel("Internal label").fill("Playwright MP3 release");
    await detailsForm.getByLabel("Release date").fill(dateInDays(21));
    await detailsForm
      .getByRole("button", { name: "Confirm release details" })
      .click();
    const setupSaved = page.getByText(/Release details confirmed/i);
    const staleSetup = page.getByText(
      /The latest version is loaded; review the fields and save again/i,
    );
    await expect(setupSaved.or(staleSetup)).toBeVisible();
    if (await staleSetup.isVisible()) {
      await detailsForm
        .getByRole("button", { name: "Confirm release details" })
        .click();
    }
    await expect(setupSaved).toBeVisible();
    await waitForJobSucceeded(request, ingestJobId, testInfo);

    const workflow = page.getByRole("region", { name: "Release workflow" });
    await expect(
      workflow.getByRole("progressbar", { name: "Audio progress" }),
    ).toHaveAttribute("aria-valuenow", "100", { timeout: 240_000 });
    await expect(
      workflow.getByRole("progressbar", { name: "Analysis progress" }),
    ).toHaveAttribute("aria-valuenow", "100", { timeout: 240_000 });

    const rightsForm = page
      .getByRole("heading", { name: "Allow the processing." })
      .locator("xpath=ancestor::form");
    await rightsForm.getByLabel(/right to process this audio/i).check();
    await rightsForm.getByLabel(/right to use the lyrics/i).check();
    await rightsForm.getByLabel(/right to use any visuals/i).check();
    await rightsForm.getByLabel(/allow audio, text and the visual brief/i).check();
    await rightsForm
      .getByRole("button", { name: "Save rights confirmation" })
      .click();
    const rightsSaved = page.getByText(/Rights confirmed/i);
    const staleRights = page.getByText(
      /The latest rights state is loaded; review it and save again/i,
    );
    await expect(rightsSaved.or(staleRights)).toBeVisible();
    if (await staleRights.isVisible()) {
      await rightsForm
        .getByRole("button", { name: "Save rights confirmation" })
        .click();
    }
    await expect(rightsSaved).toBeVisible();

    await page.goto(`/releases/${projectId}/transcript`);
    const warning = page.getByLabel("Looks correct");
    await expect(warning).toBeVisible({ timeout: 240_000 });
    await warning.check();
    await page.getByRole("button", { name: "Save revision" }).click();
    const transcriptSaved = page.getByText(/Transcript revision saved/i);
    const staleTranscript = page.getByText(
      /Your transcript edits are still open against the latest version; review and save again/i,
    );
    await expect(transcriptSaved.or(staleTranscript)).toBeVisible();
    if (await staleTranscript.isVisible()) {
      await page.getByRole("button", { name: "Save revision" }).click();
    }
    await expect(transcriptSaved).toBeVisible();
    await expect(
      page.getByRole("button", { name: "Approve transcript" }),
    ).toBeEnabled();
    await page.getByRole("button", { name: "Approve transcript" }).click();
    await expect(page.getByText(/Transcript approved/i)).toBeVisible();

    await page.goto(`/releases/${projectId}/artwork`);
    const firstCandidate = page.getByRole("radio", { name: /Candidate 1/ });
    const startPack = page.getByRole("button", { name: "Start cover pack" });
    await expect(firstCandidate.or(startPack)).toBeVisible({ timeout: 240_000 });
    if (await startPack.isVisible()) {
      await page
        .getByLabel("Visual brief")
        .fill("Bold geometric night sky, high contrast, no text.");
      await startPack.click();
    }
    await expect(firstCandidate).toBeVisible({ timeout: 240_000 });
    await firstCandidate.check();
    await page.getByRole("button", { name: "Save composition" }).click();
    await expect(page.getByText(/Cover composition saved/i)).toBeVisible();
    await expect(
      page.getByRole("button", { name: "Approve official cover" }),
    ).toBeEnabled();
    await page.getByRole("button", { name: "Approve official cover" }).click();
    await expect(page.getByText(/Official cover approved/i)).toBeVisible();

    await page.goto(`/releases/${projectId}/campaign`);
    await expect(page.getByText("18 / 18 items")).toBeVisible({
      timeout: 300_000,
    });
    const preview = page.locator("video");
    await expect(preview).toHaveAttribute("src", /^http/, {
      timeout: 300_000,
    });
    const previewUrl = await preview.getAttribute("src");
    expectAuthenticatedApiUrl(
      previewUrl,
      new RegExp(
        `^/api/v1/releases/${projectId}/assets/[0-9a-f-]+/content$`,
      ),
    );
    const previewPath = join(testInfo.outputDir, "preview.mp4");
    await downloadTo(request, previewUrl, previewPath);
    expect(videoContract(previewPath)).toMatchObject({
      width: 540,
      height: 960,
      videoCodec: "h264",
      audioCodec: "aac",
    });

    await page.getByRole("button", { name: "Unlock full pack" }).click();
    await expect(page).toHaveURL(/[?&]checkout=fixture-complete(?:&|$)/, {
      timeout: 120_000,
    });
    const zipLink = page.getByRole("link", { name: "Download ZIP" });
    await expect(zipLink).toBeVisible({ timeout: 20 * 60_000 });
    const zipUrl = await zipLink.getAttribute("href");
    expectAuthenticatedApiUrl(
      zipUrl,
      new RegExp(`^/api/v1/releases/${projectId}/downloads/[0-9a-f-]+$`),
    );
    const zipPath = join(testInfo.outputDir, "release-pack.zip");
    await downloadTo(request, zipUrl, zipPath);

    const entries = execFileSync("unzip", ["-Z1", zipPath], {
      encoding: "utf8",
    })
      .split(/\r?\n/)
      .filter(Boolean);
    const videos = entries.filter((entry) => entry.endsWith(".mp4"));
    expect(videos).toHaveLength(18);
    expect(entries.some((entry) => /manifest/i.test(entry))).toBe(true);
    expect(entries.some((entry) => /calendar/i.test(entry))).toBe(true);
    expect(entries.some((entry) => /copy|caption/i.test(entry))).toBe(true);

    const representativePath = join(testInfo.outputDir, "representative.mp4");
    writeFileSync(
      representativePath,
      execFileSync("unzip", ["-p", zipPath, videos[0]], {
        maxBuffer: 80 * 1024 * 1024,
      }),
    );
    expect(videoContract(representativePath)).toMatchObject({
      width: 1080,
      height: 1920,
      videoCodec: "h264",
      audioCodec: "aac",
    });
  });

  test("advanced Audio-first draft ingests a real WAV", async ({ page }) => {
    await ensureWorkspace(page);
    await page.goto("/releases/new");
    await page.getByText(/Audio-first setup — WAV or MP3/i).click();
    await page.getByLabel("Internal project label").fill("Playwright WAV release");
    await page.getByLabel("Artist name").fill("Playwright Artist");
    await page.getByLabel("Track title").fill("Lossless Signal");
    await page
      .getByRole("textbox", { name: "Lyrics", exact: true })
      .fill("The waveform carries through the night.");
    await page.getByLabel("Release date").fill(dateInDays(28));
    await page
      .getByRole("button", { name: "Create draft and upload WAV or MP3" })
      .click();

    await expect(page).toHaveURL(/\/releases\/[0-9a-f-]+$/);
    await page.getByLabel("Master audio file").setInputFiles(wavFixture!);
    await expect(
      page.getByRole("progressbar", { name: "Audio progress" }),
    ).toHaveAttribute("aria-valuenow", "100", { timeout: 240_000 });
    await expect(page.getByLabel("Master audio file")).toHaveCount(0);
    await expect(
      page.getByRole("progressbar", { name: "Analysis progress" }),
    ).not.toHaveAttribute("aria-valuenow", "0", { timeout: 240_000 });
  });
});

async function ensureWorkspace(page: import("@playwright/test").Page) {
  const accountResponse = page.waitForResponse(
    (response) =>
      response.request().method() === "GET" &&
      new URL(response.url()).pathname === "/api/v1/account/me",
  );
  await page.goto("/dashboard");
  const account = (await (await accountResponse).json()) as {
    onboardingRequired: boolean;
  };
  const onboarding = page.getByRole("heading", { name: "Name your workspace." });
  if (account.onboardingRequired) {
    await expect(onboarding).toBeVisible();
    await page.getByLabel("Workspace name").fill("Playwright E2E workspace");
    await page.getByLabel("Artist display name").fill("Playwright Artist");
    await page.getByLabel(/I accept the current draft Terms/i).check();
    await page.getByLabel(/I accept the current draft Privacy/i).check();
    await page.getByRole("button", { name: "Enter workspace" }).click();
    await expect(page).toHaveURL(/\/dashboard$/);
    await expect(page.getByText("Playwright E2E workspace")).toBeVisible();
  } else {
    await expect(page.getByRole("link", { name: "New release" }).first()).toBeVisible();
  }
}

async function downloadTo(
  request: import("@playwright/test").APIRequestContext,
  url: string | null,
  path: string,
) {
  const normalized = authenticatedApiUrl(url);
  const response = await request.get(normalized.toString(), {
    headers: { Authorization: `Bearer ${authToken}` },
  });
  expect(response.ok(), `${response.status()} ${response.statusText()}`).toBe(true);
  writeFileSync(path, await response.body());
}

function expectAuthenticatedApiUrl(url: string | null, path: RegExp) {
  const normalized = authenticatedApiUrl(url);
  expect(normalized.pathname).toMatch(path);
  expect(normalized.search).toBe("");
}

function authenticatedApiUrl(url: string | null) {
  expect(url).toBeTruthy();
  const normalized = new URL(url!, apiBaseUrl);
  expect(normalized.origin).toBe(new URL(apiBaseUrl).origin);
  return normalized;
}

async function waitForJobSucceeded(
  request: import("@playwright/test").APIRequestContext,
  jobId: string,
  testInfo: import("@playwright/test").TestInfo,
) {
  let latest: {
    state: string;
    progressPercent: number;
    progressStage?: string | null;
    errorCode?: string | null;
    errorMessage?: string | null;
    attemptCount: number;
  } | undefined;

  try {
    await expect
      .poll(
        async () => {
          const response = await request.get(
            `${apiBaseUrl}/api/v1/jobs/${jobId}`,
            {
              headers: { Authorization: `Bearer ${authToken}` },
            },
          );
          expect(
            response.ok(),
            `GET job ${jobId}: ${response.status()} ${response.statusText()}`,
          ).toBe(true);
          latest = await response.json();
          if (latest?.state === "failed" || latest?.state === "cancelled") {
            throw new Error(
              `Ingest ${latest.state}: ${latest.errorCode ?? "unknown"} — ${
                latest.errorMessage ?? "No safe error message."
              }`,
            );
          }
          return latest?.state;
        },
        {
          message: `mediaIngest job ${jobId} should succeed`,
          timeout: 240_000,
        },
      )
      .toBe("succeeded");
  } finally {
    await testInfo.attach("media-ingest-job.json", {
      body: Buffer.from(JSON.stringify({ jobId, latest }, null, 2)),
      contentType: "application/json",
    });
  }
}

function videoContract(path: string) {
  const raw = execFileSync(
    "ffprobe",
    [
      "-v",
      "error",
      "-show_entries",
      "stream=codec_type,codec_name,width,height",
      "-of",
      "json",
      path,
    ],
    { encoding: "utf8" },
  );
  const streams = (JSON.parse(raw) as {
    streams: Array<{
      codec_type: string;
      codec_name: string;
      width?: number;
      height?: number;
    }>;
  }).streams;
  const video = streams.find((stream) => stream.codec_type === "video");
  const audio = streams.find((stream) => stream.codec_type === "audio");
  return {
    width: video?.width,
    height: video?.height,
    videoCodec: video?.codec_name,
    audioCodec: audio?.codec_name,
  };
}

function dateInDays(days: number) {
  const value = new Date();
  value.setUTCDate(value.getUTCDate() + days);
  return value.toISOString().slice(0, 10);
}
