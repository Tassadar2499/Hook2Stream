"use client";

import { FormEvent, useState } from "react";
import { useRouter } from "next/navigation";
import { useAppAuth } from "@/components/app-auth-provider";
import { StatusPanel } from "@/components/status-panel";
import { ApiRequestError, apiFetch } from "@/lib/api";

const legalVersion = "draft-2026-07-16";

export function OnboardingClient() {
  const { getToken } = useAppAuth();
  const router = useRouter();
  const [workspaceName, setWorkspaceName] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [acceptTerms, setAcceptTerms] = useState(false);
  const [acceptPrivacy, setAcceptPrivacy] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string>();

  async function submit(event: FormEvent) {
    event.preventDefault();
    setSaving(true);
    setError(undefined);
    try {
      const token = await getToken();
      if (!token) throw new Error("No session token.");
      await apiFetch("/api/v1/account/onboarding", token, {
        method: "PUT",
        body: JSON.stringify({
          workspaceName,
          displayName: displayName || undefined,
          acceptTerms,
          acceptPrivacy,
          termsVersion: legalVersion,
          privacyVersion: legalVersion,
        }),
      });
      router.replace("/dashboard");
    } catch (caught) {
      setError(
        caught instanceof ApiRequestError
          ? caught.message
          : "Could not create your workspace.",
      );
    } finally {
      setSaving(false);
    }
  }

  return (
    <main className="shell grid min-h-screen place-items-center py-8 sm:py-12">
      <section className="paper-card surface-soft grid w-full max-w-5xl overflow-hidden lg:grid-cols-[0.8fr_1.2fr]">
        <div className="surface-inset border-b border-[var(--line)] p-7 sm:p-10 lg:border-b-0 lg:border-r">
          <p className="brand-mark display text-xl">
            Hook<span className="text-[var(--orange)]">2</span>Stream
          </p>
          <p className="eyebrow mt-10 text-[var(--orange)]">
            Welcome to Hook2Stream
          </p>
          <h1 className="display mt-3 text-5xl sm:text-6xl">
            Name your workspace.
          </h1>
          <p className="mt-5 text-lg leading-8 text-[var(--muted)]">
            This is your private release room. The MVP creates one personal
            workspace and never exposes another artist&apos;s projects by ID.
          </p>
          <span className="status-chip mt-8">
            <span
              className="size-1.5 rounded-full bg-[var(--success)]"
              aria-hidden="true"
            />
            Private by default
          </span>
        </div>

        <form className="grid content-center gap-6 p-7 sm:p-10" onSubmit={submit}>
          <label className="field">
            <span>Workspace name</span>
            <input
              required
              maxLength={160}
              value={workspaceName}
              onChange={(event) => setWorkspaceName(event.target.value)}
              placeholder="NEЯСЫТЬ release room"
            />
          </label>
          <label className="field">
            <span>Artist display name</span>
            <input
              maxLength={120}
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
              placeholder="NEЯСЫТЬ"
            />
          </label>

          <div className="surface-inset grid gap-3 rounded-2xl border border-[var(--line)] p-5">
            <label className="flex min-h-11 items-start gap-3 font-semibold">
              <input
                className="mt-1 size-5"
                type="checkbox"
                checked={acceptTerms}
                onChange={(event) => setAcceptTerms(event.target.checked)}
              />
              <span>I accept the current draft Terms for local MVP testing.</span>
            </label>
            <label className="flex min-h-11 items-start gap-3 font-semibold">
              <input
                className="mt-1 size-5"
                type="checkbox"
                checked={acceptPrivacy}
                onChange={(event) => setAcceptPrivacy(event.target.checked)}
              />
              <span>I accept the current draft Privacy policy.</span>
            </label>
          </div>

          {error ? (
            <StatusPanel
              title="Could not create workspace"
              message={error}
              tone="error"
            />
          ) : null}

          <button
            className="button-primary justify-self-start"
            type="submit"
            disabled={saving || !acceptTerms || !acceptPrivacy}
          >
            {saving ? "Creating workspace…" : "Enter workspace"}
          </button>
        </form>
      </section>
    </main>
  );
}
