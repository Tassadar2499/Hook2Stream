"use client";

import Link from "next/link";
import { redirect } from "next/navigation";
import { ConfigurationRequired } from "@/components/configuration-required";
import { useAppAuth } from "@/components/app-auth-provider";
import { getAppAuthMode } from "@/lib/auth-config";

export default function SignInPage() {
  const authMode = getAppAuthMode();
  if (authMode === "local") {
    redirect("/dashboard");
  }
  if (authMode === "unconfigured") {
    return <ConfigurationRequired />;
  }

  return <SignInClient />;
}

function SignInClient() {
  const { signIn } = useAppAuth();

  return (
    <main className="shell grid min-h-screen place-items-center py-10">
      <section className="paper-card surface-soft mx-auto w-full max-w-xl p-7 sm:p-10">
        <Link
          className="brand-mark display inline-flex text-xl"
          href="/"
          aria-label="Hook2Stream home"
        >
          Hook<span className="text-[var(--orange)]">2</span>Stream
        </Link>
        <span className="status-chip surface-inset mt-9">
          <span
            className="size-1.5 rounded-full bg-[var(--success)]"
            aria-hidden="true"
          />
          Secure workspace access
        </span>
        <p className="eyebrow mt-5 text-[var(--orange)]">Welcome back</p>
        <h1 className="display mt-3 text-4xl sm:text-5xl">Sign in to Hook2Stream</h1>
        <p className="mt-5 text-lg leading-7 text-[var(--muted)]">
          Continue your release campaign. Sign in with the Google account that
          owns your releases and billing.
        </p>
        <div className="mt-8 flex flex-col gap-3">
          <button
            type="button"
            className="button-primary"
            onClick={() => signIn("/dashboard")}
          >
            Sign in with Google
          </button>
          <Link className="button-quiet text-center" href="/">
            Back to landing
          </Link>
        </div>
      </section>
    </main>
  );
}
