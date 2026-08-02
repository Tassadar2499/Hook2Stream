"use client";

import Link from "next/link";
import { useAppAuth } from "@/components/app-auth-provider";

export function AuthActions() {
  const { mode, isLoaded, isSignedIn, signIn, signOut } = useAppAuth();

  if (mode === "local") {
    return (
      <>
        <Link className="button-primary" href="/dashboard">
          Dashboard
        </Link>
        <span className="status-chip surface-inset hidden sm:inline-flex">
          <span
            className="size-1.5 rounded-full bg-[var(--success)]"
            aria-hidden="true"
          />
          Local developer
        </span>
      </>
    );
  }

  if (mode === "unconfigured") {
    return (
      <Link className="button-primary" href="/setup">
        Configure auth
      </Link>
    );
  }

  if (!isLoaded) {
    return (
      <span className="status-chip surface-inset text-[var(--muted)]">
        Loading…
      </span>
    );
  }

  if (!isSignedIn) {
    return (
      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          className="button-quiet hidden sm:inline-flex"
          onClick={() => signIn("/dashboard")}
        >
          Sign in
        </button>
        <button
          type="button"
          className="button-primary"
          onClick={() => signIn("/onboarding")}
        >
          <span className="sm:hidden">Get started</span>
          <span className="hidden sm:inline">Make a release pack</span>
        </button>
      </div>
    );
  }

  return (
    <div className="flex items-center gap-2">
      <Link className="button-primary" href="/dashboard">
        Dashboard
      </Link>
      <button type="button" className="button-quiet hidden sm:inline-flex" onClick={signOut}>
        Sign out
      </button>
    </div>
  );
}
