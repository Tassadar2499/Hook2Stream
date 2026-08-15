#!/bin/sh
set -eu
set -f

fail() { printf '%s\n' "forced deploy: $*" >&2; exit 1; }
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
case "$MIN_ROLLBACK_RELEASE_SHA" in *[!0-9a-f]*|'') fail "MIN_ROLLBACK_RELEASE_SHA is invalid" ;; esac
[ "${#MIN_ROLLBACK_RELEASE_SHA}" -eq 40 ] || fail "MIN_ROLLBACK_RELEASE_SHA is invalid"
[ -x "$HOOK2STREAM_E2E_HOOK" ] && [ ! -L "$HOOK2STREAM_E2E_HOOK" ] || fail "E2E hook must be an executable non-symlink file"
[ "$(stat -c '%u:%a' "$HOOK2STREAM_E2E_HOOK")" = "0:500" ] || fail "E2E hook must be root-owned mode 0500"
[ -d "$HOOK2STREAM_RELEASE_STATE_DIR" ] && [ ! -L "$HOOK2STREAM_RELEASE_STATE_DIR" ] \
  && [ "$(stat -c '%u:%a' "$HOOK2STREAM_RELEASE_STATE_DIR")" = "0:700" ] \
  || fail "release state must be a root-owned non-symlink directory mode 0700"
forced_lock=$HOOK2STREAM_RELEASE_STATE_DIR/forced-command.lock
if [ ! -e "$forced_lock" ]; then (umask 077 && : > "$forced_lock"); fi
[ -f "$forced_lock" ] && [ ! -L "$forced_lock" ] && [ "$(stat -c '%u:%a' "$forced_lock")" = "0:600" ] \
  || fail "forced-command lock must be a root-owned regular file mode 0600"
exec 8<>"$forced_lock"
flock -n 8 || fail "another forced deployment or rollback is already running"

old_ifs=$IFS; IFS=' '; set -- ${SSH_ORIGINAL_COMMAND:-}; IFS=$old_ifs
operation=${1:-}; identifier=${2:-}
[ "$#" -ge 2 ] && [ "$#" -le 3 ] || fail "allowed commands: deploy CANDIDATE_ID or rollback COMMIT_SHA H2SEv1"

case "$operation" in
  deploy)
    [ "$#" -eq 2 ] || fail "deploy accepts exactly one candidate ID"
    case "$identifier" in release-candidate-[0-9a-f]*-[0-9]*-[0-9]*) ;; *) fail "invalid candidate ID" ;; esac
    incoming=$(mktemp -d "$HOOK2STREAM_RELEASE_STATE_DIR/incoming.XXXXXX")
    trap 'rm -rf "$incoming"' EXIT HUP INT TERM
    envelope=$incoming/envelope.tar
    dd bs=1048576 count=257 of="$envelope" 2>/dev/null
    [ "$(wc -c < "$envelope")" -le 268435456 ] || fail "deployment envelope exceeds 256 MiB"
    tar -tf "$envelope" | while IFS= read -r member; do
      case "$member" in .|./|candidate|candidate/*|approval|approval/*|./candidate|./candidate/*|./approval|./approval/*) ;; *) fail "envelope path is not allowed" ;; esac
      case "$member" in /*|../*|*/../*|*/..) fail "envelope path traversal detected" ;; esac
    done
    if tar -tvf "$envelope" | awk '$1 !~ /^[d-]/ {bad=1} END {exit bad ? 0 : 1}'; then fail "envelope links and special files are forbidden"; fi
    tar -tvf "$envelope" | awk '{total += $3} END {exit total <= 536870912 ? 0 : 1}' || fail "expanded envelope exceeds 512 MiB"
    tar -xf "$envelope" --no-same-owner --no-same-permissions -C "$incoming"
    chmod -R go-w "$incoming/candidate" "${incoming}/approval" 2>/dev/null || true
    environment=$(read_deployment_environment "$HOOK2STREAM_ENV_FILE")
    if [ "$environment" = production ]; then approval=$incoming/approval; else approval=; fi
    script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
    "$script_dir/validate-candidate.sh" "$incoming/candidate" ${approval:+"$approval"}
    artifact_name=$(jq -r '.artifactName' "$incoming/candidate/release-metadata.json")
    [ "$artifact_name" = "$identifier" ] || fail "candidate ID differs from artifactName"
    commit=$(jq -r '.commitSha' "$incoming/candidate/release-metadata.json")
    release_dir=$HOOK2STREAM_RELEASES_DIR/$commit
    bundle_sha=$(sha256sum "$incoming/candidate/deploy-bundle.tar.gz" | awk '{print $1}')
    if [ -e "$release_dir" ]; then
      [ -d "$release_dir" ] && [ ! -L "$release_dir" ] && [ -r "$release_dir/.deploy-bundle.sha256" ] || fail "existing release path is not validated"
      [ "$(cat "$release_dir/.deploy-bundle.sha256")" = "$bundle_sha" ] || fail "existing release has a conflicting bundle"
    else
      release_tmp=$(mktemp -d "$HOOK2STREAM_RELEASES_DIR/.${commit}.XXXXXX")
      tar -xzf "$incoming/candidate/deploy-bundle.tar.gz" --no-same-owner --no-same-permissions -C "$release_tmp"
      printf '%s\n' "$bundle_sha" > "$release_tmp/.deploy-bundle.sha256"
      chmod 0600 "$release_tmp/.deploy-bundle.sha256"
      mv "$release_tmp" "$release_dir" || fail "could not atomically publish release directory"
    fi
    [ -x "$release_dir/deploy/scripts/deploy-release.sh" ] \
      && [ -x "$release_dir/deploy/scripts/rollback-application.sh" ] \
      || fail "release lacks the forward deploy or application-only rollback implementation"
    release_env=$HOOK2STREAM_RELEASE_STATE_DIR/candidate-$commit.env
    image_names=' API_IMAGE WORKER_IMAGE BOOTSTRAPPER_IMAGE WEB_IMAGE POSTGRES_BACKUP_IMAGE CADDY_IMAGE POSTGRES_IMAGE PGBOUNCER_IMAGE EGRESS_PROXY_IMAGE RELEASE_VERSION '
    awk -F= -v names="$image_names" 'index(names, " " $1 " ") == 0 {print}' "$HOOK2STREAM_ENV_FILE" > "$release_env.tmp"
    cat "$incoming/candidate/release-images.env" >> "$release_env.tmp"
    chmod 0600 "$release_env.tmp"; mv -f "$release_env.tmp" "$release_env"
    HOOK2STREAM_ENV_FILE=$release_env HOOK2STREAM_DEFER_SUCCESS_MARKER=true env -u HOOK2STREAM_RELEASE_STATE_DIR "$release_dir/deploy/scripts/deploy-release.sh"
    "$HOOK2STREAM_E2E_HOOK" "$environment" "$release_env" "$commit"

    actual_images='{}'
    for mapping in 'API_IMAGE:api' 'WORKER_IMAGE:worker-media' 'WORKER_IMAGE:worker-analysis' 'WORKER_IMAGE:worker-control' 'WORKER_IMAGE:worker-render' 'WORKER_IMAGE:worker-export' 'WEB_IMAGE:web' 'POSTGRES_BACKUP_IMAGE:postgres-backup' 'CADDY_IMAGE:caddy' 'POSTGRES_IMAGE:postgres' 'PGBOUNCER_IMAGE:pgbouncer' 'EGRESS_PROXY_IMAGE:egress-api' 'EGRESS_PROXY_IMAGE:egress-s3' 'EGRESS_PROXY_IMAGE:egress-control'; do
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
    receipt=$(jq -cn --arg environment "$environment" --arg artifact "$artifact_name" --arg commit "$commit" --arg imagesSha "$images_sha" --arg bundleSha "$bundle_sha" --argjson images "$actual_images" '{schemaVersion:1,kind:"hook2stream-remote-deploy-result",environment:$environment,result:"success",candidateArtifact:$artifact,commitSha:$commit,releaseImagesSha256:$imagesSha,deployBundleSha256:$bundleSha,actualImages:$images,checks:["pre-migration-backup","migration","smoke","e2e","digest-verification"]}')
    successful_dir=$HOOK2STREAM_RELEASE_STATE_DIR/successful
    install -d -o root -g root -m 0700 "$successful_dir"
    install -m 0600 "$release_env" "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp"
    mv -f "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp" "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env"
    install -m 0600 "$release_env" "$successful_dir/$commit.env"
    jq -cn --arg sha "$commit" '{schemaVersion:1,releaseSha:$sha,storageFormats:["H2SEv1"]}' > "$successful_dir/$commit.capabilities.json.tmp"
    chmod 0600 "$successful_dir/$commit.capabilities.json.tmp"
    mv -f "$successful_dir/$commit.capabilities.json.tmp" "$successful_dir/$commit.capabilities.json"
    printf 'HOOK2STREAM_REMOTE_RECEIPT=%s\n' "$(printf '%s' "$receipt" | base64 | tr -d '\n')"
    ;;
  rollback)
    [ "$#" -eq 3 ] && [ "$3" = H2SEv1 ] || fail "rollback requires COMMIT_SHA H2SEv1"
    case "$identifier" in *[!0-9a-f]*|'') fail "rollback requires a full 40-character commit SHA" ;; esac
    [ "${#identifier}" -eq 40 ] || fail "rollback requires a full 40-character commit SHA"
    rollback_env=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$identifier.env; rollback_dir=$HOOK2STREAM_RELEASES_DIR/$identifier
    current_env=$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env
    [ -f "$rollback_env" ] && [ ! -L "$rollback_env" ] && [ "$(stat -c '%u:%a' "$rollback_env")" = "0:600" ] \
      && [ -x "$rollback_dir/deploy/scripts/rollback-application.sh" ] \
      || fail "commit is not an application-rollback-capable locally successful release"
    [ -f "$current_env" ] && [ ! -L "$current_env" ] && [ "$(stat -c '%u:%a' "$current_env")" = "0:600" ] \
      || fail "current successful environment is unavailable or unsafe"
    capabilities=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$identifier.capabilities.json
    [ -f "$capabilities" ] && [ ! -L "$capabilities" ] && [ "$(stat -c '%u:%a' "$capabilities")" = "0:600" ] \
      || fail "rollback target has no safely recorded storage capability"
    jq -e --arg sha "$identifier" '.schemaVersion == 1 and .releaseSha == $sha and (.storageFormats | index("H2SEv1") != null)' "$capabilities" >/dev/null \
      || fail "rollback target cannot read H2SEv1"
    environment=$(read_deployment_environment "$rollback_env")
    [ "$(read_deployment_environment "$current_env")" = "$environment" ] \
      || fail "rollback target belongs to a different environment"
    active_rollback_env=$HOOK2STREAM_RELEASE_STATE_DIR/active-rollback-$identifier.env
    env -u HOOK2STREAM_RELEASE_STATE_DIR "$rollback_dir/deploy/scripts/rollback-application.sh" \
      "$current_env" "$rollback_env" "$active_rollback_env" "$identifier"
    "$HOOK2STREAM_E2E_HOOK" "$environment" "$active_rollback_env" "$identifier"
    actual_images='{}'
    for mapping in 'API_IMAGE:api' 'WORKER_IMAGE:worker-media' 'WORKER_IMAGE:worker-analysis' 'WORKER_IMAGE:worker-control' 'WORKER_IMAGE:worker-render' 'WORKER_IMAGE:worker-export' 'WEB_IMAGE:web' 'POSTGRES_BACKUP_IMAGE:postgres-backup' 'CADDY_IMAGE:caddy' 'POSTGRES_IMAGE:postgres' 'PGBOUNCER_IMAGE:pgbouncer' 'EGRESS_PROXY_IMAGE:egress-api' 'EGRESS_PROXY_IMAGE:egress-s3' 'EGRESS_PROXY_IMAGE:egress-control'; do
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
    rollback_receipt=$(jq -cn --arg environment "$environment" --arg sha "$identifier" --arg minimum "$MIN_ROLLBACK_RELEASE_SHA" --arg bootstrap "$preserved_bootstrap" --argjson images "$actual_images" '{schemaVersion:1,kind:"hook2stream-remote-rollback-result",environment:$environment,result:"success",releaseSha:$sha,storageFormat:"H2SEv1",minimumRollbackReleaseSha:$minimum,actualRunningImages:$images,preservedBootstrapImage:$bootstrap,checks:["target-recorded-success","storage-format-compatible","application-images-only","infrastructure-unchanged","no-migrations","smoke","e2e","digest-verification"]}')
    printf 'HOOK2STREAM_ROLLBACK_RECEIPT=%s\n' "$(printf '%s' "$rollback_receipt" | base64 | tr -d '\n')"
    ;;
  *) fail "operation is not allowed" ;;
esac
