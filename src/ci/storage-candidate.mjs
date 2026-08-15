#!/usr/bin/env node

import { createHash } from "node:crypto";
import {
  existsSync,
  lstatSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { basename, join, relative, resolve } from "node:path";
import { spawnSync } from "node:child_process";

const IMAGE_KEYS = ["MINIO_IMAGE", "MINIO_MC_IMAGE", "CADDY_IMAGE"];
const CANDIDATE_FILES = [
  "storage-metadata.json",
  "storage-images.env",
  "storage-bundle.tar.gz",
  "SHA256SUMS",
];
const SHA_RE = /^[0-9a-f]{40}$/;
const DIGEST_IMAGE_RE = /^(?:[a-z0-9]+(?:[._-][a-z0-9]+)*(?::[0-9]+)?\/)?[a-z0-9]+(?:[._/-][a-z0-9]+)*@sha256:[0-9a-f]{64}$/;

function fail(message) {
  console.error(`storage-candidate: ${message}`);
  process.exit(1);
}

function usage() {
  console.error(`Usage:
  storage-candidate.mjs create --output DIR --fragments DIR --storage-dir DIR --repository OWNER/REPO --sha SHA --run-id ID --run-attempt N
  storage-candidate.mjs validate --candidate DIR [--repository OWNER/REPO] [--sha SHA] [--run-id ID] [--run-attempt N]`);
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

function run(command, commandArgs, options = {}) {
  const result = spawnSync(command, commandArgs, { encoding: "utf8", ...options });
  if (result.status !== 0) {
    if (result.error && result.status === null) fail(`could not run ${command}: ${result.error.message}`);
    const detail = (result.stderr || result.stdout || "").trim();
    fail(`${command} failed${detail ? `: ${detail}` : ""}`);
  }
  return result.stdout;
}

function sha256(path) {
  return createHash("sha256").update(readFileSync(path)).digest("hex");
}

function validateRepository(repository) {
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) fail("repository must be OWNER/REPO");
}

function validatePositiveInteger(value, label) {
  if (!/^[1-9][0-9]*$/.test(String(value))) fail(`${label} must be a positive integer`);
}

function hasExactKeys(value, keys) {
  return value && typeof value === "object" && !Array.isArray(value) &&
    Object.keys(value).sort().join("\0") === [...keys].sort().join("\0");
}

function sameRecord(left, right, keys) {
  return hasExactKeys(left, keys) && hasExactKeys(right, keys) && keys.every((key) => left[key] === right[key]);
}

function parseEnvironment(path) {
  const values = new Map();
  for (const [index, rawLine] of readFileSync(path, "utf8").split(/\r?\n/).entries()) {
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

function validateImageRepositories(images, owner) {
  const escapedOwner = owner.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const policies = {
    MINIO_IMAGE: new RegExp(`^ghcr\\.io/${escapedOwner}/hook2stream-minio@sha256:`),
    MINIO_MC_IMAGE: /^(?:docker\.io\/)?minio\/mc@sha256:/,
    CADDY_IMAGE: /^(?:docker\.io\/library\/)?caddy@sha256:/,
  };
  for (const key of IMAGE_KEYS) {
    if (!policies[key].test(images[key])) fail(`${key} repository is outside the storage allowlist`);
  }
}

function validateImages(path, expectedSha, repository) {
  const values = parseEnvironment(path);
  const expectedKeys = ["STORAGE_RELEASE_VERSION", ...IMAGE_KEYS];
  if (values.size !== expectedKeys.length || expectedKeys.some((key) => !values.has(key))) {
    fail(`storage-images.env must contain exactly ${expectedKeys.join(", ")}`);
  }
  const releaseVersion = values.get("STORAGE_RELEASE_VERSION");
  if (!SHA_RE.test(releaseVersion)) fail("STORAGE_RELEASE_VERSION must be a lowercase 40-character commit SHA");
  if (expectedSha && releaseVersion !== expectedSha) fail("STORAGE_RELEASE_VERSION does not match the candidate commit");
  const images = Object.fromEntries(IMAGE_KEYS.map((key) => [key, values.get(key)]));
  for (const key of IMAGE_KEYS) {
    if (!DIGEST_IMAGE_RE.test(images[key])) fail(`${key} is not a digest-only image reference`);
  }
  validateImageRepositories(images, repository.split("/")[0].toLowerCase());
  return images;
}

function validateBundle(path) {
  if (statSync(path).size > 64 * 1024 * 1024) fail("storage bundle exceeds 64 MiB");
  const entries = run("tar", ["-tzf", path]).split("\n").filter(Boolean);
  if (entries.length === 0) fail("storage bundle is empty");
  for (const entry of entries) {
    if (entry.includes("\\") || entry.startsWith("/") || /(^|\/)\.\.?(\/|$)/.test(entry)) {
      fail(`unsafe storage bundle path: ${JSON.stringify(entry)}`);
    }
    if (entry !== "storage" && entry !== "storage/" && !entry.startsWith("storage/")) {
      fail(`storage bundle entry is outside storage/: ${JSON.stringify(entry)}`);
    }
    if (/[[\]\x00-\x1f\x7f]/.test(entry)) fail("storage bundle path contains control characters");
  }
  for (const line of run("tar", ["-tvzf", path]).split("\n").filter(Boolean)) {
    if (!["-", "d"].includes(line[0])) fail("storage bundle may contain only regular files and directories");
  }
  if (!entries.includes("storage/storage-release.json")) fail("storage bundle is missing storage/storage-release.json");
  let storageRelease;
  try {
    storageRelease = JSON.parse(run("tar", ["-xOzf", path, "storage/storage-release.json"]));
  } catch (error) {
    fail(`storage/storage-release.json is invalid JSON: ${error.message}`);
  }
  if (!hasExactKeys(storageRelease, [
    "schemaVersion", "kind", "protocolVersion", "storageFormatVersion", "objectFormat", "minioRelease",
    "minioSourceCommit",
  ]) ||
      storageRelease.schemaVersion !== 1 || storageRelease.kind !== "hook2stream-storage-runtime" ||
      storageRelease.protocolVersion !== 1 || storageRelease.storageFormatVersion !== 1 ||
      storageRelease.objectFormat !== "H2SEv1" ||
      storageRelease.minioRelease !== "RELEASE.2025-10-15T17-29-55Z" ||
      storageRelease.minioSourceCommit !== "9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a") {
    fail("storage/storage-release.json does not declare the supported H2SEv1 runtime and MinIO source pin");
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

function readMetadata(path) {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (error) {
    fail(`storage-metadata.json is invalid JSON: ${error.message}`);
  }
}

function validateCandidate(candidateDirectory, expected = {}) {
  const directory = resolve(candidateDirectory);
  if (!existsSync(directory) || !lstatSync(directory).isDirectory()) fail("candidate directory does not exist");
  const actualFiles = readdirSync(directory).sort();
  if (JSON.stringify(actualFiles) !== JSON.stringify([...CANDIDATE_FILES].sort())) {
    fail(`candidate must contain exactly ${CANDIDATE_FILES.join(", ")}`);
  }
  for (const name of CANDIDATE_FILES) {
    if (!lstatSync(join(directory, name)).isFile()) fail(`${name} must be a regular non-symlink file`);
  }

  const checksums = parseChecksums(join(directory, "SHA256SUMS"));
  const coveredFiles = CANDIDATE_FILES.filter((name) => name !== "SHA256SUMS");
  if (checksums.size !== coveredFiles.length || coveredFiles.some((name) => !checksums.has(name))) {
    fail(`SHA256SUMS must cover exactly ${coveredFiles.join(", ")}`);
  }
  for (const name of coveredFiles) {
    if (sha256(join(directory, name)) !== checksums.get(name)) fail(`${name} checksum mismatch`);
  }

  const metadata = readMetadata(join(directory, "storage-metadata.json"));
  const metadataKeys = [
    "schemaVersion", "kind", "protocolVersion", "sourceRef", "sourceEvent", "workflow", "workflowName",
    "artifactName", "repository", "commitSha", "ciRunId", "ciRunAttempt", "createdAt", "images", "storageBundle",
  ];
  if (!hasExactKeys(metadata, metadataKeys)) fail("storage metadata contains missing or unknown fields");
  if (metadata.schemaVersion !== 1 || metadata.kind !== "hook2stream-storage-candidate") fail("unsupported storage metadata schema");
  if (metadata.protocolVersion !== "storage-forced-command-v1") fail("unsupported storage deployment protocol");
  if (metadata.sourceRef !== "refs/heads/main" || metadata.sourceEvent !== "push") fail("candidate was not produced by a main push");
  if (metadata.workflow !== ".github/workflows/storage-ci.yml" || metadata.workflowName !== "Storage CI") {
    fail("candidate workflow identity is invalid");
  }
  validateRepository(metadata.repository ?? "");
  if (!SHA_RE.test(metadata.commitSha ?? "")) fail("metadata commitSha is invalid");
  validatePositiveInteger(metadata.ciRunId, "metadata ciRunId");
  validatePositiveInteger(metadata.ciRunAttempt, "metadata ciRunAttempt");
  if (!/^storage-candidate-[0-9a-f]{40}-[1-9][0-9]*-[1-9][0-9]*$/.test(metadata.artifactName ?? "")) {
    fail("metadata artifactName is invalid");
  }
  const canonicalName = `storage-candidate-${metadata.commitSha}-${metadata.ciRunId}-${metadata.ciRunAttempt}`;
  if (metadata.artifactName !== canonicalName) fail("metadata artifactName does not match its storage release identity");
  if (typeof metadata.createdAt !== "string" || Number.isNaN(Date.parse(metadata.createdAt))) {
    fail("metadata createdAt is invalid");
  }

  const images = validateImages(join(directory, "storage-images.env"), metadata.commitSha, metadata.repository);
  if (!sameRecord(metadata.images, images, IMAGE_KEYS)) fail("metadata images do not match storage-images.env");
  if (!hasExactKeys(metadata.storageBundle, ["file", "sha256"]) ||
      metadata.storageBundle.file !== "storage-bundle.tar.gz" ||
      metadata.storageBundle.sha256 !== checksums.get("storage-bundle.tar.gz")) {
    fail("metadata storageBundle does not match the candidate");
  }
  validateBundle(join(directory, "storage-bundle.tar.gz"));

  if (expected.repository && metadata.repository !== expected.repository) fail("candidate repository does not match --repository");
  if (expected.sha && metadata.commitSha !== expected.sha) fail("candidate commit does not match --sha");
  if (expected["run-id"] && String(metadata.ciRunId) !== expected["run-id"]) fail("candidate run does not match --run-id");
  if (expected["run-attempt"] && String(metadata.ciRunAttempt) !== expected["run-attempt"]) {
    fail("candidate attempt does not match --run-attempt");
  }
  return metadata;
}

function createCandidate(args) {
  const output = resolve(required(args, "output"));
  const fragments = resolve(required(args, "fragments"));
  const storageDirectory = resolve(required(args, "storage-dir"));
  const repository = required(args, "repository");
  const commitSha = required(args, "sha");
  const ciRunId = required(args, "run-id");
  const ciRunAttempt = required(args, "run-attempt");
  validateRepository(repository);
  if (!SHA_RE.test(commitSha)) fail("--sha must be a lowercase 40-character commit SHA");
  validatePositiveInteger(ciRunId, "--run-id");
  validatePositiveInteger(ciRunAttempt, "--run-attempt");
  if (!existsSync(storageDirectory) || !statSync(storageDirectory).isDirectory()) fail("--storage-dir must be a directory");

  const repositoryRoot = resolve(run("git", ["-C", storageDirectory, "rev-parse", "--show-toplevel"]).trim());
  if (relative(repositoryRoot, storageDirectory).replaceAll("\\", "/") !== "src/deploy/storage") {
    fail("--storage-dir must be the repository src/deploy/storage directory");
  }
  run("git", ["-C", repositoryRoot, "cat-file", "-e", `${commitSha}:src/deploy/storage`]);

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
  const images = Object.fromEntries(IMAGE_KEYS.map((key) => [key, fragmentValues.get(key)]));
  for (const key of IMAGE_KEYS) {
    if (!DIGEST_IMAGE_RE.test(images[key])) fail(`${key} is not a digest-only image reference`);
  }
  validateImageRepositories(images, repository.split("/")[0].toLowerCase());

  writeFileSync(join(output, "storage-images.env"), [
    `STORAGE_RELEASE_VERSION=${commitSha}`,
    ...IMAGE_KEYS.map((key) => `${key}=${images[key]}`),
    "",
  ].join("\n"), { mode: 0o644 });

  const uncompressedBundle = join(output, "storage-bundle.tar");
  const bundlePath = join(output, "storage-bundle.tar.gz");
  run("git", [
    "-C", repositoryRoot, "archive", "--format=tar", "--prefix=storage/", `--output=${uncompressedBundle}`,
    `${commitSha}:src/deploy/storage`,
  ]);
  const compressed = spawnSync("gzip", ["-n", "-9", "-c", uncompressedBundle], { encoding: null });
  if (compressed.status !== 0) fail(`gzip failed: ${compressed.error?.message ?? compressed.stderr?.toString().trim()}`);
  writeFileSync(bundlePath, compressed.stdout, { mode: 0o644 });
  rmSync(uncompressedBundle);
  validateBundle(bundlePath);

  const metadata = {
    schemaVersion: 1,
    kind: "hook2stream-storage-candidate",
    protocolVersion: "storage-forced-command-v1",
    sourceRef: "refs/heads/main",
    sourceEvent: "push",
    workflow: ".github/workflows/storage-ci.yml",
    workflowName: "Storage CI",
    artifactName: `storage-candidate-${commitSha}-${ciRunId}-${ciRunAttempt}`,
    repository,
    commitSha,
    ciRunId: Number(ciRunId),
    ciRunAttempt: Number(ciRunAttempt),
    createdAt: new Date().toISOString(),
    images,
    storageBundle: { file: "storage-bundle.tar.gz", sha256: sha256(bundlePath) },
  };
  writeFileSync(join(output, "storage-metadata.json"), `${JSON.stringify(metadata, null, 2)}\n`, { mode: 0o644 });
  writeFileSync(join(output, "SHA256SUMS"), ["storage-bundle.tar.gz", "storage-images.env", "storage-metadata.json"]
    .map((name) => `${sha256(join(output, name))}  ${name}`)
    .join("\n") + "\n", { mode: 0o644 });
  validateCandidate(output, { repository, sha: commitSha, "run-id": ciRunId, "run-attempt": ciRunAttempt });
}

const [command, ...rest] = process.argv.slice(2);
const args = parseArgs(rest);
if (command === "create") createCandidate(args);
else if (command === "validate") validateCandidate(required(args, "candidate"), args);
else usage();
