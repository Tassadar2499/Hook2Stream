#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
temporary_dir=$(mktemp -d)

cleanup() {
    rm -rf "$temporary_dir"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

fail() {
    printf '%s\n' "compose secret contract test: $*" >&2
    exit 1
}

command -v docker >/dev/null 2>&1 || fail "docker is required"
docker compose version >/dev/null 2>&1 \
    || fail "the Docker Compose v2 plugin is required"
command -v node >/dev/null 2>&1 || fail "node is required"

chmod 0750 "$temporary_dir"
mkdir -p "$temporary_dir/vault-auth" "$temporary_dir/vault-candidate"
: > "$temporary_dir/vault-ca.pem"
umask 027
for secret_name in \
    postgres_password \
    s3_runtime_access_key \
    s3_runtime_secret_key \
    google_client_secret \
    stripe_secret_key \
    stripe_webhook_secret \
    openrouter_api_key \
    media_keyring \
    invited_emails \
    backup_s3_access_key \
    backup_s3_secret_key \
    backup_age_recipient; do
    printf '%s\n' "test-secret" > "$temporary_dir/$secret_name"
done

render_model() {
    destination=$1
    shift

    (
        unset SECRETS_GID
        SECRET_PROVIDER=file
        SECRETS_DIR=$temporary_dir
        export SECRET_PROVIDER SECRETS_DIR
        if [ "$#" -gt 0 ]; then
            SECRETS_GID=$1
            export SECRETS_GID
        fi

        docker compose \
            --env-file "$deployment_dir/.env.example" \
            --profile tools \
            -f "$deployment_dir/compose.yaml" \
            config --format json
    ) > "$destination"
}

render_model "$temporary_dir/default.json"
render_model "$temporary_dir/override.json" 2468

(
    unset SECRETS_GID
    SECRET_PROVIDER=vault
    SECRETS_DIR=$temporary_dir
    VAULT_AUTH_DIR=$temporary_dir/vault-auth
    VAULT_CACERT=$temporary_dir/vault-ca.pem
    VAULT_CANDIDATE_DIR=$temporary_dir/vault-candidate
    export \
        SECRET_PROVIDER \
        SECRETS_DIR \
        VAULT_AUTH_DIR \
        VAULT_CACERT \
        VAULT_CANDIDATE_DIR
    docker compose \
        --env-file "$deployment_dir/.env.example" \
        --profile tools \
        -f "$deployment_dir/compose.yaml" \
        -f "$deployment_dir/compose.vault.yaml" \
        config --format json
) > "$temporary_dir/vault.json"

node - \
    "$temporary_dir/default.json" \
    "$temporary_dir/override.json" \
    "$temporary_dir/vault.json" <<'NODE'
const fs = require("node:fs");

const expectedSecrets = {
  api: [
    "google_client_secret",
    "postgres_password",
    "s3_runtime_access_key",
    "s3_runtime_secret_key",
    "stripe_secret_key",
    "stripe_webhook_secret",
    "invited_emails",
    "media_keyring",
  ],
  "worker-media": [
    "media_keyring",
    "postgres_password",
    "s3_runtime_access_key",
    "s3_runtime_secret_key",
  ],
  "worker-analysis": [
    "media_keyring",
    "postgres_password",
    "s3_runtime_access_key",
    "s3_runtime_secret_key",
  ],
  "worker-control": [
    "media_keyring",
    "openrouter_api_key",
    "postgres_password",
    "s3_runtime_access_key",
    "s3_runtime_secret_key",
  ],
  "worker-render": [
    "media_keyring",
    "postgres_password",
    "s3_runtime_access_key",
    "s3_runtime_secret_key",
  ],
  "worker-export": [
    "media_keyring",
    "postgres_password",
    "s3_runtime_access_key",
    "s3_runtime_secret_key",
  ],
  bootstrapper: [
    "media_keyring",
    "postgres_password",
    "s3_runtime_access_key",
    "s3_runtime_secret_key",
  ],
  pgbouncer: ["postgres_password"],
  postgres: ["postgres_password"],
  "postgres-backup": [
    "backup_age_recipient",
    "backup_s3_access_key",
    "backup_s3_secret_key",
    "postgres_password",
  ],
  "storage-probe": [
    "s3_runtime_access_key",
    "s3_runtime_secret_key",
  ],
  "storage-janitor": [
    "s3_runtime_access_key",
    "s3_runtime_secret_key",
  ],
};

function fail(message) {
  process.stderr.write(`compose secret contract test: ${message}\n`);
  process.exit(1);
}

function mountedSecretNames(service) {
  return (service.secrets ?? [])
    .map((secret) => typeof secret === "string" ? secret : secret.source)
    .sort();
}

function mountedConfigNames(service) {
  return (service.configs ?? [])
    .map((config) => typeof config === "string" ? config : config.source)
    .sort();
}

function networkNames(service) {
  return (Array.isArray(service.networks)
    ? service.networks
    : Object.keys(service.networks ?? {})).sort();
}

function assertNetworks(model, serviceName, expectedNetworks) {
  const actual = networkNames(model.services?.[serviceName] ?? {});
  const expected = [...expectedNetworks].sort();
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    fail(
      `${serviceName} networks differ: expected ${expected.join(",")}; ` +
      `received ${actual.join(",")}`,
    );
  }
}

function assertModel(path, expectedGid) {
  const model = JSON.parse(fs.readFileSync(path, "utf8"));
  const mountedSecrets = new Set();

  for (const [serviceName, secretNames] of Object.entries(expectedSecrets)) {
    const service = model.services?.[serviceName];
    if (!service) {
      fail(`missing service ${serviceName}`);
    }

    const actualSecrets = mountedSecretNames(service);
    actualSecrets.forEach((secret) => mountedSecrets.add(secret));
    const expected = [...secretNames].sort();
    if (JSON.stringify(actualSecrets) !== JSON.stringify(expected)) {
      fail(
        `${serviceName} secrets differ: expected ${expected.join(",")}; ` +
        `received ${actualSecrets.join(",")}`,
      );
    }

    const supplementalGroups = (service.group_add ?? []).map(String);
    if (
      supplementalGroups.length !== 1 ||
      supplementalGroups[0] !== expectedGid
    ) {
      fail(
        `${serviceName} must have only supplemental secrets group ` +
        `${expectedGid}; received ${supplementalGroups.join(",")}`,
      );
    }
  }

  for (const [serviceName, service] of Object.entries(model.services ?? {})) {
    const actualSecrets = mountedSecretNames(service);
    if (actualSecrets.length > 0 && !(serviceName in expectedSecrets)) {
      fail(`unexpected secret-consuming service ${serviceName}`);
    }

    if (!(serviceName in expectedSecrets) && (service.group_add ?? []).length > 0) {
      fail(`non-consumer ${serviceName} unexpectedly joins the secrets group`);
    }
  }

  const declaredSecrets = Object.keys(model.secrets ?? {}).sort();
  const expectedDeclaredSecrets = [...mountedSecrets].sort();
  if (JSON.stringify(declaredSecrets) !== JSON.stringify(expectedDeclaredSecrets)) {
    fail(
      `declared secrets differ from mounted secrets: declared ` +
      `${declaredSecrets.join(",")}; mounted ${expectedDeclaredSecrets.join(",")}`,
    );
  }

  if (!mountedConfigNames(model.services.postgres).includes("postgres_set_password")) {
    fail("postgres must mount the audited password-rotation helper");
  }

  for (const serviceName of [
    "worker-media",
    "worker-analysis",
    "worker-render",
    "worker-export",
    "bootstrapper",
  ]) {
    assertNetworks(model, serviceName, ["backend", "media-egress"]);
  }
  assertNetworks(model, "storage-probe", ["media-egress"]);
  assertNetworks(model, "storage-janitor", ["media-egress"]);
  assertNetworks(model, "egress-s3", ["media-egress", "public-egress"]);
  assertNetworks(model, "postgres-backup", ["backend", "backup-egress"]);
  assertNetworks(model, "egress-backup", ["backup-egress", "public-egress"]);
}

assertModel(process.argv[2], "2000");
assertModel(process.argv[3], "2468");

const vaultModel = JSON.parse(fs.readFileSync(process.argv[4], "utf8"));
const vaultRenderer = vaultModel.services?.["vault-renderer"];
if (!vaultRenderer) {
  fail("Vault overlay did not render vault-renderer");
}
if (JSON.stringify(vaultRenderer.entrypoint) !== JSON.stringify(["/bin/vault"])) {
  fail("vault-renderer must bypass the vendor privilege-dropping entrypoint");
}
if (vaultRenderer.user !== "0:2000") {
  fail(`vault-renderer must run as 0:2000; received ${vaultRenderer.user}`);
}
if (vaultRenderer.command?.[0] !== "agent") {
  fail("vault-renderer must invoke Vault Agent through /bin/vault");
}
const expectedVaultConfigs = [
  "vault_agent_config",
  "vault_template_api",
  "vault_template_backup_encryption",
  "vault_template_backup_s3",
  "vault_template_control",
  "vault_template_foundation",
  "vault_template_media_security",
  "vault_template_runtime_s3",
].sort();
const actualVaultConfigs = mountedConfigNames(vaultRenderer);
if (JSON.stringify(actualVaultConfigs) !== JSON.stringify(expectedVaultConfigs)) {
  fail(
    `Vault renderer configs differ: ${actualVaultConfigs.join(",")}`,
  );
}
if (Object.keys(vaultModel.configs ?? {}).some((name) =>
  name.includes("bootstrap_s3") || name.includes("backup_key")
)) {
  fail("Vault overlay retains an obsolete bootstrap or backup-key config");
}
NODE

printf '%s\n' \
    "compose secret contract test: secret mounts, groups, and Vault renderer are valid"
