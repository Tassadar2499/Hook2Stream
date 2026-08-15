#!/usr/bin/env node

import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { basename, join, resolve } from "node:path";

const SHA_RE = /^[0-9a-f]{40}$/;
const IMAGE_KEYS = ["MINIO_IMAGE", "MINIO_MC_IMAGE", "CADDY_IMAGE"];
const SUCCESSFUL_CHECKS = [
  "policy-verification",
  "quota-verification",
  "versioning-verification",
  "lifecycle-verification",
  "digest-verification",
];
const ENVIRONMENTS = ["storage-staging", "storage-production"];

function fail(message) {
  console.error(`storage-receipt: ${message}`);
  process.exit(1);
}

function usage() {
  console.error(`Usage:
  storage-receipt.mjs validate-remote --candidate DIR --result FILE --environment storage-staging|storage-production
  storage-receipt.mjs create --candidate DIR --remote-result FILE --output FILE
  storage-receipt.mjs validate --candidate DIR --receipt FILE [--repository OWNER/REPO] [--sha SHA] [--run-id ID] [--run-attempt N]`);
  process.exit(2);
}

function parseArgs(values) {
  const result = {};
  for (let index = 0; index < values.length; index += 2) {
    const key = values[index];
    const value = values[index + 1];
    if (!key?.startsWith("--") || value === undefined || value.startsWith("--")) usage();
    result[key.slice(2)] = value;
  }
  return result;
}

function required(args, key) {
  if (!args[key]) fail(`missing --${key}`);
  return args[key];
}

function readJson(path, label) {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (error) {
    fail(`${label} is invalid JSON: ${error.message}`);
  }
}

function sha256(path) {
  return createHash("sha256").update(readFileSync(path)).digest("hex");
}

function hasExactKeys(value, keys) {
  return value && typeof value === "object" && !Array.isArray(value) &&
    Object.keys(value).sort().join("\0") === [...keys].sort().join("\0");
}

function sameImages(left, right) {
  return hasExactKeys(left, IMAGE_KEYS) && hasExactKeys(right, IMAGE_KEYS) &&
    IMAGE_KEYS.every((key) => left[key] === right[key]);
}

function sameChecks(checks) {
  return JSON.stringify(checks) === JSON.stringify(SUCCESSFUL_CHECKS);
}

function candidateIdentity(candidateDirectory) {
  const directory = resolve(candidateDirectory);
  const metadata = readJson(join(directory, "storage-metadata.json"), "storage metadata");
  if (metadata.schemaVersion !== 1 || metadata.kind !== "hook2stream-storage-candidate") {
    fail("unsupported storage candidate metadata");
  }
  return {
    directory,
    metadata,
    hashes: {
      storageMetadataSha256: sha256(join(directory, "storage-metadata.json")),
      storageImagesSha256: sha256(join(directory, "storage-images.env")),
      storageBundleSha256: sha256(join(directory, "storage-bundle.tar.gz")),
      checksumsSha256: sha256(join(directory, "SHA256SUMS")),
    },
  };
}

function validateRemote(candidateDirectory, resultPath, environment) {
  if (!ENVIRONMENTS.includes(environment)) fail("invalid storage environment");
  const { metadata, hashes } = candidateIdentity(candidateDirectory);
  const result = readJson(resolve(resultPath), "remote storage result");
  const resultKeys = [
    "schemaVersion", "kind", "environment", "result", "candidateArtifact", "commitSha",
    "storageImagesSha256", "storageBundleSha256", "actualImages", "checks",
  ];
  if (!hasExactKeys(result, resultKeys)) fail("remote storage result contains missing or unknown fields");
  if (result.schemaVersion !== 1 || result.kind !== "hook2stream-storage-remote-deploy-result" ||
      result.environment !== environment || result.result !== "success" ||
      result.candidateArtifact !== metadata.artifactName || result.commitSha !== metadata.commitSha ||
      result.storageImagesSha256 !== hashes.storageImagesSha256 ||
      result.storageBundleSha256 !== hashes.storageBundleSha256 ||
      !sameImages(result.actualImages, metadata.images) || !sameChecks(result.checks)) {
    fail("remote storage result does not bind the required verified deployment state");
  }
  return result;
}

function validateReceipt(args) {
  const { metadata, hashes } = candidateIdentity(required(args, "candidate"));
  const receipt = readJson(resolve(required(args, "receipt")), "storage staging receipt");
  const receiptKeys = [
    "schemaVersion", "kind", "environment", "result", "repository", "commitSha", "ciRunId",
    "ciRunAttempt", "candidateArtifact", "deployedAt", "checks", "hashes", "remoteResult",
  ];
  if (!hasExactKeys(receipt, receiptKeys)) fail("storage receipt contains missing or unknown fields");
  if (receipt.schemaVersion !== 1 || receipt.kind !== "hook2stream-storage-staging-receipt" ||
      receipt.environment !== "storage-staging" || receipt.result !== "success") {
    fail("receipt is not a successful storage staging deployment");
  }
  if (receipt.repository !== metadata.repository || receipt.commitSha !== metadata.commitSha ||
      receipt.ciRunId !== metadata.ciRunId || receipt.ciRunAttempt !== metadata.ciRunAttempt ||
      receipt.candidateArtifact !== metadata.artifactName) {
    fail("storage receipt release identity does not match the candidate");
  }
  if (!hasExactKeys(receipt.hashes, Object.keys(hashes)) ||
      JSON.stringify(receipt.hashes) !== JSON.stringify(hashes)) {
    fail("storage receipt hashes do not match the candidate");
  }
  if (!sameChecks(receipt.checks)) fail("storage receipt does not contain all required checks");
  const remoteResult = receipt.remoteResult;
  const remoteKeys = [
    "schemaVersion", "kind", "environment", "result", "candidateArtifact", "commitSha",
    "storageImagesSha256", "storageBundleSha256", "actualImages", "checks",
  ];
  if (!hasExactKeys(remoteResult, remoteKeys) ||
      remoteResult.schemaVersion !== 1 || remoteResult.kind !== "hook2stream-storage-remote-deploy-result" ||
      remoteResult.environment !== "storage-staging" || remoteResult.result !== "success" ||
      remoteResult.candidateArtifact !== metadata.artifactName || remoteResult.commitSha !== metadata.commitSha ||
      remoteResult.storageImagesSha256 !== hashes.storageImagesSha256 ||
      remoteResult.storageBundleSha256 !== hashes.storageBundleSha256 ||
      !sameImages(remoteResult.actualImages, metadata.images) || !sameChecks(remoteResult.checks)) {
    fail("storage receipt does not bind the verified remote staging state");
  }
  if (typeof receipt.deployedAt !== "string" || Number.isNaN(Date.parse(receipt.deployedAt))) {
    fail("storage receipt deployedAt is invalid");
  }
  if (args.repository && receipt.repository !== args.repository) fail("storage receipt repository does not match --repository");
  if (args.sha && (!SHA_RE.test(args.sha) || receipt.commitSha !== args.sha)) fail("storage receipt commit does not match --sha");
  if (args["run-id"] && String(receipt.ciRunId) !== args["run-id"]) fail("storage receipt run does not match --run-id");
  if (args["run-attempt"] && String(receipt.ciRunAttempt) !== args["run-attempt"]) {
    fail("storage receipt attempt does not match --run-attempt");
  }
  return receipt;
}

function createReceipt(args) {
  const candidate = required(args, "candidate");
  const output = resolve(required(args, "output"));
  const { metadata, hashes } = candidateIdentity(candidate);
  const remoteResult = validateRemote(candidate, required(args, "remote-result"), "storage-staging");
  const receipt = {
    schemaVersion: 1,
    kind: "hook2stream-storage-staging-receipt",
    environment: "storage-staging",
    result: "success",
    repository: metadata.repository,
    commitSha: metadata.commitSha,
    ciRunId: metadata.ciRunId,
    ciRunAttempt: metadata.ciRunAttempt,
    candidateArtifact: metadata.artifactName,
    deployedAt: new Date().toISOString(),
    checks: SUCCESSFUL_CHECKS,
    hashes,
    remoteResult,
  };
  writeFileSync(output, `${JSON.stringify(receipt, null, 2)}\n`, { mode: 0o644 });
  validateReceipt({ candidate, receipt: output });
  console.log(`created ${basename(output)}`);
}

const [command, ...rest] = process.argv.slice(2);
const args = parseArgs(rest);
if (command === "validate-remote") {
  validateRemote(required(args, "candidate"), required(args, "result"), required(args, "environment"));
} else if (command === "create") {
  createReceipt(args);
} else if (command === "validate") {
  validateReceipt(args);
} else {
  usage();
}
