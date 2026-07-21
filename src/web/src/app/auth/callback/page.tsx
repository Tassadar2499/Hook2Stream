"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import { writeSessionToken } from "@/components/app-auth-provider";

const authErrorCopy: Record<string, string> = {
  denied: "Google sign-in was cancelled.",
  state_invalid: "Sign-in state was invalid. Please try again.",
  missing_code: "The sign-in response was incomplete. Please try again.",
  exchange_failed: "Google did not return a usable session. Please try again.",
  email_unverified: "Verify the Google account email before signing in.",
};

type CallbackState =
  | { kind: "processing"; next: string }
  | { kind: "error"; message: string };

function readCallbackState(searchParams: URLSearchParams): CallbackState {
  const errorParam = searchParams.get("auth");
  if (errorParam) {
    return {
      kind: "error",
      message: authErrorCopy[errorParam] ?? "Sign-in failed. Please try again.",
    };
  }

  if (typeof window === "undefined") {
    return { kind: "error", message: "Sign-in can only complete in the browser." };
  }

  const fragment = window.location.hash.replace(/^#/, "");
  const params = new URLSearchParams(fragment);
  const token = params.get("token");
  const expiresAt = params.get("expires_at");
  const next = sanitizeNext(params.get("next"));

  if (!token || !expiresAt) {
    return {
      kind: "error",
      message: "Sign-in response is missing the session token. Please try again.",
    };
  }

  return { kind: "processing", next };
}

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
  const [state] = useState(() => readCallbackState(searchParams));

  useEffect(() => {
    if (state.kind !== "processing") return;

    const fragment = window.location.hash.replace(/^#/, "");
    const params = new URLSearchParams(fragment);
    const token = params.get("token");
    const expiresAt = params.get("expires_at");
    if (!token || !expiresAt) return;

    writeSessionToken(token, expiresAt);
    window.location.hash = "";
    router.replace(state.next);
  }, [state, router]);

  if (state.kind === "error") {
    return (
      <main className="shell grid min-h-screen place-items-center py-10">
        <section className="paper-card mx-auto max-w-xl p-7 text-center">
          <p className="eyebrow text-[var(--orange)]">Sign-in failed</p>
          <h1 className="display mt-4 text-4xl">Couldn&apos;t sign you in</h1>
          <p className="mt-5 text-lg leading-7 opacity-80">{state.message}</p>
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
      <section className="paper-card mx-auto max-w-xl p-7 text-center">
        <p className="eyebrow text-[var(--orange)]">Signing you in</p>
        <h1 className="display mt-4 text-4xl">Finishing your session…</h1>
        <p className="mt-5 text-lg leading-7 opacity-80">
          Returning you to your release workspace.
        </p>
      </section>
    </main>
  );
}

function sanitizeNext(next?: string | null) {
  if (!next || typeof next !== "string") return "/dashboard";
  if (!next.startsWith("/")) return "/dashboard";
  if (next.startsWith("//")) return "/dashboard";
  return next;
}
