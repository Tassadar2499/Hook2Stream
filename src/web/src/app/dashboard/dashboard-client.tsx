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
      <section className="flex flex-col justify-between gap-5 sm:flex-row sm:items-end">
        <div>
          <p className="eyebrow text-[var(--violet)]">Workspace</p>
          <h1 className="display mt-2 text-5xl sm:text-7xl">
            {account?.workspaceName ?? "Your releases"}
          </h1>
          <p className="mt-3 max-w-xl text-lg">
            One song in. A complete three-week content campaign out.
          </p>
        </div>
        <Link className="button-primary" href="/releases/new">
          New release
        </Link>
      </section>

      {error ? (
        <div className="mt-8">
          <StatusPanel title="Could not load dashboard" message={error} tone="error" />
        </div>
      ) : releases.length === 0 ? (
        <section className="paper-card mt-8 overflow-hidden">
          <div className="grid gap-8 p-7 sm:p-10 lg:grid-cols-[1fr_.75fr]">
            <div>
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
            <div className="rounded-3xl bg-[var(--ink)] p-6 text-white">
              <p className="eyebrow text-[var(--lime)]">The flow</p>
              <ul className="mt-5 grid gap-4 font-bold">
                <li>01 · Upload one MP3 or WAV</li>
                <li>02 · Review the transcript</li>
                <li>03 · Choose the official cover</li>
                <li>04 · Tune hooks and storyboard</li>
                <li>05 · Preview, purchase and export</li>
              </ul>
            </div>
          </div>
        </section>
      ) : (
        <section className="mt-8 grid gap-4 lg:grid-cols-2">
          {releases.map((release) => (
            <Link
              key={release.id}
              href={`/releases/${release.id}`}
              className="paper-card group p-6 transition hover:-translate-y-1"
            >
              <div className="flex items-start justify-between gap-4">
                <div>
                  <p className="eyebrow text-[var(--orange)]">{release.state}</p>
                  <h2 className="display mt-2 text-4xl">{release.trackTitle}</h2>
                  <p className="mt-2 font-bold">{release.artistName}</p>
                </div>
                <span className="rounded-full border border-[var(--line)] px-3 py-1 text-xs font-black uppercase">
                  {release.mode}
                </span>
              </div>
              <div className="mt-7 flex items-center justify-between text-sm font-bold">
                <span>
                  {workflows[release.id]?.nextAction
                    ? titleCase(workflows[release.id].nextAction ?? "")
                    : `${release.assets.filter((asset) => asset.isActive).length} assets`}
                </span>
                <span className="group-hover:text-[var(--violet)]">Open workflow →</span>
              </div>
            </Link>
          ))}
        </section>
      )}
    </AppShell>
  );
}
