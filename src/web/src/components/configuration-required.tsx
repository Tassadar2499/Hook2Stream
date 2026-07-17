import Link from "next/link";

export function ConfigurationRequired() {
  return (
    <main className="shell py-16">
      <section className="paper-card mx-auto max-w-2xl p-7 sm:p-10">
        <p className="eyebrow text-[var(--orange)]">Local configuration</p>
        <h1 className="display mt-3 text-5xl sm:text-7xl">Start with AppHost.</h1>
        <p className="mt-6 max-w-xl text-lg leading-8">
          Start the complete local stack with <code>./scripts/run.sh</code> to
          use the loopback-only development identity. To test real Clerk, add
          both <code>Clerk:Issuer</code> and <code>Clerk:PublishableKey</code> to
          the Aspire AppHost user secrets before starting it.
        </p>
        <Link className="button-secondary mt-8" href="/">
          Back to landing
        </Link>
      </section>
    </main>
  );
}
