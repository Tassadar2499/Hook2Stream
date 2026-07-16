"use client";

import Link from "next/link";
import {
  SignInButton,
  SignUpButton,
  Show,
  UserButton,
} from "@clerk/nextjs";

export function AuthActions() {
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
