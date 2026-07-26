"use client";

import Link from "next/link";
import { FormEvent, useState } from "react";
import { UploadManager } from "@/components/upload-manager";
import { StatusPanel } from "@/components/status-panel";
import { ApiRequestError, Release, RightsAttestation, apiFetch } from "@/lib/api";
import { createWorkflowCheckpointFormState } from "@/lib/release-workflow-form-state";
import { Workflow, laneOrder, titleCase } from "@/lib/workflow";
import { useAppAuth } from "@/components/app-auth-provider";

type Props = {
  projectId: string;
  release: Release;
  rights?: RightsAttestation;
  workflow: Workflow;
  etag?: string;
  onRefresh: () => Promise<void>;
};

const laneLinks: Partial<Record<string, string>> = {
  transcript: "transcript",
  artwork: "artwork",
  hooks: "campaign",
  campaign: "campaign",
  preview: "campaign",
  finalrender: "campaign",
};

export function ReleaseWorkflowHub({
  projectId,
  release,
  rights,
  workflow,
  etag,
  onRefresh,
}: Props) {
  const { getToken } = useAppAuth();
  const [setupSaving, setSetupSaving] = useState(false);
  const [rightsSaving, setRightsSaving] = useState(false);
  const [checkpoint, setCheckpoint] = useState(() =>
    createWorkflowCheckpointFormState(release, rights),
  );
  const [mode, setMode] = useState<"upcoming" | "released">(
    release.mode === "released" ? "released" : "upcoming",
  );
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();

  async function saveSetup(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!etag) return;
    setSetupSaving(true);
    setError(undefined);
    setNotice(undefined);
    const form = new FormData(event.currentTarget);
    try {
      const token = await requireToken();
      await apiFetch(`/api/v1/releases/${projectId}/setup`, token, {
        method: "PUT",
        headers: { "If-Match": etag },
        body: JSON.stringify({
          projectLabel: form.get("projectLabel"),
          artistName: form.get("artistName"),
          trackTitle: form.get("trackTitle"),
          language: form.get("language"),
          mode,
          releaseDate: form.get("releaseDate") || undefined,
          campaignStartDate:
            mode === "released" ? form.get("campaignStartDate") || undefined : undefined,
          isInstrumental: form.get("instrumental") === "on",
          isInstrumentalConfirmed: checkpoint.instrumentalConfirmed,
          internalNotes: form.get("internalNotes") || undefined,
        }),
      });
      setNotice("Release details confirmed. Artwork can start after rights are saved.");
      await onRefresh();
    } catch (caught) {
      if (caught instanceof ApiRequestError && caught.status === 412) {
        await onRefresh();
        setError(
          "This release changed in another tab. The latest version is loaded; review the fields and save again.",
        );
      } else {
        setError(messageFor(caught, "Could not save release details."));
      }
    } finally {
      setSetupSaving(false);
    }
  }

  async function saveRights(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!etag) return;
    setRightsSaving(true);
    setError(undefined);
    setNotice(undefined);
    try {
      const token = await requireToken();
      await apiFetch(`/api/v1/releases/${projectId}/rights`, token, {
        method: "PUT",
        headers: { "If-Match": etag },
        body: JSON.stringify({
          ownsAudioRights: checkpoint.ownsAudioRights,
          ownsLyricsRights: checkpoint.ownsLyricsRights,
          ownsVisualRights: checkpoint.ownsVisualRights,
          allowsExternalAiArtwork: checkpoint.allowsExternalAiProcessing,
          allowsExternalAiProcessing: checkpoint.allowsExternalAiProcessing,
          syntheticContentStatus: checkpoint.syntheticContentStatus,
          policyVersion: "external-ai-zdr-v1",
        }),
      });
      setNotice("Rights confirmed. OpenRouter transcription, artwork and campaign generation may now run.");
      await onRefresh();
    } catch (caught) {
      if (caught instanceof ApiRequestError && caught.status === 412) {
        await onRefresh();
        setError(
          "This release changed in another tab. The latest rights state is loaded; review it and save again.",
        );
      } else {
        setError(messageFor(caught, "Could not save rights confirmation."));
      }
    } finally {
      setRightsSaving(false);
    }
  }

  async function requireToken() {
    const token = await getToken();
    if (!token) throw new Error("No session token.");
    return token;
  }

  const orderedLanes = [...workflow.lanes].sort(
    (left, right) =>
      laneOrder.map(normalizeLane).indexOf(normalizeLane(left.lane)) -
      laneOrder.map(normalizeLane).indexOf(normalizeLane(right.lane)),
  );
  const hasAcceptedAudio = release.assets.some(
    (asset) =>
      asset.kind === "audio" &&
      asset.isActive &&
      ["uploaded", "processing", "ready"].includes(asset.state),
  );

  return (
    <>
      <div className="flex flex-col justify-between gap-5 lg:flex-row lg:items-end">
        <div>
          <p className="eyebrow text-[var(--orange)]">Audio-first release</p>
          <h1 className="display mt-2 text-5xl sm:text-7xl">
            {release.trackTitle || "Analysing your track"}
          </h1>
          <p className="mt-3 text-xl font-black">
            {release.artistName || "Artist name pending"}
          </p>
        </div>
        <div className="max-w-sm rounded-2xl border border-[var(--line)] bg-white/65 p-4">
          <p className="eyebrow">Next action</p>
          <p className="mt-2 font-black">
            {workflow.nextAction ? titleCase(workflow.nextAction) : "Pipeline is up to date"}
          </p>
        </div>
      </div>

      {error ? (
        <div className="mt-6">
          <StatusPanel title="Action needs attention" message={error} tone="error" />
        </div>
      ) : null}
      {notice ? (
        <div className="mt-6">
          <StatusPanel title="Saved" message={notice} tone="success" />
        </div>
      ) : null}

      {!hasAcceptedAudio ? (
        <section className="paper-card mt-8 p-6 sm:p-8" aria-labelledby="audio-upload-title">
          <p className="eyebrow text-[var(--violet)]">Next: audio master</p>
          <h2 id="audio-upload-title" className="display mt-2 text-4xl sm:text-5xl">
            Upload the finished track.
          </h2>
          <p className="mt-3 max-w-3xl leading-7">
            Choose one MP3 or WAV. The original goes directly to secure storage;
            validation, analysis and RU / EN transcription start after the upload.
          </p>
          <div className="mt-6">
            <UploadManager
              projectId={projectId}
              kind="audio"
              title="Master audio"
              description="One MP3 or WAV, up to 250 MB and 10 minutes."
              accept=".mp3,.wav,audio/mpeg,audio/wav,audio/x-wav"
              onCompleted={onRefresh}
            />
          </div>
        </section>
      ) : null}

      <section className="mt-8 grid gap-4 md:grid-cols-2 xl:grid-cols-4" aria-label="Release workflow">
        {orderedLanes.map((lane) => {
          const normalized = normalizeLane(lane.lane);
          const link = laneLinks[normalized];
          const content = (
            <>
              <div className="flex items-center justify-between gap-3">
                <span className="eyebrow text-[var(--violet)]">{titleCase(lane.lane)}</span>
                <span className={`stage-dot stage-dot-${toneForState(lane.state)}`} aria-hidden="true" />
              </div>
              <p className="mt-3 text-lg font-black">{titleCase(lane.state)}</p>
              <div
                className="mt-4 h-2 overflow-hidden rounded-full bg-black/10"
                role="progressbar"
                aria-label={`${titleCase(lane.lane)} progress`}
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={lane.progressPercent}
              >
                <div
                  className="h-full bg-[var(--violet)]"
                  style={{ width: `${Math.max(0, Math.min(100, lane.progressPercent))}%` }}
                />
              </div>
              <p className="mt-3 min-h-10 text-sm leading-5 opacity-70">
                {lane.blockerCode
                  ? titleCase(lane.blockerCode)
                  : lane.message || descriptionForLane(normalized)}
              </p>
            </>
          );
          return link ? (
            <Link
              key={lane.lane}
              href={`/releases/${projectId}/${link}`}
              className="paper-card p-5 transition hover:-translate-y-1"
            >
              {content}
            </Link>
          ) : (
            <article key={lane.lane} className="paper-card p-5">
              {content}
            </article>
          );
        })}
      </section>

      {workflow.blockers.length > 0 ? (
        <section className="paper-card mt-6 p-6">
          <p className="eyebrow text-[var(--orange)]">Before campaign generation</p>
          <ul className="mt-4 grid gap-2 sm:grid-cols-2">
            {workflow.blockers.map((blocker) => (
              <li key={blocker} className="rounded-xl border border-[var(--line)] bg-white/55 p-3 font-bold">
                {titleCase(blocker)}
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      <section className="mt-6 grid gap-6 xl:grid-cols-2">
        <form className="paper-card p-6 sm:p-8" onSubmit={saveSetup}>
          <p className="eyebrow text-[var(--orange)]">Release checkpoint</p>
          <h2 className="display mt-2 text-4xl">Confirm the details.</h2>
          <p className="mt-3 text-sm leading-6 opacity-70">
            ID3 and filename values are suggestions. Confirmation unlocks the first artwork pack.
          </p>
          <div className="mt-6 grid gap-4 sm:grid-cols-2">
            <label className="field sm:col-span-2">
              <span>Internal label</span>
              <input name="projectLabel" defaultValue={release.projectLabel} required maxLength={160} />
            </label>
            <label className="field">
              <span>Artist</span>
              <input name="artistName" defaultValue={release.artistName} required maxLength={160} />
            </label>
            <label className="field">
              <span>Track title</span>
              <input name="trackTitle" defaultValue={release.trackTitle} required maxLength={160} />
            </label>
            <label className="field">
              <span>Language</span>
              <select name="language" defaultValue={release.language || "en"}>
                <option value="en">English</option>
                <option value="ru">Russian</option>
              </select>
            </label>
            <label className="field">
              <span>Timing</span>
              <select value={mode} onChange={(event) => setMode(event.target.value as "upcoming" | "released")}>
                <option value="upcoming">Upcoming</option>
                <option value="released">Already released</option>
              </select>
            </label>
            <label className="field">
              <span>{mode === "upcoming" ? "Release date" : "Actual release date"}</span>
              <input name="releaseDate" type="date" defaultValue={release.releaseDate ?? ""} required />
            </label>
            {mode === "released" ? (
              <label className="field">
                <span>Campaign start</span>
                <input name="campaignStartDate" type="date" defaultValue={release.campaignStartDate ?? ""} required />
              </label>
            ) : null}
            <label className="flex min-h-12 items-center gap-3 rounded-xl border border-[var(--line)] bg-white/55 p-3 font-bold sm:col-span-2">
              <input type="checkbox" name="instrumental" defaultChecked={release.isInstrumental} />
              This is an instrumental track
            </label>
            <label className="flex min-h-12 items-center gap-3 rounded-xl border border-[var(--line)] bg-white/55 p-3 font-bold sm:col-span-2">
              <input
                type="checkbox"
                name="instrumentalConfirmed"
                checked={checkpoint.instrumentalConfirmed}
                onChange={(event) =>
                  setCheckpoint((current) => ({
                    ...current,
                    instrumentalConfirmed: event.target.checked,
                  }))
                }
              />
              I explicitly confirm instrumental mode if selected
            </label>
            <label className="field sm:col-span-2">
              <span>Creative direction</span>
              <textarea name="internalNotes" defaultValue={release.internalNotes ?? ""} maxLength={4000} />
            </label>
          </div>
          <button className="button-primary mt-6" type="submit" disabled={setupSaving}>
            {setupSaving ? "Saving…" : "Confirm release details"}
          </button>
        </form>

        <form className="paper-card p-6 sm:p-8" onSubmit={saveRights}>
          <p className="eyebrow text-[var(--violet)]">Rights checkpoint</p>
          <h2 className="display mt-2 text-4xl">Allow the processing.</h2>
          <p className="mt-3 text-sm leading-6 opacity-70">
            Transcription, artwork and campaign drafting use OpenRouter only after this
            confirmation. Requests are restricted to Zero Data Retention endpoints.
          </p>
          <div className="mt-6 grid gap-3">
            {[
              ["audio", "I have the right to process this audio."],
              ["lyrics", "I have the right to use the lyrics or performance."],
              ["visuals", "I have the right to use any visuals I upload."],
              ["externalAi", "I allow audio, text and the visual brief to be processed through OpenRouter using Zero Data Retention."],
            ].map(([name, label]) => {
              const field = rightsField(name);
              return (
                <label key={name} className="flex min-h-12 items-start gap-3 rounded-xl border border-[var(--line)] bg-white/55 p-3 font-bold">
                  <input
                    className="mt-0.5 size-5"
                    type="checkbox"
                    name={name}
                    checked={checkpoint[field]}
                    onChange={(event) =>
                      setCheckpoint((current) => ({
                        ...current,
                        [field]: event.target.checked,
                      }))
                    }
                    required={name !== "visuals" && (name !== "lyrics" || !release.isInstrumental)}
                  />
                  {label}
                </label>
              );
            })}
            <label className="field mt-2">
              <span>Synthetic / altered content</span>
              <select
                name="synthetic"
                value={checkpoint.syntheticContentStatus}
                onChange={(event) =>
                  setCheckpoint((current) => ({
                    ...current,
                    syntheticContentStatus: event.target.value as RightsAttestation["syntheticContentStatus"],
                  }))
                }
              >
                <option value="none">None</option>
                <option value="assisted">AI assisted</option>
                <option value="fullySynthetic">Fully synthetic</option>
                <option value="unknown">Not classified</option>
              </select>
            </label>
          </div>
          <button className="button-primary mt-6" type="submit" disabled={rightsSaving}>
            {rightsSaving ? "Saving…" : "Save rights confirmation"}
          </button>
        </form>
      </section>

      <details id="sources" className="paper-card mt-6 p-6 sm:p-8">
        <summary className="cursor-pointer font-black">Advanced sources and manual overrides</summary>
        <div className="mt-6 grid gap-4">
          {hasAcceptedAudio ? (
            <UploadManager
              projectId={projectId}
              kind="audio"
              title="Replace master"
              description="MP3 or WAV. Replacing audio invalidates analysis and dependent revisions."
              accept=".mp3,.wav,audio/mpeg,audio/wav,audio/x-wav"
              onCompleted={onRefresh}
            />
          ) : null}
          <UploadManager
            projectId={projectId}
            kind="cover"
            title="Upload your own cover"
            description="JPG, PNG or WebP becomes a selectable candidate after validation."
            accept=".jpg,.jpeg,.png,.webp,image/jpeg,image/png,image/webp"
            onCompleted={onRefresh}
          />
          <UploadManager
            projectId={projectId}
            kind="visual"
            title="Optional visual library"
            description="Images or short video clips complement the three generated backgrounds."
            accept=".jpg,.jpeg,.png,.webp,.mp4,.mov,.webm,image/*,video/*"
            multiple
            onCompleted={onRefresh}
          />
        </div>
      </details>
    </>
  );
}

function normalizeLane(value: string) {
  return value.replaceAll("_", "").toLowerCase();
}

function rightsField(name: string) {
  const fields = {
    audio: "ownsAudioRights",
    lyrics: "ownsLyricsRights",
    visuals: "ownsVisualRights",
    externalAi: "allowsExternalAiProcessing",
  } as const;
  return fields[name as keyof typeof fields];
}

function toneForState(state: string) {
  const normalized = state.toLowerCase();
  if (normalized.includes("failed")) return "error";
  if (normalized.includes("waiting") || normalized.includes("stale")) return "attention";
  if (normalized.includes("succeeded") || normalized.includes("ready")) return "success";
  return "running";
}

function descriptionForLane(lane: string) {
  const descriptions: Record<string, string> = {
    audio: "Upload validation and browser-safe derivatives.",
    analysis: "Deterministic timing, beats, sections and energy.",
    transcript: "Review low-confidence phrases before approval.",
    artwork: "Choose and tune the official cover.",
    hooks: "Three editable 10–30 second moments.",
    campaign: "Exactly 18 posts across a 21-day window.",
    preview: "One free low-resolution watermarked render.",
    finalrender: "Clean paid videos and export bundle.",
  };
  return descriptions[lane] ?? "Pipeline stage";
}

function messageFor(caught: unknown, fallback: string) {
  return caught instanceof ApiRequestError ? caught.message : fallback;
}
