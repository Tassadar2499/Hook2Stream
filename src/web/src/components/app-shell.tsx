"use client";

import Link from "next/link";
import { UserButton } from "@clerk/nextjs";
import { usePathname } from "next/navigation";

const navigation = [
  { href: "/dashboard", label: "Releases" },
  { href: "/releases/new", label: "New release" },
  { href: "/settings/brand", label: "Brand kit" },
];

export function AppShell({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();

  return (
    <div className="min-h-screen">
      <header className="border-b border-[var(--line)] bg-[var(--paper)]/85 backdrop-blur">
        <div className="shell flex min-h-20 items-center justify-between gap-4">
          <Link className="display text-2xl" href="/dashboard">
            Hook<span className="text-[var(--orange)]">2</span>Stream
          </Link>
          <nav
            className="hidden items-center gap-1 md:flex"
            aria-label="Workspace navigation"
          >
            {navigation.map((item) => {
              const active =
                pathname === item.href ||
                (item.href !== "/dashboard" && pathname.startsWith(item.href));
              return (
                <Link
                  key={item.href}
                  className={`rounded-full px-4 py-2 text-sm font-black ${
                    active ? "bg-[var(--ink)] text-white" : "hover:bg-white/60"
                  }`}
                  href={item.href}
                >
                  {item.label}
                </Link>
              );
            })}
          </nav>
          <UserButton />
        </div>
      </header>
      <div className="shell grid gap-7 py-7 md:grid-cols-[13rem_1fr] md:py-10">
        <aside className="paper-card hidden h-fit p-3 md:block">
          {navigation.map((item) => {
            const active =
              pathname === item.href ||
              (item.href !== "/dashboard" && pathname.startsWith(item.href));
            return (
              <Link
                key={item.href}
                className={`block rounded-xl px-4 py-3 font-bold ${
                  active ? "bg-[var(--lime)]" : "hover:bg-white/60"
                }`}
                href={item.href}
              >
                {item.label}
              </Link>
            );
          })}
        </aside>
        <main className="min-w-0">{children}</main>
      </div>
      <nav
        className="fixed inset-x-3 bottom-3 z-20 flex justify-around rounded-2xl border border-[var(--line)] bg-[var(--paper-strong)]/95 p-2 shadow-xl backdrop-blur md:hidden"
        aria-label="Mobile workspace navigation"
      >
        {navigation.map((item) => (
          <Link
            key={item.href}
            className={`min-h-11 rounded-xl px-3 py-2 text-center text-xs font-black ${
              pathname === item.href ? "bg-[var(--lime)]" : ""
            }`}
            href={item.href}
          >
            {item.label}
          </Link>
        ))}
      </nav>
    </div>
  );
}
