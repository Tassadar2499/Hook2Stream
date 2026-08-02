"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import { refreshOAuthSession } from "@/lib/auth-session";

const authErrorCopy: Record<string, string> = {
  denied: "Google sign-in was cancelled.",
  state_invalid: "Sign-in state was invalid. Please try again.",
  missing_code: "The sign-in response was incomplete. Please try again.",
  exchange_failed: "Google did not return a usable session. Please try again.",
  email_unverified: "Verify the Google account email before signing in.",
};

export default function AuthCallbackPage() {
  return (
    <Suspense fallback={<AuthCallbackFallback />}>
      <AuthCallbackInner />
    </Suspense>
  );
}

function AuthCallbackInner() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const errorParam = searchParams.get("auth");
  const [error, setError] = useState(
    errorParam ? authErrorCopy[errorParam] ?? "Sign-in failed. Please try again." : "",
  );

  useEffect(() => {
    if (errorParam) return;

    void refreshOAuthSession().then((session) => {
      if (session.authenticated) {
        router.replace(sanitizeNext(searchParams.get("next")));
      } else {
        setError("The browser session is missing or expired. Please sign in again.");
      }
    });
  }, [errorParam, router, searchParams]);

  if (error) {
    return (
      <main className="shell grid min-h-screen place-items-center py-10">
        <section className="paper-card surface-soft mx-auto w-full max-w-xl p-7 text-center sm:p-10">
          <Link
            className="brand-mark display inline-flex text-xl"
            href="/"
            aria-label="Hook2Stream home"
          >
            Hook<span className="text-[var(--orange)]">2</span>Stream
          </Link>
          <p className="eyebrow mt-9 text-[var(--danger)]">Sign-in failed</p>
          <h1 className="display mt-4 text-4xl">Couldn&apos;t sign you in</h1>
          <p className="mt-5 text-lg leading-7 text-[var(--muted)]">{error}</p>
          <Link className="button-primary mt-7" href="/sign-in">
            Back to sign in
          </Link>
        </section>
      </main>
    );
  }

  return <AuthCallbackFallback />;
}

function AuthCallbackFallback() {
  return (
    <main className="shell grid min-h-screen place-items-center py-10">
      <section className="paper-card surface-soft mx-auto w-full max-w-xl p-7 text-center sm:p-10">
        <p className="brand-mark display text-xl">
          Hook<span className="text-[var(--orange)]">2</span>Stream
        </p>
        <span className="status-chip surface-inset mt-9">
          <span
            className="size-1.5 animate-pulse rounded-full bg-[var(--violet)]"
            aria-hidden="true"
          />
          Secure session
        </span>
        <p className="eyebrow mt-5 text-[var(--orange)]">Signing you in</p>
        <h1 className="display mt-4 text-4xl">Finishing your session…</h1>
        <p className="mt-5 text-lg leading-7 text-[var(--muted)]">
          Returning you to your release workspace.
        </p>
      </section>
    </main>
  );
}

function sanitizeNext(next?: string | null) {
  if (!next || !next.startsWith("/") || next.startsWith("//") || next.includes("\\")) {
    return "/dashboard";
  }
  return next;
}
