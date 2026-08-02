#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
policy=${deployment_dir}/vault/policies/backup-encryption-writer.hcl

node - "$policy" <<'NODE'
const fs = require("node:fs");

const policy = fs.readFileSync(process.argv[2], "utf8");
const archive = policy.match(
  /path "hook2stream-kv\/data\/production\/backup-encryption\/keys\/\+"\s*\{([\s\S]*?)\}/,
);
if (!archive) {
  throw new Error("historical backup-key policy block is missing");
}
const capabilities = archive[1].match(/capabilities\s*=\s*\[([^\]]*)\]/);
if (!capabilities) {
  throw new Error("historical backup-key capabilities are missing");
}
const values = [...capabilities[1].matchAll(/"([^"]+)"/g)]
  .map((match) => match[1])
  .sort();
if (JSON.stringify(values) !== JSON.stringify(["create"])) {
  throw new Error(
    `historical backup-key paths must be create-only; received ${values.join(",")}`,
  );
}
NODE

printf '%s\n' "vault policy contract test: historical backup keys are create-only"
