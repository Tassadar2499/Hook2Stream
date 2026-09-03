#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
policy=${deployment_dir}/vault/policies/host-renderer.hcl
templates=${deployment_dir}/vault/templates
agent=${deployment_dir}/vault/agent.hcl

node - "$policy" "$templates" "$agent" <<'NODE'
const fs = require("node:fs");
const path = require("node:path");

const policyPath = process.argv[2];
const templatesDir = process.argv[3];
const agentPath = process.argv[4];
const policy = fs.readFileSync(policyPath, "utf8");
const expectedPaths = [
  "hook2stream-kv/data/production/api",
  "hook2stream-kv/data/production/backup-encryption",
  "hook2stream-kv/data/production/backup-s3",
  "hook2stream-kv/data/production/control",
  "hook2stream-kv/data/production/foundation",
  "hook2stream-kv/data/production/media-security",
  "hook2stream-kv/data/production/runtime-s3",
].sort();

const blocks = [...policy.matchAll(/path "([^"]+)"\s*\{([\s\S]*?)\}/g)];
const actualPaths = blocks.map((match) => match[1]).sort();
if (JSON.stringify(actualPaths) !== JSON.stringify(expectedPaths)) {
  throw new Error(`host renderer paths differ: ${actualPaths.join(",")}`);
}

for (const [, vaultPath, body] of blocks) {
  const capabilities = body.match(/capabilities\s*=\s*\[([^\]]*)\]/);
  const values = capabilities
    ? [...capabilities[1].matchAll(/"([^"]+)"/g)]
        .map((match) => match[1])
        .sort()
    : [];
  if (JSON.stringify(values) !== JSON.stringify(["read"])) {
    throw new Error(`${vaultPath} must be read-only; received ${values.join(",")}`);
  }
}

const policyFiles = fs.readdirSync(path.dirname(policyPath))
  .filter((name) => name.endsWith(".hcl"))
  .sort();
if (JSON.stringify(policyFiles) !== JSON.stringify(["host-renderer.hcl"])) {
  throw new Error(`obsolete Vault policies remain: ${policyFiles.join(",")}`);
}

if (/backup-encryption\/(?:current|keys)|passphrase|private.*identity/i.test(policy)) {
  throw new Error("host renderer policy retains backup-key history or private material");
}

const expectedTemplates = {
  "api.json.ctmpl": {
    vaultPath: "hook2stream-kv/data/production/api",
    fields: ["google_client_secret", "stripe_secret_key", "stripe_webhook_secret"],
  },
  "backup-encryption.json.ctmpl": {
    vaultPath: "hook2stream-kv/data/production/backup-encryption",
    fields: ["age_recipient"],
  },
  "backup-s3.json.ctmpl": {
    vaultPath: "hook2stream-kv/data/production/backup-s3",
    fields: ["access_key_id", "secret_access_key"],
  },
  "control.json.ctmpl": {
    vaultPath: "hook2stream-kv/data/production/control",
    fields: ["openrouter_api_key"],
  },
  "foundation.json.ctmpl": {
    vaultPath: "hook2stream-kv/data/production/foundation",
    fields: ["postgres_password"],
  },
  "media-security.json.ctmpl": {
    vaultPath: "hook2stream-kv/data/production/media-security",
    fields: ["invited_emails", "media_keyring"],
  },
  "runtime-s3.json.ctmpl": {
    vaultPath: "hook2stream-kv/data/production/runtime-s3",
    fields: ["access_key_id", "secret_access_key"],
  },
};
const templateFiles = fs.readdirSync(templatesDir)
  .filter((name) => name.endsWith(".ctmpl"))
  .sort();
if (JSON.stringify(templateFiles) !== JSON.stringify(Object.keys(expectedTemplates).sort())) {
  throw new Error(`Vault template set differs: ${templateFiles.join(",")}`);
}

for (const [fileName, expected] of Object.entries(expectedTemplates)) {
  const template = fs.readFileSync(path.join(templatesDir, fileName), "utf8");
  const pathMatch = template.match(/with secret "([^"]+)"/);
  const fields = [...template.matchAll(/\.Data\.data\.([A-Za-z0-9_]+)/g)]
    .map((match) => match[1])
    .sort();
  if (pathMatch?.[1] !== expected.vaultPath) {
    throw new Error(`${fileName} reads ${pathMatch?.[1] ?? "no path"}`);
  }
  if (JSON.stringify(fields) !== JSON.stringify([...expected.fields].sort())) {
    throw new Error(`${fileName} fields differ: ${fields.join(",")}`);
  }
}

const apiTemplate = fs.readFileSync(path.join(templatesDir, "api.json.ctmpl"), "utf8");
if (!apiTemplate.includes('if eq (env "BILLING_MODE") "stripe"')) {
  throw new Error("api.json.ctmpl does not condition Stripe fields on BILLING_MODE=stripe");
}

const agent = fs.readFileSync(agentPath, "utf8");
const agentSources = [...agent.matchAll(/source\s*=\s*"\/vault\/templates\/([^"]+)"/g)]
  .map((match) => match[1])
  .sort();
if (JSON.stringify(agentSources) !== JSON.stringify(templateFiles)) {
  throw new Error(`Vault Agent template set differs: ${agentSources.join(",")}`);
}
NODE

printf '%s\n' \
    "vault policy contract test: seven exact read-only records/templates and no key history"
