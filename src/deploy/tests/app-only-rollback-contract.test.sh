#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
rollback_script=$deployment_dir/scripts/rollback-application.sh
temporary_dir=$(mktemp -d)
trap 'rm -rf "$temporary_dir"' EXIT HUP INT TERM
fail() { printf '%s\n' "app-only rollback contract test: $*" >&2; exit 1; }

current_sha=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
target_sha=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
current_app_digest=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
target_app_digest=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
current_infra_digest=cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc
old_infra_digest=dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd
current_env=$temporary_dir/current.env
target_env=$temporary_dir/target.env
active_env=$temporary_dir/active.env
docker_log=$temporary_dir/docker.log
registry_dir=$temporary_dir/registry-auth
registry_username=hook2stream-staging-pull
registry_credential_identity=hook2stream-staging-0123456789abcdef0123456789abcdef
mkdir -m 0700 "$registry_dir"
registry_auth=$(printf '%s' "$registry_username:fixture-read-packages-only" | base64 | tr -d '\n')
printf '%s\n' "{\"auths\":{\"ghcr.io\":{\"auth\":\"$registry_auth\"}}}" \
    > "$registry_dir/config.json"
chmod 0600 "$registry_dir/config.json"
registry_auth_sha256=$(printf '%s' "$registry_auth" | sha256sum | awk '{ print $1 }')
printf '%s\n' \
    'schema=hook2stream-ghcr-pull-identity-v1' \
    'environment=staging' \
    "username=$registry_username" \
    "credential_identity=$registry_credential_identity" \
    'operator_attests_read_packages_only=true' \
    'operator_attests_environment_exclusive=true' \
    'scope_verification=provider-unavailable' \
    > "$registry_dir/identity.attestation"
chmod 0600 "$registry_dir/identity.attestation"
registry_identity_sha256=$(sha256sum "$registry_dir/identity.attestation" | awk '{ print $1 }')

write_environment() {
    environment_path=$1
    release_sha=$2
    app_digest=$3
    infra_digest=$4
    cat > "$environment_path" <<EOF
DEPLOYMENT_ENVIRONMENT=staging
PUBLIC_ORIGIN=https://staging.hook2stream.com
SECRET_PROVIDER=file
STORAGE_MODE=external
BILLING_MODE=stripe
RELEASE_VERSION=$release_sha
API_IMAGE=registry.example/hook2stream-api@sha256:$app_digest
WORKER_IMAGE=registry.example/hook2stream-worker@sha256:$app_digest
WEB_IMAGE=registry.example/hook2stream-web@sha256:$app_digest
BOOTSTRAPPER_IMAGE=registry.example/hook2stream-bootstrapper@sha256:$infra_digest
POSTGRES_BACKUP_IMAGE=registry.example/hook2stream-postgres-backup@sha256:$infra_digest
CADDY_IMAGE=registry.example/caddy@sha256:$infra_digest
POSTGRES_IMAGE=registry.example/hook2stream-postgres@sha256:$infra_digest
PGBOUNCER_IMAGE=registry.example/pgbouncer@sha256:$infra_digest
EGRESS_PROXY_IMAGE=registry.example/squid@sha256:$infra_digest
EOF
    chmod 0600 "$environment_path"
}
write_environment "$current_env" "$current_sha" "$current_app_digest" "$current_infra_digest"
write_environment "$target_env" "$target_sha" "$target_app_digest" "$old_infra_digest"

# A pre-change target may carry an unsafe rollback implementation. The trusted
# rollback orchestrator receives only the target environment, never this code.
prechange_target_bundle=$temporary_dir/prechange-target/deploy/scripts
prechange_execution_marker=$temporary_dir/prechange-target-executed
mkdir -p "$prechange_target_bundle"
printf '%s\n' '#!/bin/sh' "touch '$prechange_execution_marker'" \
    > "$prechange_target_bundle/rollback-application.sh"
chmod 0700 "$prechange_target_bundle/rollback-application.sh"

mkdir -p "$temporary_dir/bin"
cat > "$temporary_dir/bin/docker" <<'EOF'
#!/bin/sh
set -eu
if [ "${1:-}" = --config ]; then
    [ "${2:-}" = "$ROLLBACK_EXPECTED_DOCKER_CONFIG" ] \
        || { printf '%s\n' 'unexpected Docker config path' >&2; exit 1; }
    shift 2
fi
printf '%s\n' "$*" >> "$ROLLBACK_DOCKER_LOG"
if [ "${1:-}" = compose ] && [ "${2:-}" = version ]; then
    exit 0
fi
if [ "${1:-}" = compose ]; then
    case " $* " in
        *' config --quiet '*) exit 0 ;;
        *' ps -q '*)
            service=
            for argument in "$@"; do service=$argument; done
            printf 'cid-%s\n' "$service"
            exit 0
            ;;
        *' pull '*|*' up -d --no-deps '*|*' exec -T '*) exit 0 ;;
        *) printf '%s\n' "unexpected docker compose invocation: $*" >&2; exit 1 ;;
    esac
fi
if [ "${1:-}" = inspect ] && [ "${2:-}" = --format ]; then
    format=$3
    container=$4
    case "$format" in
        *State.Health*) printf '%s\n' healthy; exit 0 ;;
        *Config.Image*)
            service=${container#cid-}
            case "$service" in
                api)
                    printf '%s\n' "registry.example/hook2stream-api@sha256:$ROLLBACK_TARGET_APP_DIGEST"
                    ;;
                worker-media|worker-analysis|worker-control|worker-render|worker-export)
                    printf '%s\n' "registry.example/hook2stream-worker@sha256:$ROLLBACK_TARGET_APP_DIGEST"
                    ;;
                web)
                    printf '%s\n' "registry.example/hook2stream-web@sha256:$ROLLBACK_TARGET_APP_DIGEST"
                    ;;
                postgres-backup|storage-janitor)
                    printf '%s\n' "registry.example/hook2stream-postgres-backup@sha256:$ROLLBACK_CURRENT_INFRA_DIGEST"
                    ;;
                caddy)
                    printf '%s\n' "registry.example/caddy@sha256:$ROLLBACK_CURRENT_INFRA_DIGEST"
                    ;;
                postgres)
                    printf '%s\n' "registry.example/hook2stream-postgres@sha256:$ROLLBACK_CURRENT_INFRA_DIGEST"
                    ;;
                pgbouncer)
                    printf '%s\n' "registry.example/pgbouncer@sha256:$ROLLBACK_CURRENT_INFRA_DIGEST"
                    ;;
                egress-api|egress-s3|egress-control|egress-backup)
                    printf '%s\n' "registry.example/squid@sha256:$ROLLBACK_CURRENT_INFRA_DIGEST"
                    ;;
                *) printf '%s\n' "unexpected inspected service: $service" >&2; exit 1 ;;
            esac
            exit 0
            ;;
    esac
fi
if [ "${1:-}" = image ] && [ "${2:-}" = pull ]; then
    [ "${ROLLBACK_FAIL_PULL:-false}" != true ] || exit 17
    exit 0
fi
printf '%s\n' "unexpected docker invocation: $*" >&2
exit 1
EOF
cat > "$temporary_dir/bin/curl" <<'EOF'
#!/bin/sh
printf '%s' 200
EOF
cat > "$temporary_dir/bin/jq" <<'EOF'
#!/usr/bin/env node
const fs = require("fs");
const args = process.argv.slice(2);
const parsed = JSON.parse(fs.readFileSync(args.at(-1), "utf8"));
const auth = parsed?.auths?.["ghcr.io"]?.auth;
if (args.includes("-jr")) { process.stdout.write(auth ?? ""); process.exit(typeof auth === "string" ? 0 : 1); }
const index = args.indexOf("--arg");
const username = index >= 0 ? args[index + 2] : "";
const credential = typeof auth === "string" ? Buffer.from(auth, "base64").toString("utf8") : "";
const exact = parsed && Object.keys(parsed).length === 1 &&
  Object.keys(parsed.auths ?? {}).length === 1 &&
  Object.keys(parsed.auths?.["ghcr.io"] ?? {}).length === 1;
process.exit(exact && credential.startsWith(`${username}:`) &&
  credential.length > username.length + 1 && credential.split(":").length === 2 ? 0 : 1);
EOF
chmod 0755 "$temporary_dir/bin/docker" "$temporary_dir/bin/curl" "$temporary_dir/bin/jq"

duplicate_target_env=$temporary_dir/duplicate-target.env
cp "$target_env" "$duplicate_target_env"
printf '%s\n' "API_IMAGE=registry.example/hook2stream-api@sha256:$target_app_digest" \
    >> "$duplicate_target_env"
duplicate_docker_log=$temporary_dir/duplicate-docker.log
if PATH=$temporary_dir/bin:$PATH \
    ROLLBACK_DOCKER_LOG=$duplicate_docker_log \
    ROLLBACK_TARGET_APP_DIGEST=$target_app_digest \
    ROLLBACK_CURRENT_INFRA_DIGEST=$current_infra_digest \
        "$rollback_script" "$current_env" "$duplicate_target_env" \
        "$temporary_dir/duplicate-active.env" "$target_sha" "$deployment_dir" >/dev/null 2>&1; then
    fail "duplicate target image variable was accepted"
fi
[ ! -s "$duplicate_docker_log" ] \
    || fail "invalid recorded environment caused a Docker operation"

PATH=$temporary_dir/bin:$PATH \
ROLLBACK_DOCKER_LOG=$docker_log \
ROLLBACK_EXPECTED_DOCKER_CONFIG=$registry_dir \
ROLLBACK_TARGET_APP_DIGEST=$target_app_digest \
ROLLBACK_CURRENT_INFRA_DIGEST=$current_infra_digest \
DOCKER_CONFIG=$registry_dir \
HOOK2STREAM_GHCR_USERNAME=$registry_username \
HOOK2STREAM_GHCR_AUTH_SHA256=$registry_auth_sha256 \
HOOK2STREAM_GHCR_CREDENTIAL_IDENTITY=$registry_credential_identity \
HOOK2STREAM_GHCR_IDENTITY_SHA256=$registry_identity_sha256 \
HOOK2STREAM_HEALTH_TIMEOUT_SECONDS=2 \
HOOK2STREAM_PUBLIC_SMOKE_TIMEOUT_SECONDS=5 \
    "$rollback_script" "$current_env" "$target_env" "$active_env" "$target_sha" "$deployment_dir" >/dev/null
[ ! -e "$prechange_execution_marker" ] \
    || fail "rollback executed control-plane code from an adversarial pre-change target bundle"

read_value() {
    awk -F= -v requested="$1" '$1 == requested {print substr($0,index($0,"=")+1)}' "$active_env"
}
[ "$(read_value RELEASE_VERSION)" = "$target_sha" ] \
    || fail "active environment did not select the target RELEASE_VERSION"
[ "$(read_value API_IMAGE)" = "registry.example/hook2stream-api@sha256:$target_app_digest" ] \
    || fail "API_IMAGE did not come from the rollback target"
[ "$(read_value WORKER_IMAGE)" = "registry.example/hook2stream-worker@sha256:$target_app_digest" ] \
    || fail "WORKER_IMAGE did not come from the rollback target"
[ "$(read_value WEB_IMAGE)" = "registry.example/hook2stream-web@sha256:$target_app_digest" ] \
    || fail "WEB_IMAGE did not come from the rollback target"
for infrastructure_variable in \
    BOOTSTRAPPER_IMAGE POSTGRES_BACKUP_IMAGE CADDY_IMAGE POSTGRES_IMAGE \
    PGBOUNCER_IMAGE EGRESS_PROXY_IMAGE; do
    case "$(read_value "$infrastructure_variable")" in
        *@sha256:"$current_infra_digest") ;;
        *) fail "$infrastructure_variable did not remain at the current digest" ;;
    esac
done

mutation_log=$temporary_dir/mutations.log
awk '
    /^image pull / {print}
    /^compose / && ($0 ~ / up -d / || $0 ~ / run / || $0 ~ / create / || $0 ~ / restart /) {print}
' "$docker_log" > "$mutation_log"
[ -s "$mutation_log" ] || fail "test did not observe application mutations"
for application_image in hook2stream-api hook2stream-worker hook2stream-web; do
    grep -Fq "image pull registry.example/$application_image@sha256:$target_app_digest" "$mutation_log" \
        || fail "$application_image target digest was not pulled"
done
for application_service in api worker-media worker-analysis worker-control worker-render worker-export web; do
    grep -Fq "$application_service" "$mutation_log" \
        || fail "$application_service was not rolled back"
done
if grep -Eq 'bootstrapper|postgres-backup|storage-janitor|(^|[/ ])caddy(@| |$)|(^|[/ ])postgres(@| |$)|(^|[/ ])pgbouncer(@| |$)|egress-(api|s3|control|backup)' "$mutation_log"; then
    fail "rollback invoked a mutating command for bootstrapper or infrastructure"
fi
if grep -Eq '(^| )run( |$)|migrat|bootstrap' "$mutation_log"; then
    fail "rollback invoked a one-shot job, migration, or bootstrapper"
fi

grep -Fq ' exec -T api /bin/sh /opt/hook2stream/http-healthcheck.sh' "$docker_log" \
    || fail "internal API smoke was not executed"

failed_active=$temporary_dir/failed-active.env
printf '%s\n' 'preexisting-state-must-survive' > "$failed_active"
failed_active_sha=$(sha256sum "$failed_active" | awk '{ print $1 }')
if PATH=$temporary_dir/bin:$PATH \
    ROLLBACK_DOCKER_LOG=$temporary_dir/failed-pull-docker.log \
    ROLLBACK_EXPECTED_DOCKER_CONFIG=$registry_dir \
    ROLLBACK_TARGET_APP_DIGEST=$target_app_digest \
    ROLLBACK_CURRENT_INFRA_DIGEST=$current_infra_digest \
    ROLLBACK_FAIL_PULL=true \
    DOCKER_CONFIG=$registry_dir \
    HOOK2STREAM_GHCR_USERNAME=$registry_username \
    HOOK2STREAM_GHCR_AUTH_SHA256=$registry_auth_sha256 \
    HOOK2STREAM_GHCR_CREDENTIAL_IDENTITY=$registry_credential_identity \
    HOOK2STREAM_GHCR_IDENTITY_SHA256=$registry_identity_sha256 \
    HOOK2STREAM_HEALTH_TIMEOUT_SECONDS=2 \
    HOOK2STREAM_PUBLIC_SMOKE_TIMEOUT_SECONDS=5 \
        "$rollback_script" "$current_env" "$target_env" "$failed_active" \
        "$target_sha" "$deployment_dir" >/dev/null 2>&1; then
    fail "rollback succeeded after an authenticated image pull failed"
fi
[ "$(sha256sum "$failed_active" | awk '{ print $1 }')" = "$failed_active_sha" ] \
    || fail "failed image pull published or replaced the active rollback environment"
printf '%s\n' "app-only rollback contract test: application-only mutation, current infrastructure digests, smoke and exact digest checks passed"
