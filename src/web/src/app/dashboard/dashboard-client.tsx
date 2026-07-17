"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useAppAuth } from "@/components/app-auth-provider";
import { AppShell } from "@/components/app-shell";
import { StatusPanel } from "@/components/status-panel";
import { Account, ApiRequestError, Release, apiFetch } from "@/lib/api";

export function DashboardClient() {
  const { getToken, isLoaded, isSignedIn } = useAppAuth();
  const router = useRouter();
  const [account, setAccount] = useState<Account>();
  const [releases, setReleases] = useState<Release[]>([]);
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
                Start with metadata and lyrics. Then upload the master, cover
                and a small visual library directly to secure object storage.
              </p>
              <Link className="button-secondary mt-7" href="/releases/new">
                Set up the release
              </Link>
            </div>
            <div className="rounded-3xl bg-[var(--ink)] p-6 text-white">
              <p className="eyebrow text-[var(--lime)]">You will need</p>
              <ul className="mt-5 grid gap-4 font-bold">
                <li>01 · MP3 or WAV master</li>
                <li>02 · Lyrics or instrumental mode</li>
                <li>03 · Cover artwork</li>
                <li>04 · 3–10 images or short videos</li>
                <li>05 · Rights confirmation</li>
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
                <span>{release.assets.filter((asset) => asset.isActive).length} assets</span>
                <span className="group-hover:text-[var(--violet)]">Open setup →</span>
              </div>
            </Link>
          ))}
        </section>
      )}
    </AppShell>
  );
}
