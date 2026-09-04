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
    billing_mode=$2
    requested_gid=${3:-}

    (
        unset SECRETS_GID
        SECRET_PROVIDER=file
        SECRETS_DIR=$temporary_dir
        BILLING_MODE=$billing_mode
        STRIPE_PRICE_ART_CREDITS_5=price_test_art
        STRIPE_PRICE_MINI_RELEASE=price_test_mini
        STRIPE_PRICE_RELEASE_PACK=price_test_pack
        STRIPE_PRICE_CLEAN_COVER=price_test_cover
        STRIPE_PRICE_ACTIVE_ARTIST=price_test_artist
        export \
            SECRET_PROVIDER SECRETS_DIR BILLING_MODE \
            STRIPE_PRICE_ART_CREDITS_5 STRIPE_PRICE_MINI_RELEASE \
            STRIPE_PRICE_RELEASE_PACK STRIPE_PRICE_CLEAN_COVER \
            STRIPE_PRICE_ACTIVE_ARTIST
        if [ -n "$requested_gid" ]; then
            SECRETS_GID=$requested_gid
            export SECRETS_GID
        fi

        case "$billing_mode" in
            disabled)
                docker compose \
                    --env-file "$deployment_dir/.env.example" \
                    --profile tools \
                    -f "$deployment_dir/compose.yaml" \
                    config --format json
                ;;
            stripe)
                docker compose \
                    --env-file "$deployment_dir/.env.example" \
                    --profile tools \
                    -f "$deployment_dir/compose.yaml" \
                    -f "$deployment_dir/compose.billing-stripe.yaml" \
                    config --format json
                ;;
        esac
    ) > "$destination"
}

render_model "$temporary_dir/default.json" disabled
render_model "$temporary_dir/override.json" disabled 2468
render_model "$temporary_dir/stripe.json" stripe

render_vault_model() {
    destination=$1
    billing_mode=$2
    (
        unset SECRETS_GID
        SECRET_PROVIDER=vault
        SECRETS_DIR=$temporary_dir
        BILLING_MODE=$billing_mode
        VAULT_AUTH_DIR=$temporary_dir/vault-auth
        VAULT_CACERT=$temporary_dir/vault-ca.pem
        VAULT_CANDIDATE_DIR=$temporary_dir/vault-candidate
        STRIPE_PRICE_ART_CREDITS_5=price_test_art
        STRIPE_PRICE_MINI_RELEASE=price_test_mini
        STRIPE_PRICE_RELEASE_PACK=price_test_pack
        STRIPE_PRICE_CLEAN_COVER=price_test_cover
        STRIPE_PRICE_ACTIVE_ARTIST=price_test_artist
        export \
            SECRET_PROVIDER \
            SECRETS_DIR \
            BILLING_MODE \
            VAULT_AUTH_DIR \
            VAULT_CACERT \
            VAULT_CANDIDATE_DIR \
            STRIPE_PRICE_ART_CREDITS_5 \
            STRIPE_PRICE_MINI_RELEASE \
            STRIPE_PRICE_RELEASE_PACK \
            STRIPE_PRICE_CLEAN_COVER \
            STRIPE_PRICE_ACTIVE_ARTIST
        case "$billing_mode" in
            disabled)
                docker compose \
                    --env-file "$deployment_dir/.env.example" \
                    --profile tools \
                    -f "$deployment_dir/compose.yaml" \
                    -f "$deployment_dir/compose.vault.yaml" \
                    config --format json
                ;;
            stripe)
                docker compose \
                    --env-file "$deployment_dir/.env.example" \
                    --profile tools \
                    -f "$deployment_dir/compose.yaml" \
                    -f "$deployment_dir/compose.billing-stripe.yaml" \
                    -f "$deployment_dir/compose.vault.yaml" \
                    config --format json
                ;;
        esac
    ) > "$destination"
}

render_vault_model "$temporary_dir/vault.json" disabled
render_vault_model "$temporary_dir/vault-stripe.json" stripe

node - \
    "$temporary_dir/default.json" \
    "$temporary_dir/override.json" \
    "$temporary_dir/stripe.json" \
    "$temporary_dir/vault.json" \
    "$temporary_dir/vault-stripe.json" <<'NODE'
const fs = require("node:fs");

const expectedSecrets = {
  api: [
    "google_client_secret",
    "postgres_password",
    "s3_runtime_access_key",
    "s3_runtime_secret_key",
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

function expectedSecretsForBillingMode(billingMode) {
  const selected = Object.fromEntries(
    Object.entries(expectedSecrets).map(([service, secrets]) => [service, [...secrets]]),
  );
  if (billingMode === "stripe") {
    selected.api.push("stripe_secret_key", "stripe_webhook_secret");
  }
  return selected;
}

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

function assertModel(path, expectedGid, billingMode) {
  const model = JSON.parse(fs.readFileSync(path, "utf8"));
  const mountedSecrets = new Set();
  const selectedExpectedSecrets = expectedSecretsForBillingMode(billingMode);
  const apiEnvironment = model.services?.api?.environment ?? {};
  const priceEnvironment = {
    Stripe__PriceIds__art_credits_5: "price_test_art",
    Stripe__PriceIds__mini_release: "price_test_mini",
    Stripe__PriceIds__release_pack: "price_test_pack",
    Stripe__PriceIds__clean_cover: "price_test_cover",
    Stripe__PriceIds__active_artist: "price_test_artist",
  };

  if (billingMode === "disabled") {
    if (apiEnvironment.Stripe__Mode !== "Disabled") {
      fail("billing-disabled API does not render Stripe__Mode=Disabled");
    }
    for (const forbiddenName of [
      "STRIPE_SECRET_KEY_FILE",
      "STRIPE_WEBHOOK_SECRET_FILE",
      ...Object.keys(priceEnvironment),
    ]) {
      if (Object.hasOwn(apiEnvironment, forbiddenName)) {
        fail(`billing-disabled API retained ${forbiddenName}`);
      }
    }
    const residualStripeNames = Object.keys(apiEnvironment).filter(
      (name) => name !== "Stripe__Mode" &&
        (name.startsWith("Stripe__") || name.startsWith("STRIPE_")),
    );
    if (residualStripeNames.length > 0) {
      fail(`billing-disabled API retained Stripe configuration: ${residualStripeNames.join(",")}`);
    }
  } else {
    if (apiEnvironment.Stripe__Mode !== "Stripe") {
      fail("Stripe API overlay does not render Stripe__Mode=Stripe");
    }
    if (
      apiEnvironment.STRIPE_SECRET_KEY_FILE !== "/run/secrets/stripe_secret_key" ||
      apiEnvironment.STRIPE_WEBHOOK_SECRET_FILE !== "/run/secrets/stripe_webhook_secret"
    ) {
      fail("Stripe API overlay does not render exact secret file paths");
    }
    for (const [name, expectedValue] of Object.entries(priceEnvironment)) {
      if (apiEnvironment[name] !== expectedValue) {
        fail(`Stripe API overlay rendered an unexpected ${name}`);
      }
    }
  }

  for (const [serviceName, secretNames] of Object.entries(selectedExpectedSecrets)) {
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
    if (actualSecrets.length > 0 && !(serviceName in selectedExpectedSecrets)) {
      fail(`unexpected secret-consuming service ${serviceName}`);
    }

    if (!(serviceName in selectedExpectedSecrets) && (service.group_add ?? []).length > 0) {
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

assertModel(process.argv[2], "2000", "disabled");
assertModel(process.argv[3], "2468", "disabled");
assertModel(process.argv[4], "2000", "stripe");
assertModel(process.argv[5], "2000", "disabled");
assertModel(process.argv[6], "2000", "stripe");

const vaultModel = JSON.parse(fs.readFileSync(process.argv[5], "utf8"));
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
if (vaultRenderer.environment?.BILLING_MODE !== "disabled") {
  fail("disabled Vault renderer must receive BILLING_MODE=disabled");
}
const vaultStripeModel = JSON.parse(fs.readFileSync(process.argv[6], "utf8"));
if (vaultStripeModel.services?.["vault-renderer"]?.environment?.BILLING_MODE !== "stripe") {
  fail("Stripe Vault renderer must receive BILLING_MODE=stripe");
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
