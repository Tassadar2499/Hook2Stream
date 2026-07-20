import {
  CompleteUpload,
  UploadPart,
  UploadSession,
  apiFetch,
} from "./api";

export type UploadProgress = {
  percent: number;
  stage: "uploading" | "verifying" | "completed";
};

export async function uploadToSession(
  session: UploadSession,
  file: File,
  token: string,
  onProgress: (progress: UploadProgress) => void,
  signal: AbortSignal,
): Promise<CompleteUpload> {
  const completedParts = session.multipart
    ? await uploadMultipart(session, file, token, onProgress, signal)
    : await uploadSingle(session, file, onProgress, signal);

  onProgress({ percent: 92, stage: "verifying" });
  const completed = await apiFetch<CompleteUpload>(
    `/api/v1/uploads/${session.sessionId}/complete`,
    token,
    {
      method: "POST",
      body: JSON.stringify({ parts: completedParts }),
    },
  );
  onProgress({ percent: 100, stage: "completed" });
  return completed.data;
}

async function uploadSingle(
  session: UploadSession,
  file: File,
  onProgress: (progress: UploadProgress) => void,
  signal: AbortSignal,
) {
  if (!session.uploadUrl) {
    throw new Error("The upload URL is missing.");
  }

  await putBlob(
    session.uploadUrl,
    file,
    file.type || "audio/mpeg",
    (uploaded) =>
      onProgress({
        percent: Math.min(90, Math.round((uploaded / file.size) * 90)),
        stage: "uploading",
      }),
    signal,
  );
  return [] as Array<{ partNumber: number; eTag: string }>;
}

async function uploadMultipart(
  session: UploadSession,
  file: File,
  token: string,
  onProgress: (progress: UploadProgress) => void,
  signal: AbortSignal,
) {
  const completed: Array<{ partNumber: number; eTag: string }> = [];
  const uploadedByPart = new Map<number, number>();
  const partCount = Number(session.partCount);
  const partSize = Number(session.partSizeBytes);
  let nextPart = 1;

  async function worker() {
    while (nextPart <= partCount) {
      const partNumber = nextPart++;
      const start = (partNumber - 1) * partSize;
      const blob = file.slice(start, Math.min(file.size, start + partSize));
      let lastError: unknown;
      for (let attempt = 1; attempt <= 3; attempt++) {
        try {
          const signed = await apiFetch<UploadPart>(
            `/api/v1/uploads/${session.sessionId}/parts`,
            token,
            {
              method: "POST",
              body: JSON.stringify({ partNumber }),
            },
          );
          const eTag = await putBlob(
            signed.data.uploadUrl,
            blob,
            file.type || "audio/mpeg",
            (uploaded) => {
              uploadedByPart.set(partNumber, uploaded);
              const total = Array.from(uploadedByPart.values()).reduce(
                (sum, current) => sum + current,
                0,
              );
              onProgress({
                percent: Math.min(90, Math.round((total / file.size) * 90)),
                stage: "uploading",
              });
            },
            signal,
          );
          completed.push({ partNumber, eTag });
          lastError = undefined;
          break;
        } catch (caught) {
          lastError = caught;
          if (attempt < 3) await delay(400 * attempt, signal);
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

function delay(milliseconds: number, signal: AbortSignal) {
  return new Promise<void>((resolve, reject) => {
    const timer = window.setTimeout(resolve, milliseconds);
    signal.addEventListener(
      "abort",
      () => {
        window.clearTimeout(timer);
        reject(new DOMException("Upload aborted.", "AbortError"));
      },
      { once: true },
    );
  });
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
    request.onabort = () =>
      reject(new DOMException("Upload aborted.", "AbortError"));
    signal.addEventListener("abort", () => request.abort(), { once: true });
    request.send(body);
  });
}
