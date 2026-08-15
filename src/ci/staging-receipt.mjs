#!/usr/bin/env node

import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { basename, join, resolve } from "node:path";

const SHA_RE = /^[0-9a-f]{40}$/;
const SUCCESSFUL_CHECKS = ["pre-migration-backup", "migration", "smoke", "e2e", "digest-verification"];

function sameImages(left, right) {
  const keys = Object.keys(right ?? {}).sort();
  return left && right && Object.keys(left).sort().join("\0") === keys.join("\0") && keys.every((key) => left[key] === right[key]);
}

function fail(message) {
  console.error(`staging-receipt: ${message}`);
  process.exit(1);
}

function usage() {
  console.error(`Usage:
  staging-receipt.mjs create --candidate DIR --remote-result FILE --output FILE
  staging-receipt.mjs validate --candidate DIR --receipt FILE [--repository OWNER/REPO] [--sha SHA] [--run-id ID] [--run-attempt N]`);
  process.exit(2);
}

function parseArgs(values) {
  const result = {};
  for (let i = 0; i < values.length; i += 2) {
    if (!values[i]?.startsWith("--") || values[i + 1] === undefined || values[i + 1].startsWith("--")) usage();
    result[values[i].slice(2)] = values[i + 1];
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

function candidateIdentity(candidateDir) {
  const directory = resolve(candidateDir);
  const metadata = readJson(join(directory, "release-metadata.json"), "release metadata");
  return {
    metadata,
    hashes: {
      releaseMetadataSha256: sha256(join(directory, "release-metadata.json")),
      releaseImagesSha256: sha256(join(directory, "release-images.env")),
      deployBundleSha256: sha256(join(directory, "deploy-bundle.tar.gz")),
      checksumsSha256: sha256(join(directory, "SHA256SUMS")),
    },
  };
}

function validateReceipt(args) {
  const { metadata, hashes } = candidateIdentity(required(args, "candidate"));
  const receipt = readJson(resolve(required(args, "receipt")), "staging receipt");
  if (receipt.schemaVersion !== 1 || receipt.kind !== "hook2stream-staging-receipt") fail("unsupported staging receipt schema");
  if (receipt.environment !== "staging" || receipt.result !== "success") fail("receipt is not a successful staging deployment");
  if (receipt.repository !== metadata.repository || receipt.commitSha !== metadata.commitSha ||
      receipt.ciRunId !== metadata.ciRunId || receipt.ciRunAttempt !== metadata.ciRunAttempt ||
      receipt.candidateArtifact !== metadata.artifactName) {
    fail("receipt release identity does not match the candidate");
  }
  if (JSON.stringify(receipt.hashes) !== JSON.stringify(hashes)) fail("receipt hashes do not match the candidate");
  if (JSON.stringify(receipt.checks) !== JSON.stringify(SUCCESSFUL_CHECKS)) fail("receipt does not contain the required successful checks");
  if (receipt.remoteResult?.schemaVersion !== 1 || receipt.remoteResult?.kind !== "hook2stream-remote-deploy-result" ||
      receipt.remoteResult?.environment !== "staging" || receipt.remoteResult?.result !== "success" ||
      receipt.remoteResult?.candidateArtifact !== metadata.artifactName || receipt.remoteResult?.commitSha !== metadata.commitSha ||
      receipt.remoteResult?.releaseImagesSha256 !== hashes.releaseImagesSha256 ||
      receipt.remoteResult?.deployBundleSha256 !== hashes.deployBundleSha256 ||
      !sameImages(receipt.remoteResult?.actualImages, metadata.images) ||
      JSON.stringify(receipt.remoteResult?.checks) !== JSON.stringify(SUCCESSFUL_CHECKS)) {
    fail("receipt does not bind the verified remote staging state");
  }
  if (Number.isNaN(Date.parse(receipt.deployedAt ?? ""))) fail("receipt deployedAt is invalid");
  if (args.repository && receipt.repository !== args.repository) fail("receipt repository does not match --repository");
  if (args.sha && (!SHA_RE.test(args.sha) || receipt.commitSha !== args.sha)) fail("receipt commit does not match --sha");
  if (args["run-id"] && String(receipt.ciRunId) !== args["run-id"]) fail("receipt run does not match --run-id");
  if (args["run-attempt"] && String(receipt.ciRunAttempt) !== args["run-attempt"]) fail("receipt attempt does not match --run-attempt");
  return receipt;
}

function createReceipt(args) {
  const candidate = required(args, "candidate");
  const output = resolve(required(args, "output"));
  const { metadata, hashes } = candidateIdentity(candidate);
  const remoteResult = readJson(resolve(required(args, "remote-result")), "remote deployment result");
  const receipt = {
    schemaVersion: 1,
    kind: "hook2stream-staging-receipt",
    environment: "staging",
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
if (command === "create") createReceipt(args);
else if (command === "validate") validateReceipt(args);
else usage();
