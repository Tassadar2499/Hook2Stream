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
    copy: "Upload one master, lyrics, cover art and 3–10 images or short video clips.",
  },
  {
    number: "02",
    title: "Keep the taste",
    copy: "Set your palette, typography, CTA and release timing once. The pack stays visually coherent.",
  },
  {
    number: "03",
    title: "Post for three weeks",
    copy: "Review the campaign and download platform-ready vertical files, copy and calendar in one bundle.",
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
    price: "$19",
    copy: "Choose six clean shorts from the campaign.",
  },
  {
    name: "Release pack",
    price: "$39",
    copy: "All 18 shorts, captions, CTAs and calendar.",
    featured: true,
  },
  {
    name: "Active artist",
    price: "$29/mo",
    copy: "One monthly pack, reusable brand kit and release history.",
  },
];

export default function Home() {
  const authConfigured = isAppAuthConfigured();

  return (
    <>
      <SiteHeader />
      <main>
        <section className="shell grid min-h-[76vh] items-center gap-10 py-12 lg:grid-cols-[1.15fr_.85fr] lg:py-20">
          <div>
            <p className="eyebrow mb-5 inline-flex rounded-full border border-[var(--line)] bg-white/55 px-3 py-2">
              Release content for independent musicians
            </p>
            <h1 className="display max-w-5xl text-[clamp(4rem,11vw,9rem)]">
              One song.
              <br />
              <span className="text-[var(--orange)]">Three weeks</span>
              <br />
              of shorts.
            </h1>
            <p className="mt-7 max-w-2xl text-xl leading-8 sm:text-2xl">
              Turn a finished track and a handful of visuals into a coherent
              21-day campaign of ready-to-post lyric videos.
            </p>
            <div className="mt-9 flex flex-wrap gap-3">
              <Link
                className="button-primary"
                href={authConfigured ? "/dashboard" : "/setup"}
              >
                Build a release pack
              </Link>
              <a className="button-secondary" href="#how-it-works">
                See the workflow
              </a>
            </div>
            <p className="mt-5 text-sm font-semibold opacity-65">
              No virality promise. No autopublishing. Just the release content
              you would otherwise spend days editing.
            </p>
          </div>

          <div className="relative mx-auto aspect-[4/5] w-full max-w-[34rem]">
            <div className="absolute inset-x-[12%] top-0 h-[88%] rotate-6 rounded-[2.3rem] border border-black bg-[var(--violet)] shadow-[12px_14px_0_#121212]" />
            <div className="absolute inset-x-[6%] top-[6%] h-[88%] -rotate-3 overflow-hidden rounded-[2.3rem] border border-black bg-[var(--orange)] shadow-[8px_10px_0_#121212]">
              <div className="grid h-full place-items-center bg-[radial-gradient(circle_at_40%_30%,#ffad85,transparent_27%),linear-gradient(150deg,#ff5c35,#bb2649)] p-8 text-white">
                <div className="w-full">
                  <p className="eyebrow">Day 00 · Out now</p>
                  <p className="display mt-6 text-6xl sm:text-7xl">
                    I kept
                    <br />
                    the fire
                    <br />
                    awake.
                  </p>
                  <div className="mt-9 h-1.5 overflow-hidden rounded-full bg-white/30">
                    <div className="h-full w-[68%] bg-[var(--lime)]" />
                  </div>
                  <div className="mt-4 flex items-center justify-between text-sm font-black uppercase tracking-widest">
                    <span>NEЯСЫТЬ</span>
                    <span>0:14 / 0:21</span>
                  </div>
                </div>
              </div>
            </div>
            <div className="absolute -bottom-2 right-0 rotate-3 rounded-full border border-black bg-[var(--lime)] px-5 py-3 font-black shadow-[4px_5px_0_#121212]">
              18 videos · one ZIP
            </div>
          </div>
        </section>

        <section className="border-y border-[var(--line)] bg-[var(--ink)] py-7 text-white">
          <div className="shell grid gap-7 sm:grid-cols-3">
            {deliverables.map(([value, label]) => (
              <div key={value} className="flex items-start gap-4">
                <span className="display text-5xl text-[var(--lime)]">{value}</span>
                <p className="max-w-xs pt-1 font-bold leading-6">{label}</p>
              </div>
            ))}
          </div>
        </section>

        <section id="how-it-works" className="shell py-24">
          <div className="max-w-3xl">
            <p className="eyebrow text-[var(--violet)]">How it works</p>
            <h2 className="display mt-4 text-6xl sm:text-8xl">
              Your release, minus the content treadmill.
            </h2>
          </div>
          <div className="mt-12 grid gap-5 lg:grid-cols-3">
            {steps.map((step, index) => (
              <article
                key={step.number}
                className={`paper-card min-h-72 p-7 ${
                  index === 1 ? "lg:translate-y-8" : ""
                }`}
              >
                <span className="display text-5xl text-[var(--orange)]">
                  {step.number}
                </span>
                <h3 className="display mt-10 text-4xl">{step.title}</h3>
                <p className="mt-5 text-lg leading-7">{step.copy}</p>
              </article>
            ))}
          </div>
        </section>

        <section className="bg-[var(--violet)] py-24 text-white">
          <div className="shell grid gap-10 lg:grid-cols-2">
            <div>
              <p className="eyebrow text-[var(--lime)]">Designed for</p>
              <h2 className="display mt-4 text-6xl sm:text-8xl">
                Music without a camera crew.
              </h2>
            </div>
            <div className="grid content-center gap-3 text-xl font-black sm:grid-cols-2">
              {[
                "AI bands",
                "Faceless musicians",
                "Suno / Udio artists",
                "Bedroom producers",
                "DIY performers",
                "Small music teams",
              ].map((item) => (
                <div
                  key={item}
                  className="rounded-2xl border border-white/25 bg-white/10 p-5"
                >
                  {item}
                </div>
              ))}
            </div>
          </div>
        </section>

        <section id="pricing" className="shell py-24">
          <div className="flex flex-col justify-between gap-6 md:flex-row md:items-end">
            <div>
              <p className="eyebrow text-[var(--orange)]">Simple pricing</p>
              <h2 className="display mt-4 text-6xl sm:text-8xl">
                Pay for the release.
              </h2>
            </div>
            <p className="max-w-md text-lg leading-7">
              Generative backgrounds stay separate from the core pack, so
              retries never quietly destroy the value of your plan.
            </p>
          </div>
          <div className="mt-12 grid gap-4 md:grid-cols-2 xl:grid-cols-4">
            {plans.map((plan) => (
              <article
                key={plan.name}
                className={`paper-card flex min-h-72 flex-col p-6 ${
                  plan.featured
                    ? "border-black bg-[var(--lime)] shadow-[7px_8px_0_#121212]"
                    : ""
                }`}
              >
                <p className="eyebrow">{plan.name}</p>
                <p className="display mt-5 text-5xl">{plan.price}</p>
                <p className="mt-5 flex-1 text-lg leading-7">{plan.copy}</p>
                <Link
                  className={plan.featured ? "button-primary" : "button-quiet"}
                  href={authConfigured ? "/dashboard" : "/setup"}
                >
                  Start a release
                </Link>
              </article>
            ))}
          </div>
        </section>
      </main>
      <footer className="border-t border-[var(--line)] py-10">
        <div className="shell flex flex-col justify-between gap-4 sm:flex-row">
          <p className="display text-2xl">Hook2Stream</p>
          <p className="text-sm font-semibold opacity-65">
            One song. Three weeks of ready-to-post lyric shorts.
          </p>
        </div>
      </footer>
    </>
  );
}
