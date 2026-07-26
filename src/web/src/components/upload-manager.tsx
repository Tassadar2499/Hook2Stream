"use client";

import { ChangeEvent, useRef, useState } from "react";
import { useAppAuth } from "@/components/app-auth-provider";
import {
  ApiRequestError,
  AssetKind,
  CompleteUpload,
  Job,
  UploadPart,
  UploadSession,
  apiFetch,
  streamJobEvents,
} from "@/lib/api";

type UploadManagerProps = {
  projectId: string;
  kind: AssetKind;
  title: string;
  description: string;
  accept: string;
  multiple?: boolean;
  onCompleted: () => void | Promise<void>;
};

export function UploadManager({
  projectId,
  kind,
  title,
  description,
  accept,
  multiple = false,
  onCompleted,
}: UploadManagerProps) {
  const { getToken } = useAppAuth();
  const [progress, setProgress] = useState(0);
  const [stage, setStage] = useState("Waiting for a file");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [sessionId, setSessionId] = useState<string>();
  const cancellation = useRef<AbortController | undefined>(undefined);

  async function chooseFiles(event: ChangeEvent<HTMLInputElement>) {
    const files = Array.from(event.target.files ?? []);
    event.target.value = "";
    if (files.length === 0) return;

    setBusy(true);
    setError(undefined);
    cancellation.current = new AbortController();

    try {
      for (let index = 0; index < files.length; index++) {
        setStage(
          files.length === 1
            ? `Reserving ${files[index].name}`
            : `Uploading ${index + 1} of ${files.length}: ${files[index].name}`,
        );
        await uploadOne(files[index], cancellation.current.signal);
      }
      setProgress(100);
      setStage(files.length === 1 ? "Asset is ready" : `${files.length} assets are ready`);
      await onCompleted();
    } catch (caught) {
      if (caught instanceof DOMException && caught.name === "AbortError") {
        setStage("Upload cancelled");
      } else {
        setError(
          caught instanceof ApiRequestError ? caught.message : "The upload could not be completed.",
        );
        setStage("Upload needs attention");
      }
    } finally {
      setBusy(false);
      cancellation.current = undefined;
    }
  }

  async function uploadOne(file: File, signal: AbortSignal) {
    const token = await requireToken();
    const storageKey = `hook2stream-upload:${projectId}:${kind}:${file.name}:${file.size}`;
    let session: UploadSession | undefined;
    const storedSessionId = window.localStorage.getItem(storageKey);

    if (storedSessionId) {
      try {
        session = (
          await apiFetch<UploadSession>(
            `/api/v1/uploads/${storedSessionId}`,
            token,
          )
        ).data;
        setStage(`Resuming ${file.name}`);
      } catch {
        window.localStorage.removeItem(storageKey);
      }
    }

    if (!session) {
      session = (
        await apiFetch<UploadSession>(
          `/api/v1/releases/${projectId}/uploads`,
          token,
          {
            method: "POST",
            body: JSON.stringify({
              kind,
              fileName: file.name,
              contentType: file.type || inferContentType(file.name),
              sizeBytes: file.size,
            }),
          },
        )
      ).data;
      window.localStorage.setItem(storageKey, session.sessionId);
    }

    setSessionId(session.sessionId);
    setProgress(0);
    setStage(session.multipart ? "Uploading multipart media" : "Uploading media");

    const completedParts = session.multipart
      ? await uploadMultipart(session, file, token, signal)
      : await uploadSingle(session, file, signal);

    setStage("Verifying upload");
    const accepted = (
      await apiFetch<CompleteUpload>(
        `/api/v1/uploads/${session.sessionId}/complete`,
        token,
        {
          method: "POST",
          body: JSON.stringify({ parts: completedParts }),
        },
      )
    ).data;

    setStage("Processing media");
    await monitorJob(accepted.jobId, token, signal);
    window.localStorage.removeItem(storageKey);
  }

  async function uploadSingle(
    session: UploadSession,
    file: File,
    signal: AbortSignal,
  ) {
    if (!session.uploadUrl) {
      throw new Error("The single-part upload URL is missing.");
    }
    await putBlob(
      session.uploadUrl,
      file,
      file.type || inferContentType(file.name),
      (uploaded) => setProgress(Math.round((uploaded / file.size) * 70)),
      signal,
    );
    return [] as Array<{ partNumber: number; eTag: string }>;
  }

  async function uploadMultipart(
    session: UploadSession,
    file: File,
    token: string,
    signal: AbortSignal,
  ) {
    const completed: Array<{ partNumber: number; eTag: string }> = [];
    const uploadedByPart = new Map<number, number>();
    const partCount = Number(session.partCount);
    const partSizeBytes = Number(session.partSizeBytes);
    let nextPart = 1;

    async function worker() {
      while (nextPart <= partCount) {
        const partNumber = nextPart++;
        const start = (partNumber - 1) * partSizeBytes;
        const end = Math.min(file.size, start + partSizeBytes);
        const blob = file.slice(start, end);

        let lastError: unknown;
        for (let attempt = 1; attempt <= 3; attempt++) {
          try {
            const signed = (
              await apiFetch<UploadPart>(
                `/api/v1/uploads/${session.sessionId}/parts`,
                token,
                {
                  method: "POST",
                  body: JSON.stringify({ partNumber }),
                },
              )
            ).data;
            const eTag = await putBlob(
              signed.uploadUrl,
              blob,
              file.type || inferContentType(file.name),
              (uploaded) => {
                uploadedByPart.set(partNumber, uploaded);
                const total = Array.from(uploadedByPart.values()).reduce(
                  (sum, value) => sum + value,
                  0,
                );
                setProgress(Math.round((total / file.size) * 70));
              },
              signal,
            );
            completed.push({ partNumber, eTag });
            lastError = undefined;
            break;
          } catch (caught) {
            lastError = caught;
            if (attempt < 3) {
              await wait(500 * attempt, signal);
            }
          }
        }

        if (lastError) throw lastError;
      }
    }

    await Promise.all(
      Array.from({ length: Math.min(4, partCount) }, () => worker()),
    );
    return completed.sort((left, right) => left.partNumber - right.partNumber);
  }

  async function monitorJob(jobId: string, token: string, signal: AbortSignal) {
    try {
      await streamJobEvents(
        jobId,
        token,
        (event) => {
          if (event.type === "progress" && isProgressEvent(event.data)) {
            setProgress(70 + Math.round(event.data.progressPercent * 0.3));
            setStage(humanize(event.data.stage));
          }
        },
        signal,
      );
    } catch {
      // Polling below is the required fallback for proxies that buffer SSE.
    }

    while (!signal.aborted) {
      const job = (await apiFetch<Job>(`/api/v1/jobs/${jobId}`, token)).data;
      setProgress(70 + Math.round(Number(job.progressPercent) * 0.3));
      setStage(humanize(job.progressStage ?? job.state));
      if (job.state === "succeeded") return;
      if (job.state === "failed" || job.state === "cancelled") {
        throw new ApiRequestError(
          job.errorMessage ?? "Media processing failed.",
          409,
          job.errorCode ?? "job.failed",
        );
      }
      await wait(1000, signal);
    }
  }

  async function cancel() {
    cancellation.current?.abort();
    if (!sessionId) return;
    try {
      const token = await requireToken();
      await apiFetch(`/api/v1/uploads/${sessionId}/abort`, token, {
        method: "POST",
      });
    } catch {
      // The server will expire or recover the session if cancellation races completion.
    }
  }

  async function requireToken() {
    const token = await getToken();
    if (!token) throw new Error("No session token.");
    return token;
  }

  return (
    <section className="rounded-2xl border border-[var(--line)] bg-white/55 p-5">
      <div className="flex flex-col justify-between gap-4 sm:flex-row sm:items-center">
        <div>
          <h3 className="text-lg font-black">{title}</h3>
          <p className="mt-1 text-sm leading-6 opacity-70">{description}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          <label className={`button-secondary cursor-pointer ${busy ? "pointer-events-none opacity-55" : ""}`}>
            <input
              className="sr-only"
              type="file"
              aria-label={`${title} file`}
              accept={accept}
              multiple={multiple}
              disabled={busy}
              onChange={chooseFiles}
            />
            {busy ? "Working…" : multiple ? "Choose files" : "Choose file"}
          </label>
          {busy ? (
            <button className="button-quiet" type="button" onClick={cancel}>
              Cancel
            </button>
          ) : null}
        </div>
      </div>
      <div
        className="mt-5 h-2 overflow-hidden rounded-full bg-black/10"
        role="progressbar"
        aria-label={`${title} upload progress`}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={progress}
      >
        <div
          className="h-full bg-[var(--violet)] transition-[width]"
          style={{ width: `${progress}%` }}
        />
      </div>
      <div className="mt-2 flex justify-between gap-4 text-xs font-black uppercase tracking-wider">
        <span aria-live="polite">{stage}</span>
        <span>{progress}%</span>
      </div>
      {error ? (
        <p className="mt-4 rounded-xl bg-red-100 p-3 text-sm font-bold text-red-950" role="alert">
          {error}
        </p>
      ) : null}
    </section>
  );
}

function putBlob(
  url: string,
  body: Blob,
  contentType: string,
  onProgress: (uploadedBytes: number) => void,
  signal: AbortSignal,
) {
  return new Promise<string>((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open("PUT", url);
    request.setRequestHeader("Content-Type", contentType);
    request.upload.onprogress = (event) => onProgress(event.loaded);
    request.onload = () => {
      if (request.status >= 200 && request.status < 300) {
        resolve(request.getResponseHeader("ETag") ?? "");
      } else {
        reject(new Error(`Object storage returned ${request.status}.`));
      }
    };
    request.onerror = () => reject(new Error("Object storage upload failed."));
    request.onabort = () => reject(new DOMException("Upload aborted.", "AbortError"));
    signal.addEventListener("abort", () => request.abort(), { once: true });
    request.send(body);
  });
}

function wait(milliseconds: number, signal: AbortSignal) {
  return new Promise<void>((resolve, reject) => {
    const timer = window.setTimeout(resolve, milliseconds);
    signal.addEventListener(
      "abort",
      () => {
        window.clearTimeout(timer);
        reject(new DOMException("Operation aborted.", "AbortError"));
      },
      { once: true },
    );
  });
}

function inferContentType(fileName: string) {
  const extension = fileName.split(".").pop()?.toLowerCase();
  const types: Record<string, string> = {
    mp3: "audio/mpeg",
    wav: "audio/wav",
    jpg: "image/jpeg",
    jpeg: "image/jpeg",
    png: "image/png",
    webp: "image/webp",
    mp4: "video/mp4",
    mov: "video/quicktime",
    webm: "video/webm",
  };
  return types[extension ?? ""] ?? "application/octet-stream";
}

function humanize(value: string) {
  return value.replaceAll("_", " ").replace(/\b\w/g, (character) => character.toUpperCase());
}

function isProgressEvent(
  value: unknown,
): value is { progressPercent: number; stage: string } {
  return (
    typeof value === "object" &&
    value !== null &&
    "progressPercent" in value &&
    "stage" in value &&
    typeof value.progressPercent === "number" &&
    typeof value.stage === "string"
  );
}
