import Link from "next/link";
import { SiteHeader } from "@/components/site-header";
import { isAppAuthConfigured } from "@/lib/auth-config";

const deliverables = [
  ["12", "hook-driven lyric, cover and visual-loop variants"],
  ["6", "teaser, countdown and out-now campaign pieces"],
  ["21", "days of captions, calls to action and posting slots"],
];

const steps = [
  {
    number: "01",
    title: "Drop the song",
    copy: "Upload one finished MP3 or WAV. Audio analysis and RU / EN transcription start automatically.",
    icon: "upload",
  },
  {
    number: "02",
    title: "Keep the taste",
    copy: "Check the transcript, release details and three generated cover directions. Edit only what needs your taste.",
    icon: "tune",
  },
  {
    number: "03",
    title: "Post for three weeks",
    copy: "Review or tune 18 generated video cards and prepare the copy and calendar you need. Clean exports remain protected by entitlement.",
    icon: "calendar",
  },
];

const plans = [
  {
    name: "Preview",
    price: "$0",
    copy: "One watermarked short to check the direction.",
  },
  {
    name: "Mini release",
    price: "$5",
    copy: "Choose six clean shorts from the campaign.",
  },
  {
    name: "Release pack",
    price: "$9.90",
    copy: "All 18 shorts, captions, CTAs and calendar.",
    featured: true,
  },
  {
    name: "Active artist",
    price: "$29/mo",
    copy: "One monthly pack, reusable brand kit and release history.",
  },
];

const addOns = [
  {
    name: "Clean cover",
    price: "$2",
    copy: "The approved 3000×3000 cover without a preview watermark.",
  },
  {
    name: "Artwork refill",
    price: "$1",
    copy: "Five additional three-candidate artwork generations.",
  },
];

function StepIcon({ icon }: { icon: string }) {
  if (icon === "upload") {
    return (
      <svg aria-hidden="true" viewBox="0 0 24 24" width="21" height="21" fill="none">
        <path d="M12 16V4m0 0L7.5 8.5M12 4l4.5 4.5M5 14v4.5A1.5 1.5 0 006.5 20h11a1.5 1.5 0 001.5-1.5V14" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    );
  }

  if (icon === "tune") {
    return (
      <svg aria-hidden="true" viewBox="0 0 24 24" width="21" height="21" fill="none">
        <path d="M5 7h8m4 0h2M5 17h2m4 0h8M13 4v6M7 14v6" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" />
      </svg>
    );
  }

  return (
    <svg aria-hidden="true" viewBox="0 0 24 24" width="21" height="21" fill="none">
      <rect x="4" y="5.5" width="16" height="14" rx="2" stroke="currentColor" strokeWidth="1.7" />
      <path d="M8 3.5v4M16 3.5v4M4 10h16M8 14h3m2 0h3" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" />
    </svg>
  );
}

export default function Home() {
  const authConfigured = isAppAuthConfigured();
  const startHref = authConfigured ? "/dashboard" : "/setup";

  return (
    <>
      <SiteHeader />
      <main id="main-content">
        <section className="hero-grid shell grid items-center gap-14 py-14 lg:grid-cols-[1.03fr_.97fr] lg:py-20">
          <div className="relative z-10">
            <p className="eyebrow surface-soft mb-6 inline-flex rounded-full px-3.5 py-2 text-[var(--violet)]">
              A release studio for independent musicians
            </p>
            <h1 className="display hero-title">
              One song.
              <br />
              <span className="hero-title-accent">Three weeks</span>
              <br />
              of shorts.
            </h1>
            <p className="mt-7 max-w-2xl text-lg leading-8 text-[var(--muted)] sm:text-xl">
              Turn one finished MP3 or WAV into an editable transcript, artwork and
              a coherent 21-day campaign of ready-to-post vertical videos.
            </p>
            <div className="mt-8 flex flex-wrap gap-3">
              <Link className="button-primary" href={startHref}>
                Build a release pack
                <span aria-hidden="true">→</span>
              </Link>
              <a className="button-quiet" href="#how-it-works">
                See the workflow
              </a>
            </div>
            <div className="mt-7 flex flex-wrap gap-x-5 gap-y-2 text-sm text-[var(--muted)]">
              {["18 videos · one ZIP", "RU / EN transcript", "Editable end to end"].map(
                (item) => (
                  <span className="inline-flex items-center gap-2" key={item}>
                    <span className="size-1.5 rounded-full bg-[var(--lime)]" aria-hidden="true" />
                    {item}
                  </span>
                ),
              )}
            </div>
            <p className="mt-5 max-w-xl text-sm leading-6 text-[var(--muted)]">
              No virality promise. No autopublishing. Just the release content you
              would otherwise spend days editing.
            </p>
          </div>

          <div
            className="hero-visual py-4 sm:px-4"
            role="img"
            aria-label="A Hook2Stream campaign workspace showing an artwork, audio waveform and scheduled short videos"
          >
            <div className="hero-studio p-4 sm:p-5" aria-hidden="true">
              <div className="flex items-center justify-between gap-4 border-b border-[var(--line)] pb-4">
                <div className="flex items-center gap-3">
                  <span className="grid size-9 place-items-center rounded-xl bg-[var(--violet)]/12 text-[var(--violet)]">
                    <svg viewBox="0 0 24 24" width="18" height="18" fill="none">
                      <path d="M4 13h3l1.5-6L11 18l2.2-8 1.5 4H20" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" />
                    </svg>
                  </span>
                  <div>
                    <p className="text-sm font-bold">Release workspace</p>
                    <p className="mt-0.5 text-xs text-[var(--muted)]">NEЯСЫТЬ · Fire awake</p>
                  </div>
                </div>
                <span className="status-chip">
                  <span className="size-1.5 rounded-full bg-[var(--success)]" /> Ready
                </span>
              </div>

              <div className="grid gap-4 py-5 sm:grid-cols-[.9fr_1.1fr]">
                <div className="hero-cover-art aspect-square p-5">
                  <div className="relative z-10 flex h-full flex-col justify-between">
                    <p className="eyebrow !text-white/70">Official cover</p>
                    <div>
                      <p className="display text-3xl text-white sm:text-4xl">Fire awake</p>
                      <p className="mt-2 text-xs font-bold uppercase tracking-[.16em] text-white/75">NEЯСЫТЬ</p>
                    </div>
                  </div>
                </div>

                <div className="surface-inset flex flex-col justify-between rounded-2xl p-4">
                  <div>
                    <div className="flex items-center justify-between gap-3 text-xs">
                      <span className="font-bold">Hook 02 · Chorus</span>
                      <span className="text-[var(--muted)]">00:42–00:59</span>
                    </div>
                    <div className="waveform mt-4" aria-hidden="true">
                      {[12, 23, 16, 30, 22, 38, 27, 20, 34, 18, 28, 40, 31, 21, 35, 25, 17, 30, 22, 13].map(
                        (height, index) => (
                          <span key={`${height}-${index}`} style={{ height }} />
                        ),
                      )}
                    </div>
                  </div>
                  <div className="mt-4 border-t border-[var(--line)] pt-4">
                    <p className="text-xs text-[var(--muted)]">Selected lyric</p>
                    <p className="mt-2 text-lg font-semibold leading-6">
                      “I kept the fire awake.”
                    </p>
                  </div>
                </div>
              </div>

              <div className="grid grid-cols-3 gap-2.5 border-t border-[var(--line)] pt-4">
                {[
                  ["18", "video cards"],
                  ["21", "posting days"],
                  ["1", "export ZIP"],
                ].map(([value, label]) => (
                  <div className="surface-soft rounded-xl p-3" key={label}>
                    <p className="text-lg font-bold">{value}</p>
                    <p className="mt-0.5 text-[.68rem] leading-4 text-[var(--muted)]">{label}</p>
                  </div>
                ))}
              </div>
            </div>

            <div className="hero-float-card right-0 top-[18%] px-3 py-2.5">
              <p className="text-xs font-bold">Cover approved</p>
              <p className="mt-1 text-[.68rem] text-[var(--success)]">3 backgrounds ready</p>
            </div>
            <div className="hero-float-card -bottom-2 left-0 px-3.5 py-3">
              <p className="eyebrow !text-[.62rem] !text-[var(--orange)]">Release pack</p>
              <p className="mt-1 text-sm font-bold">18 videos, bundled in one ZIP</p>
            </div>
          </div>
        </section>

        <section className="metric-strip py-8" aria-label="Release pack contents">
          <div className="shell grid sm:grid-cols-3">
            {deliverables.map(([value, label]) => (
              <div className="metric-item flex items-start gap-4 px-1 py-5 sm:px-6 sm:py-2 first:pl-0 last:pr-0" key={value}>
                <span className="display min-w-14 text-4xl text-[var(--violet)]">{value}</span>
                <p className="max-w-xs pt-0.5 text-sm font-semibold leading-6 text-[var(--muted)]">{label}</p>
              </div>
            ))}
          </div>
        </section>

        <section id="how-it-works" className="shell scroll-mt-24 py-24 sm:py-28">
          <div className="grid items-end gap-7 lg:grid-cols-[1fr_.55fr]">
            <div className="max-w-3xl">
              <p className="eyebrow text-[var(--violet)]">A guided workflow</p>
              <h2 className="display mt-4 text-5xl sm:text-7xl">
                Your release, minus the content treadmill.
              </h2>
            </div>
            <p className="max-w-lg text-lg leading-8 text-[var(--muted)] lg:justify-self-end">
              Automation handles the repetitive work. You keep final say over the words,
              visuals and moments that represent the song.
            </p>
          </div>
          <div className="mt-12 grid gap-4 lg:grid-cols-3">
            {steps.map((step) => (
              <article className="paper-card step-card min-h-72 p-6 sm:p-7" key={step.number}>
                <div className="flex items-center justify-between">
                  <span className="step-icon">
                    <StepIcon icon={step.icon} />
                  </span>
                  <span className="eyebrow text-[var(--muted)]">{step.number} / 03</span>
                </div>
                <h3 className="display mt-10 text-3xl sm:text-4xl">{step.title}</h3>
                <p className="relative z-10 mt-5 text-base leading-7 text-[var(--muted)]">{step.copy}</p>
              </article>
            ))}
          </div>
        </section>

        <section className="audience-panel py-24 sm:py-28">
          <div className="shell grid items-center gap-12 lg:grid-cols-[.9fr_1.1fr]">
            <div>
              <p className="eyebrow text-[var(--orange)]">Made for independent releases</p>
              <h2 className="display mt-4 max-w-2xl text-5xl sm:text-7xl">
                Music without a camera crew.
              </h2>
              <p className="mt-6 max-w-xl text-lg leading-8 text-[var(--muted)]">
                A focused release room for artists who want consistency without
                turning every launch into a full-time editing job.
              </p>
            </div>
            <div className="grid gap-3 sm:grid-cols-2">
              {[
                "AI bands",
                "Faceless musicians",
                "Suno / Udio artists",
                "Bedroom producers",
                "DIY performers",
                "Small music teams",
              ].map((item, index) => (
                <div className="audience-pill flex min-h-16 items-center gap-3 p-4" key={item}>
                  <span className={`size-2 rounded-full ${index % 2 === 0 ? "bg-[var(--violet)]" : "bg-[var(--orange)]"}`} aria-hidden="true" />
                  <span className="font-semibold">{item}</span>
                </div>
              ))}
            </div>
          </div>
        </section>

        <section id="pricing" className="shell scroll-mt-24 py-24 sm:py-28">
          <div className="flex flex-col justify-between gap-7 lg:flex-row lg:items-end">
            <div>
              <p className="eyebrow text-[var(--violet)]">Straightforward pricing</p>
              <h2 className="display mt-4 text-5xl sm:text-7xl">Pay for the release.</h2>
            </div>
            <p className="max-w-lg text-lg leading-8 text-[var(--muted)]">
              Start with a free direction check, then unlock only the clean files
              your release needs. No long contract and no hidden render credits.
            </p>
          </div>

          <div className="mt-12 grid gap-4 md:grid-cols-2 xl:grid-cols-4">
            {plans.map((plan) => (
              <article className={`paper-card pricing-card flex flex-col p-6 ${plan.featured ? "pricing-card-featured" : ""}`} key={plan.name}>
                <div className="flex items-start justify-between gap-3">
                  <p className={`eyebrow ${plan.featured ? "text-[var(--violet)]" : ""}`}>{plan.name}</p>
                  {plan.featured ? <span className="status-chip !text-[var(--violet)]">Complete</span> : null}
                </div>
                <p className="display mt-6 text-4xl">{plan.price}</p>
                <p className="mt-5 flex-1 text-sm leading-6 text-[var(--muted)]">{plan.copy}</p>
                <Link className={plan.featured ? "button-primary mt-7" : "button-quiet mt-7"} href={startHref}>
                  Start a release
                </Link>
              </article>
            ))}
          </div>

          <div className="mt-5 grid gap-4 md:grid-cols-2">
            {addOns.map((plan) => (
              <article className="surface-soft grid gap-5 rounded-[1.25rem] p-5 sm:grid-cols-[1fr_auto] sm:items-center" key={plan.name}>
                <div>
                  <p className="eyebrow text-[var(--orange)]">Optional add-on · {plan.name}</p>
                  <p className="mt-2 text-sm leading-6 text-[var(--muted)]">{plan.copy}</p>
                </div>
                <p className="display text-3xl sm:text-4xl">{plan.price}</p>
              </article>
            ))}
          </div>
        </section>

        <section className="shell pb-24">
          <div className="paper-card relative overflow-hidden px-6 py-12 text-center sm:px-12 sm:py-16">
            <div className="absolute inset-0 bg-[radial-gradient(circle_at_50%_115%,rgba(179,163,223,.2),transparent_45%)]" aria-hidden="true" />
            <div className="relative mx-auto max-w-3xl">
              <p className="eyebrow text-[var(--orange)]">Your next release can start here</p>
              <h2 className="display mt-4 text-4xl sm:text-6xl">Bring the song. Keep the taste.</h2>
              <p className="mx-auto mt-5 max-w-xl text-lg leading-8 text-[var(--muted)]">
                Build the first campaign direction before deciding what to unlock.
              </p>
              <Link className="button-primary mt-8" href={startHref}>
                Build a release pack <span aria-hidden="true">→</span>
              </Link>
            </div>
          </div>
        </section>
      </main>

      <footer className="border-t border-[var(--line)] py-10">
        <div className="shell flex flex-col justify-between gap-5 sm:flex-row sm:items-center">
          <Link className="brand-mark" href="/" aria-label="Hook2Stream home">
            <span className="brand-mark-icon" aria-hidden="true">
              <svg viewBox="0 0 24 24" width="19" height="19" fill="none">
                <path d="M4 13.5h3l1.4-5 2.6 9 2.1-7 1.4 3H20" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </span>
            <span>Hook<span className="text-[var(--orange)]">2</span>Stream</span>
          </Link>
          <p className="text-sm text-[var(--muted)]">
            One song. Three weeks of ready-to-post lyric shorts.
          </p>
        </div>
      </footer>
    </>
  );
}
