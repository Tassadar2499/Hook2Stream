"use client";

import Link from "next/link";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { AppShell } from "@/components/app-shell";
import { useAppAuth } from "@/components/app-auth-provider";
import { StatusPanel } from "@/components/status-panel";
import { ApiRequestError, Release, apiFetch } from "@/lib/api";
import {
  Campaign,
  CampaignItem,
  ArtworkRevision,
  BillingEntitlement,
  BillingSummary,
  HookCandidate,
  HookSet,
  RenderBatchQueued,
  RenderBatchStatus,
  Workflow,
  createIdempotencyKey,
  titleCase,
} from "@/lib/workflow";
import { useProjectAutoRefresh } from "@/lib/use-project-auto-refresh";

type CompositionControls = {
  relativeDay: number;
  phase: string;
  durationMilliseconds: number;
  cta: string;
  caption: string;
  primaryColor: string;
  secondaryColor: string;
  brandVersion: number;
  fit: "fill" | "fit";
  focalX: number;
  focalY: number;
  opening: string;
  textLayout: string;
};

type RenderKind = "initial" | "contentChange" | "technicalRetry";

const schedule = [-10, -9, -8, -6, -5, -3, -2, -1, 0, 0, 1, 2, 3, 5, 6, 7, 9, 10];

export function CampaignReviewClient({ projectId }: { projectId: string }) {
  const { getToken, isLoaded, isSignedIn } = useAppAuth();
  const router = useRouter();
  const [release, setRelease] = useState<Release>();
  const [hooks, setHooks] = useState<HookSet>();
  const [hookDrafts, setHookDrafts] = useState<HookCandidate[]>([]);
  const [projectEtag, setProjectEtag] = useState<string>();
  const [campaign, setCampaign] = useState<Campaign>();
  const [backgroundAssetIds, setBackgroundAssetIds] = useState<string[]>([]);
  const [campaignEtag, setCampaignEtag] = useState<string>();
  const [selectedItem, setSelectedItem] = useState<CampaignItem>();
  const [itemDraft, setItemDraft] = useState<CampaignItem>();
  const [composition, setComposition] = useState<CompositionControls>();
  const [miniSelection, setMiniSelection] = useState<string[]>([]);
  const [billing, setBilling] = useState<BillingSummary>();
  const [renderBatch, setRenderBatch] = useState<RenderBatchStatus>();
  const [workflowBatchId, setWorkflowBatchId] = useState<string | null>();
  const [previewUrl, setPreviewUrl] = useState<string>();
  const [previewState, setPreviewState] = useState<string>();
  const [initialBatchId, setInitialBatchId] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const checkoutPollCount = useRef(0);

  const load = useCallback(async () => {
    const token = await getToken();
    if (!token) throw new Error("No session token.");
    const releaseResult = await apiFetch<Release>(`/api/v1/releases/${projectId}`, token);
    setRelease(releaseResult.data);
    setProjectEtag(releaseResult.etag ?? `"${releaseResult.data.version}"`);
    const previewAsset = releaseResult.data.assets
      .filter(
        (asset) =>
          asset.origin === "generated" &&
          asset.purpose === "previewVideo" &&
          asset.isActive &&
          asset.state === "ready",
      )
      .at(-1);
    const [hookResult, campaignResult, artworkResult, billingResult, workflowResult, previewResult] = await Promise.all([
      apiFetch<HookSet>(`/api/v1/releases/${projectId}/hooks`, token).catch((caught) => {
        if (caught instanceof ApiRequestError && caught.status === 404) return undefined;
        throw caught;
      }),
      apiFetch<Campaign>(`/api/v1/releases/${projectId}/campaign`, token).catch((caught) => {
        if (caught instanceof ApiRequestError && caught.status === 404) return undefined;
        throw caught;
      }),
      apiFetch<ArtworkRevision>(`/api/v1/releases/${projectId}/artwork`, token).catch(
        (caught) => {
          if (caught instanceof ApiRequestError && caught.status === 404) return undefined;
          throw caught;
        },
      ),
      apiFetch<BillingSummary>("/api/v1/billing/summary", token),
      apiFetch<Workflow>(`/api/v1/releases/${projectId}/workflow`, token),
      previewAsset
        ? apiFetch<{ url: string }>(
            `/api/v1/releases/${projectId}/assets/${previewAsset.id}/view-url`,
            token,
          ).catch((caught) => {
            if (caught instanceof ApiRequestError && [404, 409].includes(caught.status)) {
              return undefined;
            }
            throw caught;
          })
        : Promise.resolve(undefined),
    ]);
    setHooks(hookResult?.data);
    setHookDrafts(hookResult?.data.hooks ?? []);
    setCampaign(campaignResult?.data);
    setBilling(billingResult.data);
    setPreviewState(
      workflowResult.data.lanes.find((lane) => lane.lane === "preview")?.state,
    );
    setWorkflowBatchId(workflowResult.data.currentRenderBatchId ?? null);
    setPreviewUrl(previewResult?.data.url);
    setBackgroundAssetIds([
      ...(artworkResult?.data.backgroundAssetIds ?? []),
      ...releaseResult.data.assets
        .filter(
          (asset) =>
            asset.isActive &&
            asset.state === "ready" &&
            (asset.purpose === "campaignBackground" ||
              asset.purpose === "approvedCover" ||
              (asset.origin === "uploaded" &&
                asset.purpose === "source" &&
                asset.kind === "visual" &&
                (asset.contentType.startsWith("image/") || asset.contentType.startsWith("video/")))),
        )
        .map((asset) => asset.id),
    ].filter((value, index, all) => all.indexOf(value) === index));
    setCampaignEtag(campaignResult?.etag ?? (campaignResult ? `"${campaignResult.data.version}"` : undefined));
    if (campaignResult?.data.items.length) {
      setMiniSelection((current) =>
        current.length === 6
          ? current
          : campaignResult.data.items.slice(0, 6).map((item) => item.id),
      );
    }
  }, [getToken, projectId]);

  const loadRenderBatch = useCallback(async (batchId: string) => {
    const token = await getToken();
    if (!token) throw new Error("No session token.");
    const result = await apiFetch<RenderBatchStatus>(
      `/api/v1/releases/${projectId}/renders/${batchId}`,
      token,
    );
    setRenderBatch(result.data);
    return result.data;
  }, [getToken, projectId]);

  useEffect(() => {
    if (!isLoaded) return;
    if (!isSignedIn) {
      router.replace("/");
      return;
    }
    const timer = window.setTimeout(() => {
      void load()
        .catch((caught) => setError(messageFor(caught, "Could not load the campaign.")))
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
      (!campaign ||
        campaign.state === "processing" ||
        (!previewUrl &&
          !["failed", "cancelled", "succeeded", "stale", "waitingUser"].includes(
            previewState ?? "notStarted",
          ))),
  );

  useEffect(() => {
    if (!isLoaded || !isSignedIn) return;
    const storedBatchId = window.localStorage.getItem(renderStorageKey(projectId));
    const storedInitialBatchId = window.localStorage.getItem(initialRenderStorageKey(projectId));
    // The persisted workflow pointer is authoritative. localStorage remains a
    // compatibility fallback only until the first workflow snapshot arrives.
    const batchId = workflowBatchId === undefined ? storedBatchId : workflowBatchId;
    const timer = window.setTimeout(() => {
      setInitialBatchId(storedInitialBatchId ?? undefined);
      if (!batchId) {
        setRenderBatch(undefined);
        return;
      }
      window.localStorage.setItem(renderStorageKey(projectId), batchId);
      void loadRenderBatch(batchId).catch((caught) => {
        if (caught instanceof ApiRequestError && caught.status === 404) {
          if (workflowBatchId === undefined) {
            window.localStorage.removeItem(renderStorageKey(projectId));
          }
          return;
        }
        setError(messageFor(caught, "Could not restore render progress."));
      });
    }, 0);
    return () => window.clearTimeout(timer);
  }, [isLoaded, isSignedIn, loadRenderBatch, projectId, workflowBatchId]);

  useEffect(() => {
    if (!renderBatch || !["queued", "running"].includes(renderBatch.state)) return;
    const timer = window.setInterval(() => {
      void loadRenderBatch(renderBatch.batchId).catch((caught) =>
        setError(messageFor(caught, "Could not refresh render progress.")),
      );
    }, 3_000);
    return () => window.clearInterval(timer);
  }, [loadRenderBatch, renderBatch]);

  const videoEntitlements = useMemo(
    () =>
      (billing?.entitlements ?? []).filter(
        (entitlement) =>
          entitlement.projectId === projectId &&
          entitlement.state === "active" &&
          entitlement.includedItemCount > 0 ||
          entitlement.productCode === "active_artist" &&
          entitlement.projectId == null &&
          entitlement.state === "active",
      ),
    [billing, projectId],
  );

  const startRender = useCallback(async (
    entitlement: BillingEntitlement,
    kind: RenderKind,
    requestedItemIds?: string[],
  ) => {
    const itemIds = requestedItemIds ?? (
      entitlement.productCode === "active_artist" && entitlement.itemIds.length === 0
        ? campaign?.items.map((item) => item.id) ?? []
        : entitlement.itemIds
    );
    if (itemIds.length === 0) return;
    setBusy(true);
    setError(undefined);
    try {
      const token = await getToken();
      if (!token) throw new Error("No session token.");
      const result = await apiFetch<RenderBatchQueued>(
        `/api/v1/releases/${projectId}/renders`,
        token,
        {
          method: "POST",
          headers: {
            "Idempotency-Key": kind === "initial"
              ? `initial-render:${entitlement.id}`
              : createIdempotencyKey(`render-${kind}`),
          },
          body: JSON.stringify({
            entitlementId: entitlement.id,
            itemIds,
            kind,
          }),
        },
      );
      window.localStorage.setItem(renderStorageKey(projectId), result.data.batchId);
      if (kind === "initial") {
        window.localStorage.setItem(initialRenderStorageKey(projectId), result.data.batchId);
        setInitialBatchId(result.data.batchId);
      }
      setNotice(
        kind === "contentChange"
          ? "The edited video is rendering with its included content rerender."
          : kind === "technicalRetry"
            ? "A free technical retry has started for the failed output."
            : "Payment confirmed. Clean video rendering has started automatically.",
      );
      await Promise.all([loadRenderBatch(result.data.batchId), load()]);
    } catch (caught) {
      setError(messageFor(caught, "Could not start the clean video render."));
    } finally {
      setBusy(false);
    }
  }, [campaign, getToken, load, loadRenderBatch, projectId]);

  useEffect(() => {
    if (loading || renderBatch) return;
    const url = new URL(window.location.href);
    if (url.searchParams.get("billing") !== "success") return;
    const entitlement = videoEntitlements[0];
    if (entitlement) {
      url.searchParams.delete("billing");
      window.history.replaceState({}, "", `${url.pathname}${url.search}${url.hash}`);
      const timer = window.setTimeout(() => void startRender(entitlement, "initial"), 0);
      return () => window.clearTimeout(timer);
    }
    if (checkoutPollCount.current >= 10) {
      return;
    }
    const timer = window.setTimeout(() => {
      checkoutPollCount.current += 1;
      void load().catch((caught) =>
        setError(messageFor(caught, "Could not refresh payment status.")),
      );
    }, 2_000);
    return () => window.clearTimeout(timer);
  }, [load, loading, renderBatch, startRender, videoEntitlements]);

  const hookErrors = useMemo(
    () =>
      hookDrafts.flatMap((hook, index) => {
        const duration = hook.endMilliseconds - hook.startMilliseconds;
        return duration < 10_000 || duration > 30_000
          ? [`Hook ${index + 1} must be between 10 and 30 seconds.`]
          : [];
      }),
    [hookDrafts],
  );

  function updateHook(id: string, patch: Partial<HookCandidate>) {
    setHookDrafts((current) =>
      current.map((hook) => (hook.id === id ? { ...hook, ...patch } : hook)),
    );
  }

  async function saveHooks() {
    if (!hooks || !projectEtag || hookErrors.length > 0) return;
    setBusy(true);
    setError(undefined);
    try {
      const token = await requireToken();
      const result = await apiFetch<HookSet>(
        `/api/v1/releases/${projectId}/hooks`,
        token,
        {
          method: "PUT",
          headers: {
            "If-Match": projectEtag,
            "Idempotency-Key": createIdempotencyKey("hook-revision"),
          },
          body: JSON.stringify({ hooks: hookDrafts }),
        },
      );
      setHooks(result.data);
      setHookDrafts(result.data.hooks);
      setNotice("Hook revision saved. The campaign is being refreshed for the new timings.");
      await load();
    } catch (caught) {
      setError(messageFor(caught, "Could not save hooks."));
    } finally {
      setBusy(false);
    }
  }

  function openItem(item: CampaignItem) {
    setSelectedItem(item);
    setItemDraft({ ...item });
    setComposition(parseComposition(item.compositionJson, item.slot));
  }

  async function saveItem() {
    if (!campaign || !selectedItem || !itemDraft || !composition || !campaignEtag) return;
    setBusy(true);
    setError(undefined);
    try {
      const token = await requireToken();
      const result = await apiFetch<Campaign>(
        `/api/v1/releases/${projectId}/campaign/items/${selectedItem.id}`,
        token,
        {
          method: "PUT",
          headers: {
            "If-Match": campaignEtag,
            "Idempotency-Key": createIdempotencyKey("campaign-item-revision"),
          },
          body: JSON.stringify({
            template: itemDraft.template,
            hookId: itemDraft.hookId,
            backgroundAssetId: itemDraft.backgroundAssetId,
            text: itemDraft.text,
            compositionJson: JSON.stringify({
              ...composition,
              headline: itemDraft.text,
            }),
          }),
        },
      );
      setCampaign(result.data);
      setCampaignEtag(result.etag ?? `"${result.data.version}"`);
      // The server cancels an in-flight preview for the superseded revision or
      // marks an already-consumed preview stale. Do not keep showing the old URL
      // while the persisted workflow snapshot refreshes.
      setPreviewUrl(undefined);
      setPreviewState("notStarted");
      setSelectedItem(undefined);
      setItemDraft(undefined);
      setComposition(undefined);
      setNotice(`Item ${selectedItem.slot} updated without invalidating the other cards.`);
      await load();
    } catch (caught) {
      if (caught instanceof ApiRequestError && caught.code === "campaign.slot_assignment_immutable") {
        setItemDraft((current) => current ? {
          ...current,
          template: selectedItem.template,
          hookId: selectedItem.hookId,
        } : current);
        setError("Template and hook are fixed for this campaign slot. Your other composition edits are still open.");
      } else {
        setError(messageFor(caught, "Could not update this campaign item."));
      }
    } finally {
      setBusy(false);
    }
  }

  async function checkout(productCode: string) {
    setBusy(true);
    setError(undefined);
    try {
      const token = await requireToken();
      const result = await apiFetch<{ checkoutUrl: string }>("/api/v1/billing/checkouts", token, {
        method: "POST",
        headers: { "Idempotency-Key": createIdempotencyKey(`checkout-${productCode}`) },
        body: JSON.stringify({
          productCode,
          projectId: productCode === "active_artist" ? undefined : projectId,
          itemIds: productCode === "mini_release" ? miniSelection : undefined,
          returnPath: `/releases/${projectId}/campaign`,
        }),
      });
      window.location.assign(result.data.checkoutUrl);
    } catch (caught) {
      setError(messageFor(caught, "Checkout is not available yet."));
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
      <Link className="text-sm font-black" href={`/releases/${projectId}`}>← Release workflow</Link>
      <div className="mt-6 flex flex-col justify-between gap-4 lg:flex-row lg:items-end">
        <div>
          <p className="eyebrow text-[var(--orange)]">Campaign review</p>
          <h1 className="display mt-2 text-5xl sm:text-7xl">18 posts. 21 days.</h1>
          <p className="mt-4 max-w-2xl leading-7">Three hooks become twelve lyric, cover and visual-loop variants. Six campaign cards complete the release arc.</p>
        </div>
        <span className={`rounded-full border px-4 py-2 text-sm font-black uppercase ${campaign?.items.length === 18 ? "border-green-700 bg-green-100" : "border-amber-700 bg-amber-100"}`}>
          {campaign?.items.length ?? 0} / 18 items
        </span>
      </div>

      {error ? <div className="mt-6"><StatusPanel title="Campaign needs attention" message={error} tone="error" /></div> : null}
      {notice ? <div className="mt-6"><StatusPanel title="Campaign updated" message={notice} tone="success" /></div> : null}

      {loading ? (
        <div className="mt-7"><StatusPanel title="Loading campaign" message="Reading hooks, storyboard and preview state…" tone="neutral" /></div>
      ) : (
        <>
          <section className="paper-card mt-7 p-6 sm:p-8">
            <div className="flex flex-wrap items-end justify-between gap-4">
              <div>
                <p className="eyebrow text-[var(--violet)]">Automatic hooks</p>
                <h2 className="display mt-2 text-4xl">Tune the three moments.</h2>
              </div>
              <button className="button-secondary" type="button" disabled={!hooks || busy || hookErrors.length > 0} onClick={saveHooks}>Save hook revision</button>
            </div>
            {!hooks ? (
              <p className="mt-5 rounded-2xl border border-dashed border-[var(--line)] p-5 font-bold">Hooks appear after an approved transcript and analysis.</p>
            ) : (
              <div className="mt-6 grid gap-4 lg:grid-cols-3">
                {hookDrafts.map((hook, index) => (
                  <article className="rounded-2xl border border-[var(--line)] bg-white/55 p-4" key={hook.id}>
                    <p className="eyebrow text-[var(--orange)]">{titleCase(hook.kind)}</p>
                    <label className="field mt-4"><span>Label</span><input value={hook.label ?? ""} onChange={(event) => updateHook(hook.id, { label: event.target.value })} /></label>
                    <div className="mt-3 grid grid-cols-2 gap-3">
                      <label className="field"><span>In, s</span><input type="number" min={0} step={0.1} value={(hook.startMilliseconds / 1000).toFixed(1)} onChange={(event) => updateHook(hook.id, { startMilliseconds: Math.round(Number(event.target.value) * 1000) })} /></label>
                      <label className="field"><span>Out, s</span><input type="number" min={0} step={0.1} value={(hook.endMilliseconds / 1000).toFixed(1)} onChange={(event) => updateHook(hook.id, { endMilliseconds: Math.round(Number(event.target.value) * 1000) })} /></label>
                    </div>
                    <p className="mt-3 text-sm font-bold">{((hook.endMilliseconds - hook.startMilliseconds) / 1000).toFixed(1)} seconds · hook {index + 1}</p>
                  </article>
                ))}
              </div>
            )}
            {hookErrors.length > 0 ? <ul className="mt-4 rounded-xl bg-red-100 p-3 text-sm font-bold text-red-950" role="alert">{hookErrors.map((item) => <li key={item}>{item}</li>)}</ul> : null}
          </section>

          {!campaign ? (
            <div className="mt-6"><StatusPanel title="Storyboard is gated" message="Approve the current transcript and cover, confirm details and rights, and wait for three backgrounds." tone="neutral" /></div>
          ) : (
            <section className="mt-6">
              <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                {campaign.items.map((item) => {
                  const controls = parseComposition(item.compositionJson, item.slot);
                  const miniSelected = miniSelection.includes(item.id);
                  return (
                    <article className="paper-card overflow-hidden" key={item.id}>
                      <div className="campaign-poster grid aspect-[9/12] place-items-center bg-[var(--ink)] p-6 text-center text-white">
                        <div>
                          <p className="eyebrow text-[var(--lime)]">Day {formatDay(controls.relativeDay)}</p>
                          <p className="display mt-4 text-4xl">{item.text || release?.trackTitle}</p>
                          <p className="mt-4 text-sm font-bold opacity-75">{titleCase(item.template)}</p>
                        </div>
                      </div>
                      <div className="p-5">
                        <div className="flex items-center justify-between gap-3">
                          <span className="eyebrow">Slot {item.slot}</span>
                          <span className="rounded-full bg-[var(--lime)] px-2.5 py-1 text-xs font-black uppercase">{controls.phase}</span>
                        </div>
                        <p className="mt-3 text-sm leading-6 opacity-70">{controls.caption || `${titleCase(item.template)} campaign variation`}</p>
                        <div className="mt-4 flex flex-wrap gap-2">
                          <button className="button-quiet" type="button" onClick={() => openItem(item)}>Edit card</button>
                          <label className="flex items-center gap-2 rounded-full border border-[var(--line)] px-3 py-2 text-xs font-black uppercase">
                            <input type="checkbox" checked={miniSelected} onChange={(event) => setMiniSelection((current) => event.target.checked ? current.length < 6 ? [...current, item.id] : current : current.filter((id) => id !== item.id))} />Mini
                          </label>
                        </div>
                      </div>
                    </article>
                  );
                })}
              </div>
            </section>
          )}

          {campaign ? (
            <section className="paper-card mt-7 p-6 sm:p-8">
              <p className="eyebrow text-[var(--orange)]">Preview and clean export</p>
              <h2 className="display mt-2 text-4xl">One preview is free.</h2>
              <p className="mt-4 max-w-3xl leading-7">The best item renders at 540×960 with a watermark. Paid packs unlock clean 1080×1920 H.264/AAC files; the clean 3000×3000 cover is available separately.</p>
              <div className="mt-6 max-w-sm overflow-hidden rounded-3xl bg-[var(--ink)] text-white">
                {previewUrl && previewState !== "stale" ? (
                  <video
                    className="aspect-[9/16] w-full bg-black object-contain"
                    controls
                    playsInline
                    preload="metadata"
                    src={previewUrl}
                  >
                    Your browser cannot play the watermarked preview.
                  </video>
                ) : (
                  <div className="grid aspect-[9/16] place-items-center p-8 text-center font-black">
                    {previewState === "stale"
                      ? "The free preview belongs to an earlier release revision. Paid renders use the current approved campaign."
                      : previewState === "failed" || previewState === "cancelled"
                        ? "Preview rendering needs attention. Edit a campaign card to create a fresh revision."
                        : "The watermarked preview is rendering automatically…"}
                  </div>
                )}
              </div>
              <div className="mt-6 grid gap-4 md:grid-cols-3">
                <PlanCard title="Mini Release" price="$5" description={`Exactly six clean videos · ${miniSelection.length}/6 selected`} action="Choose Mini" disabled={miniSelection.length !== 6 || busy} onClick={() => checkout("mini_release")} />
                <PlanCard title="Release Pack" price="$9.90" description="All 18 clean videos, copy and calendar" action="Unlock full pack" disabled={busy} onClick={() => checkout("release_pack")} />
                <PlanCard title="Active Artist" price="$29/mo" description="One Release Pack per billing period" action="Subscribe" disabled={busy} onClick={() => checkout("active_artist")} />
              </div>
              {videoEntitlements.length > 0 ? (
                <div className="mt-6 rounded-2xl border border-[var(--line)] bg-white/60 p-5">
                  <p className="eyebrow text-[var(--violet)]">Purchased clean exports</p>
                  <div className="mt-4 grid gap-3">
                    {videoEntitlements.map((entitlement) => (
                      <div className="flex flex-wrap items-center justify-between gap-3" key={entitlement.id}>
                        <div>
                          <p className="font-black">{titleCase(entitlement.productCode)}</p>
                          <p className="text-sm opacity-70">
                            {entitlement.itemIds.length || entitlement.includedItemCount} videos · {entitlement.remainingContentRerenders} content rerenders remain
                          </p>
                        </div>
                        <button
                          className="button-secondary"
                          type="button"
                          disabled={busy || renderBatch?.entitlementId === entitlement.id}
                          onClick={() => startRender(entitlement, "initial")}
                        >
                          {renderBatch?.entitlementId === entitlement.id ? "Rendering started" : "Render included videos"}
                        </button>
                      </div>
                    ))}
                  </div>
                </div>
              ) : null}
              {renderBatch ? (
                <RenderProgress
                  batch={renderBatch}
                  busy={busy}
                  remainingContentRerenders={
                    videoEntitlements.find(
                      (entitlement) => entitlement.id === renderBatch.entitlementId,
                    )?.remainingContentRerenders ?? 0
                  }
                  onContentRerender={(itemId) => {
                    const entitlement = videoEntitlements.find(
                      (value) => value.id === renderBatch.entitlementId,
                    );
                    if (entitlement) void startRender(entitlement, "contentChange", [itemId]);
                  }}
                  onTechnicalRetry={(itemIds) => {
                    const entitlement = videoEntitlements.find(
                      (value) => value.id === renderBatch.entitlementId,
                    );
                    if (entitlement) void startRender(entitlement, "technicalRetry", itemIds);
                  }}
                  onBackToInitial={
                    initialBatchId && initialBatchId !== renderBatch.batchId
                      ? () => {
                          window.localStorage.setItem(renderStorageKey(projectId), initialBatchId);
                          void loadRenderBatch(initialBatchId);
                        }
                      : undefined
                  }
                />
              ) : null}
            </section>
          ) : null}
        </>
      )}

      {selectedItem && itemDraft && composition ? (
        <div className="fixed inset-0 z-50 grid bg-black/45 p-3 sm:place-items-center" role="dialog" aria-modal="true" aria-labelledby="item-editor-title">
          <section className="paper-card max-h-[calc(100vh-1.5rem)] w-full max-w-3xl overflow-y-auto p-6 sm:p-8">
            <div className="flex items-start justify-between gap-4">
              <div><p className="eyebrow text-[var(--violet)]">Item {selectedItem.slot}</p><h2 id="item-editor-title" className="display mt-2 text-4xl">Edit one composition.</h2></div>
              <button className="button-quiet" type="button" onClick={() => setSelectedItem(undefined)}>Close</button>
            </div>
            <div className="mt-6 grid gap-4 sm:grid-cols-2">
              <label className="field"><span>Template · fixed for this slot</span><input value={titleCase(itemDraft.template)} readOnly /></label>
              <label className="field"><span>Hook · fixed for this slot</span><input value={hookLabel(itemDraft.hookId, hookDrafts)} readOnly /></label>
              <p className="sm:col-span-2 text-sm font-bold opacity-70">Edit the card content and composition below. Template and hook assignments stay stable so the 18-item campaign contract remains valid.</p>
              <label className="field sm:col-span-2"><span>On-screen text</span><textarea value={itemDraft.text} onChange={(event) => setItemDraft({ ...itemDraft, text: event.target.value })} /></label>
              <label className="field"><span>Opening</span><select value={composition.opening} onChange={(event) => setComposition({ ...composition, opening: event.target.value })}><option value="fade">Fade</option><option value="punch">Punch</option><option value="reveal">Reveal</option></select></label>
              <label className="field"><span>Text layout</span><select value={composition.textLayout} onChange={(event) => setComposition({ ...composition, textLayout: event.target.value })}><option value="center">Center</option><option value="lowerThird">Lower third</option><option value="stacked">Stacked</option></select></label>
              <label className="field"><span>Duration, seconds</span><input type="number" min={10} max={30} step={1} value={composition.durationMilliseconds / 1000} onChange={(event) => setComposition({ ...composition, durationMilliseconds: Math.round(Number(event.target.value) * 1000) })} /></label>
              <label className="field"><span>Fit</span><select value={composition.fit} onChange={(event) => setComposition({ ...composition, fit: event.target.value as "fit" | "fill" })}><option value="fill">Fill</option><option value="fit">Fit</option></select></label>
              <label className="field"><span>Background</span><select value={itemDraft.backgroundAssetId ?? ""} onChange={(event) => setItemDraft({ ...itemDraft, backgroundAssetId: event.target.value || null })}><option value="">Approved cover</option>{backgroundAssetIds.map((id, index) => <option key={id} value={id}>Visual source {index + 1}</option>)}</select></label>
              <label className="field"><span>CTA</span><input value={composition.cta} onChange={(event) => setComposition({ ...composition, cta: event.target.value })} /></label>
              <label className="field"><span>Primary color</span><input type="color" value={composition.primaryColor} onChange={(event) => setComposition({ ...composition, primaryColor: event.target.value })} /></label>
              <label className="field"><span>Secondary color</span><input type="color" value={composition.secondaryColor} onChange={(event) => setComposition({ ...composition, secondaryColor: event.target.value })} /></label>
              <label className="field sm:col-span-2"><span>Caption</span><textarea value={composition.caption} onChange={(event) => setComposition({ ...composition, caption: event.target.value })} /></label>
              <label className="field"><span>Focal X</span><input type="range" min={0} max={1} step={0.01} value={composition.focalX} onChange={(event) => setComposition({ ...composition, focalX: Number(event.target.value) })} /></label>
              <label className="field"><span>Focal Y</span><input type="range" min={0} max={1} step={0.01} value={composition.focalY} onChange={(event) => setComposition({ ...composition, focalY: Number(event.target.value) })} /></label>
            </div>
            <div className="mt-7 flex flex-wrap justify-end gap-2"><button className="button-quiet" type="button" onClick={() => setSelectedItem(undefined)}>Cancel</button><button className="button-primary" type="button" onClick={saveItem} disabled={busy}>Apply item revision</button></div>
          </section>
        </div>
      ) : null}
    </AppShell>
  );
}

function PlanCard({ title, price, description, action, disabled, onClick }: { title: string; price: string; description: string; action: string; disabled: boolean; onClick: () => void }) {
  return <article className="rounded-2xl border border-[var(--line)] bg-white/55 p-5"><p className="eyebrow">{title}</p><p className="display mt-2 text-4xl">{price}</p><p className="mt-3 min-h-12 text-sm leading-6 opacity-70">{description}</p><button className="button-secondary mt-4 w-full" type="button" disabled={disabled} onClick={onClick}>{action}</button></article>;
}

function hookLabel(hookId: string, hooks: HookCandidate[]) {
  const hook = hooks.find((candidate) => candidate.id === hookId);
  return hook ? titleCase(hook.kind) : "Campaign hook";
}

function RenderProgress({
  batch,
  busy,
  remainingContentRerenders,
  onContentRerender,
  onTechnicalRetry,
  onBackToInitial,
}: {
  batch: RenderBatchStatus;
  busy: boolean;
  remainingContentRerenders: number;
  onContentRerender: (itemId: string) => void;
  onTechnicalRetry: (itemIds: string[]) => void;
  onBackToInitial?: () => void;
}) {
  const completed = batch.items.filter((item) => item.download).length;
  const failedItemIds = batch.items
    .filter((item) => item.state === "failed")
    .map((item) => item.campaignItemId);
  const progress = batch.items.length === 0 ? 0 : Math.round((completed / batch.items.length) * 100);
  return (
    <div className="mt-6 rounded-2xl border border-[var(--line)] bg-[var(--ink)] p-5 text-white">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <p className="eyebrow text-[var(--lime)]">Clean render · {titleCase(batch.state)}</p>
          <p className="mt-2 font-black">{completed} / {batch.items.length} videos ready</p>
        </div>
        {batch.export ? (
          <a className="button-primary" href={batch.export.url}>Download ZIP</a>
        ) : null}
      </div>
      <div className="mt-4 h-2 overflow-hidden rounded-full bg-white/20" role="progressbar" aria-label="Clean video render progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={progress}>
        <div className="h-full bg-[var(--lime)]" style={{ width: `${progress}%` }} />
      </div>
      <div className="mt-4 flex flex-wrap gap-2">
        {batch.items.map((item, index) => (
          <div className="flex items-center gap-1" key={item.campaignItemId}>
            {item.download ? (
              <a className="button-quiet border-white/30 text-white" href={item.download.url}>
                Video {index + 1}
              </a>
            ) : (
              <span className="rounded-full border border-white/20 px-3 py-2 text-xs font-black uppercase opacity-70">
                {index + 1} · {titleCase(item.state)}
              </span>
            )}
            {item.download && batch.kind === "initial" ? (
              <button
                className="rounded-full border border-white/20 px-3 py-2 text-xs font-black uppercase"
                type="button"
                disabled={busy || remainingContentRerenders === 0}
                onClick={() => onContentRerender(item.campaignItemId)}
              >
                Rerender edit
              </button>
            ) : null}
          </div>
        ))}
      </div>
      {failedItemIds.length > 0 ? (
        <button
          className="button-secondary mt-4"
          type="button"
          disabled={busy}
          onClick={() => onTechnicalRetry(failedItemIds)}
        >
          Retry failed videos free
        </button>
      ) : null}
      {onBackToInitial ? (
        <button className="button-quiet ml-2 mt-4 border-white/30 text-white" type="button" onClick={onBackToInitial}>
          Back to full render batch
        </button>
      ) : null}
    </div>
  );
}

function renderStorageKey(projectId: string) {
  return `hook2stream:render-batch:${projectId}`;
}

function initialRenderStorageKey(projectId: string) {
  return `hook2stream:initial-render-batch:${projectId}`;
}

function parseComposition(value: string, slot: number): CompositionControls {
  const fallback: CompositionControls = {
    relativeDay: schedule[Math.max(0, Math.min(schedule.length - 1, slot - 1))],
    phase: slot <= 8 ? "pre-release" : slot <= 10 ? "release" : "post-release",
    durationMilliseconds: 15_000,
    cta: "Listen now",
    caption: "",
    primaryColor: "#121212",
    secondaryColor: "#fffaf2",
    brandVersion: 0,
    fit: "fill",
    focalX: 0.5,
    focalY: 0.5,
    opening: "fade",
    textLayout: "center",
  };
  try {
    const parsed = JSON.parse(value) as Partial<CompositionControls> & {
      dayOffset?: number;
      callToAction?: string;
    };
    return {
      ...fallback,
      ...parsed,
      relativeDay: parsed.relativeDay ?? parsed.dayOffset ?? fallback.relativeDay,
      cta: parsed.cta ?? parsed.callToAction ?? fallback.cta,
    };
  } catch {
    return fallback;
  }
}

function formatDay(day: number) {
  if (day === 0) return "0";
  return day > 0 ? `+${day}` : String(day);
}

function messageFor(caught: unknown, fallback: string) {
  return caught instanceof ApiRequestError ? caught.message : fallback;
}
