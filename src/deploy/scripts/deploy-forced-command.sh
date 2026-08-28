#!/bin/sh
set -eu
set -f
umask 077
PATH=/usr/sbin:/usr/bin:/sbin:/bin
export PATH
unset CDPATH ENV BASH_ENV

fail() { printf '%s\n' "forced deploy: $*" >&2; exit 1; }
[ "$#" -eq 0 ] || fail "internal invocation arguments are forbidden"
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/lib/forced-command-trust.sh"
read_deployment_environment() {
  env_file=$1
  env_count=$(awk -F= '$1 == "DEPLOYMENT_ENVIRONMENT" {count++} END {print count + 0}' "$env_file")
  [ "$env_count" -eq 1 ] || fail "environment file must contain exactly one DEPLOYMENT_ENVIRONMENT"
  env_value=$(awk -F= '$1 == "DEPLOYMENT_ENVIRONMENT" {print substr($0,index($0,"=")+1)}' "$env_file")
  case "$env_value" in staging|production) printf '%s\n' "$env_value" ;; *) fail "DEPLOYMENT_ENVIRONMENT must be staging or production" ;; esac
}
[ "$(id -u)" -eq 0 ] || fail "wrapper must run as root through the exact sudoers rule"
: "${HOOK2STREAM_ENV_FILE:?HOOK2STREAM_ENV_FILE is required}"
: "${HOOK2STREAM_RELEASES_DIR:=/srv/hook2stream/releases}"
: "${HOOK2STREAM_RELEASE_STATE_DIR:=/srv/hook2stream/release-state}"
: "${HOOK2STREAM_E2E_HOOK:?HOOK2STREAM_E2E_HOOK must name the root-owned post-deploy E2E hook}"
: "${MIN_ROLLBACK_RELEASE_SHA:?MIN_ROLLBACK_RELEASE_SHA must identify the first approved H2SE release}"
[ "$HOOK2STREAM_RELEASES_DIR" = /srv/hook2stream/releases ] \
  || fail "release directory is not canonical"
[ "$HOOK2STREAM_RELEASE_STATE_DIR" = /srv/hook2stream/release-state ] \
  || fail "release-state directory is not canonical"
case "$HOOK2STREAM_ENV_FILE" in
  /srv/hook2stream/config/staging.env|/srv/hook2stream/config/production.env) ;;
  *) fail "environment file path is not canonical" ;;
esac
hook2stream_trusted_directory /srv/hook2stream 0:0 755 \
  || fail "/srv/hook2stream must be root:root mode 0755"
hook2stream_trusted_directory /srv/hook2stream/config 0:0 700 \
  || fail "configuration directory must be root:root mode 0700"
hook2stream_trusted_directory "$HOOK2STREAM_RELEASES_DIR" 0:0 700 \
  || fail "releases directory must be root:root mode 0700"
hook2stream_trusted_directory "$HOOK2STREAM_RELEASE_STATE_DIR" 0:0 700 \
  || fail "release-state directory must be root:root mode 0700"
hook2stream_trusted_file "$HOOK2STREAM_ENV_FILE" 0:0 600 \
  || fail "environment file must be root:root mode 0600"
configured_environment=$(read_deployment_environment "$HOOK2STREAM_ENV_FILE")
case "$HOOK2STREAM_ENV_FILE:$configured_environment" in
  /srv/hook2stream/config/staging.env:staging|/srv/hook2stream/config/production.env:production) ;;
  *) fail "environment file name and DEPLOYMENT_ENVIRONMENT differ" ;;
esac
if [ "$configured_environment" = production ]; then
  : "${HOOK2STREAM_STAGING_SIGNERS:?HOOK2STREAM_STAGING_SIGNERS is required for production}"
  [ "$HOOK2STREAM_STAGING_SIGNERS" = /etc/hook2stream/staging-receipt-allowed-signers ] \
    || fail "staging signer path is not canonical"
  hook2stream_trusted_file "$HOOK2STREAM_STAGING_SIGNERS" 0:0 600 \
    || fail "staging signers must be root:root mode 0600"
fi
case "$MIN_ROLLBACK_RELEASE_SHA" in *[!0-9a-f]*|'') fail "MIN_ROLLBACK_RELEASE_SHA is invalid" ;; esac
[ "${#MIN_ROLLBACK_RELEASE_SHA}" -eq 40 ] || fail "MIN_ROLLBACK_RELEASE_SHA is invalid"
hook2stream_trusted_file "$HOOK2STREAM_E2E_HOOK" 0:0 500 \
  || fail "E2E hook must be root:root mode 0500"
command -v timeout >/dev/null 2>&1 || fail "timeout is required"
forced_lock=$HOOK2STREAM_RELEASE_STATE_DIR/forced-command.lock
if [ ! -e "$forced_lock" ]; then (umask 077 && : > "$forced_lock"); fi
[ -f "$forced_lock" ] && [ ! -L "$forced_lock" ] && [ "$(stat -c '%u:%a' "$forced_lock")" = "0:600" ] \
  || fail "forced-command lock must be a root-owned regular file mode 0600"
exec 8<>"$forced_lock"
flock -n 8 || fail "another forced deployment or rollback is already running"

old_ifs=$IFS; IFS=' '; set -- ${SSH_ORIGINAL_COMMAND:-}; IFS=$old_ifs
operation=${1:-}; identifier=${2:-}
[ "$#" -ge 2 ] && [ "$#" -le 3 ] \
  || fail "allowed commands: deploy CANDIDATE_ID, soak CANDIDATE_ID, or rollback COMMIT_SHA H2SEv1"

case "$operation" in
  deploy)
    [ "$#" -eq 2 ] || fail "deploy accepts exactly one candidate ID"
    case "$identifier" in release-candidate-[0-9a-f]*-[0-9]*-[0-9]*) ;; *) fail "invalid candidate ID" ;; esac
    incoming=$(mktemp -d "$HOOK2STREAM_RELEASE_STATE_DIR/incoming.XXXXXX")
    trap 'rm -rf "$incoming"' EXIT
    trap 'exit 130' HUP INT TERM
    envelope=$incoming/envelope.tar
    dd iflag=fullblock bs=1048576 count=257 of="$envelope" 2>/dev/null
    [ "$(wc -c < "$envelope")" -le 268435456 ] || fail "deployment envelope exceeds 256 MiB"
    tar -tf "$envelope" | while IFS= read -r member; do
      case "$member" in .|./|candidate|candidate/*|approval|approval/*|./candidate|./candidate/*|./approval|./approval/*) ;; *) fail "envelope path is not allowed" ;; esac
      case "$member" in /*|../*|*/../*|*/..) fail "envelope path traversal detected" ;; esac
    done
    if tar -tvf "$envelope" | awk '$1 !~ /^[d-]/ {bad=1} END {exit bad ? 0 : 1}'; then fail "envelope links and special files are forbidden"; fi
    tar -tvf "$envelope" | awk '{total += $3} END {exit total <= 536870912 ? 0 : 1}' || fail "expanded envelope exceeds 512 MiB"
    tar -xf "$envelope" --no-same-owner --no-same-permissions -C "$incoming"
    chmod -R go-w "$incoming/candidate" "${incoming}/approval" 2>/dev/null || true
    environment=$configured_environment
    if [ "$environment" = production ]; then approval=$incoming/approval; else approval=; fi
    "$script_dir/validate-candidate.sh" "$incoming/candidate" ${approval:+"$approval"}
    artifact_name=$(jq -r '.artifactName' "$incoming/candidate/release-metadata.json")
    [ "$artifact_name" = "$identifier" ] || fail "candidate ID differs from artifactName"
    commit=$(jq -r '.commitSha' "$incoming/candidate/release-metadata.json")
    release_dir=$HOOK2STREAM_RELEASES_DIR/$commit
    bundle_sha=$(sha256sum "$incoming/candidate/deploy-bundle.tar.gz" | awk '{print $1}')
    if [ -e "$release_dir" ]; then
      hook2stream_trusted_directory "$release_dir" 0:0 700 \
        && hook2stream_trusted_file "$release_dir/.deploy-bundle.sha256" 0:0 600 \
        || fail "existing release path is not root-private"
      [ "$(cat "$release_dir/.deploy-bundle.sha256")" = "$bundle_sha" ] || fail "existing release has a conflicting bundle"
    else
      release_tmp=$(mktemp -d "$HOOK2STREAM_RELEASES_DIR/.${commit}.XXXXXX")
      tar -xzf "$incoming/candidate/deploy-bundle.tar.gz" --no-same-owner --no-same-permissions -C "$release_tmp"
      chmod -R go-rwx "$release_tmp"
      printf '%s\n' "$bundle_sha" > "$release_tmp/.deploy-bundle.sha256"
      chmod 0600 "$release_tmp/.deploy-bundle.sha256"
      mv "$release_tmp" "$release_dir" || fail "could not atomically publish release directory"
    fi
    hook2stream_trusted_file "$release_dir/deploy/scripts/deploy-release.sh" 0:0 700 \
      && hook2stream_trusted_file "$release_dir/deploy/scripts/rollback-application.sh" 0:0 700 \
      || fail "release lacks the forward deploy or application-only rollback implementation"
    release_env=$HOOK2STREAM_RELEASE_STATE_DIR/candidate-$commit.env
    image_names=' API_IMAGE WORKER_IMAGE BOOTSTRAPPER_IMAGE WEB_IMAGE POSTGRES_BACKUP_IMAGE CADDY_IMAGE POSTGRES_IMAGE PGBOUNCER_IMAGE EGRESS_PROXY_IMAGE RELEASE_VERSION '
    awk -F= -v names="$image_names" 'index(names, " " $1 " ") == 0 {print}' "$HOOK2STREAM_ENV_FILE" > "$release_env.tmp"
    cat "$incoming/candidate/release-images.env" >> "$release_env.tmp"
    chmod 0600 "$release_env.tmp"; mv -f "$release_env.tmp" "$release_env"
    HOOK2STREAM_ENV_FILE=$release_env HOOK2STREAM_DEFER_SUCCESS_MARKER=true env -u HOOK2STREAM_RELEASE_STATE_DIR "$release_dir/deploy/scripts/deploy-release.sh"
    "$HOOK2STREAM_E2E_HOOK" "$environment" "$release_env" "$commit"

    actual_images='{}'
    for mapping in 'API_IMAGE:api' 'WORKER_IMAGE:worker-media' 'WORKER_IMAGE:worker-analysis' 'WORKER_IMAGE:worker-control' 'WORKER_IMAGE:worker-render' 'WORKER_IMAGE:worker-export' 'WEB_IMAGE:web' 'POSTGRES_BACKUP_IMAGE:postgres-backup' 'POSTGRES_BACKUP_IMAGE:storage-janitor' 'CADDY_IMAGE:caddy' 'POSTGRES_IMAGE:postgres' 'PGBOUNCER_IMAGE:pgbouncer' 'EGRESS_PROXY_IMAGE:egress-api' 'EGRESS_PROXY_IMAGE:egress-s3' 'EGRESS_PROXY_IMAGE:egress-control' 'EGRESS_PROXY_IMAGE:egress-backup'; do
      name=${mapping%%:*}; service=${mapping#*:}
      container=$(HOOK2STREAM_ENV_FILE=$release_env sh -c '. "$1/scripts/lib/deployment-common.sh"; compose ps -q "$2"' _ "$release_dir/deploy" "$service")
      actual=$(docker inspect --format '{{.Config.Image}}' "$container")
      expected=$(awk -F= -v name="$name" '$1 == name {print substr($0,index($0,"=")+1)}' "$release_env")
      [ "$actual" = "$expected" ] || fail "$service is not running the candidate digest"
      actual_images=$(printf '%s' "$actual_images" | jq -c --arg name "$name" --arg value "$actual" '. + {($name):$value}')
    done
    bootstrap_expected=$(awk -F= '$1 == "BOOTSTRAPPER_IMAGE" {print substr($0,index($0,"=")+1)}' "$release_env")
    docker image inspect "$bootstrap_expected" >/dev/null 2>&1 || fail "bootstrapper image is absent after migration"
    actual_images=$(printf '%s' "$actual_images" | jq -c --arg value "$bootstrap_expected" '. + {BOOTSTRAPPER_IMAGE:$value}')
    images_sha=$(sha256sum "$incoming/candidate/release-images.env" | awk '{print $1}')
    receipt=$(jq -cn --arg environment "$environment" --arg artifact "$artifact_name" --arg commit "$commit" --arg minimum "$MIN_ROLLBACK_RELEASE_SHA" --arg imagesSha "$images_sha" --arg bundleSha "$bundle_sha" --argjson images "$actual_images" '{schemaVersion:1,kind:"hook2stream-remote-deploy-result",environment:$environment,result:"success",candidateArtifact:$artifact,commitSha:$commit,minimumRollbackReleaseSha:$minimum,releaseImagesSha256:$imagesSha,deployBundleSha256:$bundleSha,actualImages:$images,checks:["pre-migration-backup","migration","smoke","e2e","digest-verification"]}')
    successful_dir=$HOOK2STREAM_RELEASE_STATE_DIR/successful
    install -d -o root -g root -m 0700 "$successful_dir"
    install -m 0600 "$release_env" "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp"
    mv -f "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp" "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env"
    install -m 0600 "$release_env" "$successful_dir/$commit.env"
    jq -cn --arg sha "$commit" '{schemaVersion:1,releaseSha:$sha,storageFormats:["H2SEv1"]}' > "$successful_dir/$commit.capabilities.json.tmp"
    chmod 0600 "$successful_dir/$commit.capabilities.json.tmp"
    mv -f "$successful_dir/$commit.capabilities.json.tmp" "$successful_dir/$commit.capabilities.json"
    current_candidate=$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json
    if [ "$environment" = staging ]; then
      activated_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
      jq -cn --arg artifact "$artifact_name" --arg commit "$commit" --arg activatedAt "$activated_at" \
        '{schema:"hook2stream-current-candidate-v1",environment:"staging",candidateArtifact:$artifact,commitSha:$commit,activatedAt:$activatedAt}' \
        > "$current_candidate.tmp"
      chmod 0600 "$current_candidate.tmp"
      mv -f "$current_candidate.tmp" "$current_candidate"
    fi
    printf 'HOOK2STREAM_REMOTE_RECEIPT=%s\n' "$(printf '%s' "$receipt" | base64 | tr -d '\n')"
    ;;
  soak)
    [ "$#" -eq 2 ] || fail "soak accepts exactly one candidate ID"
    [ "$configured_environment" = staging ] || fail "soak is allowed only on staging"
    case "$identifier" in release-candidate-[0-9a-f]*-[0-9]*-[0-9]*) ;; *) fail "invalid candidate ID" ;; esac
    current_candidate=$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json
    hook2stream_trusted_file "$current_candidate" 0:0 600 \
      || fail "current successful candidate marker is unavailable or unsafe"
    current_state=$(jq -ce --arg artifact "$identifier" 'select(
      (keys | sort) == ["activatedAt","candidateArtifact","commitSha","environment","schema"] and
      .schema == "hook2stream-current-candidate-v1" and .environment == "staging" and
      .candidateArtifact == $artifact and
      (.commitSha | type == "string" and test("^[0-9a-f]{40}$")) and
      (.activatedAt | type == "string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$") and fromdateiso8601 >= 0)
    )' "$current_candidate") || fail "soak candidate is not the current successful staging candidate"
    commit=$(printf '%s' "$current_state" | jq -r '.commitSha')
    release_env=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$commit.env
    current_env=$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env
    release_dir=$HOOK2STREAM_RELEASES_DIR/$commit
    hook2stream_trusted_file "$release_env" 0:0 600 \
      && hook2stream_trusted_file "$current_env" 0:0 600 \
      && hook2stream_trusted_directory "$release_dir" 0:0 700 \
      || fail "current successful release state is unavailable or unsafe"
    cmp -s "$release_env" "$current_env" \
      || fail "soak candidate is no longer the active successful release"
    [ "$(awk -F= '$1 == "RELEASE_VERSION" {print substr($0,index($0,"=")+1)}' "$current_env")" = "$commit" ] \
      || fail "active release version differs from the soak candidate"

    soak_dir=$(mktemp -d "$HOOK2STREAM_RELEASE_STATE_DIR/soak.XXXXXX")
    trap 'rm -rf "$soak_dir"' EXIT
    trap 'exit 130' HUP INT TERM
    started_epoch=$(date +%s)
    started_at=$(date -u -d "@$started_epoch" '+%Y-%m-%dT%H:%M:%SZ')
    if ! timeout --signal=TERM --kill-after=5s 3890s \
      "$HOOK2STREAM_E2E_HOOK" staging "$current_env" "$commit" soak-60m \
      > "$soak_dir/hook.stdout" 2> "$soak_dir/hook.stderr"; then
      fail "trusted sustained render/network soak hook failed"
    fi
    completed_epoch=$(date +%s)
    completed_at=$(date -u -d "@$completed_epoch" '+%Y-%m-%dT%H:%M:%SZ')
    elapsed_seconds=$((completed_epoch - started_epoch))
    [ "$elapsed_seconds" -ge 3600 ] && [ "$elapsed_seconds" -le 3900 ] \
      || fail "sustained render/network soak elapsed time is outside 3600-3900 seconds"
    [ "$(wc -c < "$soak_dir/hook.stdout")" -le 8192 ] \
      && [ "$(wc -l < "$soak_dir/hook.stdout")" -eq 1 ] \
      && [ "$(tail -c 1 "$soak_dir/hook.stdout" | od -An -tu1 | tr -d ' ')" = 10 ] \
      || fail "soak hook output must be one bounded newline-terminated JSON line"
    hook_result=$(jq -ce --argjson elapsed "$elapsed_seconds" 'select(
      (keys | sort) == ["completedRenderCount","cpuThrottled","maxConcurrentRenderJobs","networkChecks","networkFailures","oomKilled","renderActiveSeconds","schema"] and
      .schema == "hook2stream-soak-hook-result-v1" and
      (.completedRenderCount | type == "number" and floor == . and . > 0) and
      (.renderActiveSeconds | type == "number" and floor == . and . >= 3300 and . <= $elapsed) and
      .maxConcurrentRenderJobs == 1 and
      (.networkChecks | type == "number" and floor == . and . >= 60) and
      .networkFailures == 0 and .cpuThrottled == false and .oomKilled == false
    )' "$soak_dir/hook.stdout") || fail "soak hook result is invalid"

    render_containers=$(HOOK2STREAM_ENV_FILE=$current_env sh -c \
      '. "$1/scripts/lib/deployment-common.sh"; compose ps -q worker-render' _ "$release_dir/deploy" | sed '/^$/d')
    [ -n "$render_containers" ] \
      && [ "$(printf '%s\n' "$render_containers" | wc -l | tr -d ' ')" -eq 1 ] \
      || fail "exactly one worker-render container must exist after soak"
    render_container=$render_containers
    render_state=$(docker inspect --format '{{json .State}}' "$render_container")
    printf '%s' "$render_state" | jq -e \
      '.Running == true and .OOMKilled == false and .Health.Status == "healthy"' >/dev/null \
      || fail "worker-render is not healthy or was OOM-killed"
    actual_render_image=$(docker inspect --format '{{.Config.Image}}' "$render_container")
    expected_render_image=$(awk -F= '$1 == "WORKER_IMAGE" {print substr($0,index($0,"=")+1)}' "$current_env")
    [ "$actual_render_image" = "$expected_render_image" ] \
      || fail "worker-render no longer runs the soak candidate digest"

    soak_receipt=$(jq -cn --arg artifact "$identifier" --arg commit "$commit" \
      --arg startedAt "$started_at" --arg completedAt "$completed_at" \
      --argjson elapsedSeconds "$elapsed_seconds" \
      --argjson hookResult "$hook_result" \
      '{schemaVersion:1,kind:"hook2stream-remote-soak-result",environment:"staging",result:"success",candidateArtifact:$artifact,commitSha:$commit,startedAt:$startedAt,completedAt:$completedAt,elapsedSeconds:$elapsedSeconds,hookResult:$hookResult,workerRenderInstances:1,workerRenderHealthy:true,workerRenderOomKilled:false,checks:["render-network-soak","elapsed-window","single-render-worker","no-oom"]}')
    printf 'HOOK2STREAM_REMOTE_SOAK_RECEIPT=%s\n' "$(printf '%s' "$soak_receipt" | base64 | tr -d '\n')"
    ;;
  rollback)
    [ "$#" -eq 3 ] && [ "$3" = H2SEv1 ] || fail "rollback requires COMMIT_SHA H2SEv1"
    case "$identifier" in *[!0-9a-f]*|'') fail "rollback requires a full 40-character commit SHA" ;; esac
    [ "${#identifier}" -eq 40 ] || fail "rollback requires a full 40-character commit SHA"
    rollback_env=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$identifier.env; rollback_dir=$HOOK2STREAM_RELEASES_DIR/$identifier
    current_env=$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env
    hook2stream_trusted_directory "$rollback_dir" 0:0 700 \
      && hook2stream_trusted_file "$rollback_dir/.deploy-bundle.sha256" 0:0 600 \
      && hook2stream_trusted_file "$rollback_env" 0:0 600 \
      && hook2stream_trusted_file "$rollback_dir/deploy/scripts/rollback-application.sh" 0:0 700 \
      || fail "commit is not an application-rollback-capable locally successful release"
    hook2stream_trusted_file "$current_env" 0:0 600 \
      || fail "current successful environment is unavailable or unsafe"
    capabilities=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$identifier.capabilities.json
    hook2stream_trusted_file "$capabilities" 0:0 600 \
      || fail "rollback target has no safely recorded storage capability"
    jq -e --arg sha "$identifier" '.schemaVersion == 1 and .releaseSha == $sha and (.storageFormats | index("H2SEv1") != null)' "$capabilities" >/dev/null \
      || fail "rollback target cannot read H2SEv1"
    environment=$(read_deployment_environment "$rollback_env")
    [ "$environment" = "$configured_environment" ] \
      || fail "rollback target differs from the configured host environment"
    [ "$(read_deployment_environment "$current_env")" = "$environment" ] \
      || fail "rollback target belongs to a different environment"
    active_rollback_env=$HOOK2STREAM_RELEASE_STATE_DIR/active-rollback-$identifier.env
    env -u HOOK2STREAM_RELEASE_STATE_DIR "$rollback_dir/deploy/scripts/rollback-application.sh" \
      "$current_env" "$rollback_env" "$active_rollback_env" "$identifier"
    "$HOOK2STREAM_E2E_HOOK" "$environment" "$active_rollback_env" "$identifier"
    actual_images='{}'
    for mapping in 'API_IMAGE:api' 'WORKER_IMAGE:worker-media' 'WORKER_IMAGE:worker-analysis' 'WORKER_IMAGE:worker-control' 'WORKER_IMAGE:worker-render' 'WORKER_IMAGE:worker-export' 'WEB_IMAGE:web' 'POSTGRES_BACKUP_IMAGE:postgres-backup' 'POSTGRES_BACKUP_IMAGE:storage-janitor' 'CADDY_IMAGE:caddy' 'POSTGRES_IMAGE:postgres' 'PGBOUNCER_IMAGE:pgbouncer' 'EGRESS_PROXY_IMAGE:egress-api' 'EGRESS_PROXY_IMAGE:egress-s3' 'EGRESS_PROXY_IMAGE:egress-control' 'EGRESS_PROXY_IMAGE:egress-backup'; do
      name=${mapping%%:*}; service=${mapping#*:}
      container=$(HOOK2STREAM_ENV_FILE=$active_rollback_env sh -c '. "$1/scripts/lib/deployment-common.sh"; compose ps -q "$2"' _ "$rollback_dir/deploy" "$service")
      actual=$(docker inspect --format '{{.Config.Image}}' "$container")
      expected=$(awk -F= -v name="$name" '$1 == name {print substr($0,index($0,"=")+1)}' "$active_rollback_env")
      [ "$actual" = "$expected" ] || fail "$service is not running the rollback digest"
      actual_images=$(printf '%s' "$actual_images" | jq -c --arg name "$name" --arg value "$actual" '. + {($name):$value}')
    done
    preserved_bootstrap=$(awk -F= '$1 == "BOOTSTRAPPER_IMAGE" {print substr($0,index($0,"=")+1)}' "$active_rollback_env")
    install -m 0600 "$active_rollback_env" "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp"
    mv -f "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp" "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env"
    if [ "$environment" = staging ]; then
      rolled_back_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
      jq -cn --arg commit "$identifier" --arg rolledBackAt "$rolled_back_at" \
        '{schema:"hook2stream-current-rollback-v1",environment:"staging",commitSha:$commit,rolledBackAt:$rolledBackAt}' \
        > "$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json.tmp"
      chmod 0600 "$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json.tmp"
      mv -f "$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json.tmp" \
        "$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json"
    fi
    rollback_receipt=$(jq -cn --arg environment "$environment" --arg sha "$identifier" --arg minimum "$MIN_ROLLBACK_RELEASE_SHA" --arg bootstrap "$preserved_bootstrap" --argjson images "$actual_images" '{schemaVersion:1,kind:"hook2stream-remote-rollback-result",environment:$environment,result:"success",releaseSha:$sha,storageFormat:"H2SEv1",minimumRollbackReleaseSha:$minimum,actualRunningImages:$images,preservedBootstrapImage:$bootstrap,checks:["target-recorded-success","storage-format-compatible","application-images-only","infrastructure-unchanged","no-migrations","smoke","e2e","digest-verification"]}')
    printf 'HOOK2STREAM_ROLLBACK_RECEIPT=%s\n' "$(printf '%s' "$rollback_receipt" | base64 | tr -d '\n')"
    ;;
  *) fail "operation is not allowed" ;;
esac
