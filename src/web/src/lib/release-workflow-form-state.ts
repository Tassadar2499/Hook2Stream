import type { Release, RightsAttestation } from "./api";

type CanonicalRelease = Pick<Release, "isInstrumentalConfirmed">;

export type WorkflowCheckpointFormState = {
  canonicalInstrumentalConfirmed: boolean;
  canonicalRightsKey: string;
  instrumentalConfirmed: boolean;
  ownsAudioRights: boolean;
  ownsLyricsRights: boolean;
  ownsVisualRights: boolean;
  allowsExternalAiArtwork: boolean;
  allowsExternalAiProcessing: boolean;
  syntheticContentStatus: RightsAttestation["syntheticContentStatus"];
};

export function createWorkflowCheckpointFormState(
  release: CanonicalRelease,
  rights?: RightsAttestation,
): WorkflowCheckpointFormState {
  return {
    canonicalInstrumentalConfirmed: release.isInstrumentalConfirmed,
    canonicalRightsKey: rightsKey(rights),
    instrumentalConfirmed: release.isInstrumentalConfirmed,
    ownsAudioRights: rights?.ownsAudioRights ?? false,
    ownsLyricsRights: rights?.ownsLyricsRights ?? false,
    ownsVisualRights: rights?.ownsVisualRights ?? false,
    allowsExternalAiArtwork: rights?.allowsExternalAiArtwork ?? false,
    allowsExternalAiProcessing: rights?.allowsExternalAiProcessing ?? false,
    syntheticContentStatus: rights?.syntheticContentStatus ?? "unknown",
  };
}

export function workflowCheckpointCanonicalKey(
  release: Pick<Release, "id" | "isInstrumentalConfirmed">,
  rights?: RightsAttestation,
): string {
  return `${release.id}:${release.isInstrumentalConfirmed}:${rightsKey(rights)}`;
}

function rightsKey(rights?: RightsAttestation) {
  return rights
    ? JSON.stringify([
        rights.id,
        rights.acceptedAt,
        rights.ownsAudioRights,
        rights.ownsLyricsRights,
        rights.ownsVisualRights,
        rights.allowsExternalAiArtwork,
        rights.allowsExternalAiProcessing,
        rights.syntheticContentStatus,
        rights.policyVersion,
        rights.audioAssetId,
        rights.audioFingerprint,
      ])
    : "none";
}
