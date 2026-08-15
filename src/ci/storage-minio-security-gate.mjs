#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

function fail(message) {
  throw new Error(`storage MinIO security gate: ${message}`);
}

function parseArgs(argv) {
  const values = new Map();
  for (let index = 0; index < argv.length; index += 2) {
    const key = argv[index];
    const value = argv[index + 1];
    if (!key?.startsWith("--") || value === undefined || value.startsWith("--")) {
      fail("expected --release-manifest <path> --dockerfile <path> --policy <path>");
    }
    if (values.has(key)) fail(`duplicate argument ${key}`);
    values.set(key, value);
  }
  for (const key of ["--release-manifest", "--dockerfile", "--policy"]) {
    if (!values.has(key)) fail(`missing ${key}`);
  }
  if (values.size !== 3) fail("unknown argument");
  return {
    releaseManifest: values.get("--release-manifest"),
    dockerfile: values.get("--dockerfile"),
    policy: values.get("--policy"),
  };
}

function readRegularFile(file, label) {
  const resolved = path.resolve(file);
  const metadata = fs.lstatSync(resolved);
  if (!metadata.isFile() || metadata.isSymbolicLink()) {
    fail(`${label} must be a regular non-symlink file`);
  }
  return fs.readFileSync(resolved, "utf8");
}

function readJsonFile(file, label) {
  try {
    return JSON.parse(readRegularFile(file, label));
  } catch (error) {
    if (error.message.startsWith("storage MinIO security gate:")) throw error;
    fail(`${label} is not valid JSON: ${error.message}`);
  }
}

function exactKeys(value, expected, label) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    fail(`${label} must be an object`);
  }
  const actual = Object.keys(value).sort();
  const wanted = [...expected].sort();
  if (JSON.stringify(actual) !== JSON.stringify(wanted)) {
    fail(`${label} must contain exactly ${wanted.join(", ")}`);
  }
}

function validateRelease(value) {
  return /^RELEASE\.\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}Z$/.test(value);
}

function dockerfilePin(file) {
  const source = readRegularFile(file, "MinIO Dockerfile");
  const releaseMatches = [...source.matchAll(/^ARG MINIO_RELEASE=(\S+)$/gm)];
  const commitMatches = [...source.matchAll(/^ARG MINIO_COMMIT=([0-9a-f]{40})$/gm)];
  const goImageMatches = [...source.matchAll(/^ARG GO_IMAGE=(\S+)$/gm)];
  const alpineImageMatches = [...source.matchAll(/^ARG ALPINE_IMAGE=(\S+)$/gm)];
  if (releaseMatches.length !== 1 || commitMatches.length !== 1 ||
      !validateRelease(releaseMatches[0][1])) {
    fail("MinIO Dockerfile must contain one exact release and one 40-hex source commit pin");
  }
  if (goImageMatches.length !== 1 || alpineImageMatches.length !== 1 ||
      !/^docker\.io\/library\/golang:[^@\s]+@sha256:[0-9a-f]{64}$/.test(goImageMatches[0][1]) ||
      !/^docker\.io\/library\/alpine:[^@\s]+@sha256:[0-9a-f]{64}$/.test(alpineImageMatches[0][1])) {
    fail("MinIO Dockerfile builder and runtime images must be exact Docker Official Image digests");
  }
  const releaseLabelMatches = source.match(
    /^(?:LABEL[ \t]+|[ \t]+)com\.hook2stream\.minio\.source-release="\$\{MINIO_RELEASE\}"[ \t]*\\?$/gm) ?? [];
  const commitLabelMatches = source.match(
    /^(?:LABEL[ \t]+|[ \t]+)com\.hook2stream\.minio\.source-commit="\$\{MINIO_COMMIT\}"[ \t]*\\?$/gm) ?? [];
  if (releaseLabelMatches.length !== 1 || commitLabelMatches.length !== 1) {
    fail("MinIO Dockerfile must bind its source pins to the immutable Hook2Stream labels");
  }
  return { release: releaseMatches[0][1], commit: commitMatches[0][1] };
}

function validateAdvisories(advisories) {
  if (!Array.isArray(advisories) || advisories.length === 0) {
    fail("security policy must retain at least one blocking advisory");
  }
  const seen = new Set();
  for (const [index, advisory] of advisories.entries()) {
    exactKeys(advisory, ["id", "severity", "url", "patchedOssRelease"],
      `blocking advisory ${index}`);
    if (!/^CVE-\d{4}-\d{4,}$/.test(advisory.id) ||
        !["high", "critical"].includes(advisory.severity) ||
        !/^https:\/\/github\.com\/advisories\/GHSA-[a-z0-9-]+$/.test(advisory.url) ||
        advisory.patchedOssRelease !== null || seen.has(advisory.id)) {
      fail(`blocking advisory ${index} is invalid or duplicated`);
    }
    seen.add(advisory.id);
  }
}

function validatePolicy(value) {
  exactKeys(value,
    ["schemaVersion", "kind", "reviewedAt", "approvedSourceReleases", "blockingAdvisories"],
    "security policy");
  if (value.schemaVersion !== 1 || value.kind !== "hook2stream-minio-security-policy" ||
      !/^\d{4}-\d{2}-\d{2}$/.test(value.reviewedAt) ||
      !Array.isArray(value.approvedSourceReleases)) {
    fail("security policy schema, kind, review date, or approval set is invalid");
  }
  validateAdvisories(value.blockingAdvisories);
  const seen = new Set();
  for (const [index, approval] of value.approvedSourceReleases.entries()) {
    exactKeys(approval, ["release", "commit", "source", "reviewedAt", "securitySequence"],
      `approved source release ${index}`);
    const identity = `${approval.release}:${approval.commit}`;
    if (!validateRelease(approval.release) || !/^[0-9a-f]{40}$/.test(approval.commit) ||
        approval.source !== "https://github.com/minio/minio" ||
        !/^\d{4}-\d{2}-\d{2}$/.test(approval.reviewedAt) || seen.has(identity)) {
      fail(`approved source release ${index} is invalid or duplicated`);
    }
    if (!Number.isSafeInteger(approval.securitySequence) || approval.securitySequence < 1 ||
        value.approvedSourceReleases.some((entry, otherIndex) =>
          otherIndex < index && entry.securitySequence === approval.securitySequence)) {
      fail(`approved source release ${index} has an invalid or duplicated securitySequence`);
    }
    seen.add(identity);
  }
}

export function enforceMinioSecurityGate(releaseManifest, dockerfile, policyFile) {
  const manifest = readJsonFile(releaseManifest, "release manifest");
  const policy = readJsonFile(policyFile, "security policy");
  const pin = dockerfilePin(dockerfile);
  exactKeys(manifest, ["schemaVersion", "kind", "protocolVersion", "storageFormatVersion",
    "objectFormat", "minioRelease", "minioSourceCommit"], "release manifest");
  if (manifest.schemaVersion !== 1 || manifest.kind !== "hook2stream-storage-runtime" ||
      manifest.protocolVersion !== 1 || manifest.storageFormatVersion !== 1 ||
      manifest.objectFormat !== "H2SEv1" || !validateRelease(manifest.minioRelease) ||
      !/^[0-9a-f]{40}$/.test(manifest.minioSourceCommit)) {
    fail("release manifest schema or MinIO source identity is invalid");
  }
  if (manifest.minioRelease !== pin.release || manifest.minioSourceCommit !== pin.commit) {
    fail("release manifest and Dockerfile MinIO source pins differ");
  }
  validatePolicy(policy);
  const approved = policy.approvedSourceReleases.some((entry) =>
    entry.release === pin.release && entry.commit === pin.commit);
  if (!approved) {
    const ids = policy.blockingAdvisories.map((entry) => entry.id).join(", ");
    fail(`${pin.release}@${pin.commit} has no reviewed High/Critical-clean source approval; blockers: ${ids}`);
  }
  return `storage MinIO security gate: approved exact source ${pin.release}@${pin.commit}; digest scans remain mandatory`;
}

const invokedAsScript = process.argv[1] &&
  import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
if (invokedAsScript) {
  try {
    const args = parseArgs(process.argv.slice(2));
    process.stdout.write(`${enforceMinioSecurityGate(args.releaseManifest, args.dockerfile, args.policy)}\n`);
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  }
}
