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
RELEASE_VERSION=$release_sha
API_IMAGE=registry.example/hook2stream-api@sha256:$app_digest
WORKER_IMAGE=registry.example/hook2stream-worker@sha256:$app_digest
WEB_IMAGE=registry.example/hook2stream-web@sha256:$app_digest
BOOTSTRAPPER_IMAGE=registry.example/hook2stream-bootstrapper@sha256:$infra_digest
POSTGRES_BACKUP_IMAGE=registry.example/hook2stream-postgres-backup@sha256:$infra_digest
CADDY_IMAGE=registry.example/caddy@sha256:$infra_digest
POSTGRES_IMAGE=registry.example/postgres@sha256:$infra_digest
PGBOUNCER_IMAGE=registry.example/pgbouncer@sha256:$infra_digest
EGRESS_PROXY_IMAGE=registry.example/squid@sha256:$infra_digest
EOF
    chmod 0600 "$environment_path"
}
write_environment "$current_env" "$current_sha" "$current_app_digest" "$current_infra_digest"
write_environment "$target_env" "$target_sha" "$target_app_digest" "$old_infra_digest"

mkdir -p "$temporary_dir/bin"
cat > "$temporary_dir/bin/docker" <<'EOF'
#!/bin/sh
set -eu
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
                    printf '%s\n' "registry.example/postgres@sha256:$ROLLBACK_CURRENT_INFRA_DIGEST"
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
    exit 0
fi
printf '%s\n' "unexpected docker invocation: $*" >&2
exit 1
EOF
cat > "$temporary_dir/bin/curl" <<'EOF'
#!/bin/sh
printf '%s' 200
EOF
chmod 0755 "$temporary_dir/bin/docker" "$temporary_dir/bin/curl"

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
        "$temporary_dir/duplicate-active.env" "$target_sha" >/dev/null 2>&1; then
    fail "duplicate target image variable was accepted"
fi
[ ! -s "$duplicate_docker_log" ] \
    || fail "invalid recorded environment caused a Docker operation"

PATH=$temporary_dir/bin:$PATH \
ROLLBACK_DOCKER_LOG=$docker_log \
ROLLBACK_TARGET_APP_DIGEST=$target_app_digest \
ROLLBACK_CURRENT_INFRA_DIGEST=$current_infra_digest \
HOOK2STREAM_HEALTH_TIMEOUT_SECONDS=2 \
HOOK2STREAM_PUBLIC_SMOKE_TIMEOUT_SECONDS=5 \
    "$rollback_script" "$current_env" "$target_env" "$active_env" "$target_sha" >/dev/null

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
printf '%s\n' "app-only rollback contract test: application-only mutation, current infrastructure digests, smoke and exact digest checks passed"
