"use client";

import Link from "next/link";
import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { AppShell } from "@/components/app-shell";
import { useAppAuth } from "@/components/app-auth-provider";
import { StatusPanel } from "@/components/status-panel";
import { UploadManager } from "@/components/upload-manager";
import { ApiRequestError, Release, apiFetch } from "@/lib/api";
import {
  type ArtworkEditSpec,
  coverFontCss,
  coverFontFamilies,
  coverHexWithAlpha,
  coverImageCropStyle,
  coverTextLayoutStyle,
  createDefaultArtworkEdit,
  parseArtworkEdit,
} from "@/lib/artwork-edit";
import {
  ArtworkRevision,
  BillingSummary,
  DownloadGrant,
  createIdempotencyKey,
} from "@/lib/workflow";
import { useProjectAutoRefresh } from "@/lib/use-project-auto-refresh";

type AssetReadUrl = { assetId: string; url: string; expiresAt: string };

export function ArtworkReviewClient({ projectId }: { projectId: string }) {
  const { getToken, isLoaded, isSignedIn } = useAppAuth();
  const router = useRouter();
  const [release, setRelease] = useState<Release>();
  const [artwork, setArtwork] = useState<ArtworkRevision>();
  const [billing, setBilling] = useState<BillingSummary>();
  const [cleanCover, setCleanCover] = useState<DownloadGrant>();
  const [assetUrls, setAssetUrls] = useState<Record<string, string>>({});
  const [selectedAssetId, setSelectedAssetId] = useState<string>();
  const [edit, setEdit] = useState<ArtworkEditSpec>(() => createDefaultArtworkEdit());
  const [etag, setEtag] = useState<string>();
  const [prompt, setPrompt] = useState("");
  const [style, setStyle] = useState("editorial music artwork");
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const approvalSubmitInFlight = useRef(false);

  const load = useCallback(async () => {
    const token = await getToken();
    if (!token) throw new Error("No session token.");
    const releaseResult = await apiFetch<Release>(`/api/v1/releases/${projectId}`, token);
    setRelease(releaseResult.data);
    const billingResult = await apiFetch<BillingSummary>("/api/v1/billing/summary", token)
      .catch(() => undefined);
    setBilling(billingResult?.data);
    const hasCleanCover = billingResult?.data.entitlements.some(
      (entitlement) =>
        entitlement.projectId === projectId &&
        entitlement.productCode === "clean_cover" &&
        entitlement.state === "active",
    );
    if (hasCleanCover) {
      const download = await apiFetch<DownloadGrant>(
        `/api/v1/releases/${projectId}/artwork/clean-cover/download-url`,
        token,
      ).catch((caught) => {
        if (caught instanceof ApiRequestError && caught.status === 409) return undefined;
        throw caught;
      });
      setCleanCover(download?.data);
    } else {
      setCleanCover(undefined);
    }
    try {
      const result = await apiFetch<ArtworkRevision>(
        `/api/v1/releases/${projectId}/artwork`,
        token,
      );
      setArtwork(result.data);
      setPrompt(result.data.prompt);
      setSelectedAssetId(result.data.selectedAssetId ?? undefined);
      setEdit(parseArtworkEdit(result.data.compositionJson));
      setEtag(result.etag ?? `"${result.data.version}"`);
      const uploadedCoverIds = releaseResult.data.assets
        .filter(
          (asset) =>
            asset.kind === "cover" &&
            asset.origin === "uploaded" &&
            asset.purpose === "source" &&
            asset.state === "ready",
        )
        .map((asset) => asset.id);
      const urls = await Promise.all(
        [
          ...result.data.candidateAssetIds,
          ...uploadedCoverIds,
          ...result.data.backgroundAssetIds,
        ].filter((value, index, all) => all.indexOf(value) === index).map(async (assetId) => {
          try {
            const read = await apiFetch<AssetReadUrl>(
              `/api/v1/releases/${projectId}/assets/${assetId}/view-url`,
              token,
            );
            return [assetId, read.data.url] as const;
          } catch {
            return [assetId, ""] as const;
          }
        }),
      );
      setAssetUrls(Object.fromEntries(urls));
    } catch (caught) {
      if (!(caught instanceof ApiRequestError && caught.status === 404)) throw caught;
      setArtwork(undefined);
    }
  }, [getToken, projectId]);

  useEffect(() => {
    if (!isLoaded) return;
    if (!isSignedIn) {
      router.replace("/");
      return;
    }
    const timer = window.setTimeout(() => {
      void load()
        .catch((caught) => setError(messageFor(caught, "Could not load artwork.")))
        .finally(() => setLoading(false));
    }, 0);
    return () => window.clearTimeout(timer);
  }, [isLoaded, isSignedIn, load, router]);

  useProjectAutoRefresh(
    projectId,
    getToken,
    load,
    isLoaded &&
      isSignedIn &&
      (!artwork ||
        artwork.state === "processing" ||
        (artwork.state === "approved" && artwork.backgroundAssetIds.length !== 3)),
  );

  const hasCleanCoverEntitlement = billing?.entitlements.some(
    (entitlement) =>
      entitlement.projectId === projectId &&
      entitlement.productCode === "clean_cover" &&
      entitlement.state === "active",
  );

  useEffect(() => {
    if (!hasCleanCoverEntitlement || cleanCover) return;
    const timer = window.setInterval(() => {
      void load().catch((caught) =>
        setError(messageFor(caught, "Could not refresh the clean cover.")),
      );
    }, 3_000);
    return () => window.clearInterval(timer);
  }, [cleanCover, hasCleanCoverEntitlement, load]);

  const selectedUrl = selectedAssetId ? assetUrls[selectedAssetId] : undefined;
  const candidateAssetIds = useMemo(
    () =>
      [
        ...(artwork?.candidateAssetIds ?? []),
        ...(release?.assets
          .filter(
            (asset) =>
              asset.kind === "cover" &&
              asset.origin === "uploaded" &&
              asset.purpose === "source" &&
              asset.state === "ready",
          )
          .map((asset) => asset.id) ?? []),
      ].filter((value, index, all) => all.indexOf(value) === index),
    [artwork, release],
  );
  const remainingIncluded = artwork ? Math.max(0, 3 - artwork.operationNumber) : 3;
  const availableArtworkOperations = remainingIncluded + (billing?.workspaceArtworkCredits ?? 0);
  const composition = useMemo(() => JSON.stringify(edit), [edit]);
  const compositionDirty = artwork
    ? composition !== JSON.stringify(parseArtworkEdit(artwork.compositionJson))
    : false;
  const approvedPack = artwork?.state === "approved";
  const canRetryBackgrounds = approvedPack && artwork.backgroundAssetIds.length === 0;
  const backgroundsComplete = approvedPack && artwork.backgroundAssetIds.length === 3;
  const approvalActionLabel = canRetryBackgrounds
    ? "Retry campaign backgrounds"
    : backgroundsComplete
      ? "Campaign backgrounds complete"
      : approvedPack
        ? "Campaign backgrounds are processing"
        : compositionDirty || artwork?.selectedAssetId !== selectedAssetId
          ? "Save before approval"
          : "Approve official cover";
  const approvalActionDisabled =
    busy ||
    (approvedPack
      ? !canRetryBackgrounds
      : !selectedAssetId || compositionDirty || artwork?.selectedAssetId !== selectedAssetId);
  const coverCropStyle = coverImageCropStyle(edit);
  const textLayoutStyle = coverTextLayoutStyle(edit);

  async function generate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError(undefined);
    setNotice(undefined);
    try {
      const token = await requireToken();
      await apiFetch(`/api/v1/releases/${projectId}/artwork`, token, {
        method: "POST",
        headers: { "Idempotency-Key": createIdempotencyKey("artwork-pack") },
        body: JSON.stringify({ prompt, style }),
      });
      setNotice("Artwork generation queued. This operation targets three candidates.");
      await load();
    } catch (caught) {
      setError(messageFor(caught, "Could not generate artwork."));
    } finally {
      setBusy(false);
    }
  }

  async function saveSelection() {
    if (!artwork || !selectedAssetId || !etag) return;
    setBusy(true);
    setError(undefined);
    try {
      const token = await requireToken();
      const result = await apiFetch<ArtworkRevision>(
        `/api/v1/releases/${projectId}/artwork/selection`,
        token,
        {
          method: "PUT",
          headers: {
            "If-Match": etag,
            "Idempotency-Key": createIdempotencyKey("artwork-selection"),
          },
          body: JSON.stringify({
            packRevisionId: artwork.revisionId,
            selectedAssetId,
            compositionJson: composition,
          }),
        },
      );
      setArtwork(result.data);
      setEtag(result.etag ?? `"${result.data.version}"`);
      setNotice("Cover composition saved. Approve it when the crop and typography are final.");
    } catch (caught) {
      setError(messageFor(caught, "Could not save the cover composition."));
    } finally {
      setBusy(false);
    }
  }

  async function approveCover() {
    if (!artwork || approvalSubmitInFlight.current) return;
    const retryingBackgrounds =
      artwork.state === "approved" && artwork.backgroundAssetIds.length === 0;
    if (
      artwork.state === "approved"
        ? !retryingBackgrounds
        : !selectedAssetId || compositionDirty || artwork.selectedAssetId !== selectedAssetId
    ) return;

    approvalSubmitInFlight.current = true;
    setBusy(true);
    setError(undefined);
    try {
      const token = await requireToken();
      await apiFetch(`/api/v1/releases/${projectId}/artwork/cover-approval`, token, {
        method: "POST",
        headers: {
          "If-Match": etag ?? `"${artwork.version}"`,
          "Idempotency-Key": createIdempotencyKey("cover-approval"),
        },
        body: JSON.stringify({ revisionId: artwork.revisionId }),
      });
      setNotice(
        retryingBackgrounds
          ? "Campaign background retry queued. Three coherent 9:16 backgrounds are being prepared."
          : "Official cover approved. Three coherent 9:16 backgrounds are being prepared.",
      );
      await load();
    } catch (caught) {
      setError(messageFor(
        caught,
        retryingBackgrounds
          ? "Could not retry campaign backgrounds."
          : "Could not approve this cover.",
      ));
    } finally {
      approvalSubmitInFlight.current = false;
      setBusy(false);
    }
  }

  async function checkout(productCode: "art_credits_5" | "clean_cover") {
    setBusy(true);
    setError(undefined);
    try {
      const token = await requireToken();
      const result = await apiFetch<{ checkoutUrl: string }>(
        "/api/v1/billing/checkouts",
        token,
        {
          method: "POST",
          headers: {
            "Idempotency-Key": createIdempotencyKey(`checkout-${productCode}`),
          },
          body: JSON.stringify({
            productCode,
            projectId: productCode === "clean_cover" ? projectId : undefined,
            returnPath: `/releases/${projectId}/artwork`,
          }),
        },
      );
      window.location.assign(result.data.checkoutUrl);
    } catch (caught) {
      setError(messageFor(caught, "Could not open checkout."));
    } finally {
      setBusy(false);
    }
  }

  async function requireToken() {
    const token = await getToken();
    if (!token) throw new Error("No session token.");
    return token;
  }

  return (
    <AppShell>
      <Link className="text-sm font-black" href={`/releases/${projectId}`}>
        ← Release workflow
      </Link>
      <div className="mt-6 flex flex-col justify-between gap-4 lg:flex-row lg:items-end">
        <div>
          <p className="eyebrow text-[var(--orange)]">Required review</p>
          <h1 className="display mt-2 text-5xl sm:text-7xl">Choose the visual world.</h1>
          <p className="mt-4 max-w-2xl leading-7">
            AI creates the raster concept; Hook2Stream applies precise artist and title typography locally.
          </p>
        </div>
        <div
          className="surface-soft rounded-2xl border border-[var(--line)] p-4 text-sm font-black"
          role="status"
        >
          <p>Included operations remaining: {remainingIncluded}</p>
          <p className="mt-1">Purchased operations: {billing?.workspaceArtworkCredits ?? 0}</p>
        </div>
      </div>

      {error ? <div className="mt-6"><StatusPanel title="Artwork needs attention" message={error} tone="error" /></div> : null}
      {notice ? <div className="mt-6"><StatusPanel title="Artwork updated" message={notice} tone="success" /></div> : null}

      {loading ? (
        <div className="mt-7"><StatusPanel title="Loading artwork" message="Reading candidate history…" tone="neutral" /></div>
      ) : (
        <>
          <form
            className="paper-card mt-7 p-6 sm:p-8"
            onSubmit={generate}
            aria-label="Artwork generation"
            aria-busy={busy}
          >
            <div className="grid gap-5 lg:grid-cols-[1fr_.45fr_auto] lg:items-end">
              <label className="field">
                <span>Visual brief</span>
                <textarea value={prompt} onChange={(event) => setPrompt(event.target.value)} placeholder="Mood, imagery, materials and details to avoid…" required maxLength={2000} />
              </label>
              <label className="field">
                <span>Style</span>
                <select value={style} onChange={(event) => setStyle(event.target.value)}>
                  <option value="editorial music artwork">Editorial</option>
                  <option value="cinematic photographic artwork">Cinematic photo</option>
                  <option value="bold graphic album artwork">Bold graphic</option>
                  <option value="surreal mixed-media collage">Surreal collage</option>
                </select>
              </label>
              <button className="button-secondary" type="submit" disabled={busy || !prompt.trim() || availableArtworkOperations === 0}>
                {artwork ? "Generate new pack" : "Start cover pack"}
              </button>
            </div>
            <p className="mt-3 text-sm text-[var(--muted)]">
              A successful pack contains three candidates. Technical retries and moderation corrections do not spend another operation.
            </p>
            {availableArtworkOperations === 0 ? (
              <div className="surface-inset mt-4 flex flex-wrap items-center gap-3 rounded-2xl border border-[var(--line)] p-4">
                <p className="flex-1 font-bold">Need another direction? Add five complete artwork generations for $1.</p>
                <button className="button-quiet" type="button" disabled={busy} onClick={() => checkout("art_credits_5")}>Add 5 generations · $1</button>
              </div>
            ) : null}
          </form>

          {artwork ? (
            <>
              <fieldset className="mt-6 grid gap-4 lg:grid-cols-3">
                <legend className="sr-only">Cover candidates</legend>
                {candidateAssetIds.map((assetId, index) => (
                  <label
                    key={assetId}
                    className={`paper-card cursor-pointer overflow-hidden p-3 focus-within:ring-4 focus-within:ring-[var(--violet)] ${
                      selectedAssetId === assetId ? "ring-4 ring-[var(--violet)]" : ""
                    }`}
                  >
                    <span className="sr-only">Candidate {index + 1}</span>
                    <div className="surface-inset aspect-square overflow-hidden rounded-2xl">
                      {assetUrls[assetId] ? (
                        // Generated images are copied into owner-scoped storage before display.
                        // eslint-disable-next-line @next/next/no-img-element
                        <img className="size-full object-cover" src={assetUrls[assetId]} alt={`Generated cover candidate ${index + 1}`} />
                      ) : (
                        <div className="grid size-full place-items-center p-6 text-center font-black">Candidate is processing</div>
                      )}
                    </div>
                    <div className="mt-3 flex items-center gap-2 font-black">
                      <input type="radio" name="coverCandidate" checked={selectedAssetId === assetId} onChange={() => setSelectedAssetId(assetId)} />
                      Candidate {index + 1}
                    </div>
                  </label>
                ))}
              </fieldset>

              <section className="paper-card mt-6 grid gap-7 p-6 sm:p-8 xl:grid-cols-[.75fr_1fr]">
                <div
                  className="cover-preview relative aspect-square overflow-hidden rounded-3xl"
                  style={{
                    backgroundColor: edit.palette[0],
                    containerType: "inline-size",
                  } as React.CSSProperties}
                >
                  {selectedUrl ? (
                    // eslint-disable-next-line @next/next/no-img-element
                    <img
                      className="absolute max-w-none object-cover"
                      style={{ ...coverCropStyle, transform: "none", objectPosition: "center" }}
                      src={selectedUrl}
                      alt="Selected cover preview"
                    />
                  ) : null}
                  {edit.showArtist || edit.showTitle ? (
                    <>
                      <div
                        className="absolute inset-x-0"
                        style={{
                          top: textLayoutStyle.bandTop,
                          height: textLayoutStyle.bandHeight,
                          backgroundColor: coverHexWithAlpha(edit.palette[0], 0.58),
                        }}
                      />
                      {edit.showArtist ? (
                        <p
                          className="absolute max-w-[88%] overflow-hidden whitespace-nowrap"
                          style={{
                            top: textLayoutStyle.artistTop,
                            left: textLayoutStyle.textLeft,
                            transform: textLayoutStyle.textTransform,
                            color: edit.palette[2],
                            fontFamily: coverFontCss(edit.fontFamily),
                            fontSize: `${edit.artistFontSize / 30}cqi`,
                            fontWeight: 400,
                            lineHeight: 1,
                          }}
                        >
                          {release?.artistName || "Artist"}
                        </p>
                      ) : null}
                      {edit.showTitle ? (
                        <p
                          className="absolute max-w-[88%] overflow-hidden whitespace-nowrap"
                          style={{
                            top: textLayoutStyle.titleTop,
                            left: textLayoutStyle.textLeft,
                            transform: textLayoutStyle.textTransform,
                            color: edit.palette[1],
                            fontFamily: coverFontCss(edit.fontFamily),
                            fontSize: `${edit.titleFontSize / 30}cqi`,
                            fontWeight: 400,
                            lineHeight: 1,
                          }}
                        >
                          {release?.trackTitle || "Track title"}
                        </p>
                      ) : null}
                    </>
                  ) : null}
                </div>
                <div>
                  <p className="eyebrow text-[var(--violet)]">Controlled editor</p>
                  <h2 className="display mt-2 text-4xl">Finish the official cover.</h2>
                  <div className="mt-6 grid gap-4 sm:grid-cols-2">
                    {([
                      ["cropX", "Horizontal crop"],
                      ["cropY", "Vertical crop"],
                    ] as const).map(([key, label]) => (
                      <label className="field" key={key}>
                        <span>{label}</span>
                        <input
                          type="range"
                          min={0}
                          max={1}
                          step={0.01}
                          value={edit[key]}
                          aria-valuetext={`${Math.round(edit[key] * 100)}%`}
                          onChange={(event) => setEdit((current) => ({ ...current, [key]: Number(event.target.value) }))}
                        />
                      </label>
                    ))}
                    <label className="field sm:col-span-2">
                      <span>Crop scale</span>
                      <input
                        type="range"
                        min={1}
                        max={2}
                        step={0.01}
                        value={edit.cropScale}
                        aria-valuetext={`${Math.round(edit.cropScale * 100)}%`}
                        onChange={(event) => setEdit((current) => ({ ...current, cropScale: Number(event.target.value) }))}
                      />
                    </label>
                    <label className="field sm:col-span-2">
                      <span>Font family</span>
                      <select value={edit.fontFamily} onChange={(event) => setEdit((current) => ({ ...current, fontFamily: event.target.value as ArtworkEditSpec["fontFamily"] }))}>
                        {coverFontFamilies.map((font) => <option key={font.value} value={font.value}>{font.label}</option>)}
                      </select>
                    </label>
                    <label className="field">
                      <span>Artist size · {edit.artistFontSize}px</span>
                      <input type="range" min={72} max={220} step={2} value={edit.artistFontSize} onChange={(event) => setEdit((current) => ({ ...current, artistFontSize: Number(event.target.value) }))} />
                    </label>
                    <label className="field">
                      <span>Title size · {edit.titleFontSize}px</span>
                      <input type="range" min={96} max={360} step={2} value={edit.titleFontSize} onChange={(event) => setEdit((current) => ({ ...current, titleFontSize: Number(event.target.value) }))} />
                    </label>
                    <label className="field">
                      <span>Text position X · {Math.round(edit.textX * 100)}%</span>
                      <input type="range" min={0} max={1} step={0.01} value={edit.textX} onChange={(event) => setEdit((current) => ({ ...current, textX: Number(event.target.value) }))} />
                    </label>
                    <label className="field">
                      <span>Text position Y · {Math.round(edit.textY * 100)}%</span>
                      <input type="range" min={0} max={1} step={0.01} value={edit.textY} onChange={(event) => setEdit((current) => ({ ...current, textY: Number(event.target.value) }))} />
                    </label>
                    <label className="flex items-center gap-3 rounded-xl border border-[var(--line)] p-3 font-bold"><input type="checkbox" checked={edit.showArtist} onChange={(event) => setEdit((current) => ({ ...current, showArtist: event.target.checked }))} />Show artist</label>
                    <label className="flex items-center gap-3 rounded-xl border border-[var(--line)] p-3 font-bold"><input type="checkbox" checked={edit.showTitle} onChange={(event) => setEdit((current) => ({ ...current, showTitle: event.target.checked }))} />Show title</label>
                    <div className="sm:col-span-2">
                      <span className="eyebrow">Palette</span>
                      <div className="mt-2 flex flex-wrap gap-3">
                        {(["Backdrop", "Title", "Artist"] as const).map((label, index) => (
                          <label className="flex items-center gap-2 text-sm font-bold" key={label}>
                            <input aria-label={`${label} color`} type="color" value={edit.palette[index]} onChange={(event) => setEdit((current) => ({ ...current, palette: current.palette.map((item, itemIndex) => itemIndex === index ? event.target.value : item) as ArtworkEditSpec["palette"] }))} />
                            {label}
                          </label>
                        ))}
                      </div>
                    </div>
                  </div>
                  <div className="mt-7 flex flex-wrap gap-2">
                    <button className="button-secondary" type="button" onClick={saveSelection} disabled={busy || !selectedAssetId}>Save composition</button>
                    <button className="button-primary" type="button" onClick={approveCover} disabled={approvalActionDisabled}>{approvalActionLabel}</button>
                    {cleanCover ? (
                      <a className="button-quiet" href={cleanCover.url}>Download clean 3000×3000 cover</a>
                    ) : artwork.state === "approved" && hasCleanCoverEntitlement ? (
                      <span className="status-chip surface-inset">
                        <span className="size-1.5 rounded-full bg-[var(--violet)]" aria-hidden="true" />
                        Clean cover is rendering…
                      </span>
                    ) : artwork.state === "approved" ? (
                      <button className="button-quiet" type="button" disabled={busy} onClick={() => checkout("clean_cover")}>Clean 3000×3000 cover · $2</button>
                    ) : null}
                  </div>
                  <p className="mt-3 text-sm text-[var(--muted)]">Changing an approved cover starts a new artwork operation because its three backgrounds must be rebuilt.</p>
                </div>
              </section>
              {artwork.backgroundAssetIds.length > 0 ? (
                <section className="paper-card mt-6 p-6 sm:p-8">
                  <p className="eyebrow text-[var(--violet)]">Coherent 9:16 pack</p>
                  <h2 className="display mt-2 text-4xl">Campaign backgrounds.</h2>
                  <p className="mt-3 max-w-2xl text-sm leading-6 text-[var(--muted)]">
                    These three backgrounds inherit the approved cover. Individual campaign cards can switch between them and adjust fit or focal point.
                  </p>
                  <div className="mt-6 grid gap-4 sm:grid-cols-3">
                    {artwork.backgroundAssetIds.map((assetId, index) => (
                      <div key={assetId} className="surface-inset aspect-[9/16] overflow-hidden rounded-2xl">
                        {assetUrls[assetId] ? (
                          // eslint-disable-next-line @next/next/no-img-element
                          <img className="size-full object-cover" src={assetUrls[assetId]} alt={`Generated campaign background ${index + 1}`} />
                        ) : (
                          <div className="grid size-full place-items-center p-4 text-center font-black">Background {index + 1} is processing</div>
                        )}
                      </div>
                    ))}
                  </div>
                </section>
              ) : null}
            </>
          ) : (
            <div className="mt-6"><StatusPanel title="Artwork is waiting for setup" message="Confirm artist, title, RU/EN language, release timing and rights in the project workflow." tone="neutral" /></div>
          )}

          <details className="paper-card mt-6 p-6 sm:p-8">
            <summary className="cursor-pointer font-black">Use your own cover</summary>
            <div className="mt-5">
              <UploadManager projectId={projectId} kind="cover" title="Manual cover candidate" description="JPG, PNG or WebP. It still needs selection and approval." accept=".jpg,.jpeg,.png,.webp,image/jpeg,image/png,image/webp" onCompleted={load} />
            </div>
          </details>
        </>
      )}
    </AppShell>
  );
}

function messageFor(caught: unknown, fallback: string) {
  return caught instanceof ApiRequestError ? caught.message : fallback;
}
