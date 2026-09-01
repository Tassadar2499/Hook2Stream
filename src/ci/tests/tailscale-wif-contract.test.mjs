#!/usr/bin/env node

import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const read = (path) => readFileSync(join(repoRoot, path), "utf8");

function assert(condition, message) {
  if (!condition) {
    console.error(`tailscale-wif-contract: ${message}`);
    process.exit(1);
  }
}

function exactJson(actual, expected, message) {
  assert(JSON.stringify(actual) === JSON.stringify(expected), message);
}

const policy = JSON.parse(read("deploy/providers/serversguru/tailscale-policy.hujson"));
const deploymentGuide = read(".github/DEPLOYMENT.md");

const immutableSubjects = [
  "repo:Tassadar2499@34176883/Hook2Stream@1295804906:environment:staging",
  "repo:Tassadar2499@34176883/Hook2Stream@1295804906:environment:production",
];
for (const subject of immutableSubjects) {
  assert(deploymentGuide.split(subject).length - 1 === 1,
    `deployment guide must contain exact immutable WIF subject once: ${subject}`);
}
for (const legacySubject of [
  "repo:Tassadar2499/Hook2Stream:environment:staging",
  "repo:Tassadar2499/Hook2Stream:environment:production",
]) {
  assert(!deploymentGuide.includes(legacySubject),
    `deployment guide must not authorize legacy name-based subject: ${legacySubject}`);
}
assert(deploymentGuide.includes("never leave name-based, wildcard, and immutable credentials active in\nparallel"),
  "deployment guide must forbid overlapping fallback WIF credentials");

exactJson(Object.keys(policy).sort(), ["grants", "hosts", "tagOwners", "tests"],
  "policy must contain only tagOwners, hosts, grants, and deny tests");
assert(!Object.hasOwn(policy, "ssh") && !Object.hasOwn(policy, "sshTests"),
  "ordinary OpenSSH policy must not configure Tailscale SSH or SSH tests");
assert(!Object.hasOwn(policy, "acls"), "policy must use only deny-by-default grants");

exactJson(policy.tagOwners, {
  "tag:hook2stream-ci-staging": ["autogroup:admin"],
  "tag:hook2stream-ci-production": ["autogroup:admin"],
}, "only tailnet administrators may issue the two CI tags");

exactJson(policy.hosts, {
  "h2s-staging-vps": "100.90.109.67",
  "h2s-production-vps": "100.70.235.93",
}, "host aliases must resolve to the accepted live Servers.Guru Tailscale IPv4 addresses");

const expectedGrants = [
  {
    src: ["autogroup:owner"],
    dst: ["h2s-staging-vps", "h2s-production-vps"],
    ip: ["tcp:22"],
  },
  {
    src: ["tag:hook2stream-ci-staging"],
    dst: ["h2s-staging-vps"],
    ip: ["tcp:22"],
  },
  {
    src: ["tag:hook2stream-ci-production"],
    dst: ["h2s-production-vps"],
    ip: ["tcp:22"],
  },
];
exactJson(policy.grants, expectedGrants,
  "grants must allow only owner and environment-matched CI access to ordinary OpenSSH");
for (const grant of policy.grants) {
  const selectors = [...grant.src, ...grant.dst, ...grant.ip];
  assert(selectors.every((selector) => !selector.includes("*")),
    "wildcard selectors are forbidden in grants");
}

const expectedTests = [
  {
    src: "tag:hook2stream-ci-staging",
    proto: "tcp",
    accept: ["h2s-staging-vps:22"],
    deny: [
      "h2s-production-vps:22",
      "h2s-staging-vps:80",
      "h2s-staging-vps:443",
      "h2s-production-vps:80",
      "h2s-production-vps:443",
    ],
  },
  {
    src: "tag:hook2stream-ci-production",
    proto: "tcp",
    accept: ["h2s-production-vps:22"],
    deny: [
      "h2s-staging-vps:22",
      "h2s-staging-vps:80",
      "h2s-staging-vps:443",
      "h2s-production-vps:80",
      "h2s-production-vps:443",
    ],
  },
];
exactJson(policy.tests, expectedTests,
  "policy tests must prove matching SSH access, cross-environment denial, and non-SSH denial");

const action = "tailscale/github-action@780049a30b6ff5c378a9e7b389d15ece7a204888";
const commonInputs = [
  "oauth-client-id: ${{ secrets.TS_OAUTH_CLIENT_ID }}",
  "audience: ${{ secrets.TS_AUDIENCE }}",
  "ping: ${{ secrets.DEPLOY_HOST }}",
];
const workflows = [
  {
    name: "staging",
    text: read(".github/workflows/stage-candidate.yml"),
    job: "  deploy-staging:",
    environment: "environment: staging",
    tag: "tags: tag:hook2stream-ci-staging",
  },
  {
    name: "production",
    text: read(".github/workflows/promote-production.yml"),
    job: "  deploy-production:",
    environment: "environment: production",
    tag: "tags: tag:hook2stream-ci-production",
  },
  {
    name: "rollback",
    text: read(".github/workflows/rollback.yml"),
    job: "  rollback:",
    environment: "environment: ${{ inputs.environment }}",
    tag: "tags: tag:hook2stream-ci-${{ inputs.environment }}",
  },
];

for (const workflow of workflows) {
  const body = workflow.text.split(workflow.job)[1];
  assert(typeof body === "string" && body.length > 0,
    `${workflow.name} credential-bearing job is missing`);
  assert(workflow.text.match(/tailscale\/github-action@/g)?.length === 1 &&
    workflow.text.split(action).length - 1 === 1,
    `${workflow.name} must use the exact reviewed Tailscale action SHA once`);
  assert(body.match(/^\s+id-token: write$/gm)?.length === 1,
    `${workflow.name} must permit GitHub OIDC token issuance`);
  assert(body.includes(workflow.environment),
    `${workflow.name} WIF subject must be bound through the matching GitHub Environment`);
  for (const input of commonInputs) {
    assert(body.split(input).length - 1 === 1,
      `${workflow.name} must contain exactly one WIF input: ${input}`);
  }
  assert(body.match(/^\s+tags:/gm)?.length === 1 && body.includes(workflow.tag),
    `${workflow.name} must request only its environment-specific CI tag`);
  assert(!/^\s*(?:authkey|auth-key|oauth-secret):/m.test(workflow.text),
    `${workflow.name} must not use a reusable auth key or OAuth client secret`);
}

console.log("Tailscale policy and WIF workflow contracts passed");
