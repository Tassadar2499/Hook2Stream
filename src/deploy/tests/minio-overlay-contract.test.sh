#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
overlay=${deployment_dir}/compose.minio.yaml
base_compose=${deployment_dir}/compose.yaml
caddyfile=${deployment_dir}/Caddyfile.minio
minio_dir=${deployment_dir}/minio

fail() {
    printf '%s\n' "MinIO overlay contract test: $*" >&2
    exit 1
}

service_block() {
    service_name=$1
    awk -v marker="  ${service_name}:" '
        $0 == marker {
            found = 1
            print
            next
        }
        found && /^  [A-Za-z0-9_-]+:$/ { exit }
        found { print }
    ' "$overlay"
}

assert_contains() {
    text=$1
    expected=$2
    message=$3
    printf '%s\n' "$text" | grep -Fq -- "$expected" || fail "$message"
}

[ -r "$overlay" ] || fail "compose.minio.yaml is missing"
[ -r "$caddyfile" ] || fail "Caddyfile.minio is missing"
sh -n "$minio_dir/minio-entrypoint.sh" "$minio_dir/minio-init.sh"

entrypoint_test_root=$(mktemp -d)
printf '%s\n' root-secret-value > "$entrypoint_test_root/root-password"
printf 'root-admin\nsecond-line\n' > "$entrypoint_test_root/root-user"
if STORAGE_MODE=minio \
    MINIO_ROOT_USER_FILE="$entrypoint_test_root/root-user" \
    MINIO_ROOT_PASSWORD_FILE="$entrypoint_test_root/root-password" \
    sh "$minio_dir/minio-entrypoint.sh" /bin/true >/dev/null 2>&1; then
    fail "MinIO entrypoint accepted a multiline root credential"
fi
printf 'root-admin\n\n' > "$entrypoint_test_root/root-user"
if STORAGE_MODE=minio \
    MINIO_ROOT_USER_FILE="$entrypoint_test_root/root-user" \
    MINIO_ROOT_PASSWORD_FILE="$entrypoint_test_root/root-password" \
    sh "$minio_dir/minio-entrypoint.sh" /bin/true >/dev/null 2>&1; then
    fail "MinIO entrypoint accepted more than one trailing newline"
fi
rm -rf "$entrypoint_test_root"

caddy_service=$(service_block caddy)
bootstrap_service=$(service_block bootstrapper)
postgres_service=$(service_block postgres)
media_worker=$(service_block worker-media)
analysis_worker=$(service_block worker-analysis)
render_worker=$(service_block worker-render)
minio_service=$(service_block minio)

assert_contains "$caddy_service" \
    'S3_MEDIA_BUCKET: ${S3_MEDIA_BUCKET:?Set S3_MEDIA_BUCKET}' \
    "Caddy does not receive the exact media bucket name"
assert_contains "$bootstrap_service" \
    'Storage__ConfigureBucketCors: "false"' \
    "the MinIO bootstrapper must not call unsupported PutBucketCors"
assert_contains "$bootstrap_service" \
    'Storage__ConfigureMultipartAbortLifecycle: "false"' \
    "the MinIO bootstrapper must not submit the unsupported abort lifecycle rule"
grep -Fq 'Storage__ConfigureBucketCors: "true"' "$base_compose" \
    || fail "external S3 mode no longer retains bucket-level CORS bootstrap"
assert_contains "$minio_service" \
    'MINIO_API_CORS_ALLOW_ORIGIN: "off"' \
    "cluster-wide MinIO CORS must be disabled"

grep -Fq 'path /{$S3_MEDIA_BUCKET}/*' "$caddyfile" \
    || fail "CORS is not scoped to the path-style media bucket"
grep -Fq 'header Origin https://{$APP_DOMAIN}' "$caddyfile" \
    || fail "CORS does not require the exact application Origin"
grep -Fq 'method OPTIONS' "$caddyfile" \
    || fail "media preflight does not match OPTIONS"
grep -Fq 'respond 204' "$caddyfile" \
    || fail "media preflight does not terminate with 204"
grep -Fq 'Access-Control-Allow-Methods "GET, HEAD, PUT, POST"' "$caddyfile" \
    || fail "media preflight methods are incomplete or overly broad"
grep -Fq 'Access-Control-Allow-Headers "{http.request.header.Access-Control-Request-Headers}"' \
    "$caddyfile" || fail "media preflight does not authorize requested headers"
grep -Fq 'Access-Control-Expose-Headers "ETag"' "$caddyfile" \
    || fail "media CORS does not expose ETag"
grep -Fq 'header_up Host {http.request.hostport}' "$caddyfile" \
    || fail "the complete SigV4 Host header, including a non-default port, is not preserved"
grep -Fq '/minio/metrics/*' "$caddyfile" \
    || fail "the current MinIO metrics API is publicly proxied"
[ "$(grep -Fc 'Access-Control-Allow-Origin "https://{$APP_DOMAIN}"' "$caddyfile")" -eq 2 ] \
    || fail "CORS headers must exist only in media preflight and media proxy handlers"
if grep -Fq 'BACKUP_S3_BUCKET' "$caddyfile"; then
    fail "the backup bucket must not receive a browser CORS route"
fi
if grep -Fq 'Access-Control-Allow-Origin "*"' "$caddyfile"; then
    fail "wildcard CORS origins are forbidden"
fi

assert_contains "$postgres_service" 'shared_buffers=512MB' \
    "PostgreSQL shared_buffers must be exactly 512MB"
assert_contains "$postgres_service" 'memory: 2G' \
    "PostgreSQL memory limit must be exactly 2G"
assert_contains "$media_worker" 'memory: 1G' \
    "media worker memory limit must be at most 1G"
assert_contains "$analysis_worker" 'memory: 1G' \
    "analysis worker memory limit must be at most 1G"
assert_contains "$render_worker" 'memory: 3G' \
    "render worker memory limit must be exactly 3G"
if grep -Eq '^[[:space:]]+replicas:' "$overlay"; then
    fail "the 16 GiB staging profile must not scale worker replicas"
fi

node - "$minio_dir/backup-lifecycle.json" "$minio_dir/policies" <<'NODE'
const fs = require("node:fs");
const path = require("node:path");

const lifecycle = JSON.parse(fs.readFileSync(process.argv[2], "utf8"));
if (!Array.isArray(lifecycle.Rules) || lifecycle.Rules.length !== 1) {
  throw new Error("backup lifecycle must contain exactly one audited rule");
}
const rule = lifecycle.Rules[0];
if (rule.Status !== "Enabled" ||
    rule.ID !== "hook2stream-staging-backup-retention-7d" ||
    rule.Expiration?.Days !== 6 ||
    rule.NoncurrentVersionExpiration?.NoncurrentDays !== 1) {
  throw new Error("backup lifecycle must retain current+noncurrent data for at most 7 days");
}

for (const name of ["runtime-media.json", "bootstrap-media.json", "postgres-backup.json"]) {
  const policy = JSON.parse(fs.readFileSync(path.join(process.argv[3], name), "utf8"));
  for (const statement of policy.Statement ?? []) {
    const actions = Array.isArray(statement.Action) ? statement.Action : [statement.Action];
    if (actions.includes("*") || actions.some((action) => String(action).endsWith(":*"))) {
      throw new Error(`${name} contains a wildcard IAM action`);
    }
  }
}
NODE

test_root=$(mktemp -d)
trap 'rm -rf "$test_root"' EXIT HUP INT TERM
mkdir -p "$test_root/bin" "$test_root/policy-config" "$test_root/secrets"
cp "$minio_dir/backup-lifecycle.json" "$test_root/policy-config/backup-lifecycle.json"
cp "$minio_dir/policies/runtime-media.json" "$test_root/policy-config/runtime-media.json"
cp "$minio_dir/policies/bootstrap-media.json" "$test_root/policy-config/bootstrap-media.json"
cp "$minio_dir/policies/postgres-backup.json" "$test_root/policy-config/postgres-backup.json"
cat > "$test_root/bin/mc" <<'MC_STUB'
#!/bin/sh
set -eu
printf '%s\n' "$*" >> "$MC_LOG"
MC_STUB
chmod 0755 "$test_root/bin/mc"

printf '%s\n' root-admin > "$test_root/secrets/minio_root_user"
printf '%s\n' root-secret-value > "$test_root/secrets/minio_root_password"
printf '%s\n' runtime-user > "$test_root/secrets/s3_runtime_access_key"
printf '%s\n' runtime-secret-value > "$test_root/secrets/s3_runtime_secret_key"
printf '%s\n' bootstrap-user > "$test_root/secrets/s3_bootstrap_access_key"
printf '%s\n' bootstrap-secret-value > "$test_root/secrets/s3_bootstrap_secret_key"
printf '%s\n' backup-user > "$test_root/secrets/backup_s3_access_key"
printf '%s\n' backup-secret-value > "$test_root/secrets/backup_s3_secret_key"
: > "$test_root/mc.log"

run_init() {
    PATH="$test_root/bin:$PATH" \
    MC_LOG="$test_root/mc.log" \
    STORAGE_MODE=minio \
    MINIO_ENDPOINT=http://minio:9000 \
    MINIO_REGION=us-east-1 \
    MINIO_MEDIA_BUCKET=hook2stream-staging-media \
    MINIO_BACKUP_BUCKET=hook2stream-staging-pg-backups \
    MINIO_BACKUP_PREFIX=hook2stream/staging/postgres \
    MINIO_MEDIA_QUOTA_GIB=180 \
    MINIO_BACKUP_QUOTA_GIB=20 \
    MINIO_ROOT_USER_FILE="$test_root/secrets/minio_root_user" \
    MINIO_ROOT_PASSWORD_FILE="$test_root/secrets/minio_root_password" \
    S3_RUNTIME_ACCESS_KEY_FILE="$test_root/secrets/s3_runtime_access_key" \
    S3_RUNTIME_SECRET_KEY_FILE="$test_root/secrets/s3_runtime_secret_key" \
    S3_BOOTSTRAP_ACCESS_KEY_FILE="$test_root/secrets/s3_bootstrap_access_key" \
    S3_BOOTSTRAP_SECRET_KEY_FILE="$test_root/secrets/s3_bootstrap_secret_key" \
    BACKUP_S3_ACCESS_KEY_FILE="$test_root/secrets/backup_s3_access_key" \
    BACKUP_S3_SECRET_KEY_FILE="$test_root/secrets/backup_s3_secret_key" \
    MC_CONFIG_DIR="$test_root/mc-config" \
    MINIO_POLICY_DIR="$test_root/policy-config" \
    sh "$minio_dir/minio-init.sh" >/dev/null
}

run_init
run_init

[ "$(grep -Fc 'mb --ignore-existing' "$test_root/mc.log")" -eq 4 ] \
    || fail "repeatable bucket creation must use --ignore-existing"
[ "$(grep -Fc 'version suspend hook2stream/hook2stream-staging-media' "$test_root/mc.log")" -eq 2 ] \
    || fail "every init run must enforce suspended media versioning"
[ "$(grep -Fc 'version enable hook2stream/hook2stream-staging-pg-backups' "$test_root/mc.log")" -eq 2 ] \
    || fail "every init run must enforce backup versioning"
[ "$(grep -Fc 'admin policy create' "$test_root/mc.log")" -eq 6 ] \
    || fail "every init run must upsert all three policies"
[ "$(grep -Fc 'admin user add' "$test_root/mc.log")" -eq 6 ] \
    || fail "every init run must upsert all three users"

printf '%s\n' "MinIO overlay contract test: passed"
