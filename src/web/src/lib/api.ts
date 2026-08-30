import type { components } from "./api-schema";
import {
  COOKIE_SESSION_MARKER,
  markOAuthSessionSignedOut,
  readOAuthCsrfToken,
} from "./auth-session";
import { buildApiUrl } from "./api-url";

type Schemas = components["schemas"];

export type Account = Schemas["AccountResponse"];
export type BrandKit = Schemas["BrandKitResponse"];
export type AssetKind = Schemas["AssetKind"];
export type AssetState = Schemas["AssetState"];
export type Asset = Schemas["AssetResponse"];
export type ReleaseMode = Schemas["ReleaseMode"];
export type Release = Schemas["ReleaseResponse"];
export type RightsAttestation = Schemas["RightsAttestationResponse"];
export type Readiness = Schemas["ReadinessResponse"];
export type UploadSession = Schemas["UploadSessionResponse"];
export type UploadPart = Schemas["UploadPartResponse"];
export type CompleteUpload = Schemas["CompleteUploadResponse"];
export type Job = Schemas["JobResponse"];

type ProblemDetails = {
  title?: string;
  detail?: string;
  code?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
};

export class ApiRequestError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly code: string,
    public readonly errors?: Record<string, string[]>,
    public readonly traceId?: string,
  ) {
    super(message);
  }
}

export function isStaleMutationError(caught: unknown): caught is ApiRequestError {
  return caught instanceof ApiRequestError && (
    caught.status === 412 ||
    (caught.status === 409 && caught.code === "concurrency.conflict")
  );
}

export async function apiFetch<T>(
  path: string,
  token: string,
  init: RequestInit = {},
): Promise<{ data: T; etag?: string }> {
  const headers = new Headers(init.headers);
  if (token !== COOKIE_SESSION_MARKER) {
    headers.set("Authorization", `Bearer ${token}`);
  }
  headers.set("Accept", "application/json");
  if (init.body && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }
  if (token === COOKIE_SESSION_MARKER && isUnsafeMethod(init.method)) {
    const csrfToken = readOAuthCsrfToken();
    if (csrfToken) headers.set("X-CSRF-Token", csrfToken);
  }

  const response = await fetch(buildApiUrl(path), {
    ...init,
    headers,
    credentials: "include",
    cache: "no-store",
  });

  if (!response.ok) {
    if (response.status === 401 && token === COOKIE_SESSION_MARKER) {
      markOAuthSessionSignedOut();
    }
    let problem: ProblemDetails = {};
    try {
      problem = (await response.json()) as ProblemDetails;
    } catch {
      // Keep the safe HTTP fallback.
    }
    throw new ApiRequestError(
      problem.detail ?? `Request failed with status ${response.status}.`,
      response.status,
      problem.code ?? problem.title ?? "request.failed",
      problem.errors,
      problem.traceId,
    );
  }

  if (response.status === 204) {
    return { data: undefined as T, etag: response.headers.get("ETag") ?? undefined };
  }

  return {
    data: (await response.json()) as T,
    etag: response.headers.get("ETag") ?? undefined,
  };
}

export function apiUploadPart(
  sessionId: string,
  partNumber: number,
  body: Blob,
  token: string,
  onProgress: (uploadedBytes: number) => void,
  signal: AbortSignal,
): Promise<UploadPart> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open("PUT", buildApiUrl(`/api/v1/uploads/${sessionId}/parts/${partNumber}`));
    request.withCredentials = true;
    request.setRequestHeader("Content-Type", "application/octet-stream");
    request.setRequestHeader("Accept", "application/json");
    if (token !== COOKIE_SESSION_MARKER) request.setRequestHeader("Authorization", `Bearer ${token}`);
    else {
      const csrf = readOAuthCsrfToken();
      if (csrf) request.setRequestHeader("X-CSRF-Token", csrf);
    }
    request.upload.onprogress = (event) => onProgress(event.loaded);
    request.onload = () => {
      if (request.status >= 200 && request.status < 300) {
        try { resolve(JSON.parse(request.responseText) as UploadPart); }
        catch { reject(new Error("The upload receipt was invalid.")); }
      } else {
        let problem: ProblemDetails = {};
        try { problem = JSON.parse(request.responseText) as ProblemDetails; }
        catch { /* Keep the safe HTTP fallback. */ }
        reject(new ApiRequestError(
          problem.detail ?? `Upload failed with status ${request.status}.`,
          request.status,
          problem.code ?? problem.title ?? "upload.part_failed",
          problem.errors,
          problem.traceId,
        ));
      }
    };
    request.onerror = () => reject(new Error("The same-origin upload failed."));
    request.onabort = () => reject(new DOMException("Upload aborted.", "AbortError"));
    signal.addEventListener("abort", () => request.abort(), { once: true });
    request.send(body);
  });
}

export async function streamJobEvents(
  jobId: string,
  token: string,
  onEvent: (event: { id?: string; type: string; data: unknown }) => void,
  signal: AbortSignal,
) {
  const headers = authenticatedHeaders(token, "text/event-stream");
  const response = await fetch(buildApiUrl(`/api/v1/jobs/${jobId}/events`), {
    headers,
    credentials: "include",
    cache: "no-store",
    signal,
  });
  if (!response.ok || !response.body) {
    throw new ApiRequestError(
      "Live progress is unavailable.",
      response.status,
      "jobs.stream_unavailable",
    );
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (!signal.aborted) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    const frames = buffer.split("\n\n");
    buffer = frames.pop() ?? "";
    for (const frame of frames) {
      if (frame.startsWith(":")) continue;
      let id: string | undefined;
      let type = "message";
      let rawData = "{}";
      for (const line of frame.split("\n")) {
        if (line.startsWith("id:")) id = line.slice(3).trim();
        if (line.startsWith("event:")) type = line.slice(6).trim();
        if (line.startsWith("data:")) rawData = line.slice(5).trim();
      }

      let data: unknown = rawData;
      try {
        data = JSON.parse(rawData);
      } catch {
        // Preserve non-JSON server data.
      }
      onEvent({ id, type, data });
    }
  }
}

export async function streamProjectEvents(
  projectId: string,
  token: string,
  onEvent: (event: { id?: string; type: string; data: unknown }) => void,
  signal: AbortSignal,
) {
  const headers = authenticatedHeaders(token, "text/event-stream");
  const response = await fetch(
    buildApiUrl(`/api/v1/releases/${projectId}/events`),
    {
      headers,
      credentials: "include",
      cache: "no-store",
      signal,
    },
  );
  if (!response.ok || !response.body) {
    throw new ApiRequestError(
      "Live project progress is unavailable.",
      response.status,
      "project.stream_unavailable",
    );
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (!signal.aborted) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    const frames = buffer.split("\n\n");
    buffer = frames.pop() ?? "";
    for (const frame of frames) {
      if (frame.startsWith(":")) continue;
      let id: string | undefined;
      let type = "message";
      const dataLines: string[] = [];
      for (const line of frame.split("\n")) {
        if (line.startsWith("id:")) id = line.slice(3).trim();
        if (line.startsWith("event:")) type = line.slice(6).trim();
        if (line.startsWith("data:")) dataLines.push(line.slice(5).trim());
      }

      const rawData = dataLines.join("\n") || "{}";
      let data: unknown = rawData;
      try {
        data = JSON.parse(rawData);
      } catch {
        // Preserve non-JSON server data.
      }
      onEvent({ id, type, data });
    }
  }
}

function authenticatedHeaders(token: string, accept: string) {
  const headers = new Headers({ Accept: accept });
  if (token !== COOKIE_SESSION_MARKER) {
    headers.set("Authorization", `Bearer ${token}`);
  }
  return headers;
}

function isUnsafeMethod(method?: string) {
  const normalized = (method ?? "GET").toUpperCase();
  return !["GET", "HEAD", "OPTIONS", "TRACE"].includes(normalized);
}
