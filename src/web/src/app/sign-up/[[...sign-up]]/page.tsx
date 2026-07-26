"use client";

import Link from "next/link";
import { redirect } from "next/navigation";
import { ConfigurationRequired } from "@/components/configuration-required";
import { useAppAuth } from "@/components/app-auth-provider";
import { getAppAuthMode } from "@/lib/auth-config";

export default function SignUpPage() {
  const authMode = getAppAuthMode();
  if (authMode === "local") {
    redirect("/dashboard");
  }
  if (authMode === "unconfigured") {
    return <ConfigurationRequired />;
  }

  return <SignUpClient />;
}

function SignUpClient() {
  const { signIn } = useAppAuth();

  return (
    <main className="shell grid min-h-screen place-items-center py-10">
      <section className="paper-card mx-auto w-full max-w-xl p-7 sm:p-10">
        <p className="eyebrow text-[var(--orange)]">Start your first release</p>
        <h1 className="display mt-3 text-4xl sm:text-5xl">
          Create your Hook2Stream account
        </h1>
        <p className="mt-5 text-lg leading-7 opacity-80">
          Sign in with Google to upload your first MP3 or WAV and generate a 21-day
          short-form campaign.
        </p>
        <div className="mt-8 flex flex-col gap-3">
          <button
            type="button"
            className="button-primary"
            onClick={() => signIn("/onboarding")}
          >
            Continue with Google
          </button>
          <Link className="button-quiet text-center" href="/">
            Back to landing
          </Link>
        </div>
      </section>
    </main>
  );
}
