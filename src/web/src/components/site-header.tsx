import Link from "next/link";
import { AuthActions } from "@/components/auth-actions";

export function SiteHeader() {
  const clerkEnabled = Boolean(process.env.NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY);

  return (
    <header className="shell flex min-h-20 items-center justify-between gap-4 py-4">
      <Link href="/" className="display text-2xl" aria-label="Hook2Stream home">
        Hook<span className="text-[var(--orange)]">2</span>Stream
      </Link>
      <nav className="flex items-center gap-2" aria-label="Main navigation">
        <span className="hidden sm:block">
          <Link className="button-quiet" href="/#pricing">
            Pricing
          </Link>
        </span>
        {clerkEnabled ? (
          <AuthActions />
        ) : (
          <Link className="button-primary" href="/setup">
            Configure Clerk
          </Link>
        )}
      </nav>
    </header>
  );
}
