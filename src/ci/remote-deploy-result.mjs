#!/usr/bin/env node

import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { join, resolve } from "node:path";

function fail(message) {
  console.error(`remote-deploy-result: ${message}`);
  process.exit(1);
}

function usage() {
  fail("usage: remote-deploy-result.mjs validate --candidate DIR --result FILE --environment staging|production --minimum-release-sha SHA\n       remote-deploy-result.mjs validate-rollback --result FILE --environment staging|production --release-sha SHA --storage-format H2SEv1 --minimum-release-sha SHA");
}

const command = process.argv[2];
if (!["validate", "validate-rollback"].includes(command)) usage();
const args = {};
for (let i = 3; i < process.argv.length; i += 2) {
  const key = process.argv[i];
  const value = process.argv[i + 1];
  if (!key?.startsWith("--") || !value) usage();
  args[key.slice(2)] = value;
}

function readJson(path, label) {
  try { return JSON.parse(readFileSync(path, "utf8")); }
  catch (error) { fail(`${label} is invalid JSON: ${error.message}`); }
}
function sha256(path) {
  return createHash("sha256").update(readFileSync(path)).digest("hex");
}
function sameImages(left, right) {
  const keys = Object.keys(right ?? {}).sort();
  return left && right && Object.keys(left).sort().join("\0") === keys.join("\0") && keys.every((key) => left[key] === right[key]);
}
function exactKeys(value, expected) {
  return value && typeof value === "object" && !Array.isArray(value) &&
    JSON.stringify(Object.keys(value).sort()) === JSON.stringify([...expected].sort());
}
function digestOnlyImages(value, expectedKeys) {
  return exactKeys(value, expectedKeys) && expectedKeys.every((key) =>
    typeof value[key] === "string" && /^[^\s@]+@sha256:[0-9a-f]{64}$/.test(value[key]));
}
function validateDeploy() {
  if (!args.candidate || !args.result || !["staging", "production"].includes(args.environment) ||
      !/^[0-9a-f]{40}$/.test(args["minimum-release-sha"] ?? "")) usage();
  const candidate = resolve(args.candidate);
  const metadata = readJson(join(candidate, "release-metadata.json"), "release metadata");
  const result = readJson(resolve(args.result), "remote deployment result");
  const checks = ["pre-migration-backup", "migration", "smoke", "e2e", "digest-verification"];
  const expectedKeys = [
    "schemaVersion", "kind", "environment", "result", "candidateArtifact", "commitSha",
    "releaseImagesSha256", "deployBundleSha256", "actualImages", "minimumRollbackReleaseSha", "checks",
  ];
  if (!exactKeys(result, expectedKeys) ||
      result.schemaVersion !== 1 || result.kind !== "hook2stream-remote-deploy-result" ||
      result.environment !== args.environment || result.result !== "success" ||
      result.candidateArtifact !== metadata.artifactName || result.commitSha !== metadata.commitSha ||
      result.minimumRollbackReleaseSha !== args["minimum-release-sha"] ||
      result.releaseImagesSha256 !== sha256(join(candidate, "release-images.env")) ||
      result.deployBundleSha256 !== sha256(join(candidate, "deploy-bundle.tar.gz")) ||
      !sameImages(result.actualImages, metadata.images) ||
      JSON.stringify(result.checks) !== JSON.stringify(checks)) {
    fail("remote result does not match the requested candidate and verified running state");
  }
}

function validateRollback() {
  const shaPattern = /^[0-9a-f]{40}$/;
  if (!args.result || !["staging", "production"].includes(args.environment) ||
      !shaPattern.test(args["release-sha"] ?? "") || args["storage-format"] !== "H2SEv1" ||
      !shaPattern.test(args["minimum-release-sha"] ?? "")) usage();
  const result = readJson(resolve(args.result), "remote rollback result");
  const checks = [
    "target-recorded-success",
    "storage-format-compatible",
    "application-images-only",
    "infrastructure-unchanged",
    "no-migrations",
    "smoke",
    "e2e",
    "digest-verification",
  ];
  const runningImageKeys = [
    "API_IMAGE", "WORKER_IMAGE", "WEB_IMAGE", "POSTGRES_BACKUP_IMAGE", "CADDY_IMAGE",
    "POSTGRES_IMAGE", "PGBOUNCER_IMAGE", "EGRESS_PROXY_IMAGE",
  ];
  if (!exactKeys(result, [
        "schemaVersion", "kind", "environment", "result", "releaseSha", "storageFormat",
        "minimumRollbackReleaseSha", "actualRunningImages", "preservedBootstrapImage", "checks",
      ]) ||
      result.schemaVersion !== 1 || result.kind !== "hook2stream-remote-rollback-result" ||
      result.environment !== args.environment || result.result !== "success" ||
      result.releaseSha !== args["release-sha"] || result.storageFormat !== args["storage-format"] ||
      result.minimumRollbackReleaseSha !== args["minimum-release-sha"] ||
      !digestOnlyImages(result.actualRunningImages, runningImageKeys) ||
      typeof result.preservedBootstrapImage !== "string" ||
      !/^[^\s@]+@sha256:[0-9a-f]{64}$/.test(result.preservedBootstrapImage) ||
      JSON.stringify(result.checks) !== JSON.stringify(checks)) {
    fail("remote rollback result does not match the requested H2SE-compatible release and verified running state");
  }
}

if (command === "validate") validateDeploy();
else validateRollback();
