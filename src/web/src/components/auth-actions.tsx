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
        <span className="hidden rounded-full border border-[var(--line)] bg-[var(--lime)] px-3 py-2 text-xs font-black uppercase sm:inline-flex">
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
    return <span className="text-sm font-bold opacity-60">Loading…</span>;
  }

  if (!isSignedIn) {
    return (
      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          className="button-quiet"
          onClick={() => signIn("/dashboard")}
        >
          Sign in
        </button>
        <button
          type="button"
          className="button-primary"
          onClick={() => signIn("/onboarding")}
        >
          Make a release pack
        </button>
      </div>
    );
  }

  return (
    <div className="flex items-center gap-2">
      <Link className="button-primary" href="/dashboard">
        Dashboard
      </Link>
      <button type="button" className="button-quiet" onClick={signOut}>
        Sign out
      </button>
    </div>
  );
}
