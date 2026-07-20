"use client";

import Link from "next/link";
import { ChangeEvent, useCallback, useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { AppShell } from "@/components/app-shell";
import { useAppAuth } from "@/components/app-auth-provider";
import { StatusPanel } from "@/components/status-panel";
import { ApiRequestError, Release, apiFetch } from "@/lib/api";
import {
  TranscriptPhrase,
  TranscriptRevision,
  createIdempotencyKey,
  titleCase,
} from "@/lib/workflow";
import { useProjectAutoRefresh } from "@/lib/use-project-auto-refresh";

type AssetReadUrl = { assetId: string; url: string; expiresAt: string };

export function TranscriptReviewClient({ projectId }: { projectId: string }) {
  const { getToken, isLoaded, isSignedIn } = useAppAuth();
  const router = useRouter();
  const [release, setRelease] = useState<Release>();
  const [transcript, setTranscript] = useState<TranscriptRevision>();
  const [phrases, setPhrases] = useState<TranscriptPhrase[]>([]);
  const [etag, setEtag] = useState<string>();
  const [projectEtag, setProjectEtag] = useState<string>();
  const [audioUrl, setAudioUrl] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const [importText, setImportText] = useState("");

  const load = useCallback(async () => {
    const token = await getToken();
    if (!token) throw new Error("No session token.");
    const releaseResult = await apiFetch<Release>(`/api/v1/releases/${projectId}`, token);
    setRelease(releaseResult.data);
    setProjectEtag(releaseResult.etag ?? `"${releaseResult.data.version}"`);
    const audio = releaseResult.data.assets.find(
      (asset) => asset.kind === "audio" && asset.isActive,
    );
    if (audio) {
      void apiFetch<AssetReadUrl>(
        `/api/v1/releases/${projectId}/assets/${audio.id}/view-url`,
        token,
      )
        .then((result) => setAudioUrl(result.data.url))
        .catch(() => setAudioUrl(undefined));
    }

    try {
      const result = await apiFetch<TranscriptRevision>(
        `/api/v1/releases/${projectId}/transcript`,
        token,
      );
      setTranscript(result.data);
      setPhrases(result.data.phrases);
      setEtag(result.etag ?? `"${result.data.version}"`);
    } catch (caught) {
      if (!(caught instanceof ApiRequestError && caught.status === 404)) throw caught;
      setTranscript(undefined);
      setPhrases([]);
    }
  }, [getToken, projectId]);

  useEffect(() => {
    if (!isLoaded) return;
    if (!isSignedIn) {
      router.replace("/");
      return;
    }
    const timer = window.setTimeout(() => {
      void load()
        .catch((caught) => setError(messageFor(caught, "Could not load the transcript.")))
        .finally(() => setLoading(false));
    }, 0);
    return () => window.clearTimeout(timer);
  }, [isLoaded, isSignedIn, load, router]);

  useProjectAutoRefresh(
    projectId,
    getToken,
    load,
    isLoaded && isSignedIn && (!transcript || transcript.state === "processing"),
  );

  const validation = useMemo(() => validatePhrases(phrases), [phrases]);
  const unresolvedWarnings = phrases.filter(
    (phrase) => (phrase.confidence ?? 1) < 0.75 && !phrase.warningAcknowledged,
  ).length;
  const transcriptDirty = transcript
    ? JSON.stringify(phrases) !== JSON.stringify(transcript.phrases)
    : false;

  function updatePhrase(id: string, patch: Partial<TranscriptPhrase>) {
    setPhrases((current) =>
      current.map((phrase) => (phrase.id === id ? { ...phrase, ...patch } : phrase)),
    );
  }

  function splitPhrase(index: number) {
    const phrase = phrases[index];
    const words = phrase.text.trim().split(/\s+/);
    const pivot = Math.max(1, Math.floor(words.length / 2));
    const middle = Math.round((phrase.startMilliseconds + phrase.endMilliseconds) / 2);
    const first: TranscriptPhrase = {
      ...phrase,
      text: words.slice(0, pivot).join(" "),
      endMilliseconds: middle,
      words: undefined,
    };
    const second: TranscriptPhrase = {
      ...phrase,
      id: globalThis.crypto.randomUUID(),
      order: phrase.order + 1,
      text: words.slice(pivot).join(" "),
      startMilliseconds: middle,
      words: undefined,
    };
    setPhrases(
      [...phrases.slice(0, index), first, second, ...phrases.slice(index + 1)].map(
        (item, order) => ({ ...item, order }),
      ),
    );
  }

  function mergePhrase(index: number) {
    if (index >= phrases.length - 1) return;
    const current = phrases[index];
    const next = phrases[index + 1];
    const merged: TranscriptPhrase = {
      ...current,
      text: `${current.text.trim()} ${next.text.trim()}`.trim(),
      endMilliseconds: next.endMilliseconds,
      confidence: Math.min(current.confidence ?? 1, next.confidence ?? 1),
      warningAcknowledged:
        current.warningAcknowledged && next.warningAcknowledged,
      words: undefined,
    };
    setPhrases(
      [...phrases.slice(0, index), merged, ...phrases.slice(index + 2)].map(
        (item, order) => ({ ...item, order }),
      ),
    );
  }

  function importPreparedLyrics() {
    const lines = importText
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter(Boolean);
    if (lines.length === 0) return;
    const duration = Math.max(
      Number(
        phrases.at(-1)?.endMilliseconds ??
          release?.assets.find((asset) => asset.kind === "audio")?.durationMilliseconds ??
          0,
      ),
      lines.length * 1500,
    );
    setPhrases(
      lines.map((text, order) => ({
        id: globalThis.crypto.randomUUID(),
        order,
        text,
        startMilliseconds: Math.round((duration * order) / lines.length),
        endMilliseconds: Math.round((duration * (order + 1)) / lines.length),
        confidence: 1,
        warningAcknowledged: true,
      })),
    );
    setNotice("Prepared lyrics imported. Review the provisional timing before approval.");
  }

  async function importFile(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (file) setImportText(await file.text());
  }

  async function save(source = "manual") {
    if (!transcript || !projectEtag || validation.length > 0) return;
    setSaving(true);
    setError(undefined);
    setNotice(undefined);
    try {
      const token = await requireToken();
      const result = await apiFetch<TranscriptRevision>(
        `/api/v1/releases/${projectId}/transcript`,
        token,
        {
          method: "PUT",
          headers: {
            "If-Match": projectEtag,
            "Idempotency-Key": createIdempotencyKey("transcript-revision"),
          },
          body: JSON.stringify({
            source,
            language: transcript.language,
            isInstrumental: transcript.isInstrumental,
            phrases,
          }),
        },
      );
      setTranscript(result.data);
      setPhrases(result.data.phrases);
      setEtag(result.etag ?? `"${result.data.version}"`);
      setNotice("Transcript revision saved. Approval is still required.");
      await load();
    } catch (caught) {
      setError(messageFor(caught, "Could not save the transcript."));
    } finally {
      setSaving(false);
    }
  }

  async function approve() {
    if (!transcript || transcriptDirty || validation.length > 0 || unresolvedWarnings > 0) return;
    setSaving(true);
    setError(undefined);
    try {
      const token = await requireToken();
      await apiFetch(`/api/v1/releases/${projectId}/transcript/approve`, token, {
        method: "POST",
        headers: {
          "If-Match": etag ?? `"${transcript.version}"`,
          "Idempotency-Key": createIdempotencyKey("transcript-approval"),
        },
        body: JSON.stringify({ revisionId: transcript.revisionId }),
      });
      setNotice("Transcript approved. Hook ranking and campaign gates are being refreshed.");
      await load();
    } catch (caught) {
      setError(messageFor(caught, "Could not approve the transcript."));
    } finally {
      setSaving(false);
    }
  }

  async function regenerate() {
    setSaving(true);
    setError(undefined);
    try {
      const token = await requireToken();
      await apiFetch(`/api/v1/releases/${projectId}/transcript/regenerations`, token, {
        method: "POST",
        headers: { "Idempotency-Key": createIdempotencyKey("transcript-regeneration") },
        body: JSON.stringify({ language: transcript?.language ?? release?.language ?? "en" }),
      });
      setNotice("A new local transcription job has been queued.");
    } catch (caught) {
      setError(messageFor(caught, "Could not restart transcription."));
    } finally {
      setSaving(false);
    }
  }

  async function requireToken() {
    const token = await getToken();
    if (!token) throw new Error("No session token.");
    return token;
  }

  return (
    <AppShell>
      <Link className="text-sm font-black" href={`/releases/${projectId}`}>
        ← Release workflow
      </Link>
      <div className="mt-6 flex flex-col justify-between gap-4 lg:flex-row lg:items-end">
        <div>
          <p className="eyebrow text-[var(--orange)]">Required review</p>
          <h1 className="display mt-2 text-5xl sm:text-7xl">Check every phrase.</h1>
          <p className="mt-4 max-w-2xl leading-7">
            Correct words and phrase boundaries before campaign generation. Word timing remains an internal alignment aid.
          </p>
        </div>
        {transcript ? (
          <span className="rounded-full border border-[var(--line)] bg-white/60 px-4 py-2 text-sm font-black uppercase">
            {titleCase(transcript.state)} · revision {transcript.number}
          </span>
        ) : null}
      </div>

      {error ? <div className="mt-6"><StatusPanel title="Transcript needs attention" message={error} tone="error" /></div> : null}
      {notice ? <div className="mt-6"><StatusPanel title="Transcript updated" message={notice} tone="success" /></div> : null}

      {audioUrl ? (
        <section className="sticky top-3 z-10 mt-7 rounded-2xl border border-[var(--line)] bg-[var(--paper-strong)]/95 p-4 shadow-lg backdrop-blur">
          <p className="eyebrow mb-2">Track player</p>
          <audio className="w-full" controls preload="metadata" src={audioUrl}>
            Your browser does not support audio playback.
          </audio>
        </section>
      ) : null}

      {loading ? (
        <div className="mt-7"><StatusPanel title="Loading transcript" message="Reading the current revision…" tone="neutral" /></div>
      ) : !transcript ? (
        <section className="paper-card mt-7 p-7">
          <StatusPanel title="Transcription is still running" message="You may leave this page. The workflow will recover from the server." tone="neutral" />
          <button className="button-secondary mt-5" type="button" onClick={regenerate} disabled={saving}>Retry transcription</button>
        </section>
      ) : (
        <>
          <section className="paper-card mt-7 p-6 sm:p-8">
            <div className="flex flex-wrap items-center justify-between gap-4">
              <div>
                <p className="eyebrow text-[var(--violet)]">Review queue</p>
                <h2 className="display mt-2 text-4xl">{unresolvedWarnings} issues left</h2>
              </div>
              <div className="flex flex-wrap gap-2">
                <button className="button-quiet" type="button" onClick={regenerate} disabled={saving}>Run transcription again</button>
                <button className="button-secondary" type="button" onClick={() => save()} disabled={saving || validation.length > 0}>Save revision</button>
                <button className="button-primary" type="button" onClick={approve} disabled={saving || transcriptDirty || validation.length > 0 || unresolvedWarnings > 0}>{transcriptDirty ? "Save before approval" : "Approve transcript"}</button>
              </div>
            </div>
            {validation.length > 0 ? (
              <ul className="mt-5 rounded-2xl bg-red-100 p-4 text-sm font-bold text-red-950" role="alert">
                {validation.map((message) => <li key={message}>{message}</li>)}
              </ul>
            ) : null}
            <div className="mt-6 grid gap-3">
              {phrases.map((phrase, index) => {
                const flagged = (phrase.confidence ?? 1) < 0.75;
                return (
                  <article key={phrase.id} className={`rounded-2xl border p-4 ${flagged && !phrase.warningAcknowledged ? "border-amber-600 bg-amber-50" : "border-[var(--line)] bg-white/55"}`}>
                    <div className="grid gap-3 lg:grid-cols-[5rem_5rem_1fr_auto] lg:items-start">
                      <label className="field">
                        <span>Start, s</span>
                        <input type="number" min={0} step={0.01} value={(phrase.startMilliseconds / 1000).toFixed(2)} onChange={(event) => updatePhrase(phrase.id, { startMilliseconds: Math.round(Number(event.target.value) * 1000) })} />
                      </label>
                      <label className="field">
                        <span>End, s</span>
                        <input type="number" min={0} step={0.01} value={(phrase.endMilliseconds / 1000).toFixed(2)} onChange={(event) => updatePhrase(phrase.id, { endMilliseconds: Math.round(Number(event.target.value) * 1000) })} />
                      </label>
                      <label className="field">
                        <span>Phrase {index + 1}</span>
                        <textarea className="min-h-20" value={phrase.text} onChange={(event) => updatePhrase(phrase.id, { text: event.target.value, words: undefined })} />
                      </label>
                      <div className="flex flex-wrap gap-2 lg:pt-6">
                        <button className="button-quiet" type="button" onClick={() => splitPhrase(index)} disabled={phrase.text.trim().split(/\s+/).length < 2}>Split</button>
                        <button className="button-quiet" type="button" onClick={() => mergePhrase(index)} disabled={index === phrases.length - 1}>Merge next</button>
                      </div>
                    </div>
                    <div className="mt-3 flex flex-wrap items-center justify-between gap-3 text-sm">
                      <span className="font-bold">Confidence {Math.round((phrase.confidence ?? 1) * 100)}%{flagged ? " · Review required" : ""}</span>
                      {flagged ? (
                        <label className="flex items-center gap-2 font-black">
                          <input type="checkbox" checked={phrase.warningAcknowledged} onChange={(event) => updatePhrase(phrase.id, { warningAcknowledged: event.target.checked })} />
                          Looks correct
                        </label>
                      ) : null}
                    </div>
                  </article>
                );
              })}
            </div>
          </section>

          <details className="paper-card mt-6 p-6 sm:p-8">
            <summary className="cursor-pointer font-black">Use prepared lyrics instead</summary>
            <label className="field mt-5">
              <span>One phrase per line</span>
              <textarea value={importText} onChange={(event) => setImportText(event.target.value)} placeholder="Paste final lyrics…" />
            </label>
            <div className="mt-4 flex flex-wrap gap-2">
              <label className="button-quiet cursor-pointer"><input className="sr-only" type="file" accept=".txt,text/plain" onChange={importFile} />Choose UTF-8 .txt</label>
              <button className="button-secondary" type="button" onClick={importPreparedLyrics} disabled={!importText.trim()}>Create timed draft</button>
              <button className="button-primary" type="button" onClick={() => save("imported")} disabled={saving || validation.length > 0}>Save imported revision</button>
            </div>
          </details>
        </>
      )}
    </AppShell>
  );
}

function validatePhrases(phrases: TranscriptPhrase[]) {
  const errors: string[] = [];
  phrases.forEach((phrase, index) => {
    if (!phrase.text.trim()) errors.push(`Phrase ${index + 1} is empty.`);
    if (phrase.startMilliseconds < 0 || phrase.endMilliseconds <= phrase.startMilliseconds) {
      errors.push(`Phrase ${index + 1} has an invalid time range.`);
    }
    const previous = phrases[index - 1];
    if (previous && phrase.startMilliseconds < previous.endMilliseconds) {
      errors.push(`Phrases ${index} and ${index + 1} overlap.`);
    }
  });
  return errors;
}

function messageFor(caught: unknown, fallback: string) {
  return caught instanceof ApiRequestError ? caught.message : fallback;
}
