import Link from "next/link";
import { AuthActions } from "@/components/auth-actions";

export function SiteHeader() {
  return (
    <header className="site-header">
      <a className="skip-link button-primary" href="#main-content">
        Skip to content
      </a>
      <div className="shell flex min-h-20 items-center justify-between gap-3 py-3">
        <Link href="/" className="brand-mark shrink-0" aria-label="Hook2Stream home">
          <span className="brand-mark-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="20" height="20" fill="none">
              <path
                d="M4 13.5h3l1.4-5 2.6 9 2.1-7 1.4 3H20"
                stroke="currentColor"
                strokeWidth="1.8"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
            </svg>
          </span>
          <span>
            Hook<span className="text-[var(--orange)]">2</span>Stream
          </span>
        </Link>
        <nav className="flex min-w-0 items-center justify-end gap-2" aria-label="Main navigation">
          <Link className="button-quiet hidden md:inline-flex" href="/#how-it-works">
            Workflow
          </Link>
          <Link className="button-quiet hidden sm:inline-flex" href="/#pricing">
            Pricing
          </Link>
          <AuthActions />
        </nav>
      </div>
    </header>
  );
}
