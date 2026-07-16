"use client";

import { FormEvent, useCallback, useEffect, useState } from "react";
import { useAuth } from "@clerk/nextjs";
import { useRouter } from "next/navigation";
import { AppShell } from "@/components/app-shell";
import { StatusPanel } from "@/components/status-panel";
import { UploadManager } from "@/components/upload-manager";
import {
  ApiRequestError,
  Asset,
  Readiness,
  Release,
  apiFetch,
} from "@/lib/api";

export function ReleaseSetupClient({ projectId }: { projectId: string }) {
  const { getToken, isLoaded, isSignedIn } = useAuth();
  const router = useRouter();
  const [release, setRelease] = useState<Release>();
  const [readiness, setReadiness] = useState<Readiness>();
  const [etag, setEtag] = useState<string>();
  const [error, setError] = useState<string>();
  const [rightsSaving, setRightsSaving] = useState(false);

  const refresh = useCallback(async () => {
    const token = await getToken();
    if (!token) throw new Error("No session token.");
    const [releaseResult, readinessResult] = await Promise.all([
      apiFetch<Release>(`/api/v1/releases/${projectId}`, token),
      apiFetch<Readiness>(`/api/v1/releases/${projectId}/readiness`, token),
    ]);
    setRelease(releaseResult.data);
    setReadiness(readinessResult.data);
    setEtag(releaseResult.etag ?? `"${releaseResult.data.version}"`);
  }, [getToken, projectId]);

  useEffect(() => {
    if (!isLoaded) return;
    if (!isSignedIn) {
      router.replace("/");
      return;
    }
    const timer = window.setTimeout(() => {
      void refresh().catch((caught) =>
        setError(
          caught instanceof ApiRequestError
            ? caught.message
            : "Unable to load the release.",
        ),
      );
    }, 0);
    return () => window.clearTimeout(timer);
  }, [isLoaded, isSignedIn, refresh, router]);

  async function confirmRights(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!etag) return;
    setRightsSaving(true);
    setError(undefined);
    const form = new FormData(event.currentTarget);
    try {
      const token = await getToken();
      if (!token) throw new Error("No session token.");
      await apiFetch(`/api/v1/releases/${projectId}/rights`, token, {
        method: "PUT",
        headers: { "If-Match": etag },
        body: JSON.stringify({
          ownsAudioRights: form.get("audio") === "on",
          ownsLyricsRights: form.get("lyrics") === "on",
          ownsVisualRights: form.get("visuals") === "on",
          syntheticContentStatus: form.get("synthetic"),
          policyVersion: "draft-2026-07-16",
        }),
      });
      await refresh();
    } catch (caught) {
      setError(
        caught instanceof ApiRequestError ? caught.message : "Could not save rights confirmation.",
      );
    } finally {
      setRightsSaving(false);
    }
  }

  async function deleteAsset(asset: Asset) {
    if (!window.confirm(`Remove ${asset.fileName} from this release?`)) return;
    try {
      const token = await getToken();
      if (!token) throw new Error("No session token.");
      await apiFetch(
        `/api/v1/releases/${projectId}/assets/${asset.id}`,
        token,
        {
          method: "DELETE",
          headers: { "If-Match": `"${asset.version}"` },
        },
      );
      await refresh();
    } catch (caught) {
      setError(
        caught instanceof ApiRequestError ? caught.message : "Could not remove the asset.",
      );
    }
  }

  if (!release) {
    return (
      <AppShell>
        <StatusPanel
          title={error ? "Could not load release" : "Loading release"}
          message={error ?? "Reading metadata and media state…"}
          tone={error ? "error" : "neutral"}
        />
      </AppShell>
    );
  }

  const activeAssets = release.assets.filter(
    (asset) => asset.isActive || asset.state !== "ready",
  );

  return (
    <AppShell>
      <div className="flex flex-col justify-between gap-5 lg:flex-row lg:items-end">
        <div>
          <p className="eyebrow text-[var(--orange)]">{release.state}</p>
          <h1 className="display mt-2 text-5xl sm:text-7xl">{release.trackTitle}</h1>
          <p className="mt-3 text-xl font-black">{release.artistName}</p>
        </div>
        <div
          className={`rounded-2xl border p-4 ${
            readiness?.ready
              ? "border-green-800/30 bg-green-100"
              : "border-[var(--line)] bg-white/60"
          }`}
        >
          <p className="eyebrow">{readiness?.ready ? "Inputs ready" : "Draft setup"}</p>
          <p className="mt-1 font-bold">
            {readiness?.ready
              ? "Ready for the future analysis increment."
              : `${readiness?.readyVisuals ?? 0}/3 minimum visual assets ready`}
          </p>
        </div>
      </div>

      {error ? (
        <div className="mt-6">
          <StatusPanel title="Action needs attention" message={error} tone="error" />
        </div>
      ) : null}

      <section className="paper-card mt-8 p-6 sm:p-8">
        <p className="eyebrow text-[var(--violet)]">Direct media upload</p>
        <h2 className="display mt-2 text-4xl sm:text-5xl">Feed the release.</h2>
        <p className="mt-4 max-w-3xl leading-7">
          Files bypass the API process and go to a short-lived S3 upload session.
          The worker validates actual bytes, probes codecs and builds browser-safe
          derivatives without changing the original.
        </p>
        <div className="mt-7 grid gap-4">
          <UploadManager
            projectId={projectId}
            kind="audio"
            title="Master audio"
            description="One MP3 or WAV, up to 250 MB and 10 minutes."
            accept=".mp3,.wav,audio/mpeg,audio/wav"
            onCompleted={refresh}
          />
          <UploadManager
            projectId={projectId}
            kind="cover"
            title="Cover artwork"
            description="JPEG, PNG or WebP. A 2048px proxy and thumbnail are generated."
            accept=".jpg,.jpeg,.png,.webp,image/jpeg,image/png,image/webp"
            onCompleted={refresh}
          />
          <UploadManager
            projectId={projectId}
            kind="visual"
            title="Visual library"
            description="Add 3–10 images or short MP4, MOV and WebM clips. Select several at once."
            accept=".jpg,.jpeg,.png,.webp,.mp4,.mov,.webm,image/*,video/mp4,video/quicktime,video/webm"
            multiple
            onCompleted={refresh}
          />
        </div>
      </section>

      <section className="paper-card mt-6 p-6 sm:p-8">
        <div className="flex flex-col justify-between gap-3 sm:flex-row sm:items-end">
          <div>
            <p className="eyebrow">Media state</p>
            <h2 className="display mt-2 text-4xl">Assets and revisions</h2>
          </div>
          <p className="text-sm font-bold opacity-65">
            Replacements become active only after successful ingest.
          </p>
        </div>
        {activeAssets.length === 0 ? (
          <p className="mt-6 rounded-2xl border border-dashed border-[var(--line)] p-6">
            No media reserved yet.
          </p>
        ) : (
          <div className="mt-6 grid gap-3">
            {activeAssets.map((asset) => (
              <article
                key={asset.id}
                className="grid gap-3 rounded-2xl border border-[var(--line)] bg-white/55 p-4 sm:grid-cols-[1fr_auto] sm:items-center"
              >
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="rounded-full bg-[var(--ink)] px-2.5 py-1 text-xs font-black uppercase text-white">
                      {asset.kind}
                    </span>
                    <span
                      className={`rounded-full px-2.5 py-1 text-xs font-black uppercase ${
                        asset.state === "ready"
                          ? "bg-green-200"
                          : asset.state === "rejected"
                            ? "bg-red-200"
                            : "bg-amber-200"
                      }`}
                    >
                      {asset.state}
                    </span>
                    <span className="text-xs font-bold opacity-60">
                      revision {asset.revision}
                    </span>
                  </div>
                  <p className="mt-2 truncate font-black">{asset.fileName}</p>
                  <p className="mt-1 text-sm opacity-65">
                    {formatBytes(Number(asset.actualBytes ?? asset.declaredBytes))}
                    {asset.width && asset.height
                      ? ` · ${asset.width}×${asset.height}`
                      : ""}
                  </p>
                  {asset.failureMessage ? (
                    <p className="mt-2 text-sm font-bold text-red-800">
                      {asset.failureMessage}
                    </p>
                  ) : null}
                </div>
                <button
                  className="button-quiet"
                  type="button"
                  onClick={() => deleteAsset(asset)}
                >
                  Remove
                </button>
              </article>
            ))}
          </div>
        )}
      </section>

      <section className="mt-6 grid gap-6 lg:grid-cols-2">
        <form className="paper-card p-6 sm:p-8" onSubmit={confirmRights}>
          <p className="eyebrow text-[var(--orange)]">Required attestation</p>
          <h2 className="display mt-2 text-4xl">Confirm the rights.</h2>
          <div className="mt-6 grid gap-3">
            {[
              ["audio", "I have the right to process this audio."],
              ["lyrics", "I have the right to use these lyrics."],
              ["visuals", "I have the right to use the cover and visuals."],
            ].map(([name, label]) => (
              <label
                key={name}
                className="flex min-h-12 items-start gap-3 rounded-xl border border-[var(--line)] bg-white/55 p-3 font-bold"
              >
                <input className="mt-0.5 size-5" type="checkbox" name={name} required />
                {label}
              </label>
            ))}
            <label className="field mt-2">
              <span>Synthetic / altered content</span>
              <select name="synthetic" defaultValue="unknown">
                <option value="none">None</option>
                <option value="assisted">AI assisted</option>
                <option value="fullySynthetic">Fully synthetic</option>
                <option value="unknown">Prefer not to classify yet</option>
              </select>
            </label>
          </div>
          <button
            className="button-primary mt-6"
            type="submit"
            disabled={rightsSaving}
          >
            {rightsSaving ? "Saving…" : "Save attestation"}
          </button>
        </form>

        <section className="paper-card p-6 sm:p-8">
          <p className="eyebrow text-[var(--violet)]">Readiness</p>
          <h2 className="display mt-2 text-4xl">
            {readiness?.ready ? "All inputs are in." : "Still needed"}
          </h2>
          {readiness?.ready ? (
            <StatusPanel
              title="Draft complete"
              message="Analysis, hook selection and campaign generation are intentionally the next implementation increment."
              tone="success"
            />
          ) : (
            <ol className="mt-6 grid gap-3">
              {readiness?.missing.map((item, index) => (
                <li
                  key={item}
                  className="flex gap-3 rounded-xl border border-[var(--line)] bg-white/55 p-4 font-bold"
                >
                  <span className="display text-2xl text-[var(--orange)]">
                    {String(index + 1).padStart(2, "0")}
                  </span>
                  {item}
                </li>
              ))}
            </ol>
          )}
        </section>
      </section>
    </AppShell>
  );
}

function formatBytes(bytes: number) {
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
