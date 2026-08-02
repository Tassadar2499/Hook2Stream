"use client";

import { FormEvent, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { useAppAuth } from "@/components/app-auth-provider";
import { AppShell } from "@/components/app-shell";
import { StatusPanel } from "@/components/status-panel";
import { ApiRequestError, BrandKit, apiFetch } from "@/lib/api";

const fonts = ["Inter", "Manrope", "Montserrat", "Oswald"];

export function BrandKitClient() {
  const { getToken, isLoaded, isSignedIn } = useAppAuth();
  const router = useRouter();
  const [brand, setBrand] = useState<BrandKit>();
  const [etag, setEtag] = useState<string>();
  const [message, setMessage] = useState<string>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (!isLoaded) return;
    if (!isSignedIn) {
      router.replace("/");
      return;
    }
    void (async () => {
      try {
        const token = await getToken();
        if (!token) throw new Error("No session token.");
        const result = await apiFetch<BrandKit>("/api/v1/brand-kit", token);
        setBrand(result.data);
        setEtag(result.etag ?? `"${result.data.version}"`);
      } catch (caught) {
        setError(
          caught instanceof ApiRequestError ? caught.message : "Unable to load the brand kit.",
        );
      }
    })();
  }, [getToken, isLoaded, isSignedIn, router]);

  async function save(event: FormEvent) {
    event.preventDefault();
    if (!brand || !etag) return;
    setMessage(undefined);
    setError(undefined);
    try {
      const token = await getToken();
      if (!token) throw new Error("No session token.");
      const result = await apiFetch<BrandKit>("/api/v1/brand-kit", token, {
        method: "PUT",
        headers: { "If-Match": etag },
        body: JSON.stringify(brand),
      });
      setBrand(result.data);
      setEtag(result.etag ?? `"${result.data.version}"`);
      setMessage("Brand kit saved. New releases will start with these defaults.");
    } catch (caught) {
      setError(
        caught instanceof ApiRequestError ? caught.message : "Unable to save the brand kit.",
      );
    }
  }

  return (
    <AppShell>
      <section className="surface-soft rounded-3xl border border-[var(--line)] p-6 sm:p-8">
        <p className="eyebrow text-[var(--violet)]">Reusable defaults</p>
        <h1 className="display mt-3 text-5xl sm:text-7xl">Brand kit</h1>
        <p className="mt-5 max-w-2xl text-lg leading-8 opacity-75">
          A compact visual system for every short in the release. Arbitrary font
          uploads are deliberately disabled in the MVP.
        </p>
      </section>

      {error && !brand ? (
        <div className="mt-8">
          <StatusPanel title="Could not load brand kit" message={error} tone="error" />
        </div>
      ) : brand ? (
        <form className="paper-card mt-6 grid gap-2 overflow-hidden p-2" onSubmit={save}>
          <div className="grid gap-2 lg:grid-cols-[1.25fr_.75fr]">
            <div className="surface-soft rounded-[1.35rem] p-5 sm:p-7">
              <div className="grid gap-5 md:grid-cols-2 xl:grid-cols-6">
                <label className="field md:col-span-2 xl:col-span-6">
                  <span>Artist display name</span>
                  <input
                    required
                    maxLength={120}
                    value={brand.displayName}
                    onChange={(event) =>
                      setBrand({ ...brand, displayName: event.target.value })
                    }
                  />
                </label>
                {(
                  [
                    ["Primary", "primaryColor"],
                    ["Secondary", "secondaryColor"],
                    ["Accent", "accentColor"],
                  ] as const
                ).map(([label, key]) => (
                  <div className="field xl:col-span-2" key={key}>
                    <span>{label} color</span>
                    <div className="flex gap-3">
                      <input
                        className="max-w-16 shrink-0 !p-1"
                        type="color"
                        aria-label={`${label} color picker`}
                        value={brand[key]}
                        onChange={(event) =>
                          setBrand({ ...brand, [key]: event.target.value.toUpperCase() })
                        }
                      />
                      <input
                        className="min-w-0"
                        required
                        pattern="^#[0-9A-Fa-f]{6}$"
                        aria-label={`${label} color hex value`}
                        value={brand[key]}
                        onChange={(event) =>
                          setBrand({ ...brand, [key]: event.target.value })
                        }
                      />
                    </div>
                  </div>
                ))}
                <label className="field xl:col-span-3">
                  <span>Heading font</span>
                  <select
                    value={brand.headingFont}
                    onChange={(event) =>
                      setBrand({ ...brand, headingFont: event.target.value })
                    }
                  >
                    {fonts.map((font) => (
                      <option key={font}>{font}</option>
                    ))}
                  </select>
                </label>
                <label className="field xl:col-span-3">
                  <span>Body font</span>
                  <select
                    value={brand.bodyFont}
                    onChange={(event) =>
                      setBrand({ ...brand, bodyFont: event.target.value })
                    }
                  >
                    {fonts.map((font) => (
                      <option key={font}>{font}</option>
                    ))}
                  </select>
                </label>
                <label className="field xl:col-span-3">
                  <span>Default CTA</span>
                  <input
                    required
                    maxLength={160}
                    value={brand.defaultCta}
                    onChange={(event) =>
                      setBrand({ ...brand, defaultCta: event.target.value })
                    }
                  />
                </label>
                <label className="field xl:col-span-3">
                  <span>HTTPS smart link</span>
                  <input
                    type="url"
                    placeholder="https://..."
                    value={brand.smartLink ?? ""}
                    onChange={(event) =>
                      setBrand({ ...brand, smartLink: event.target.value })
                    }
                  />
                </label>
                <label className="field md:col-span-2 xl:col-span-6">
                  <span>Tone restrictions</span>
                  <textarea
                    maxLength={1000}
                    placeholder="Words, claims or moods this campaign should avoid."
                    value={brand.toneRestrictions ?? ""}
                    onChange={(event) =>
                      setBrand({ ...brand, toneRestrictions: event.target.value })
                    }
                  />
                </label>
              </div>
            </div>

            <aside className="surface-inset rounded-[1.35rem] p-5 sm:p-7">
              <p className="eyebrow text-[var(--violet)]">Live palette check</p>
              <div
                className="mt-5 overflow-hidden rounded-3xl border border-[var(--line)] p-7"
                style={{
                  background: brand.secondaryColor,
                  color: brand.primaryColor,
                }}
              >
                <div
                  className="h-1.5 w-20 rounded-full"
                  style={{ background: brand.accentColor }}
                  aria-hidden="true"
                />
                <p
                  className="mt-6 text-4xl font-black leading-none tracking-[-0.04em] sm:text-5xl"
                  style={{ fontFamily: brand.headingFont }}
                >
                  {brand.displayName}
                </p>
                <p
                  className="mt-5 text-lg font-bold"
                  style={{ fontFamily: brand.bodyFont }}
                >
                  {brand.defaultCta} →
                </p>
              </div>
            </aside>
          </div>

          <div className="grid gap-3 px-3 pb-2 pt-3 sm:px-5">
            {message ? <StatusPanel title="Saved" message={message} tone="success" /> : null}
            {error ? <StatusPanel title="Could not save" message={error} tone="error" /> : null}
            <button className="button-primary justify-self-start" type="submit">
              Save brand kit
            </button>
          </div>
        </form>
      ) : (
        <div className="mt-8">
          <StatusPanel title="Loading brand kit" message="Reading your reusable defaults…" />
        </div>
      )}
    </AppShell>
  );
}
