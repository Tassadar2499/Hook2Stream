import assert from "node:assert/strict";
import test from "node:test";
import {
  createWorkflowCheckpointFormState,
  workflowCheckpointCanonicalKey,
} from "../src/lib/release-workflow-form-state.ts";

const attestation = {
  id: "01910101-0000-7000-8000-000000000001",
  ownsAudioRights: true,
  ownsLyricsRights: false,
  ownsVisualRights: true,
  allowsExternalAiArtwork: true,
  allowsExternalAiProcessing: true,
  syntheticContentStatus: "assisted",
  policyVersion: "mp3-first-test",
  acceptedAt: "2026-07-20T12:00:00Z",
  audioAssetId: "01910101-0000-7000-8000-000000000002",
  audioFingerprint: "c".repeat(64),
  projectVersion: 4,
};
const releaseId = "01910101-0000-7000-8000-000000000003";

test("hydrates confirmation and every rights control from canonical values", () => {
  const state = createWorkflowCheckpointFormState(
    { isInstrumentalConfirmed: true },
    attestation,
  );

  assert.equal(state.instrumentalConfirmed, true);
  assert.equal(state.ownsAudioRights, true);
  assert.equal(state.ownsLyricsRights, false);
  assert.equal(state.ownsVisualRights, true);
  assert.equal(state.allowsExternalAiArtwork, true);
  assert.equal(state.allowsExternalAiProcessing, true);
  assert.equal(state.syntheticContentStatus, "assisted");
});

test("metadata refresh preserves the form instance until canonical checkpoint values change", () => {
  const beforeMetadataSave = workflowCheckpointCanonicalKey(
    { id: releaseId, isInstrumentalConfirmed: true },
    attestation,
  );
  const afterMetadataSave = workflowCheckpointCanonicalKey(
    { id: releaseId, isInstrumentalConfirmed: true },
    { ...attestation, projectVersion: 5 },
  );
  assert.equal(afterMetadataSave, beforeMetadataSave);

  const afterRightsSave = workflowCheckpointCanonicalKey(
    { id: releaseId, isInstrumentalConfirmed: true },
    {
      ...attestation,
      acceptedAt: "2026-07-20T12:01:00Z",
    },
  );
  assert.notEqual(afterRightsSave, afterMetadataSave);

  const afterSetupSave = workflowCheckpointCanonicalKey(
    { id: releaseId, isInstrumentalConfirmed: false },
    attestation,
  );
  assert.notEqual(afterSetupSave, beforeMetadataSave);
});
