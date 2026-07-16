"use client";

import { FormEvent, useEffect, useState } from "react";
import { useAuth } from "@clerk/nextjs";
import { useRouter } from "next/navigation";
import { AppShell } from "@/components/app-shell";
import { StatusPanel } from "@/components/status-panel";
import { ApiRequestError, BrandKit, apiFetch } from "@/lib/api";

const fonts = ["Inter", "Manrope", "Montserrat", "Oswald"];

export function BrandKitClient() {
  const { getToken, isLoaded, isSignedIn } = useAuth();
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
      <p className="eyebrow text-[var(--violet)]">Reusable defaults</p>
      <h1 className="display mt-2 text-5xl sm:text-7xl">Brand kit</h1>
      <p className="mt-4 max-w-2xl text-lg leading-8">
        A compact visual system for every short in the release. Arbitrary font
        uploads are deliberately disabled in the MVP.
      </p>

      {error && !brand ? (
        <div className="mt-8">
          <StatusPanel title="Could not load brand kit" message={error} tone="error" />
        </div>
      ) : brand ? (
        <form className="paper-card mt-8 grid gap-7 p-6 sm:p-8" onSubmit={save}>
          <div className="grid gap-5 md:grid-cols-2">
            <label className="field md:col-span-2">
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
              <label className="field" key={key}>
                <span>{label} color</span>
                <div className="flex gap-3">
                  <input
                    className="max-w-16 !p-1"
                    type="color"
                    value={brand[key]}
                    onChange={(event) =>
                      setBrand({ ...brand, [key]: event.target.value.toUpperCase() })
                    }
                  />
                  <input
                    required
                    pattern="^#[0-9A-Fa-f]{6}$"
                    value={brand[key]}
                    onChange={(event) =>
                      setBrand({ ...brand, [key]: event.target.value })
                    }
                  />
                </div>
              </label>
            ))}
            <div className="hidden md:block" />
            <label className="field">
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
            <label className="field">
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
            <label className="field">
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
            <label className="field">
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
            <label className="field md:col-span-2">
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

          <div
            className="rounded-3xl border border-black p-7"
            style={{
              background: brand.secondaryColor,
              color: brand.primaryColor,
              boxShadow: `8px 8px 0 ${brand.accentColor}`,
            }}
          >
            <p className="eyebrow">Live palette check</p>
            <p className="display mt-3 text-5xl">{brand.displayName}</p>
            <p className="mt-4 text-lg font-bold">{brand.defaultCta} →</p>
          </div>

          {message ? <StatusPanel title="Saved" message={message} tone="success" /> : null}
          {error ? <StatusPanel title="Could not save" message={error} tone="error" /> : null}
          <button className="button-primary justify-self-start" type="submit">
            Save brand kit
          </button>
        </form>
      ) : (
        <div className="mt-8">
          <StatusPanel title="Loading brand kit" message="Reading your reusable defaults…" />
        </div>
      )}
    </AppShell>
  );
}
