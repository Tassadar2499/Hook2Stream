import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { enforceMinioSecurityGate } from "../storage-minio-security-gate.mjs";

const testDir = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(testDir, "../../..");
const manifest = path.join(repositoryRoot, "src/deploy/storage/storage-release.json");
const dockerfile = path.join(repositoryRoot, "src/deploy/minio/Dockerfile");
const policy = path.join(repositoryRoot, "src/deploy/storage/minio-security-policy.json");
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), "hook2stream-minio-security-gate-"));
const pin = {
  release: "RELEASE.2025-10-15T17-29-55Z",
  commit: "9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a",
};
const pinnedGoImage = "docker.io/library/golang:1.25.12-alpine3.23@sha256:f128118f1f3a7f38949c57f03d29d13e4afdb06b86f840f8f43e8031e6c1a73a";
const pinnedAlpineImage = "docker.io/library/alpine:3.22.5@sha256:7c8cb692ae09657cbc4a3f3cbd0e8d5a2690ba38386aaaf252dbb060bf5eb2e6";
const sourceIdentityLabels =
  'LABEL com.hook2stream.minio.source-release="${MINIO_RELEASE}" \\\n' +
  '      com.hook2stream.minio.source-commit="${MINIO_COMMIT}"\n';

try {
  const dockerfileSource = fs.readFileSync(dockerfile, "utf8");
  assert.match(dockerfileSource, new RegExp(`^ARG GO_IMAGE=${pinnedGoImage.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`, "m"));
  assert.match(dockerfileSource, new RegExp(`^ARG ALPINE_IMAGE=${pinnedAlpineImage.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`, "m"));

  let blockedMessage = "";
  assert.throws(() => enforceMinioSecurityGate(manifest, dockerfile, policy), (error) => {
    blockedMessage = error.message;
    return true;
  }, "unapproved final OSS MinIO release was accepted");
  for (const cve of ["CVE-2026-34204", "CVE-2026-39414", "CVE-2026-40344", "CVE-2026-41145"]) {
    assert.match(blockedMessage, new RegExp(cve), `${cve} was absent from the rejection`);
  }

  const futureManifest = path.join(scratch, "future-release.json");
  const futureDockerfile = path.join(scratch, "Dockerfile.future");
  fs.writeFileSync(futureManifest, JSON.stringify({
    schemaVersion: 1,
    kind: "hook2stream-storage-runtime",
    protocolVersion: 1,
    storageFormatVersion: 1,
    objectFormat: "H2SEv1",
    minioRelease: "RELEASE.2027-01-02T03-04-05Z",
    minioSourceCommit: "1111111111111111111111111111111111111111",
  }));
  fs.writeFileSync(futureDockerfile,
    `ARG GO_IMAGE=${pinnedGoImage}\nARG ALPINE_IMAGE=${pinnedAlpineImage}\n` +
    "ARG MINIO_RELEASE=RELEASE.2027-01-02T03-04-05Z\nARG MINIO_COMMIT=1111111111111111111111111111111111111111\n" +
    sourceIdentityLabels);
  assert.throws(() => enforceMinioSecurityGate(futureManifest, futureDockerfile, policy),
    /has no reviewed High\/Critical-clean source approval/,
    "unknown future release was accepted by an empty approval set");

  const approvedPolicy = path.join(scratch, "approved-policy.json");
  const policyFixture = JSON.parse(fs.readFileSync(policy, "utf8"));
  policyFixture.approvedSourceReleases = [{
    ...pin,
    source: "https://github.com/minio/minio",
    reviewedAt: "2026-08-15",
    securitySequence: 1,
  }];
  fs.writeFileSync(approvedPolicy, JSON.stringify(policyFixture));
  assert.match(enforceMinioSecurityGate(manifest, dockerfile, approvedPolicy),
    /approved exact source.*digest scans remain mandatory/);

  const mismatchedManifest = path.join(scratch, "mismatched-release.json");
  fs.writeFileSync(mismatchedManifest, JSON.stringify({
    ...JSON.parse(fs.readFileSync(manifest, "utf8")),
    minioRelease: "RELEASE.2024-01-01T00-00-00Z",
  }));
  assert.throws(() => enforceMinioSecurityGate(mismatchedManifest, dockerfile, approvedPolicy),
    /source pins differ/, "manifest/Dockerfile pin mismatch was accepted");

  const mutableBaseDockerfile = path.join(scratch, "Dockerfile.mutable-base");
  fs.writeFileSync(mutableBaseDockerfile,
    "ARG GO_IMAGE=golang:1.25.12-alpine3.23\nARG ALPINE_IMAGE=alpine:3.22.5\n" +
    `ARG MINIO_RELEASE=${pin.release}\nARG MINIO_COMMIT=${pin.commit}\n`);
  assert.throws(() => enforceMinioSecurityGate(manifest, mutableBaseDockerfile, approvedPolicy),
    /builder and runtime images must be exact Docker Official Image digests/,
    "mutable MinIO builder/runtime base tags were accepted");

  const mutableMetadataDockerfile = path.join(scratch, "Dockerfile.mutable-metadata-labels");
  fs.writeFileSync(mutableMetadataDockerfile,
    `ARG GO_IMAGE=${pinnedGoImage}\nARG ALPINE_IMAGE=${pinnedAlpineImage}\n` +
    `ARG MINIO_RELEASE=${pin.release}\nARG MINIO_COMMIT=${pin.commit}\n` +
    'LABEL org.opencontainers.image.version="${MINIO_RELEASE}" \\\n' +
    '      org.opencontainers.image.revision="${MINIO_COMMIT}"\n');
  assert.throws(() => enforceMinioSecurityGate(manifest, mutableMetadataDockerfile, approvedPolicy),
    /immutable Hook2Stream labels/,
    "OCI source labels that build-push metadata can override were accepted");

  const malformedPolicy = path.join(scratch, "malformed-policy.json");
  fs.writeFileSync(malformedPolicy, JSON.stringify({ ...policyFixture, bypass: true }));
  assert.throws(() => enforceMinioSecurityGate(manifest, dockerfile, malformedPolicy),
    /must contain exactly/, "unknown security-policy fields were accepted");

  process.stdout.write("storage MinIO security gate contracts passed\n");
} finally {
  fs.rmSync(scratch, { recursive: true, force: true });
}
