"use client";

import Link from "next/link";
import {
  SignInButton,
  SignUpButton,
  Show,
  UserButton,
} from "@clerk/nextjs";
import { useAppAuth } from "@/components/app-auth-provider";

export function AuthActions() {
  const { mode } = useAppAuth();

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

  return (
    <>
      <Show when="signed-out">
        <SignInButton mode="modal">
          <button className="button-quiet" type="button">
            Sign in
          </button>
        </SignInButton>
        <SignUpButton mode="modal">
          <button className="button-primary" type="button">
            Make a release pack
          </button>
        </SignUpButton>
      </Show>
      <Show when="signed-in">
        <Link className="button-primary" href="/dashboard">
          Dashboard
        </Link>
        <UserButton />
      </Show>
    </>
  );
}
