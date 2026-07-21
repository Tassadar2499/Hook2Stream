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
      <section className="paper-card mx-auto w-full max-w-xl p-7 sm:p-10">
        <p className="eyebrow text-[var(--orange)]">Welcome back</p>
        <h1 className="display mt-3 text-4xl sm:text-5xl">Sign in to Hook2Stream</h1>
        <p className="mt-5 text-lg leading-7 opacity-80">
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
