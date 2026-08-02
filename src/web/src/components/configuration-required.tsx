import Link from "next/link";

export function ConfigurationRequired() {
  return (
    <main className="shell grid min-h-[calc(100vh-5rem)] place-items-center py-10 sm:py-16">
      <section className="paper-card surface-soft mx-auto w-full max-w-2xl p-7 sm:p-10">
        <Link
          className="brand-mark display inline-flex text-xl"
          href="/"
          aria-label="Hook2Stream home"
        >
          Hook<span className="text-[var(--orange)]">2</span>Stream
        </Link>
        <p className="eyebrow text-[var(--orange)]">Local configuration</p>
        <h1 className="display mt-3 text-5xl sm:text-7xl">Start with AppHost.</h1>
        <p className="mt-6 max-w-xl text-lg leading-8 text-[var(--muted)]">
          Start the complete local stack with{" "}
          <code className="surface-inset rounded-md px-1.5 py-0.5 text-[var(--text)]">
            ./scripts/run.sh
          </code>{" "}
          to
          use the loopback-only development identity. To test real Google
          sign-in, add both{" "}
          <code className="surface-inset rounded-md px-1.5 py-0.5 text-[var(--text)]">
            Google:ClientId
          </code>{" "}
          and{" "}
          <code className="surface-inset rounded-md px-1.5 py-0.5 text-[var(--text)]">
            Google:ClientSecret
          </code>{" "}
          to the Aspire AppHost user secrets
          before starting it.
        </p>
        <Link className="button-secondary mt-8" href="/">
          Back to landing
        </Link>
      </section>
    </main>
  );
}
