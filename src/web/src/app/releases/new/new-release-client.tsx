"use client";

import { ChangeEvent, FormEvent, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { useAppAuth } from "@/components/app-auth-provider";
import { AppShell } from "@/components/app-shell";
import { StatusPanel } from "@/components/status-panel";
import {
  ApiRequestError,
  Release,
  ReleaseMode,
  UploadSession,
  apiFetch,
} from "@/lib/api";
import { uploadToSession } from "@/lib/direct-upload";
import { createIdempotencyKey } from "@/lib/workflow";

type QuickStartResponse = {
  project: Release;
  upload: UploadSession;
  workflow: unknown;
};

function isoDate(offsetDays: number) {
  const value = new Date();
  value.setUTCDate(value.getUTCDate() + offsetDays);
  return value.toISOString().slice(0, 10);
}

export function NewReleaseClient() {
  const { getToken } = useAppAuth();
  const router = useRouter();
  const [mode, setMode] = useState<ReleaseMode>("upcoming");
  const [instrumental, setInstrumental] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string>();
  const [uploadProgress, setUploadProgress] = useState(0);
  const [uploadStage, setUploadStage] = useState("Choose an MP3 to begin");
  const [quickStartBusy, setQuickStartBusy] = useState(false);
  const [externalAiConsent, setExternalAiConsent] = useState(false);
  const uploadCancellation = useRef<AbortController | undefined>(undefined);
  const defaults = useMemo(
    () => ({
      upcomingRelease: isoDate(14),
      releasedDate: isoDate(-7),
      campaignStart: isoDate(0),
    }),
    [],
  );

  async function quickStart(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) return;
    if (!externalAiConsent) {
      setError("Confirm the rights and OpenRouter Zero Data Retention processing before uploading.");
      return;
    }
    if (!file.name.toLowerCase().endsWith(".mp3")) {
      setError("The one-click flow accepts an MP3. Use Advanced source for WAV.");
      return;
    }

    setQuickStartBusy(true);
    setError(undefined);
    setUploadProgress(0);
    setUploadStage("Creating a recoverable draft");
    const cancellation = new AbortController();
    uploadCancellation.current = cancellation;

    try {
      const token = await getToken();
      if (!token) throw new Error("No session token.");
      const resumeKey = `hook2stream-quick:${file.name}:${file.size}:${file.lastModified}`;
      const idempotencyKey =
        window.localStorage.getItem(resumeKey) ?? createIdempotencyKey("audio-upload");
      window.localStorage.setItem(resumeKey, idempotencyKey);
      const started = await apiFetch<QuickStartResponse>(
        "/api/v1/releases/audio-uploads",
        token,
        {
          method: "POST",
          headers: { "Idempotency-Key": idempotencyKey },
          body: JSON.stringify({
            fileName: file.name,
            contentType: file.type || "audio/mpeg",
            sizeBytes: file.size,
            confirmsContentRights: true,
            allowsExternalAiProcessing: true,
          }),
        },
      );

      const uploadAsset = started.data.project.assets.find(
        (asset) => asset.id === started.data.upload.assetId,
      );
      const uploadStillRequired = uploadAsset
        ? uploadAsset.state === "reserved" || uploadAsset.state === "uploading"
        : Boolean(started.data.upload.multipart || started.data.upload.uploadUrl);
      if (uploadStillRequired) {
        setUploadStage("Uploading directly to secure storage");
        await uploadToSession(
          started.data.upload,
          file,
          token,
          ({ percent, stage }) => {
            setUploadProgress(percent);
            setUploadStage(
              stage === "uploading"
                ? "Uploading MP3"
                : stage === "verifying"
                  ? "Verifying the upload"
                  : "Starting transcription and analysis",
            );
          },
          cancellation.signal,
        );
      } else {
        setUploadProgress(100);
        setUploadStage("Upload already completed — restoring the workflow");
      }
      window.localStorage.removeItem(resumeKey);
      router.push(`/releases/${started.data.project.id}`);
    } catch (caught) {
      if (caught instanceof DOMException && caught.name === "AbortError") {
        setUploadStage("Upload paused — choose the same file to resume");
      } else {
        setError(
          caught instanceof ApiRequestError
            ? caught.message
            : "Could not start this release.",
        );
        setUploadStage("Upload needs attention");
      }
    } finally {
      setQuickStartBusy(false);
      uploadCancellation.current = undefined;
    }
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaving(true);
    setError(undefined);
    const form = new FormData(event.currentTarget);
    try {
      const token = await getToken();
      if (!token) throw new Error("No session token.");
      const result = await apiFetch<Release>("/api/v1/releases", token, {
        method: "POST",
        body: JSON.stringify({
          projectLabel: form.get("projectLabel"),
          artistName: form.get("artistName"),
          trackTitle: form.get("trackTitle"),
          language: form.get("language"),
          internalNotes: form.get("internalNotes") || undefined,
          lyricsText: instrumental ? undefined : form.get("lyricsText"),
          isInstrumental: instrumental,
          mode,
          releaseDate: form.get("releaseDate"),
          campaignStartDate:
            mode === "released" ? form.get("campaignStartDate") : undefined,
        }),
      });
      router.push(`/releases/${result.data.id}`);
    } catch (caught) {
      setError(
        caught instanceof ApiRequestError ? caught.message : "Could not create the release.",
      );
    } finally {
      setSaving(false);
    }
  }

  return (
    <AppShell>
      <p className="eyebrow text-[var(--orange)]">New release</p>
      <h1 className="display mt-2 text-5xl sm:text-7xl">Drop the song. We build the campaign.</h1>
      <p className="mt-4 max-w-2xl text-lg leading-8">
        Upload one finished MP3. Transcription and music analysis start
        automatically; you review the words and artwork before anything is rendered.
      </p>

      <section className="paper-card mt-8 overflow-hidden p-6 sm:p-9">
        <div className="grid gap-7 lg:grid-cols-[1fr_.72fr] lg:items-center">
          <div>
            <p className="eyebrow text-[var(--violet)]">MP3-first</p>
            <h2 className="display mt-2 text-4xl sm:text-6xl">One file is enough.</h2>
            <p className="mt-4 max-w-xl leading-7">
              We create an unscheduled draft, read its tags, analyse rhythm locally and
              prepare an editable transcript through OpenRouter. You review every result
              before the campaign is rendered.
            </p>
            <label className="mt-6 flex max-w-xl items-start gap-3 rounded-2xl border border-[var(--line)] bg-white/65 p-4 font-bold leading-6">
              <input
                className="mt-0.5 size-5 shrink-0"
                type="checkbox"
                checked={externalAiConsent}
                disabled={quickStartBusy}
                onChange={(event) => setExternalAiConsent(event.target.checked)}
              />
              <span>
                I confirm I have the rights to process this audio, lyrics and performance,
                and allow audio, text and the visual brief to be sent through OpenRouter
                using Zero Data Retention.
              </span>
            </label>
            <label
              className={`button-secondary mt-6 cursor-pointer ${
                quickStartBusy || !externalAiConsent
                  ? "pointer-events-none opacity-55"
                  : ""
              }`}
            >
              <input
                className="sr-only"
                type="file"
                accept=".mp3,audio/mpeg"
                disabled={quickStartBusy || !externalAiConsent}
                onChange={quickStart}
              />
              {quickStartBusy ? "Uploading…" : "Choose MP3"}
            </label>
            {quickStartBusy ? (
              <button
                className="button-quiet ml-3"
                type="button"
                onClick={() => uploadCancellation.current?.abort()}
              >
                Pause
              </button>
            ) : null}
          </div>
          <div className="rounded-3xl bg-[var(--ink)] p-6 text-white">
            <p className="eyebrow text-[var(--lime)]">Automatic first pass</p>
            <ol className="mt-5 grid gap-3 font-bold">
              <li>01 · Validate audio and read ID3</li>
              <li>02 · Transcribe RU / EN via OpenRouter</li>
              <li>03 · Find three musical hooks</li>
              <li>04 · Generate editable artwork via OpenRouter</li>
              <li>05 · Build an editable 18-post campaign</li>
            </ol>
          </div>
        </div>
        <div
          className="mt-7 h-2 overflow-hidden rounded-full bg-black/10"
          role="progressbar"
          aria-label="MP3 upload"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={uploadProgress}
        >
          <div
            className="h-full bg-[var(--violet)] transition-[width]"
            style={{ width: `${uploadProgress}%` }}
          />
        </div>
        <div className="mt-2 flex justify-between gap-3 text-xs font-black uppercase tracking-wider">
          <span aria-live="polite">{uploadStage}</span>
          <span>{uploadProgress}%</span>
        </div>
        {error ? (
          <div className="mt-5">
            <StatusPanel title="Could not start release" message={error} tone="error" />
          </div>
        ) : null}
      </section>

      <details className="paper-card mt-6 p-6 sm:p-8">
        <summary className="cursor-pointer font-black">
          Advanced source — WAV, prepared lyrics and full release brief
        </summary>
      <form className="mt-7 grid gap-7" onSubmit={submit}>
        <div className="grid gap-5 md:grid-cols-2">
          <label className="field">
            <span>Internal project label</span>
            <input name="projectLabel" required maxLength={160} />
          </label>
          <label className="field">
            <span>Language</span>
            <select name="language" defaultValue="en">
              <option value="en">English</option>
              <option value="ru">Russian</option>
              <option value="es">Spanish</option>
              <option value="de">German</option>
              <option value="fr">French</option>
            </select>
          </label>
          <label className="field">
            <span>Artist name</span>
            <input name="artistName" required maxLength={160} />
          </label>
          <label className="field">
            <span>Track title</span>
            <input name="trackTitle" required maxLength={160} />
          </label>
        </div>

        <fieldset className="grid gap-3">
          <legend className="eyebrow mb-2">Release timing</legend>
          <div className="grid gap-3 sm:grid-cols-2">
            {(["upcoming", "released"] as ReleaseMode[]).map((value) => (
              <label
                key={value}
                className={`flex min-h-20 cursor-pointer items-center gap-3 rounded-2xl border p-4 font-black ${
                  mode === value
                    ? "border-black bg-[var(--lime)]"
                    : "border-[var(--line)] bg-white/55"
                }`}
              >
                <input
                  type="radio"
                  name="mode"
                  value={value}
                  checked={mode === value}
                  onChange={() => setMode(value)}
                />
                {value === "upcoming" ? "Upcoming release" : "Already released"}
              </label>
            ))}
          </div>
        </fieldset>

        <div className="grid gap-5 md:grid-cols-2">
          <label className="field">
            <span>{mode === "upcoming" ? "Release date" : "Actual release date"}</span>
            <input
              type="date"
              name="releaseDate"
              required
              defaultValue={
                mode === "upcoming"
                  ? defaults.upcomingRelease
                  : defaults.releasedDate
              }
              key={mode}
            />
          </label>
          {mode === "released" ? (
            <label className="field">
              <span>Campaign start</span>
              <input
                type="date"
                name="campaignStartDate"
                required
                defaultValue={defaults.campaignStart}
              />
            </label>
          ) : null}
        </div>

        <label className="flex min-h-12 items-center gap-3 rounded-2xl border border-[var(--line)] bg-white/55 p-4 font-bold">
          <input
            className="size-5"
            type="checkbox"
            checked={instrumental}
            onChange={(event) => setInstrumental(event.target.checked)}
          />
          This track is instrumental
        </label>

        {!instrumental ? (
          <label className="field">
            <span>Lyrics</span>
            <textarea
              name="lyricsText"
              required
              maxLength={100000}
              placeholder="Paste the final lyrics. Phrase timing comes in the analysis increment."
            />
          </label>
        ) : null}

        <label className="field">
          <span>Internal notes</span>
          <textarea
            name="internalNotes"
            maxLength={4000}
            placeholder="Creative direction, references or launch context."
          />
        </label>

        {error ? <StatusPanel title="Could not create release" message={error} tone="error" /> : null}
        <button className="button-primary justify-self-start" disabled={saving} type="submit">
          {saving ? "Creating draft…" : "Continue to media"}
        </button>
      </form>
      </details>
    </AppShell>
  );
}
