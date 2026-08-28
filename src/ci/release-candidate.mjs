#!/usr/bin/env node

import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { spawnSync } from "node:child_process";

const IMAGE_KEYS = [
  "API_IMAGE",
  "WORKER_IMAGE",
  "BOOTSTRAPPER_IMAGE",
  "WEB_IMAGE",
  "POSTGRES_BACKUP_IMAGE",
  "CADDY_IMAGE",
  "POSTGRES_IMAGE",
  "PGBOUNCER_IMAGE",
  "EGRESS_PROXY_IMAGE",
];
const CANDIDATE_FILES = [
  "release-metadata.json",
  "release-images.env",
  "deploy-bundle.tar.gz",
  "SHA256SUMS",
];
const LOCAL_ONLY_DEPLOY_PATHS = [
  "deploy/Caddyfile.minio",
  "deploy/compose.minio.yaml",
  "deploy/minio",
  "deploy/storage",
  "deploy/scripts/validate-deployment.sh",
  "deploy/tests/caddy-minio-contract.test.sh",
  "deploy/tests/minio-overlay-contract.test.sh",
  "deploy/tests/minio-release-integration.test.sh",
];
const SHA_RE = /^[0-9a-f]{40}$/;
const DIGEST_IMAGE_RE = /^(?:[a-z0-9]+(?:[._-][a-z0-9]+)*(?::[0-9]+)?\/)?[a-z0-9]+(?:[._/-][a-z0-9]+)*@sha256:[0-9a-f]{64}$/;

function fail(message) {
  console.error(`release-candidate: ${message}`);
  process.exit(1);
}

function usage() {
  console.error(`Usage:
  release-candidate.mjs create --output DIR --fragments DIR --deploy-dir DIR --repository OWNER/REPO --sha SHA --run-id ID --run-attempt N
  release-candidate.mjs validate --candidate DIR [--repository OWNER/REPO] [--sha SHA] [--run-id ID] [--run-attempt N]`);
  process.exit(2);
}

function parseArgs(values) {
  const result = {};
  for (let i = 0; i < values.length; i += 2) {
    const key = values[i];
    const value = values[i + 1];
    if (!key?.startsWith("--") || value === undefined || value.startsWith("--")) usage();
    result[key.slice(2)] = value;
  }
  return result;
}

function requireArg(args, name) {
  const value = args[name];
  if (!value) fail(`missing --${name}`);
  return value;
}

function sha256(path) {
  return createHash("sha256").update(readFileSync(path)).digest("hex");
}

function sameRecord(left, right, keys) {
  return left && right && Object.keys(left).length === keys.length && Object.keys(right).length === keys.length &&
    keys.every((key) => left[key] === right[key]);
}

function run(command, args, options = {}) {
  const result = spawnSync(command, args, { encoding: "utf8", ...options });
  if (result.status !== 0) {
    if (result.error && result.status === null) {
      fail(`could not run ${command}: ${result.error.message}`);
    }
    const detail = (result.stderr || result.stdout || "").trim();
    fail(`${command} failed${detail ? `: ${detail}` : ""}`);
  }
  return result.stdout;
}

function validateRepository(repository) {
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) {
    fail("repository must be OWNER/REPO");
  }
}

function validatePositiveInteger(value, label) {
  if (!/^[1-9][0-9]*$/.test(String(value))) fail(`${label} must be a positive integer`);
}

function parseEnvironment(path) {
  const values = new Map();
  const lines = readFileSync(path, "utf8").split(/\r?\n/);
  for (const [index, rawLine] of lines.entries()) {
    if (rawLine === "") continue;
    if (rawLine.startsWith("#") || /^\s/.test(rawLine)) {
      fail(`${basename(path)}:${index + 1} comments and whitespace are not allowed`);
    }
    const match = rawLine.match(/^([A-Z][A-Z0-9_]*)=(\S+)$/);
    if (!match) fail(`${basename(path)}:${index + 1} is not a strict KEY=value record`);
    if (values.has(match[1])) fail(`${basename(path)} contains duplicate ${match[1]}`);
    values.set(match[1], match[2]);
  }
  return values;
}

function validateImages(path, expectedSha) {
  const values = parseEnvironment(path);
  const expectedKeys = ["RELEASE_VERSION", ...IMAGE_KEYS];
  if (values.size !== expectedKeys.length || expectedKeys.some((key) => !values.has(key))) {
    fail(`release-images.env must contain exactly ${expectedKeys.join(", ")}`);
  }
  if (!SHA_RE.test(values.get("RELEASE_VERSION"))) fail("RELEASE_VERSION must be a lowercase 40-character commit SHA");
  if (expectedSha && values.get("RELEASE_VERSION") !== expectedSha) fail("RELEASE_VERSION does not match the expected commit");
  for (const key of IMAGE_KEYS) {
    if (!DIGEST_IMAGE_RE.test(values.get(key))) fail(`${key} is not a digest-only image reference`);
  }
  return Object.fromEntries(IMAGE_KEYS.map((key) => [key, values.get(key)]));
}

function validateImageRepositories(images, owner) {
  const escapedOwner = owner.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const policies = {
    API_IMAGE: new RegExp(`^ghcr\\.io/${escapedOwner}/hook2stream-api@sha256:`),
    WORKER_IMAGE: new RegExp(`^ghcr\\.io/${escapedOwner}/hook2stream-worker@sha256:`),
    BOOTSTRAPPER_IMAGE: new RegExp(`^ghcr\\.io/${escapedOwner}/hook2stream-bootstrapper@sha256:`),
    WEB_IMAGE: new RegExp(`^ghcr\\.io/${escapedOwner}/hook2stream-web@sha256:`),
    POSTGRES_BACKUP_IMAGE: new RegExp(`^ghcr\\.io/${escapedOwner}/hook2stream-postgres-backup@sha256:`),
    CADDY_IMAGE: /^(?:docker\.io\/library\/)?caddy@sha256:/,
    POSTGRES_IMAGE: new RegExp(`^ghcr\\.io/${escapedOwner}/hook2stream-postgres@sha256:`),
    PGBOUNCER_IMAGE: /^(?:docker\.io\/)?edoburu\/pgbouncer@sha256:/,
    EGRESS_PROXY_IMAGE: /^(?:docker\.io\/)?ubuntu\/squid@sha256:/,
  };
  for (const key of IMAGE_KEYS) {
    if (!policies[key].test(images[key])) fail(`${key} repository is outside the release allowlist`);
  }
}

function validateBundle(path) {
  const listing = run("tar", ["-tzf", path]);
  const entries = listing.split("\n").filter(Boolean);
  if (entries.length === 0) fail("deploy bundle is empty");
  for (const entry of entries) {
    if (entry.includes("\\") || entry.startsWith("/") || /(^|\/)\.\.?(\/|$)/.test(entry)) {
      fail(`unsafe deploy bundle path: ${JSON.stringify(entry)}`);
    }
    if (entry !== "deploy" && !entry.startsWith("deploy/")) {
      fail(`deploy bundle entry is outside deploy/: ${JSON.stringify(entry)}`);
    }
    const normalizedEntry = entry.replace(/\/$/, "");
    if (LOCAL_ONLY_DEPLOY_PATHS.some((localPath) => normalizedEntry === localPath || normalizedEntry.startsWith(`${localPath}/`))) {
      fail(`deploy bundle contains local-only MinIO/storage-plane or CI validation path: ${JSON.stringify(entry)}`);
    }
    if (/[[\]\x00-\x1f\x7f]/.test(entry)) fail("deploy bundle path contains control characters");
  }
  const verbose = run("tar", ["-tvzf", path]);
  for (const line of verbose.split("\n").filter(Boolean)) {
    if (["l", "h", "b", "c", "p"].includes(line[0])) {
      fail("deploy bundle may contain only regular files and directories");
    }
  }
}

function parseChecksums(path) {
  const checksums = new Map();
  for (const [index, line] of readFileSync(path, "utf8").trimEnd().split(/\r?\n/).entries()) {
    const match = line.match(/^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]*)$/);
    if (!match) fail(`SHA256SUMS:${index + 1} is invalid`);
    if (checksums.has(match[2])) fail(`SHA256SUMS contains duplicate ${match[2]}`);
    checksums.set(match[2], match[1]);
  }
  return checksums;
}

function validateCandidate(candidateDir, expected = {}) {
  const directory = resolve(candidateDir);
  if (!existsSync(directory) || !statSync(directory).isDirectory()) fail("candidate directory does not exist");
  const actualFiles = readdirSync(directory).sort();
  const expectedFiles = [...CANDIDATE_FILES].sort();
  if (JSON.stringify(actualFiles) !== JSON.stringify(expectedFiles)) {
    fail(`candidate must contain exactly ${CANDIDATE_FILES.join(", ")}`);
  }

  const checksums = parseChecksums(join(directory, "SHA256SUMS"));
  const checksummedFiles = CANDIDATE_FILES.filter((name) => name !== "SHA256SUMS");
  if (checksums.size !== checksummedFiles.length || checksummedFiles.some((name) => !checksums.has(name))) {
    fail(`SHA256SUMS must cover exactly ${checksummedFiles.join(", ")}`);
  }
  for (const name of checksummedFiles) {
    if (sha256(join(directory, name)) !== checksums.get(name)) fail(`${name} checksum mismatch`);
  }

  let metadata;
  try {
    metadata = JSON.parse(readFileSync(join(directory, "release-metadata.json"), "utf8"));
  } catch (error) {
    fail(`release-metadata.json is invalid JSON: ${error.message}`);
  }
  if (metadata.schemaVersion !== 1 || metadata.kind !== "hook2stream-release-candidate") fail("unsupported release metadata schema");
  if (metadata.protocolVersion !== "forced-command-v1") fail("unsupported deployment protocol");
  if (metadata.sourceRef !== "refs/heads/main" || metadata.sourceEvent !== "push") fail("candidate was not produced by a main push");
  if (metadata.workflow !== ".github/workflows/ci.yml" || metadata.workflowName !== "CI") fail("candidate workflow identity is invalid");
  validateRepository(metadata.repository ?? "");
  if (!SHA_RE.test(metadata.commitSha ?? "")) fail("metadata commitSha is invalid");
  validatePositiveInteger(metadata.ciRunId, "metadata ciRunId");
  validatePositiveInteger(metadata.ciRunAttempt, "metadata ciRunAttempt");
  if (!/^release-candidate-[0-9a-f]{40}-[1-9][0-9]*-[1-9][0-9]*$/.test(metadata.artifactName ?? "")) {
    fail("metadata artifactName is invalid");
  }
  const canonicalName = `release-candidate-${metadata.commitSha}-${metadata.ciRunId}-${metadata.ciRunAttempt}`;
  if (metadata.artifactName !== canonicalName) fail("metadata artifactName does not match its release identity");
  if (Number.isNaN(Date.parse(metadata.createdAt ?? ""))) fail("metadata createdAt is invalid");

  const images = validateImages(join(directory, "release-images.env"), metadata.commitSha);
  validateImageRepositories(images, metadata.repository.split("/", 1)[0].toLowerCase());
  if (!sameRecord(metadata.images, images, IMAGE_KEYS)) fail("metadata images do not match release-images.env");
  if (metadata.deployBundle?.file !== "deploy-bundle.tar.gz" || metadata.deployBundle?.sha256 !== checksums.get("deploy-bundle.tar.gz")) {
    fail("metadata deployBundle does not match the candidate");
  }
  validateBundle(join(directory, "deploy-bundle.tar.gz"));

  if (expected.repository && metadata.repository !== expected.repository) fail("candidate repository does not match --repository");
  if (expected.sha && metadata.commitSha !== expected.sha) fail("candidate commit does not match --sha");
  if (expected["run-id"] && String(metadata.ciRunId) !== expected["run-id"]) fail("candidate run does not match --run-id");
  if (expected["run-attempt"] && String(metadata.ciRunAttempt) !== expected["run-attempt"]) fail("candidate attempt does not match --run-attempt");
  return metadata;
}

function create(args) {
  const output = resolve(requireArg(args, "output"));
  const fragments = resolve(requireArg(args, "fragments"));
  const deployDir = resolve(requireArg(args, "deploy-dir"));
  const repository = requireArg(args, "repository");
  const commitSha = requireArg(args, "sha");
  const ciRunId = requireArg(args, "run-id");
  const ciRunAttempt = requireArg(args, "run-attempt");
  validateRepository(repository);
  if (!SHA_RE.test(commitSha)) fail("--sha must be a lowercase 40-character commit SHA");
  validatePositiveInteger(ciRunId, "--run-id");
  validatePositiveInteger(ciRunAttempt, "--run-attempt");
  if (!existsSync(deployDir) || !statSync(deployDir).isDirectory() || basename(deployDir) !== "deploy") {
    fail("--deploy-dir must name the deploy directory");
  }
  if (existsSync(output) && readdirSync(output).length !== 0) fail("--output must be empty");
  mkdirSync(output, { recursive: true, mode: 0o755 });

  const fragmentValues = new Map();
  for (const file of readdirSync(fragments).sort()) {
    const path = join(fragments, file);
    if (!statSync(path).isFile()) continue;
    for (const [key, value] of parseEnvironment(path)) {
      if (fragmentValues.has(key)) fail(`duplicate ${key} across image fragments`);
      fragmentValues.set(key, value);
    }
  }
  if (fragmentValues.size !== IMAGE_KEYS.length || IMAGE_KEYS.some((key) => !fragmentValues.has(key))) {
    fail(`image fragments must contain exactly ${IMAGE_KEYS.join(", ")}`);
  }
  for (const key of IMAGE_KEYS) {
    if (!DIGEST_IMAGE_RE.test(fragmentValues.get(key))) fail(`${key} is not a digest-only image reference`);
  }
  validateImageRepositories(Object.fromEntries(IMAGE_KEYS.map((key) => [key, fragmentValues.get(key)])), repository.split("/", 1)[0].toLowerCase());
  const envPath = join(output, "release-images.env");
  writeFileSync(envPath, [
    `RELEASE_VERSION=${commitSha}`,
    ...IMAGE_KEYS.map((key) => `${key}=${fragmentValues.get(key)}`),
    "",
  ].join("\n"), { mode: 0o644 });

  const bundlePath = join(output, "deploy-bundle.tar.gz");
  const repoRoot = dirname(dirname(deployDir));
  const uncompressedBundle = join(output, "deploy-bundle.tar");
  run("git", ["-C", repoRoot, "cat-file", "-e", `${commitSha}:src/deploy`]);
  run("git", [
    "-C", repoRoot,
    "archive", "--format=tar", "--prefix=deploy/", `--output=${uncompressedBundle}`,
    `${commitSha}:src/deploy`, ".",
    ":(exclude)Caddyfile.minio",
    ":(exclude)compose.minio.yaml",
    ":(exclude)minio",
    ":(exclude)storage",
    ":(exclude)scripts/validate-deployment.sh",
    ":(exclude)tests/caddy-minio-contract.test.sh",
    ":(exclude)tests/minio-overlay-contract.test.sh",
    ":(exclude)tests/minio-release-integration.test.sh",
  ]);
  const compressed = spawnSync("gzip", ["-n", "-9", "-c", uncompressedBundle], { encoding: null });
  if (compressed.status !== 0) fail(`gzip failed: ${compressed.error?.message ?? compressed.stderr?.toString().trim()}`);
  writeFileSync(bundlePath, compressed.stdout, { mode: 0o644 });
  rmSync(uncompressedBundle);
  validateBundle(bundlePath);

  const metadataPath = join(output, "release-metadata.json");
  const images = Object.fromEntries(IMAGE_KEYS.map((key) => [key, fragmentValues.get(key)]));
  const metadata = {
    schemaVersion: 1,
    kind: "hook2stream-release-candidate",
    protocolVersion: "forced-command-v1",
    sourceRef: "refs/heads/main",
    sourceEvent: "push",
    workflow: ".github/workflows/ci.yml",
    workflowName: "CI",
    artifactName: `release-candidate-${commitSha}-${ciRunId}-${ciRunAttempt}`,
    repository,
    commitSha,
    ciRunId: Number(ciRunId),
    ciRunAttempt: Number(ciRunAttempt),
    createdAt: new Date().toISOString(),
    images,
    deployBundle: { file: "deploy-bundle.tar.gz", sha256: sha256(bundlePath) },
  };
  writeFileSync(metadataPath, `${JSON.stringify(metadata, null, 2)}\n`, { mode: 0o644 });

  const checksumsPath = join(output, "SHA256SUMS");
  writeFileSync(checksumsPath, ["deploy-bundle.tar.gz", "release-images.env", "release-metadata.json"]
    .map((name) => `${sha256(join(output, name))}  ${name}`)
    .join("\n") + "\n", { mode: 0o644 });
  validateCandidate(output, { repository, sha: commitSha, "run-id": ciRunId, "run-attempt": ciRunAttempt });
}

const [command, ...rest] = process.argv.slice(2);
const args = parseArgs(rest);
if (command === "create") create(args);
else if (command === "validate") validateCandidate(requireArg(args, "candidate"), args);
else usage();
