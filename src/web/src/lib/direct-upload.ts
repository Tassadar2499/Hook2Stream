import {
  ApiRequestError,
  CompleteUpload,
  UploadSession,
  apiFetch,
  apiUploadPart,
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
  const completedParts = await uploadMultipart(session, file, token, onProgress, signal);

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

async function uploadMultipart(
  session: UploadSession,
  file: File,
  token: string,
  onProgress: (progress: UploadProgress) => void,
  signal: AbortSignal,
) {
  await validateResumedUploadParts(session, file, signal);
  const resumed = new Map(
    (session.completedParts ?? []).map((part) => [Number(part.partNumber), part]),
  );
  const completed: Array<{ partNumber: number; eTag: string }> = Array.from(resumed.values()).map(
    (part) => ({ partNumber: Number(part.partNumber), eTag: part.eTag }),
  );
  const uploadedByPart = new Map<number, number>();
  resumed.forEach((part, number) => uploadedByPart.set(number, Number(part.plaintextLength)));
  const partCount = Number(session.partCount);
  const partSize = Number(session.partSizeBytes);
  let nextPart = 1;

  async function worker() {
    while (nextPart <= partCount) {
      const partNumber = nextPart++;
      if (resumed.has(partNumber)) continue;
      const start = (partNumber - 1) * partSize;
      const blob = file.slice(start, Math.min(file.size, start + partSize));
      let lastError: unknown;
      for (let attempt = 1; attempt <= 3; attempt++) {
        try {
          const receipt = await apiUploadPart(
            session.sessionId,
            partNumber,
            blob,
            token,
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
          completed.push({ partNumber, eTag: receipt.eTag });
          lastError = undefined;
          break;
        } catch (caught) {
          lastError = caught;
          if (caught instanceof ApiRequestError && caught.status === 409) throw caught;
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

export async function validateResumedUploadParts(
  session: UploadSession,
  file: Blob,
  signal: AbortSignal,
) {
  const partSize = Number(session.partSizeBytes);
  for (const part of session.completedParts ?? []) {
    if (signal.aborted) throw new DOMException("Upload aborted.", "AbortError");
    const partNumber = Number(part.partNumber);
    const start = (partNumber - 1) * partSize;
    const end = Math.min(file.size, start + Number(part.plaintextLength));
    if (partNumber < 1 || start < 0 || end - start !== Number(part.plaintextLength)) {
      throw new ApiRequestError(
        "The saved upload receipt no longer matches this file. A new upload session is required.",
        409,
        "upload.resume_hash_conflict",
      );
    }
    const digest = await crypto.subtle.digest("SHA-256", await file.slice(start, end).arrayBuffer());
    const actual = Array.from(new Uint8Array(digest), (value) => value.toString(16).padStart(2, "0")).join("");
    if (actual !== part.sha256.toLowerCase()) {
      throw new ApiRequestError(
        "The saved upload parts belong to different content. A new upload session is required.",
        409,
        "upload.resume_hash_conflict",
      );
    }
  }
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
