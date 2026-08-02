"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useAppAuth } from "@/components/app-auth-provider";

const navigation = [
  { href: "/dashboard", label: "Releases" },
  { href: "/releases/new", label: "New release" },
  { href: "/settings/brand", label: "Brand kit" },
];

function isNavigationActive(pathname: string, href: string) {
  if (href === "/dashboard") {
    return (
      pathname === href ||
      (pathname.startsWith("/releases/") && pathname !== "/releases/new")
    );
  }

  return pathname === href || pathname.startsWith(`${href}/`);
}

export function AppShell({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const { mode, signOut } = useAppAuth();

  return (
    <div className="min-h-dvh pb-32 md:pb-0">
      <a className="skip-link button-primary" href="#workspace-main">
        Skip to workspace
      </a>
      <header className="surface-soft sticky top-0 z-30 border-b border-[var(--line)] backdrop-blur-xl">
        <div className="shell flex min-h-20 items-center justify-between gap-4">
          <Link
            className="brand-mark"
            href="/dashboard"
            aria-label="Hook2Stream dashboard"
          >
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
          {mode === "local" ? (
            <span className="status-chip surface-inset">
              <span
                className="size-1.5 rounded-full bg-[var(--success)]"
                aria-hidden="true"
              />
              Local developer
            </span>
          ) : (
            <button
              type="button"
              className="button-quiet text-xs"
              onClick={signOut}
            >
              Sign out
            </button>
          )}
        </div>
      </header>
      <div className="shell grid gap-7 py-7 md:grid-cols-[14rem_minmax(0,1fr)] md:py-10 xl:grid-cols-[15rem_minmax(0,1fr)]">
        <aside className="hidden md:block">
          <nav
            className="paper-card surface-soft sticky top-28 grid gap-1 p-3"
            aria-label="Workspace navigation"
          >
            <p className="eyebrow px-3 pb-2 pt-1 text-[var(--muted)]">
              Workspace
            </p>
            {navigation.map((item, index) => {
              const active = isNavigationActive(pathname, item.href);
              return (
                <Link
                  key={item.href}
                  className={`group flex min-h-12 items-center gap-3 rounded-xl border px-3 py-2.5 font-bold transition-colors ${
                    active
                      ? "surface-inset border-[var(--line-strong)]"
                      : "border-transparent text-[var(--muted)] hover:border-[var(--line)] hover:bg-[var(--surface-hover)] hover:text-[var(--text)]"
                  }`}
                  href={item.href}
                  aria-current={active ? "page" : undefined}
                >
                  <span
                    className={`grid size-7 shrink-0 place-items-center rounded-lg text-[0.65rem] font-black ${
                      active
                        ? "bg-[var(--violet)] text-[var(--on-accent)]"
                        : "surface-inset text-[var(--muted)]"
                    }`}
                    aria-hidden="true"
                  >
                    {String(index + 1).padStart(2, "0")}
                  </span>
                  {item.label}
                </Link>
              );
            })}
          </nav>
        </aside>
        <main id="workspace-main" className="min-w-0">
          {children}
        </main>
      </div>
      <nav
        className="workspace-mobile-nav paper-card surface-soft fixed inset-x-3 z-40 grid grid-cols-3 gap-1 p-2 backdrop-blur-xl md:hidden"
        aria-label="Mobile workspace navigation"
      >
        {navigation.map((item) => {
          const active = isNavigationActive(pathname, item.href);
          return (
            <Link
              key={item.href}
              className={`grid min-h-12 place-items-center rounded-xl px-2 py-2 text-center text-xs font-black transition-colors ${
                active
                  ? "surface-inset text-[var(--text)]"
                  : "text-[var(--muted)] hover:bg-[var(--surface-hover)] hover:text-[var(--text)]"
              }`}
              href={item.href}
              aria-current={active ? "page" : undefined}
            >
              {item.label}
            </Link>
          );
        })}
      </nav>
    </div>
  );
}
