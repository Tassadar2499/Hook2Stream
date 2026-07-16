"use client";

import { FormEvent, useMemo, useState } from "react";
import { useAuth } from "@clerk/nextjs";
import { useRouter } from "next/navigation";
import { AppShell } from "@/components/app-shell";
import { StatusPanel } from "@/components/status-panel";
import {
  ApiRequestError,
  Release,
  ReleaseMode,
  apiFetch,
} from "@/lib/api";

function isoDate(offsetDays: number) {
  const value = new Date();
  value.setUTCDate(value.getUTCDate() + offsetDays);
  return value.toISOString().slice(0, 10);
}

export function NewReleaseClient() {
  const { getToken } = useAuth();
  const router = useRouter();
  const [mode, setMode] = useState<ReleaseMode>("upcoming");
  const [instrumental, setInstrumental] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string>();
  const defaults = useMemo(
    () => ({
      upcomingRelease: isoDate(14),
      releasedDate: isoDate(-7),
      campaignStart: isoDate(0),
    }),
    [],
  );

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
      <h1 className="display mt-2 text-5xl sm:text-7xl">Start with the song.</h1>
      <p className="mt-4 max-w-2xl text-lg leading-8">
        This draft only establishes the release brief. Media goes directly to
        object storage on the next screen.
      </p>

      <form className="paper-card mt-8 grid gap-7 p-6 sm:p-8" onSubmit={submit}>
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
    </AppShell>
  );
}
