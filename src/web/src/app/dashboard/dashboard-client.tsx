"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useAppAuth } from "@/components/app-auth-provider";
import { AppShell } from "@/components/app-shell";
import { StatusPanel } from "@/components/status-panel";
import { Account, ApiRequestError, Release, apiFetch } from "@/lib/api";
import { Workflow, titleCase } from "@/lib/workflow";

export function DashboardClient() {
  const { getToken, isLoaded, isSignedIn } = useAppAuth();
  const router = useRouter();
  const [account, setAccount] = useState<Account>();
  const [releases, setReleases] = useState<Release[]>([]);
  const [workflows, setWorkflows] = useState<Record<string, Workflow>>({});
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (!isLoaded) return;
    if (!isSignedIn) {
      router.replace("/");
      return;
    }

    let active = true;
    void (async () => {
      try {
        const token = await getToken();
        if (!token) throw new Error("No session token.");
        const accountResult = await apiFetch<Account>("/api/v1/account/me", token);
        if (!active) return;
        if (accountResult.data.onboardingRequired) {
          router.replace("/onboarding");
          return;
        }
        const releaseResult = await apiFetch<Release[]>("/api/v1/releases", token);
        if (!active) return;
        setAccount(accountResult.data);
        setReleases(releaseResult.data);
        const workflowResults = await Promise.all(
          releaseResult.data.map(async (release) => {
            try {
              const workflow = await apiFetch<Workflow>(
                `/api/v1/releases/${release.id}/workflow`,
                token,
              );
              return [release.id, workflow.data] as const;
            } catch {
              return undefined;
            }
          }),
        );
        if (active) {
          setWorkflows(
            Object.fromEntries(workflowResults.filter((item) => item !== undefined)),
          );
        }
      } catch (caught) {
        if (active) {
          setError(
            caught instanceof ApiRequestError ? caught.message : "Unable to load the workspace.",
          );
        }
      }
    })();

    return () => {
      active = false;
    };
  }, [getToken, isLoaded, isSignedIn, router]);

  return (
    <AppShell>
      <section className="surface-soft flex flex-col justify-between gap-6 rounded-3xl border border-[var(--line)] p-6 sm:flex-row sm:items-end sm:p-8">
        <div className="max-w-3xl">
          <p className="eyebrow text-[var(--violet)]">Workspace</p>
          <h1 className="display mt-2 text-5xl sm:text-7xl">
            {account?.workspaceName ?? "Your releases"}
          </h1>
          <p className="mt-4 max-w-xl text-lg leading-7 opacity-75">
            One song in. A complete three-week content campaign out.
          </p>
        </div>
        <Link className="button-primary shrink-0" href="/releases/new">
          New release
        </Link>
      </section>

      {error ? (
        <div className="mt-8">
          <StatusPanel title="Could not load dashboard" message={error} tone="error" />
        </div>
      ) : releases.length === 0 ? (
        <section className="paper-card mt-6 overflow-hidden p-2">
          <div className="grid gap-2 lg:grid-cols-[1.1fr_.9fr]">
            <div className="p-5 sm:p-8">
              <p className="eyebrow text-[var(--orange)]">First release</p>
              <h2 className="display mt-3 text-5xl sm:text-6xl">
                Bring one finished track.
              </h2>
              <p className="mt-5 max-w-xl text-lg leading-8">
                Upload one finished MP3 or WAV. We create an editable transcript,
                artwork and an 18-video campaign automatically.
              </p>
              <Link className="button-secondary mt-7" href="/releases/new">
                Set up the release
              </Link>
            </div>
            <div className="surface-inset rounded-[1.35rem] p-6 sm:p-7">
              <p className="eyebrow text-[var(--violet)]">The flow</p>
              <ul className="mt-5 grid gap-1 font-bold">
                <li className="border-b border-[var(--line)] py-3 first:pt-0">
                  01 · Upload one MP3 or WAV
                </li>
                <li className="border-b border-[var(--line)] py-3">
                  02 · Review the transcript
                </li>
                <li className="border-b border-[var(--line)] py-3">
                  03 · Choose the official cover
                </li>
                <li className="border-b border-[var(--line)] py-3">
                  04 · Tune hooks and storyboard
                </li>
                <li className="pt-3">05 · Preview, purchase and export</li>
              </ul>
            </div>
          </div>
        </section>
      ) : (
        <section className="mt-6 grid gap-4 lg:grid-cols-2">
          {releases.map((release) => (
            <Link
              key={release.id}
              href={`/releases/${release.id}`}
              className="paper-card group flex min-h-56 flex-col overflow-hidden p-6 transition hover:-translate-y-1 sm:p-7"
            >
              <div className="flex items-start justify-between gap-4">
                <div className="min-w-0">
                  <p className="eyebrow text-[var(--orange)]">{release.state}</p>
                  <h2 className="display mt-3 text-4xl sm:text-5xl">
                    {release.trackTitle}
                  </h2>
                  <p className="mt-3 font-bold opacity-75">{release.artistName}</p>
                </div>
                <span className="status-chip shrink-0">
                  {release.mode}
                </span>
              </div>
              <div className="mt-auto flex items-end justify-between gap-5 border-t border-[var(--line)] pt-6 text-sm font-bold">
                <span className="max-w-[65%] opacity-70">
                  {workflows[release.id]?.nextAction
                    ? titleCase(workflows[release.id].nextAction ?? "")
                    : `${release.assets.filter((asset) => asset.isActive).length} assets`}
                </span>
                <span className="text-right text-[var(--violet)] transition group-hover:translate-x-1">
                  Open workflow →
                </span>
              </div>
            </Link>
          ))}
        </section>
      )}
    </AppShell>
  );
}
