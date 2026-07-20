export type WorkflowLaneName =
  | "audio"
  | "analysis"
  | "transcript"
  | "artwork"
  | "hooks"
  | "campaign"
  | "preview"
  | "finalRender";

export type WorkflowLane = {
  lane: WorkflowLaneName;
  state: string;
  progressPercent: number;
  blockerCode?: string | null;
  message?: string | null;
  updatedAt?: string | null;
};

export type Workflow = {
  projectId: string;
  flowKind: "legacy" | "mp3First" | string;
  projectVersion: number;
  lanes: WorkflowLane[];
  blockers: string[];
  nextAction?: string | null;
};

export type TranscriptWord = {
  text: string;
  startMilliseconds: number;
  endMilliseconds: number;
  confidence?: number | null;
};

export type TranscriptPhrase = {
  id: string;
  order: number;
  text: string;
  startMilliseconds: number;
  endMilliseconds: number;
  confidence?: number | null;
  warningAcknowledged: boolean;
  words?: TranscriptWord[];
};

export type TranscriptRevision = {
  revisionId: string;
  number: number;
  language: string;
  isInstrumental: boolean;
  source: string;
  state: string;
  phrases: TranscriptPhrase[];
  approvedAt?: string | null;
  version: number;
};

export type ArtworkCandidate = {
  id: string;
  assetId: string;
  ordinal: number;
  viewUrl?: string | null;
  prompt?: string | null;
  altText?: string | null;
  selected: boolean;
};

export type { ArtworkEditSpec } from "./artwork-edit";

export type ArtworkRevision = {
  revisionId: string;
  number: number;
  operationNumber: number;
  state: string;
  version: number;
  prompt: string;
  candidateAssetIds: string[];
  backgroundAssetIds: string[];
  selectedAssetId?: string | null;
  approvedCoverAssetId?: string | null;
  compositionJson: string;
  approvedAt?: string | null;
};

export type HookCandidate = {
  id: string;
  kind: "chorus" | "emotionalLine" | "instrumentalDrop" | string;
  startMilliseconds: number;
  endMilliseconds: number;
  label?: string | null;
};

export type HookSet = {
  revisionId: string;
  number: number;
  transcriptRevisionId: string;
  version: number;
  hooks: HookCandidate[];
};

export type CampaignItem = {
  id: string;
  slot: number;
  template: string;
  hookId: string;
  backgroundAssetId?: string | null;
  text: string;
  compositionJson: string;
};

export type Campaign = {
  revisionId: string;
  number: number;
  state: string;
  version: number;
  items: CampaignItem[];
};

export type BillingEntitlement = {
  id: string;
  productCode: string;
  projectId?: string | null;
  state: string;
  includedItemCount: number;
  itemIds: string[];
  remainingContentRerenders: number;
  validUntil?: string | null;
};

export type BillingSummary = {
  workspaceArtworkCredits: number;
  activeSubscription?: string | null;
  entitlements: BillingEntitlement[];
};

export type DownloadGrant = {
  assetId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  width?: number | null;
  height?: number | null;
  url: string;
  expiresAt: string;
};

export type RenderBatchQueued = {
  batchId: string;
  state: string;
  jobIds: string[];
};

export type RenderBatchStatus = {
  batchId: string;
  entitlementId: string;
  state: string;
  kind: "initial" | "contentChange" | "technicalRetry" | string;
  items: Array<{
    campaignItemId: string;
    state: string;
    jobId?: string | null;
    errorCode?: string | null;
    download?: DownloadGrant | null;
  }>;
  export?: DownloadGrant | null;
  completedAt?: string | null;
};

export const laneOrder: WorkflowLaneName[] = [
  "audio",
  "analysis",
  "transcript",
  "artwork",
  "hooks",
  "campaign",
  "preview",
  "finalRender",
];

export function titleCase(value: string) {
  return value
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replaceAll("_", " ")
    .replace(/\b\w/g, (character) => character.toUpperCase());
}

export function createIdempotencyKey(scope: string) {
  const random = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;
  return `${scope}:${random}`;
}
