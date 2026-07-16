import Link from "next/link";

export function ConfigurationRequired() {
  return (
    <main className="shell py-16">
      <section className="paper-card mx-auto max-w-2xl p-7 sm:p-10">
        <p className="eyebrow text-[var(--orange)]">Local configuration</p>
        <h1 className="display mt-3 text-5xl sm:text-7xl">Connect Clerk first.</h1>
        <p className="mt-6 max-w-xl text-lg leading-8">
          Add your Clerk development keys to the Aspire AppHost user secrets or
          copy <code>src/web/.env.example</code> to <code>.env.local</code>.
          The API intentionally has no development auth bypass.
        </p>
        <Link className="button-secondary mt-8" href="/">
          Back to landing
        </Link>
      </section>
    </main>
  );
}
