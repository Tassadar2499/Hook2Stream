import { expect, Page, test } from "@playwright/test";

const projectId = "11111111-1111-4111-8111-111111111111";
const uploadSessionId = "22222222-2222-4222-8222-222222222222";
const uploadAssetId = "33333333-3333-4333-8333-333333333333";
const ingestJobId = "44444444-4444-4444-8444-444444444444";
const previewJobId = "55555555-5555-4555-8555-555555555555";
const previewRetryJobId = "66666666-6666-4666-8666-666666666666";

test("advanced Audio-first setup accepts a WAV master", async ({ page }) => {
  let createdPayload: Record<string, unknown> | undefined;
  let uploadCompleted = false;

  await page.route("**/object-upload", async (route) => {
    expect(route.request().method()).toBe("PUT");
    expect(route.request().headers()["content-type"]).toBe("audio/wav");
    await route.fulfill({ status: 200, headers: { ETag: '"fixture-etag"' } });
  });
  await page.route("**/api/v1/**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;

    if (request.method() === "POST" && path === "/api/v1/releases") {
      createdPayload = request.postDataJSON() as Record<string, unknown>;
      return json(route, releaseResponse([]), 201, '"1"');
    }
    if (
      request.method() === "POST" &&
      path === `/api/v1/releases/${projectId}/uploads`
    ) {
      const payload = request.postDataJSON() as Record<string, unknown>;
      expect(payload.kind).toBe("audio");
      expect(payload.fileName).toBe("fixture-master.wav");
      expect(payload.contentType).toBe("audio/wav");
      return json(route, {
        sessionId: uploadSessionId,
        assetId: uploadAssetId,
        multipart: false,
        uploadUrl: "http://127.0.0.1:3101/object-upload",
        multipartUploadId: null,
        partSizeBytes: 0,
        partCount: 1,
        urlExpiresAt: "2030-01-01T00:10:00Z",
        sessionExpiresAt: "2030-01-02T00:00:00Z",
      });
    }
    if (
      request.method() === "POST" &&
      path === `/api/v1/uploads/${uploadSessionId}/complete`
    ) {
      return json(route, { assetId: uploadAssetId, jobId: ingestJobId });
    }
    if (request.method() === "GET" && path === `/api/v1/jobs/${ingestJobId}`) {
      uploadCompleted = true;
      return json(route, {
        id: ingestJobId,
        type: "mediaIngest",
        state: "succeeded",
        progressPercent: 100,
        progressStage: "ready",
        errorCode: null,
        errorMessage: null,
        attemptCount: 1,
        createdAt: "2030-01-01T00:00:00Z",
        completedAt: "2030-01-01T00:00:01Z",
        version: 2,
      });
    }
    if (request.method() === "GET" && path === `/api/v1/releases/${projectId}`) {
      return json(
        route,
        releaseResponse(uploadCompleted ? [readyAudioAsset()] : []),
        200,
        uploadCompleted ? '"2"' : '"1"',
      );
    }
    if (
      request.method() === "GET" &&
      path === `/api/v1/releases/${projectId}/readiness`
    ) {
      return json(route, {
        ready: false,
        missing: uploadCompleted ? ["rights"] : ["audio", "rights"],
        readyVisuals: 0,
        hasAudio: uploadCompleted,
        hasCover: false,
        hasLyricsOrInstrumental: true,
        hasRightsAttestation: false,
      });
    }
    if (
      request.method() === "GET" &&
      path === `/api/v1/releases/${projectId}/workflow`
    ) {
      return json(
        route,
        workflowResponse(
          uploadCompleted ? "succeeded" : "waitingUser",
          uploadCompleted ? "completeSetup" : "uploadAudio",
        ),
      );
    }
    if (
      request.method() === "GET" &&
      path === `/api/v1/releases/${projectId}/rights`
    ) {
      return problem(route, 404, "rights.not_found", "Rights are not confirmed.");
    }
    return problem(route, 404, "test.unhandled_route", `${request.method()} ${path}`);
  });

  await page.goto("/releases/new");
  await page
    .getByText(/Audio-first setup — WAV or MP3/i)
    .click();
  await page.getByLabel("Internal project label").fill("WAV launch");
  await page.getByLabel("Artist name").fill("Test Artist");
  await page.getByLabel("Track title").fill("Waveform");
  await page
    .getByRole("textbox", { name: "Lyrics", exact: true })
    .fill("A fixture line for deterministic testing.");
  await page
    .getByRole("button", { name: "Create draft and upload WAV or MP3" })
    .click();

  await expect(page).toHaveURL(`/releases/${projectId}`);
  expect(createdPayload).toMatchObject({
    projectLabel: "WAV launch",
    artistName: "Test Artist",
    trackTitle: "Waveform",
    language: "en",
    isInstrumental: false,
  });
  await expect(
    page.getByRole("heading", { name: "Upload the finished track." }),
  ).toBeVisible();

  const wavInput = page.getByLabel("Master audio file");
  await expect(wavInput).toHaveAttribute("accept", /\.wav/);
  await wavInput.setInputFiles({
    name: "fixture-master.wav",
    mimeType: "audio/wav",
    buffer: Buffer.from("RIFF-fixture-wave"),
  });

  await expect(
    page.getByRole("progressbar", { name: "Audio progress" }),
  ).toHaveAttribute("aria-valuenow", "100");
  await expect(page.getByLabel("Master audio file")).toHaveCount(0);
});

test("terminal preview can be retried with concurrency and idempotency guards", async ({
  page,
}) => {
  let retryRequest:
    | {
        headers: Record<string, string>;
        body: Record<string, unknown>;
      }
    | undefined;
  let retryQueued = false;

  await installCampaignRoutes(page, async (request) => {
    retryRequest = {
      headers: request.headers(),
      body: request.postDataJSON() as Record<string, unknown>,
    };
    retryQueued = true;
  }, () => retryQueued);

  await page.goto(`/releases/${projectId}/campaign`);
  await expect(
    page.getByText(/Preview rendering stopped before a video was produced/i),
  ).toBeVisible();

  await page.getByRole("button", { name: "Retry preview" }).click();

  await expect(page.getByText(/Preview retry queued/i)).toBeVisible();
  expect(retryRequest?.headers["if-match"]).toBe('"7"');
  expect(retryRequest?.headers["idempotency-key"]).toMatch(/^preview-retry:/);
  expect(retryRequest?.body).toEqual({ failedJobId: previewJobId });
  await expect(
    page.getByText(/watermarked preview is rendering automatically/i),
  ).toBeVisible();
});

test("a cancelled preview is informative but cannot call failed-job retry", async ({
  page,
}) => {
  let retryCalls = 0;
  await installCampaignRoutes(
    page,
    async () => {
      retryCalls += 1;
    },
    () => false,
    () => false,
    "cancelled",
  );

  await page.goto(`/releases/${projectId}/campaign`);
  await expect(
    page.getByText(/cancelled because the campaign changed/i),
  ).toBeVisible();
  await expect(page.getByRole("button", { name: "Retry preview" })).toHaveCount(0);
  expect(retryCalls).toBe(0);
});

test("a stale campaign update keeps the card draft open", async ({ page }) => {
  let updateAttempts = 0;
  await installCampaignRoutes(
    page,
    async () => {
      updateAttempts += 1;
    },
    () => false,
    () => updateAttempts++ === 0,
  );

  await page.goto(`/releases/${projectId}/campaign`);
  await page.getByRole("button", { name: "Edit card" }).first().click();
  const text = page.getByLabel("On-screen text");
  await text.fill("Unsaved cross-tab-safe headline");
  await page.getByRole("button", { name: "Apply item revision" }).click();

  await expect(
    page.getByText(/card edits are still open against the latest version/i),
  ).toBeVisible();
  await expect(page.getByRole("dialog")).toBeVisible();
  await expect(text).toHaveValue("Unsaved cross-tab-safe headline");
});

async function installCampaignRoutes(
  page: Page,
  onRetry: (request: import("@playwright/test").Request) => Promise<void>,
  retryQueued: () => boolean,
  staleCampaignUpdate: () => boolean = () => false,
  terminalPreviewState = "failed",
) {
  await page.route("**/api/v1/**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;

    if (request.method() === "GET" && path === `/api/v1/releases/${projectId}`) {
      return json(route, releaseResponse([]), 200, '"7"');
    }
    if (request.method() === "GET" && path === `/api/v1/releases/${projectId}/hooks`) {
      return json(route, hookResponse(), 200, '"4"');
    }
    if (
      request.method() === "GET" &&
      path === `/api/v1/releases/${projectId}/campaign`
    ) {
      return json(route, campaignResponse(), 200, '"9"');
    }
    if (
      request.method() === "GET" &&
      path === `/api/v1/releases/${projectId}/artwork`
    ) {
      return json(route, {
        revisionId: "77777777-7777-4777-8777-777777777777",
        number: 1,
        operationNumber: 1,
        state: "approved",
        version: 2,
        prompt: "Fixture artwork",
        candidateAssetIds: [],
        backgroundAssetIds: [],
        selectedAssetId: null,
        approvedCoverAssetId: null,
        compositionJson: "{}",
        approvedAt: "2030-01-01T00:00:00Z",
      });
    }
    if (request.method() === "GET" && path === "/api/v1/billing/summary") {
      return json(route, {
        workspaceArtworkCredits: 0,
        activeSubscription: null,
        entitlements: [],
      });
    }
    if (
      request.method() === "GET" &&
      path === `/api/v1/releases/${projectId}/workflow`
    ) {
      return json(
        route,
        previewWorkflow(retryQueued() ? "retrying" : terminalPreviewState),
      );
    }
    if (
      request.method() === "POST" &&
      path === `/api/v1/releases/${projectId}/preview/retries`
    ) {
      await onRetry(request);
      return json(route, {
        jobId: previewRetryJobId,
        revisionId: "88888888-8888-4888-8888-888888888888",
      }, 202);
    }
    if (
      request.method() === "PUT" &&
      path.startsWith(`/api/v1/releases/${projectId}/campaign/items/`)
    ) {
      if (staleCampaignUpdate()) {
        return problem(
          route,
          412,
          "concurrency.precondition_failed",
          "The campaign changed.",
        );
      }
      return json(route, campaignResponse(), 200, '"10"');
    }
    return problem(route, 404, "test.unhandled_route", `${request.method()} ${path}`);
  });
}

function releaseResponse(assets: Record<string, unknown>[]) {
  return {
    id: projectId,
    projectLabel: "Fixture release",
    artistName: "Test Artist",
    trackTitle: "Waveform",
    language: "en",
    internalNotes: null,
    lyricsText: "Fixture lyrics",
    isInstrumental: false,
    isInstrumentalConfirmed: false,
    mode: "upcoming",
    releaseDate: "2030-02-01",
    campaignStartDate: null,
    state: "campaignReady",
    isArchived: false,
    version: 7,
    createdAt: "2030-01-01T00:00:00Z",
    assets,
  };
}

function readyAudioAsset() {
  return {
    id: uploadAssetId,
    kind: "audio",
    origin: "uploaded",
    purpose: "audioMaster",
    state: "ready",
    fileName: "fixture-master.wav",
    contentType: "audio/wav",
    declaredBytes: 17,
    actualBytes: 17,
    revision: 1,
    sortOrder: 0,
    isActive: true,
    failureCode: null,
    failureMessage: null,
    durationMilliseconds: 30_000,
    width: null,
    height: null,
    version: 2,
  };
}

function workflowResponse(audioState: string, nextAction: string) {
  return {
    projectId,
    flowKind: "mp3First",
    projectVersion: uploadCompletedVersion(audioState),
    workflowVersion: uploadCompletedVersion(audioState),
    blockers: audioState === "succeeded" ? ["rights.required"] : ["audio.required"],
    nextAction,
    lanes: laneNames().map((lane) => ({
      lane,
      state: lane === "audio" ? audioState : "notStarted",
      progressPercent: lane === "audio" && audioState === "succeeded" ? 100 : 0,
      blockerCode: null,
      errorCode: null,
      currentJobId: null,
    })),
    currentRenderBatchId: null,
  };
}

function previewWorkflow(state: string) {
  return {
    projectId,
    flowKind: "mp3First",
    projectVersion: 7,
    workflowVersion: 11,
    blockers: [],
    nextAction: state === "failed" ? "retryPreview" : "waitForPreview",
    lanes: laneNames().map((lane) => ({
      lane,
      state: lane === "preview" ? state : "succeeded",
      progressPercent: lane === "preview" && state !== "succeeded" ? 65 : 100,
      blockerCode: null,
      errorCode: lane === "preview" && state === "failed"
        ? "preview.render_failed"
        : null,
      currentJobId: lane === "preview"
        ? state === "failed"
          ? previewJobId
          : previewRetryJobId
        : null,
    })),
    currentRenderBatchId: null,
  };
}

function hookResponse() {
  return {
    revisionId: "99999999-9999-4999-8999-999999999999",
    number: 1,
    transcriptRevisionId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    version: 4,
    hooks: [0, 1, 2].map((index) => ({
      id: `bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb${index}`,
      kind: index === 0 ? "chorus" : index === 1 ? "emotionalLine" : "instrumentalDrop",
      startMilliseconds: index * 10_000,
      endMilliseconds: index * 10_000 + 10_000,
      label: `Hook ${index + 1}`,
    })),
  };
}

function campaignResponse() {
  const hooks = hookResponse().hooks;
  return {
    revisionId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
    number: 1,
    state: "approved",
    version: 9,
    items: Array.from({ length: 18 }, (_, index) => ({
      id: `dddddddd-dddd-4ddd-8ddd-${String(index + 1).padStart(12, "0")}`,
      slot: index + 1,
      template: index < 12 ? "lyric" : "campaign",
      hookId: hooks[index % hooks.length].id,
      backgroundAssetId: null,
      text: `Fixture campaign item ${index + 1}`,
      compositionJson: JSON.stringify({
        relativeDay: index - 10,
        phase: index < 8 ? "pre-release" : "post-release",
        durationMilliseconds: 15_000,
        cta: "Listen now",
        caption: `Fixture caption ${index + 1}`,
        primaryColor: "#121212",
        secondaryColor: "#fffaf2",
        brandVersion: 0,
        fit: "fill",
        focalX: 0.5,
        focalY: 0.5,
        opening: "fade",
        textLayout: "center",
      }),
    })),
  };
}

function laneNames() {
  return [
    "audio",
    "analysis",
    "transcript",
    "artwork",
    "hooks",
    "campaign",
    "preview",
    "finalRender",
  ];
}

function uploadCompletedVersion(audioState: string) {
  return audioState === "succeeded" ? 2 : 1;
}

async function json(
  route: import("@playwright/test").Route,
  value: unknown,
  status = 200,
  etag?: string,
) {
  await route.fulfill({
    status,
    contentType: "application/json",
    headers: etag ? { ETag: etag } : undefined,
    body: JSON.stringify(value),
  });
}

async function problem(
  route: import("@playwright/test").Route,
  status: number,
  code: string,
  detail: string,
) {
  await json(route, { title: "Request failed", code, detail }, status);
}
