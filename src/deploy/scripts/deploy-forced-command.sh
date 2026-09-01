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
read_unique_environment_value() {
  env_file=$1; env_name=$2
  env_count=$(awk -F= -v name="$env_name" '$1 == name {count++} END {print count + 0}' "$env_file")
  [ "$env_count" -eq 1 ] || fail "$env_file must contain exactly one $env_name"
  awk -F= -v name="$env_name" '$1 == name {print substr($0,index($0,"=")+1)}' "$env_file"
}
rollback_protocol=hook2stream-application-rollback-v2
validate_rollback_capability() {
  capability_file=$1; capability_sha=$2
  hook2stream_validate_rollback_capability \
    "$capability_file" "$capability_sha" "$rollback_protocol" 0:0 \
    || fail "release $capability_sha is not rollback protocol v2 and H2SEv1 capable"
}
[ "$(id -u)" -eq 0 ] || fail "wrapper must run as root through the exact sudoers rule"
: "${HOOK2STREAM_ENV_FILE:?HOOK2STREAM_ENV_FILE is required}"
: "${HOOK2STREAM_RELEASES_DIR:=/srv/hook2stream/releases}"
: "${HOOK2STREAM_RELEASE_STATE_DIR:=/srv/hook2stream/release-state}"
: "${HOOK2STREAM_E2E_HOOK:?HOOK2STREAM_E2E_HOOK must name the root-owned post-deploy E2E hook}"
: "${MIN_ROLLBACK_RELEASE_SHA:?MIN_ROLLBACK_RELEASE_SHA must identify the first approved H2SE release}"
: "${DOCKER_CONFIG:?DOCKER_CONFIG must name the encrypted GHCR pull-auth directory}"
: "${HOOK2STREAM_GHCR_USERNAME:?HOOK2STREAM_GHCR_USERNAME is required}"
: "${HOOK2STREAM_GHCR_AUTH_SHA256:?HOOK2STREAM_GHCR_AUTH_SHA256 is required}"
: "${HOOK2STREAM_GHCR_CREDENTIAL_IDENTITY:?HOOK2STREAM_GHCR_CREDENTIAL_IDENTITY is required}"
: "${HOOK2STREAM_GHCR_IDENTITY_SHA256:?HOOK2STREAM_GHCR_IDENTITY_SHA256 is required}"
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
[ "$DOCKER_CONFIG" = /srv/hook2stream/registry-auth ] \
  || fail "DOCKER_CONFIG is not the canonical encrypted registry-auth path"
validate_ghcr_pull_auth() {
  hook2stream_validate_ghcr_pull_auth \
    "$DOCKER_CONFIG" "$HOOK2STREAM_GHCR_USERNAME" \
    "$HOOK2STREAM_GHCR_AUTH_SHA256" 0:0 \
    || fail "GHCR pull authentication is missing, unsafe, malformed, or differs from the pinned environment credential"
  hook2stream_validate_ghcr_identity_attestation \
    "$DOCKER_CONFIG" "$configured_environment" "$HOOK2STREAM_GHCR_USERNAME" \
    "$HOOK2STREAM_GHCR_CREDENTIAL_IDENTITY" "$HOOK2STREAM_GHCR_IDENTITY_SHA256" 0:0 \
    || fail "GHCR credential identity attestation is missing, unsafe, malformed, or differs from its environment pin"
}
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
rollback_program=/usr/local/libexec/hook2stream/rollback-application.sh
hook2stream_trusted_file "$rollback_program" 0:0 555 \
  || fail "installed application rollback program must be root:root mode 0555"
command -v timeout >/dev/null 2>&1 || fail "timeout is required"
forced_lock=$HOOK2STREAM_RELEASE_STATE_DIR/forced-command.lock
if [ ! -e "$forced_lock" ]; then (umask 077 && : > "$forced_lock"); fi
[ -f "$forced_lock" ] && [ ! -L "$forced_lock" ] && [ "$(stat -c '%u:%a' "$forced_lock")" = "0:600" ] \
  || fail "forced-command lock must be a root-owned regular file mode 0600"
exec 8<>"$forced_lock"
flock -n 8 || fail "another forced deployment or rollback is already running"

recovery_required=$HOOK2STREAM_RELEASE_STATE_DIR/recovery-required.json
if [ -e "$recovery_required" ] || [ -L "$recovery_required" ]; then
  hook2stream_trusted_file "$recovery_required" 0:0 600 \
    || fail "unsafe recovery-required marker blocks all automated operations"
  fail "manual recovery is required; automated deploy/finalize/soak/rollback is blocked"
fi

new_operation_id() {
  operation_id=$(tr -d '-' < /proc/sys/kernel/random/uuid)
  case "$operation_id" in *[!0-9a-f]*|'') fail "kernel operation identity is invalid" ;; esac
  [ "${#operation_id}" -eq 32 ] || fail "kernel operation identity is invalid"
  printf '%s\n' "$operation_id"
}

collect_actual_images() {
  actual_environment=$1
  actual_deploy_dir=$2
  actual_images='{}'
  for mapping in 'API_IMAGE:api' 'WORKER_IMAGE:worker-media' 'WORKER_IMAGE:worker-analysis' 'WORKER_IMAGE:worker-control' 'WORKER_IMAGE:worker-render' 'WORKER_IMAGE:worker-export' 'WEB_IMAGE:web' 'POSTGRES_BACKUP_IMAGE:postgres-backup' 'POSTGRES_BACKUP_IMAGE:storage-janitor' 'CADDY_IMAGE:caddy' 'POSTGRES_IMAGE:postgres' 'PGBOUNCER_IMAGE:pgbouncer' 'EGRESS_PROXY_IMAGE:egress-api' 'EGRESS_PROXY_IMAGE:egress-s3' 'EGRESS_PROXY_IMAGE:egress-control' 'EGRESS_PROXY_IMAGE:egress-backup'; do
    name=${mapping%%:*}; service=${mapping#*:}
    container=$(HOOK2STREAM_ENV_FILE=$actual_environment sh -c '. "$1/scripts/lib/deployment-common.sh"; compose ps -q "$2"' _ "$actual_deploy_dir" "$service")
    [ -n "$container" ] || fail "$service has no running container"
    actual=$(docker inspect --format '{{.Config.Image}}' "$container")
    expected=$(read_unique_environment_value "$actual_environment" "$name")
    [ "$actual" = "$expected" ] || fail "$service is not running the selected digest"
    actual_images=$(printf '%s' "$actual_images" | jq -c --arg name "$name" --arg value "$actual" '. + {($name):$value}')
  done
  bootstrap_expected=$(read_unique_environment_value "$actual_environment" BOOTSTRAPPER_IMAGE)
  docker image inspect "$bootstrap_expected" >/dev/null 2>&1 \
    || fail "bootstrapper image is absent after migration"
  printf '%s' "$actual_images" | jq -c --arg value "$bootstrap_expected" '. + {BOOTSTRAPPER_IMAGE:$value}'
}

pending_file=$HOOK2STREAM_RELEASE_STATE_DIR/pending-deploy.json

write_recovery_required() {
  recovery_reason=$1
  recovery_artifact=$2
  recovery_commit=$3
  recovery_operation=$4
  recovery_environment_file=$5
  recovery_deploy_dir=$6
  ingress_stopped=false
  recovery_container=$(HOOK2STREAM_ENV_FILE=$recovery_environment_file sh -c \
    '. "$1/scripts/lib/deployment-common.sh"; compose ps -q caddy' \
    _ "$recovery_deploy_dir" 2>/dev/null || true)
  case "$recovery_container" in
    ''|*[!0-9a-f]*) ;;
    *)
      recovery_labels=$(docker inspect --format \
        '{{index .Config.Labels "com.docker.compose.project"}}:{{index .Config.Labels "com.docker.compose.service"}}' \
        "$recovery_container" 2>/dev/null || true)
      recovery_project=$(read_unique_environment_value \
        "$recovery_environment_file" COMPOSE_PROJECT_NAME)
      if [ "$recovery_labels" = "$recovery_project:caddy" ]; then
        docker stop --time 10 "$recovery_container" >/dev/null 2>&1 \
          || docker kill "$recovery_container" >/dev/null 2>&1 \
          || true
        if [ "$(docker inspect --format '{{.State.Running}}' \
          "$recovery_container" 2>/dev/null || printf unknown)" = false ]; then
          ingress_stopped=true
        fi
      fi
      ;;
  esac
  recovery_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
  jq -cn --arg environment "$configured_environment" \
    --arg artifact "$recovery_artifact" --arg commit "$recovery_commit" \
    --arg operation "$recovery_operation" --arg reason "$recovery_reason" \
    --argjson ingressStopped "$ingress_stopped" --arg recordedAt "$recovery_at" \
    '{schemaVersion:1,kind:"hook2stream-recovery-required",environment:$environment,candidateArtifact:$artifact,commitSha:$commit,e2eOperationId:$operation,reason:$reason,publicIngressStopped:$ingressStopped,recordedAt:$recordedAt}' \
    > "$recovery_required.tmp"
  chmod 0600 "$recovery_required.tmp"
  mv -f "$recovery_required.tmp" "$recovery_required"
  [ "$ingress_stopped" = true ] \
    || printf '%s\n' "forced deploy: WARNING: recovery is required and public Caddy could not be proven stopped" >&2
}

publish_compensated_state() {
  compensated_artifact=$1
  compensated_commit=$2
  compensated_operation=$3
  compensated_previous=$4
  compensated_reason=$5
  compensated_candidate_env=$6
  compensated_active_env=$7
  compensated_bundle=$8
  compensated_images=$9
  compensated_successful_dir=$HOOK2STREAM_RELEASE_STATE_DIR/successful
  compensated_infrastructure_dir=$HOOK2STREAM_RELEASE_STATE_DIR/infrastructure
  install -d -o root -g root -m 0700 \
    "$compensated_successful_dir" "$compensated_infrastructure_dir" || return 1
  install -m 0600 "$compensated_active_env" \
    "$compensated_infrastructure_dir/$compensated_commit.env" || return 1
  jq -cn --arg sha "$compensated_commit" --arg protocol "$rollback_protocol" \
    '{schemaVersion:2,releaseSha:$sha,storageFormats:["H2SEv1"],rollbackProtocol:$protocol}' \
    > "$compensated_infrastructure_dir/$compensated_commit.capabilities.json.tmp" || return 1
  chmod 0600 "$compensated_infrastructure_dir/$compensated_commit.capabilities.json.tmp" || return 1
  mv -f "$compensated_infrastructure_dir/$compensated_commit.capabilities.json.tmp" \
    "$compensated_infrastructure_dir/$compensated_commit.capabilities.json" || return 1
  jq -cn --arg sha "$compensated_commit" --arg bundle "$compensated_bundle" \
    --arg protocol "$rollback_protocol" \
    '{schemaVersion:2,kind:"hook2stream-active-infrastructure-release",releaseSha:$sha,deployBundleSha256:$bundle,rollbackProtocol:$protocol}' \
    > "$HOOK2STREAM_RELEASE_STATE_DIR/active-infrastructure-release.json.tmp" || return 1
  chmod 0600 "$HOOK2STREAM_RELEASE_STATE_DIR/active-infrastructure-release.json.tmp" || return 1
  mv -f "$HOOK2STREAM_RELEASE_STATE_DIR/active-infrastructure-release.json.tmp" \
    "$HOOK2STREAM_RELEASE_STATE_DIR/active-infrastructure-release.json" || return 1
  install -m 0600 "$compensated_active_env" \
    "$compensated_successful_dir/$compensated_previous.env.tmp" || return 1
  mv -f "$compensated_successful_dir/$compensated_previous.env.tmp" \
    "$compensated_successful_dir/$compensated_previous.env" || return 1
  install -m 0600 "$compensated_active_env" \
    "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp" || return 1
  mv -f "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp" \
    "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env" || return 1
  compensated_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ') || return 1
  jq -cn --arg environment "$configured_environment" \
    --arg artifact "$compensated_artifact" --arg failedCommit "$compensated_commit" \
    --arg restoredCommit "$compensated_previous" --arg operation "$compensated_operation" \
    --arg reason "$compensated_reason" --argjson images "$compensated_images" \
    --arg recordedAt "$compensated_at" \
    '{schemaVersion:1,kind:"hook2stream-compensated-deploy",environment:$environment,candidateArtifact:$artifact,failedCommitSha:$failedCommit,restoredCommitSha:$restoredCommit,e2eOperationId:$operation,reason:$reason,actualImages:$images,recordedAt:$recordedAt}' \
    > "$HOOK2STREAM_RELEASE_STATE_DIR/compensated-$compensated_operation.json.tmp" || return 1
  chmod 0600 "$HOOK2STREAM_RELEASE_STATE_DIR/compensated-$compensated_operation.json.tmp" || return 1
  mv -f "$HOOK2STREAM_RELEASE_STATE_DIR/compensated-$compensated_operation.json.tmp" \
    "$HOOK2STREAM_RELEASE_STATE_DIR/compensated-$compensated_operation.json" || return 1
  rm -f "$pending_file" || return 1
}

old_ifs=$IFS; IFS=' '; set -- ${SSH_ORIGINAL_COMMAND:-}; IFS=$old_ifs
operation=${1:-}; identifier=${2:-}
[ "$#" -ge 2 ] && [ "$#" -le 3 ] \
  || fail "allowed commands: prepare CANDIDATE_ID, deploy CANDIDATE_ID, finalize CANDIDATE_ID, soak CANDIDATE_ID, or rollback COMMIT_SHA H2SEv1"

case "$operation" in
  prepare|deploy)
    [ "$#" -eq 2 ] || fail "prepare/deploy accepts exactly one candidate ID"
    case "$identifier" in release-candidate-[0-9a-f]*-[0-9]*-[0-9]*) ;; *) fail "invalid candidate ID" ;; esac
    if [ "$operation" = prepare ] && { [ -e "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env" ] || [ -L "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env" ]; }; then
      fail "prepare is cold-bootstrap-only and requires no previous successful release"
    fi
    validate_ghcr_pull_auth
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
    hook2stream_prepare_container_config_modes "$release_dir/deploy" 0:0 \
      || fail "release container config sources are missing, unsafe, or unreadable by their non-root containers"
    hook2stream_trusted_file "$release_dir/deploy/scripts/deploy-release.sh" 0:0 700 \
      || fail "release lacks the forward deploy implementation"
    release_env=$HOOK2STREAM_RELEASE_STATE_DIR/candidate-$commit.env
    image_names=' API_IMAGE WORKER_IMAGE BOOTSTRAPPER_IMAGE WEB_IMAGE POSTGRES_BACKUP_IMAGE CADDY_IMAGE POSTGRES_IMAGE PGBOUNCER_IMAGE EGRESS_PROXY_IMAGE RELEASE_VERSION '
    awk -F= -v names="$image_names" 'index(names, " " $1 " ") == 0 {print}' "$HOOK2STREAM_ENV_FILE" > "$release_env.tmp"
    cat "$incoming/candidate/release-images.env" >> "$release_env.tmp"
    chmod 0600 "$release_env.tmp"
    images_sha=$(sha256sum "$incoming/candidate/release-images.env" | awk '{print $1}')
    release_env_sha=$(sha256sum "$release_env.tmp" | awk '{print $1}')
    staging_receipt_sha=
    staging_signature_sha=
    staging_signers_sha=
    if [ "$environment" = production ]; then
      staging_receipt_sha=$(sha256sum "$approval/staging-receipt.json" | awk '{print $1}')
      staging_signature_sha=$(sha256sum "$approval/staging-receipt.sig" | awk '{print $1}')
      staging_signers_sha=$(sha256sum "$HOOK2STREAM_STAGING_SIGNERS" | awk '{print $1}')
    fi
    previous_success=
    if [ -e "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env" ]; then
      hook2stream_trusted_file "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env" 0:0 600 \
        || fail "previous successful environment is unsafe"
      previous_success=$(read_unique_environment_value \
        "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env" RELEASE_VERSION)
      case "$previous_success" in *[!0-9a-f]*|'') fail "previous successful release is invalid" ;; esac
      [ "${#previous_success}" -eq 40 ] || fail "previous successful release is invalid"
    fi
    if [ "$operation" = deploy ] && [ -z "$previous_success" ] && \
       { [ ! -e "$pending_file" ] && [ ! -L "$pending_file" ]; }; then
      fail "first deployment requires explicit prepare followed by operator OAuth bootstrap and finalize"
    fi
    if [ "$operation" = prepare ] && [ -n "$previous_success" ]; then
      fail "prepare is cold-bootstrap-only and cannot pause over a successful release"
    fi
    if [ "$operation" = prepare ]; then
      transaction_mode=cold-prepare
    else
      transaction_mode=immediate-deploy
    fi
    if [ -e "$pending_file" ] || [ -L "$pending_file" ]; then
      hook2stream_trusted_file "$pending_file" 0:0 600 \
        || fail "existing pending deployment marker is unsafe"
      pending_state=$(jq -ce --arg environment "$environment" --arg artifact "$artifact_name" \
        --arg commit "$commit" --arg previous "$previous_success" \
        --arg imagesSha "$images_sha" --arg bundleSha "$bundle_sha" \
        --arg envSha "$release_env_sha" --arg receiptSha "$staging_receipt_sha" \
        --arg signatureSha "$staging_signature_sha" --arg signersSha "$staging_signers_sha" \
        --arg transactionMode "$transaction_mode" 'select(
          .schemaVersion == 1 and .kind == "hook2stream-pending-deploy" and
          (.phase == "intent" or .phase == "runtime-ready" or .phase == "finalizing") and
          .transactionMode == $transactionMode and
          .environment == $environment and .candidateArtifact == $artifact and .commitSha == $commit and
          .previousSuccessfulSha == (if $previous == "" then null else $previous end) and
          .releaseImagesSha256 == $imagesSha and .deployBundleSha256 == $bundleSha and
          .releaseEnvironmentSha256 == $envSha and
          .stagingReceiptSha256 == (if $receiptSha == "" then null else $receiptSha end) and
          .stagingSignatureSha256 == (if $signatureSha == "" then null else $signatureSha end) and
          .stagingAllowedSignersSha256 == (if $signersSha == "" then null else $signersSha end) and
          (.e2eOperationId | type == "string" and test("^[0-9a-f]{32}$"))
        )' "$pending_file") || fail "another or drifted deployment is already pending"
      hook2stream_trusted_file "$release_env" 0:0 600 \
        && [ "$(sha256sum "$release_env" | awk '{print $1}')" = "$release_env_sha" ] \
        || fail "persisted pending candidate environment is unavailable or drifted"
      rm -f "$release_env.tmp"
      pending_phase=$(printf '%s' "$pending_state" | jq -r '.phase')
      if [ "$pending_phase" != intent ]; then
        if [ "$operation" = deploy ]; then
          rm -rf "$incoming"
          trap - EXIT
          trap - HUP INT TERM
          exec 8>&-
          SSH_ORIGINAL_COMMAND="finalize-transaction $identifier"
          export SSH_ORIGINAL_COMMAND
          exec "$0"
        fi
        printf 'HOOK2STREAM_PENDING_RECEIPT=%s\n' \
          "$(printf '%s' "$pending_state" | base64 | tr -d '\n')"
        exit 0
      fi
    else
      mv -f "$release_env.tmp" "$release_env"
      pending_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
      operation_id=$(new_operation_id)
      jq -cn --arg environment "$environment" --arg artifact "$artifact_name" \
        --arg commit "$commit" --arg previous "$previous_success" \
        --arg imagesSha "$images_sha" --arg bundleSha "$bundle_sha" \
        --arg envSha "$release_env_sha" --arg receiptSha "$staging_receipt_sha" \
        --arg signatureSha "$staging_signature_sha" --arg signersSha "$staging_signers_sha" \
        --arg operation "$operation_id" --arg transactionMode "$transaction_mode" \
        --arg updatedAt "$pending_at" \
        '{schemaVersion:1,kind:"hook2stream-pending-deploy",phase:"intent",transactionMode:$transactionMode,environment:$environment,candidateArtifact:$artifact,commitSha:$commit,previousSuccessfulSha:(if $previous == "" then null else $previous end),releaseImagesSha256:$imagesSha,deployBundleSha256:$bundleSha,releaseEnvironmentSha256:$envSha,stagingReceiptSha256:(if $receiptSha == "" then null else $receiptSha end),stagingSignatureSha256:(if $signatureSha == "" then null else $signatureSha end),stagingAllowedSignersSha256:(if $signersSha == "" then null else $signersSha end),actualImages:null,e2eOperationId:$operation,updatedAt:$updatedAt}' \
        > "$pending_file.tmp"
      chmod 0600 "$pending_file.tmp"
      mv -f "$pending_file.tmp" "$pending_file"
    fi

    operation_id=$(jq -r '.e2eOperationId' "$pending_file")
    rollout_child=
    recover_failed_rollout() {
      rollout_reason=$1
      trap - HUP
      trap - INT
      trap - TERM
      rollout_recovery_interrupted() {
        trap - HUP
        trap - INT
        trap - TERM
        write_recovery_required rollout-compensation-interrupted "$identifier" \
          "$commit" "$operation_id" "$release_env" "$release_dir/deploy"
        exit 130
      }
      trap rollout_recovery_interrupted HUP INT TERM
      if [ -n "$previous_success" ]; then
        rollout_target=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$previous_success.env
        rollout_capability=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$previous_success.capabilities.json
        rollout_compensation=$HOOK2STREAM_RELEASE_STATE_DIR/compensation-$operation_id.env
        if rollout_candidate_images=$(collect_actual_images \
             "$release_env" "$release_dir/deploy") && \
           hook2stream_trusted_file "$rollout_target" 0:0 600 && \
           hook2stream_validate_rollback_capability "$rollout_capability" \
             "$previous_success" "$rollback_protocol" 0:0 && \
           env -u HOOK2STREAM_RELEASE_STATE_DIR "$rollback_program" \
             "$release_env" "$rollout_target" "$rollout_compensation" \
             "$previous_success" "$release_dir/deploy" && \
           rollout_restored_images=$(collect_actual_images \
             "$rollout_compensation" "$release_dir/deploy") && \
           publish_compensated_state "$identifier" "$commit" "$operation_id" \
             "$previous_success" "$rollout_reason" "$release_env" \
             "$rollout_compensation" "$bundle_sha" "$rollout_restored_images"; then
          trap - HUP
          trap - INT
          trap - TERM
          return 0
        fi
      fi
      write_recovery_required rollout-incomplete "$identifier" "$commit" \
        "$operation_id" "$release_env" "$release_dir/deploy"
      trap - HUP
      trap - INT
      trap - TERM
      return 1
    }
    interrupt_rollout() {
      trap - HUP
      trap - INT
      trap - TERM
      if [ -n "$rollout_child" ]; then
        kill -TERM "$rollout_child" 2>/dev/null || true
        wait "$rollout_child" 2>/dev/null || true
        rollout_child=
      fi
      recover_failed_rollout interrupted || true
      exit 130
    }
    trap interrupt_rollout HUP INT TERM
    HOOK2STREAM_ENV_FILE=$release_env HOOK2STREAM_DEFER_SUCCESS_MARKER=true \
      env -u HOOK2STREAM_RELEASE_STATE_DIR \
      "$release_dir/deploy/scripts/deploy-release.sh" &
    rollout_child=$!
    if ! wait "$rollout_child"; then
      rollout_child=
      if recover_failed_rollout deploy-release-failed; then
        fail "candidate rollout failed after mutation; previous application release was restored"
      fi
      fail "candidate rollout failed and could not be compensated; manual recovery is required"
    fi
    rollout_child=
    if ! actual_images=$(collect_actual_images "$release_env" "$release_dir/deploy"); then
      if recover_failed_rollout running-digest-verification-failed; then
        fail "candidate rollout digest verification failed; previous application release was restored"
      fi
      fail "candidate rollout digest verification failed; manual recovery is required"
    fi
    publish_runtime_ready() {
      ready_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ') || return 1
      jq -c --arg updatedAt "$ready_at" --argjson images "$actual_images" \
        '.phase="runtime-ready" | .actualImages=$images | .updatedAt=$updatedAt' \
        "$pending_file" > "$pending_file.tmp" || return 1
      chmod 0600 "$pending_file.tmp" || return 1
      mv -f "$pending_file.tmp" "$pending_file" || return 1
    }
    if ! publish_runtime_ready; then
      if recover_failed_rollout runtime-ready-publication-failed; then
        fail "candidate runtime-ready publication failed; previous application release was restored"
      fi
      fail "candidate runtime-ready publication failed; manual recovery is required"
    fi
    pending_result=$(cat "$pending_file")
    if [ "$operation" = deploy ]; then
      rm -rf "$incoming"
      trap - EXIT
      exec 8>&-
      SSH_ORIGINAL_COMMAND="finalize-transaction $identifier"
      export SSH_ORIGINAL_COMMAND
      exec "$0"
    fi
    printf 'HOOK2STREAM_PENDING_RECEIPT=%s\n' \
      "$(printf '%s' "$pending_result" | base64 | tr -d '\n')"
    ;;
  finalize|finalize-transaction)
    [ "$#" -eq 2 ] || fail "finalize accepts exactly one candidate ID"
    case "$identifier" in release-candidate-[0-9a-f]*-[0-9]*-[0-9]*) ;; *) fail "invalid candidate ID" ;; esac
    identifier_tail=${identifier#release-candidate-}
    requested_commit=${identifier_tail%%-*}
    case "$requested_commit" in *[!0-9a-f]*|'') fail "candidate commit is invalid" ;; esac
    [ "${#requested_commit}" -eq 40 ] || fail "candidate commit is invalid"
    stored_result=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$identifier.remote-result.json
    if [ ! -e "$pending_file" ] && [ ! -L "$pending_file" ]; then
      hook2stream_trusted_file "$stored_result" 0:0 600 \
        && hook2stream_trusted_file "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env" 0:0 600 \
        || fail "no pending deployment or re-emittable successful result exists"
      [ "$(read_unique_environment_value "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env" RELEASE_VERSION)" = "$requested_commit" ] \
        || fail "stored deployment result is not the active successful release"
      successful_env=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$requested_commit.env
      active_infrastructure=$HOOK2STREAM_RELEASE_STATE_DIR/active-infrastructure-release.json
      hook2stream_trusted_file "$successful_env" 0:0 600 \
        && hook2stream_trusted_file "$active_infrastructure" 0:0 600 \
        || fail "stored deployment result lacks trusted active release state"
      stored_receipt=$(jq -ce --arg environment "$configured_environment" \
        --arg artifact "$identifier" --arg commit "$requested_commit" 'select(
          (keys | sort) == ["actualImages","candidateArtifact","checks","commitSha","deployBundleSha256","e2eOperationId","environment","kind","minimumRollbackReleaseSha","releaseImagesSha256","result","schemaVersion"] and
          .schemaVersion == 1 and .kind == "hook2stream-remote-deploy-result" and
          .environment == $environment and .result == "success" and
          .candidateArtifact == $artifact and .commitSha == $commit and
          (.e2eOperationId | type == "string" and test("^[0-9a-f]{32}$")) and
          (.deployBundleSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
          (.releaseImagesSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
          .checks == ["pre-migration-backup","migration","smoke","e2e","digest-verification"]
        )' "$stored_result") || fail "stored deployment result identity is invalid"
      active_state=$(jq -ce --arg sha "$requested_commit" \
        --arg bundle "$(printf '%s' "$stored_receipt" | jq -r '.deployBundleSha256')" 'select(
          .schemaVersion == 2 and .kind == "hook2stream-active-infrastructure-release" and
          .releaseSha == $sha and .deployBundleSha256 == $bundle and
          .rollbackProtocol == "hook2stream-application-rollback-v2"
        )' "$active_infrastructure") || fail "active infrastructure differs from the replayed successful receipt"
      active_release_dir=$HOOK2STREAM_RELEASES_DIR/$requested_commit
      hook2stream_trusted_directory "$active_release_dir" 0:0 700 \
        && hook2stream_trusted_file "$active_release_dir/.deploy-bundle.sha256" 0:0 600 \
        || fail "active release bundle is unavailable for receipt replay"
      [ "$(cat "$active_release_dir/.deploy-bundle.sha256")" = \
        "$(printf '%s' "$stored_receipt" | jq -r '.deployBundleSha256')" ] \
        || fail "active release bundle differs from the replayed receipt"
      replay_images=$(collect_actual_images "$successful_env" "$active_release_dir/deploy")
      printf '%s' "$stored_receipt" | jq -e --argjson actual "$replay_images" \
        '.actualImages == $actual' >/dev/null \
        || fail "running images differ from the replayed successful receipt"
      printf 'HOOK2STREAM_REMOTE_RECEIPT=%s\n' \
        "$(printf '%s' "$stored_receipt" | base64 | tr -d '\n')"
      exit 0
    fi
    hook2stream_trusted_file "$pending_file" 0:0 600 \
      || fail "root-owned pending deployment is unavailable"
    pending_state=$(jq -ce --arg environment "$configured_environment" \
      --arg artifact "$identifier" 'select(
        (keys | sort) == ["actualImages","candidateArtifact","commitSha","deployBundleSha256","e2eOperationId","environment","kind","phase","previousSuccessfulSha","releaseEnvironmentSha256","releaseImagesSha256","schemaVersion","stagingAllowedSignersSha256","stagingReceiptSha256","stagingSignatureSha256","transactionMode","updatedAt"] and
        .schemaVersion == 1 and .kind == "hook2stream-pending-deploy" and
        (.phase == "runtime-ready" or .phase == "finalizing") and
        (.transactionMode == "cold-prepare" or .transactionMode == "immediate-deploy") and
        .environment == $environment and .candidateArtifact == $artifact and
        (.commitSha | type == "string" and test("^[0-9a-f]{40}$")) and
        (.previousSuccessfulSha == null or (.previousSuccessfulSha | type == "string" and test("^[0-9a-f]{40}$"))) and
        (.releaseImagesSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
        (.deployBundleSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
        (.releaseEnvironmentSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
        (if .environment == "production" then
          (.stagingReceiptSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
          (.stagingSignatureSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
          (.stagingAllowedSignersSha256 | type == "string" and test("^[0-9a-f]{64}$"))
        else
          .stagingReceiptSha256 == null and .stagingSignatureSha256 == null and
          .stagingAllowedSignersSha256 == null
        end) and
        (.actualImages | type == "object") and
        (.e2eOperationId | type == "string" and test("^[0-9a-f]{32}$")) and
        (.updatedAt | type == "string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$") and fromdateiso8601 >= 0)
    )' "$pending_file") || fail "pending deployment identity or phase is invalid"
    transaction_mode=$(printf '%s' "$pending_state" | jq -r '.transactionMode')
    case "$operation:$transaction_mode" in
      finalize:cold-prepare|finalize-transaction:immediate-deploy) ;;
      *) fail "finalize command does not match the pending deployment transaction mode" ;;
    esac
    if [ "$configured_environment" = production ]; then
      [ "$(sha256sum "$HOOK2STREAM_STAGING_SIGNERS" | awk '{print $1}')" = \
        "$(printf '%s' "$pending_state" | jq -r '.stagingAllowedSignersSha256')" ] \
        || fail "production staging-receipt authority changed after pending preparation"
    fi
    commit=$(printf '%s' "$pending_state" | jq -r '.commitSha')
    bundle_sha=$(printf '%s' "$pending_state" | jq -r '.deployBundleSha256')
    images_sha=$(printf '%s' "$pending_state" | jq -r '.releaseImagesSha256')
    release_env_sha=$(printf '%s' "$pending_state" | jq -r '.releaseEnvironmentSha256')
    previous_success=$(printf '%s' "$pending_state" | jq -r '.previousSuccessfulSha // empty')
    operation_id=$(printf '%s' "$pending_state" | jq -r '.e2eOperationId')
    successful_dir=$HOOK2STREAM_RELEASE_STATE_DIR/successful
    stored_result=$successful_dir/$identifier.remote-result.json
    receipt_verified=false
    if [ -e "$stored_result" ] || [ -L "$stored_result" ]; then
      hook2stream_trusted_file "$stored_result" 0:0 600 \
        || fail "stored successful deployment result is unsafe"
      historical_receipt=$(jq -ce --arg environment "$configured_environment" --arg artifact "$identifier" \
        --arg commit "$commit" --arg operation "$operation_id" --arg imagesSha "$images_sha" \
        --arg bundleSha "$bundle_sha" 'select(
          .schemaVersion == 1 and .kind == "hook2stream-remote-deploy-result" and
          .environment == $environment and .result == "success" and
          .candidateArtifact == $artifact and .commitSha == $commit and
          (.e2eOperationId | type == "string" and test("^[0-9a-f]{32}$")) and
          .releaseImagesSha256 == $imagesSha and
          .deployBundleSha256 == $bundleSha
        )' "$stored_result") || fail "stored successful deployment result conflicts with pending finalization"
      if [ "$(printf '%s' "$historical_receipt" | jq -r '.e2eOperationId')" = "$operation_id" ]; then
        receipt=$historical_receipt
        receipt_verified=true
      fi
    fi
    live_success=
    if [ -e "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env" ] || \
       [ -L "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env" ]; then
      hook2stream_trusted_file "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env" 0:0 600 \
        || fail "live successful environment is unsafe during pending finalization"
      live_success=$(read_unique_environment_value \
        "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env" RELEASE_VERSION)
    fi
    if [ "$receipt_verified" = true ]; then
      [ "$live_success" = "$previous_success" ] || [ "$live_success" = "$commit" ] \
        || fail "live successful release drifted during repairable result publication"
    else
      [ "$live_success" = "$previous_success" ] \
        || fail "live successful release drifted after pending deployment was prepared"
    fi
    release_dir=$HOOK2STREAM_RELEASES_DIR/$commit
    release_env=$HOOK2STREAM_RELEASE_STATE_DIR/candidate-$commit.env
    hook2stream_trusted_directory "$release_dir" 0:0 700 \
      && hook2stream_trusted_file "$release_dir/.deploy-bundle.sha256" 0:0 600 \
      && hook2stream_trusted_file "$release_dir/deploy/compose.yaml" 0:0 600 \
      && hook2stream_trusted_file "$release_dir/deploy/scripts/lib/deployment-common.sh" 0:0 600 \
      && hook2stream_trusted_file "$release_env" 0:0 600 \
      || fail "pending candidate control plane or environment is unsafe"
    [ "$(cat "$release_dir/.deploy-bundle.sha256")" = "$bundle_sha" ] \
      || fail "pending candidate bundle differs from its deployment intent"
    [ "$(sha256sum "$release_env" | awk '{print $1}')" = "$release_env_sha" ] \
      || fail "pending candidate environment differs from its deployment intent"
    [ "$(read_deployment_environment "$release_env")" = "$configured_environment" ] \
      && [ "$(read_unique_environment_value "$release_env" RELEASE_VERSION)" = "$commit" ] \
      || fail "pending candidate environment differs from its deployment intent"
    if ! actual_images=$(collect_actual_images "$release_env" "$release_dir/deploy"); then
      write_recovery_required pending-runtime-unverifiable "$identifier" "$commit" \
        "$operation_id" "$release_env" "$release_dir/deploy"
      fail "pending runtime cannot be verified; manual recovery is required"
    fi
    if ! printf '%s' "$pending_state" | jq -e --argjson actual "$actual_images" \
      '.actualImages == $actual' >/dev/null; then
      write_recovery_required pending-runtime-drift "$identifier" "$commit" \
        "$operation_id" "$release_env" "$release_dir/deploy"
      fail "running images differ from the runtime-ready pending deployment; manual recovery is required"
    fi

    install -d -o root -g root -m 0700 "$successful_dir"
    if [ "$receipt_verified" = true ]; then
      printf '%s' "$receipt" | jq -e --argjson actual "$actual_images" \
        '.actualImages == $actual' >/dev/null \
        || fail "stored successful result differs from the running candidate"
      verified_images=$actual_images
    else
      finalizing_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
      printf '%s' "$pending_state" | jq -c --arg updatedAt "$finalizing_at" \
        '.phase="finalizing" | .updatedAt=$updatedAt' \
        > "$pending_file.tmp"
      chmod 0600 "$pending_file.tmp"
      mv -f "$pending_file.tmp" "$pending_file"
      finalize_child=
      compensate_failed_finalize() {
        compensation_reason=$1
        [ -n "$previous_success" ] || return 2
        compensation_target=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$previous_success.env
        compensation_capability=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$previous_success.capabilities.json
        compensation_env=$HOOK2STREAM_RELEASE_STATE_DIR/compensation-$operation_id.env
        compensation_interrupted() {
          trap - HUP
          trap - INT
          trap - TERM
          write_recovery_required compensation-interrupted "$identifier" "$commit" \
            "$operation_id" "$release_env" "$release_dir/deploy"
          exit 130
        }
        trap compensation_interrupted HUP INT TERM
        if ! hook2stream_trusted_file "$compensation_target" 0:0 600 || \
           ! hook2stream_validate_rollback_capability \
             "$compensation_capability" "$previous_success" "$rollback_protocol" 0:0; then
          write_recovery_required compensation-target-invalid "$identifier" "$commit" \
            "$operation_id" "$release_env" "$release_dir/deploy"
          trap - HUP
          trap - INT
          trap - TERM
          return 1
        fi
        if ! env -u HOOK2STREAM_RELEASE_STATE_DIR "$rollback_program" \
          "$release_env" "$compensation_target" "$compensation_env" \
          "$previous_success" "$release_dir/deploy"; then
          write_recovery_required compensation-failed "$identifier" "$commit" \
            "$operation_id" "$release_env" "$release_dir/deploy"
          trap - HUP
          trap - INT
          trap - TERM
          return 1
        fi
        if ! compensated_images=$(collect_actual_images \
          "$compensation_env" "$release_dir/deploy"); then
          write_recovery_required compensation-digest-verification-failed \
            "$identifier" "$commit" "$operation_id" "$release_env" \
            "$release_dir/deploy"
          trap - HUP
          trap - INT
          trap - TERM
          return 1
        fi
        if ! publish_compensated_state "$identifier" "$commit" "$operation_id" \
          "$previous_success" "$compensation_reason" "$release_env" \
          "$compensation_env" "$bundle_sha" "$compensated_images"; then
          write_recovery_required compensation-state-publication-failed \
            "$identifier" "$commit" "$operation_id" "$release_env" \
            "$release_dir/deploy"
          trap - HUP
          trap - INT
          trap - TERM
          return 1
        fi
        trap - HUP
        trap - INT
        trap - TERM
        return 0
      }
      interrupt_finalize() {
        trap - HUP
        trap - INT
        trap - TERM
        if [ -n "$finalize_child" ]; then
          kill -TERM "$finalize_child" 2>/dev/null || true
          wait "$finalize_child" 2>/dev/null || true
          finalize_child=
        fi
        if [ -n "$previous_success" ]; then
          compensate_failed_finalize interrupted || true
        fi
        exit 130
      }
      trap interrupt_finalize HUP INT TERM
      "$HOOK2STREAM_E2E_HOOK" \
        "$configured_environment" "$release_env" "$commit" "$operation_id" &
      finalize_child=$!
      if ! wait "$finalize_child"; then
        finalize_child=
        if [ -n "$previous_success" ]; then
          if compensate_failed_finalize authenticated-e2e-failed; then
            fail "authenticated finalization failed; previous application release was restored and exact-candidate retry is required"
          fi
          fail "authenticated finalization and automatic compensation failed; manual recovery is required"
        fi
        fail "cold-bootstrap authenticated finalization failed; pending operation is retained for exact retry"
      fi
      finalize_child=
      if ! verified_images=$(collect_actual_images "$release_env" "$release_dir/deploy") || \
         [ "$verified_images" != "$actual_images" ]; then
        if [ -n "$previous_success" ]; then
          if compensate_failed_finalize running-digest-changed; then
            fail "running images changed during authenticated finalization; previous application release was restored"
          fi
          fail "running image verification and automatic compensation failed; manual recovery is required"
        fi
        fail "running image set changed during cold-bootstrap authenticated finalization"
      fi
      receipt=$(jq -cn --arg environment "$configured_environment" \
        --arg artifact "$identifier" --arg commit "$commit" \
        --arg operation "$operation_id" --arg minimum "$MIN_ROLLBACK_RELEASE_SHA" \
        --arg imagesSha "$images_sha" --arg bundleSha "$bundle_sha" \
        --argjson images "$verified_images" \
        '{schemaVersion:1,kind:"hook2stream-remote-deploy-result",environment:$environment,result:"success",candidateArtifact:$artifact,commitSha:$commit,e2eOperationId:$operation,minimumRollbackReleaseSha:$minimum,releaseImagesSha256:$imagesSha,deployBundleSha256:$bundleSha,actualImages:$images,checks:["pre-migration-backup","migration","smoke","e2e","digest-verification"]}')
      printf '%s\n' "$receipt" > "$stored_result.tmp"
      chmod 0600 "$stored_result.tmp"
      mv -f "$stored_result.tmp" "$stored_result"
    fi
    install -m 0600 "$release_env" "$successful_dir/$commit.env"
    jq -cn --arg sha "$commit" --arg protocol "$rollback_protocol" \
      '{schemaVersion:2,releaseSha:$sha,storageFormats:["H2SEv1"],rollbackProtocol:$protocol}' \
      > "$successful_dir/$commit.capabilities.json.tmp"
    chmod 0600 "$successful_dir/$commit.capabilities.json.tmp"
    mv -f "$successful_dir/$commit.capabilities.json.tmp" "$successful_dir/$commit.capabilities.json"
    active_infrastructure=$HOOK2STREAM_RELEASE_STATE_DIR/active-infrastructure-release.json
    jq -cn --arg sha "$commit" --arg bundle "$bundle_sha" --arg protocol "$rollback_protocol" \
      '{schemaVersion:2,kind:"hook2stream-active-infrastructure-release",releaseSha:$sha,deployBundleSha256:$bundle,rollbackProtocol:$protocol}' \
      > "$active_infrastructure.tmp"
    chmod 0600 "$active_infrastructure.tmp"
    mv -f "$active_infrastructure.tmp" "$active_infrastructure"
    install -m 0600 "$release_env" "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp"
    mv -f "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp" \
      "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env"
    rm -f \
      "$HOOK2STREAM_RELEASE_STATE_DIR/infrastructure/$commit.env" \
      "$HOOK2STREAM_RELEASE_STATE_DIR/infrastructure/$commit.capabilities.json"
    if [ "$configured_environment" = staging ]; then
      activated_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
      jq -cn --arg artifact "$identifier" --arg commit "$commit" --arg activatedAt "$activated_at" \
        '{schema:"hook2stream-current-candidate-v1",environment:"staging",candidateArtifact:$artifact,commitSha:$commit,activatedAt:$activatedAt}' \
        > "$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json.tmp"
      chmod 0600 "$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json.tmp"
      mv -f "$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json.tmp" \
        "$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json"
    fi
    if [ "$receipt_verified" != true ]; then
      trap - HUP
      trap - INT
      trap - TERM
    fi
    rm -f "$pending_file"
    printf 'HOOK2STREAM_REMOTE_RECEIPT=%s\n' \
      "$(printf '%s' "$receipt" | base64 | tr -d '\n')"
    ;;
  soak)
    [ "$#" -eq 2 ] || fail "soak accepts exactly one candidate ID"
    [ "$configured_environment" = staging ] || fail "soak is allowed only on staging"
    [ ! -e "$pending_file" ] && [ ! -L "$pending_file" ] \
      || fail "soak is forbidden while a deployment is pending finalization"
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
    [ ! -e "$pending_file" ] && [ ! -L "$pending_file" ] \
      || fail "rollback is forbidden while a forward deployment is pending; retry or finalize the exact candidate"
    case "$identifier" in *[!0-9a-f]*|'') fail "rollback requires a full 40-character commit SHA" ;; esac
    [ "${#identifier}" -eq 40 ] || fail "rollback requires a full 40-character commit SHA"
    validate_ghcr_pull_auth
    rollback_env=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$identifier.env
    current_env=$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env
    hook2stream_trusted_file "$rollback_env" 0:0 600 \
      && hook2stream_trusted_file "$current_env" 0:0 600 \
      || fail "current successful environment is unavailable or unsafe"
    validate_rollback_capability \
      "$HOOK2STREAM_RELEASE_STATE_DIR/successful/$identifier.capabilities.json" "$identifier"
    current_release=$(read_unique_environment_value "$current_env" RELEASE_VERSION)
    case "$current_release" in *[!0-9a-f]*|'') fail "current release SHA is invalid" ;; esac
    [ "${#current_release}" -eq 40 ] || fail "current release SHA is invalid"
    validate_rollback_capability \
      "$HOOK2STREAM_RELEASE_STATE_DIR/successful/$current_release.capabilities.json" "$current_release"
    active_infrastructure=$HOOK2STREAM_RELEASE_STATE_DIR/active-infrastructure-release.json
    hook2stream_trusted_file "$active_infrastructure" 0:0 600 \
      || fail "active infrastructure release marker is unavailable or unsafe"
    infrastructure_state=$(jq -ce --arg protocol "$rollback_protocol" 'select(
      (keys | sort) == ["deployBundleSha256","kind","releaseSha","rollbackProtocol","schemaVersion"] and
      .schemaVersion == 2 and .kind == "hook2stream-active-infrastructure-release" and
      .rollbackProtocol == $protocol and
      (.releaseSha | type == "string" and test("^[0-9a-f]{40}$")) and
      (.deployBundleSha256 | type == "string" and test("^[0-9a-f]{64}$"))
    )' "$active_infrastructure") || fail "active infrastructure marker is not protocol v2"
    infrastructure_release=$(printf '%s' "$infrastructure_state" | jq -r '.releaseSha')
    infrastructure_bundle_sha=$(printf '%s' "$infrastructure_state" | jq -r '.deployBundleSha256')
    infrastructure_ledger=$HOOK2STREAM_RELEASE_STATE_DIR/infrastructure
    ledger_capability=$infrastructure_ledger/$infrastructure_release.capabilities.json
    ledger_environment=$infrastructure_ledger/$infrastructure_release.env
    successful_infrastructure_capability=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$infrastructure_release.capabilities.json
    successful_infrastructure_env=$HOOK2STREAM_RELEASE_STATE_DIR/successful/$infrastructure_release.env
    if [ "$current_release" = "$infrastructure_release" ]; then
      infrastructure_capability=$successful_infrastructure_capability
      infrastructure_env=$successful_infrastructure_env
      if [ -e "$ledger_capability" ] || [ -L "$ledger_capability" ] || \
         [ -e "$ledger_environment" ] || [ -L "$ledger_environment" ]; then
        hook2stream_trusted_directory "$infrastructure_ledger" 0:0 700 \
          && hook2stream_trusted_file "$ledger_capability" 0:0 600 \
          && hook2stream_trusted_file "$ledger_environment" 0:0 600 \
          && hook2stream_trusted_file "$successful_infrastructure_capability" 0:0 600 \
          && hook2stream_trusted_file "$successful_infrastructure_env" 0:0 600 \
          && cmp -s "$ledger_capability" "$successful_infrastructure_capability" \
          && cmp -s "$ledger_environment" "$successful_infrastructure_env" \
          || fail "same-SHA compensated infrastructure ledger is stale or unsafe"
      fi
    else
      hook2stream_trusted_directory "$infrastructure_ledger" 0:0 700 \
        && hook2stream_trusted_file "$ledger_capability" 0:0 600 \
        && hook2stream_trusted_file "$ledger_environment" 0:0 600 \
        || fail "active compensated infrastructure ledger is incomplete or unsafe"
      infrastructure_capability=$ledger_capability
      infrastructure_env=$ledger_environment
    fi
    validate_rollback_capability "$infrastructure_capability" "$infrastructure_release"
    hook2stream_trusted_file "$infrastructure_env" 0:0 600 \
      || fail "active infrastructure environment is unavailable or unsafe"
    for infrastructure_variable in \
      BOOTSTRAPPER_IMAGE POSTGRES_BACKUP_IMAGE CADDY_IMAGE POSTGRES_IMAGE \
      PGBOUNCER_IMAGE EGRESS_PROXY_IMAGE; do
      [ "$(read_unique_environment_value "$current_env" "$infrastructure_variable")" = \
        "$(read_unique_environment_value "$infrastructure_env" "$infrastructure_variable")" ] \
        || fail "$infrastructure_variable differs from the active infrastructure release marker"
    done
    infrastructure_release_dir=$HOOK2STREAM_RELEASES_DIR/$infrastructure_release
    hook2stream_trusted_directory "$infrastructure_release_dir" 0:0 700 \
      && hook2stream_trusted_file "$infrastructure_release_dir/.deploy-bundle.sha256" 0:0 600 \
      && hook2stream_trusted_file "$infrastructure_release_dir/deploy/compose.yaml" 0:0 600 \
      && hook2stream_trusted_file "$infrastructure_release_dir/deploy/scripts/lib/deployment-common.sh" 0:0 600 \
      && hook2stream_trusted_file "$infrastructure_release_dir/deploy/scripts/lib/forced-command-trust.sh" 0:0 700 \
      || fail "active infrastructure bundle is unavailable or unsafe"
    [ "$(cat "$infrastructure_release_dir/.deploy-bundle.sha256")" = "$infrastructure_bundle_sha" ] \
      || fail "active infrastructure bundle differs from its successful forward-deploy marker"
    environment=$(read_deployment_environment "$rollback_env")
    [ "$environment" = "$configured_environment" ] \
      || fail "rollback target differs from the configured host environment"
    [ "$(read_deployment_environment "$current_env")" = "$environment" ] \
      || fail "rollback target belongs to a different environment"
    rollback_operation_id=$(new_operation_id)
    rollback_original_env=$HOOK2STREAM_RELEASE_STATE_DIR/rollback-original-$rollback_operation_id.env
    active_rollback_env=$HOOK2STREAM_RELEASE_STATE_DIR/active-rollback-$identifier-$rollback_operation_id.env
    rollback_restore_env=$HOOK2STREAM_RELEASE_STATE_DIR/rollback-restore-$rollback_operation_id.env
    install -m 0600 "$current_env" "$rollback_original_env"
    rollback_mutated=false
    rollback_child=
    recover_failed_rollback() {
      rollback_failure_reason=$1
      rollback_recovery_interrupted() {
        trap - HUP
        trap - INT
        trap - TERM
        write_recovery_required rollback-compensation-interrupted \
          "rollback-$identifier" "$identifier" "$rollback_operation_id" \
          "$rollback_original_env" "$infrastructure_release_dir/deploy"
        exit 130
      }
      trap rollback_recovery_interrupted HUP INT TERM
      rollback_recovered=false
      if [ "$rollback_mutated" = false ] && \
         hook2stream_trusted_file "$active_rollback_env" 0:0 600; then
        rollback_mutated=true
      fi
      if [ "$rollback_mutated" = true ] && \
         hook2stream_trusted_file "$active_rollback_env" 0:0 600 && \
         env -u HOOK2STREAM_RELEASE_STATE_DIR "$rollback_program" \
           "$active_rollback_env" "$rollback_original_env" "$rollback_restore_env" \
           "$current_release" "$infrastructure_release_dir/deploy" && \
         restored_images=$(collect_actual_images \
           "$rollback_restore_env" "$infrastructure_release_dir/deploy"); then
        rollback_recovered=true
      elif [ "$rollback_mutated" = false ] && \
           restored_images=$(collect_actual_images \
             "$rollback_original_env" "$infrastructure_release_dir/deploy"); then
        rollback_recovered=true
      fi
      if [ "$rollback_recovered" = true ] && \
         { ! install -m 0600 "$rollback_original_env" \
             "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp" || \
           ! mv -f "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp" \
             "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env"; }; then
        rollback_recovered=false
      fi
      if [ "$rollback_recovered" != true ]; then
        write_recovery_required "$rollback_failure_reason" \
          "rollback-$identifier" "$identifier" "$rollback_operation_id" \
          "$rollback_original_env" "$infrastructure_release_dir/deploy"
        trap - HUP
        trap - INT
        trap - TERM
        return 1
      fi
      trap - HUP
      trap - INT
      trap - TERM
      rm -f "$active_rollback_env" "$rollback_restore_env" "$rollback_original_env"
      return 0
    }
    interrupt_rollback() {
      trap - HUP
      trap - INT
      trap - TERM
      if [ -n "$rollback_child" ]; then
        kill -TERM "$rollback_child" 2>/dev/null || true
        wait "$rollback_child" 2>/dev/null || true
        rollback_child=
      fi
      recover_failed_rollback rollback-interrupted || true
      exit 130
    }
    trap interrupt_rollback HUP INT TERM
    env -u HOOK2STREAM_RELEASE_STATE_DIR "$rollback_program" \
      "$rollback_original_env" "$rollback_env" "$active_rollback_env" "$identifier" \
      "$infrastructure_release_dir/deploy" &
    rollback_child=$!
    if ! wait "$rollback_child"; then
      rollback_child=
      if recover_failed_rollback rollback-mutation-failed; then
        fail "rollback mutation failed; the original application release was restored"
      fi
      fail "rollback mutation and compensation failed; manual recovery is required"
    fi
    rollback_child=
    rollback_mutated=true
    "$HOOK2STREAM_E2E_HOOK" \
      "$environment" "$active_rollback_env" "$identifier" rollback-verify &
    rollback_child=$!
    if ! wait "$rollback_child"; then
      rollback_child=
      if recover_failed_rollback rollback-bounded-e2e-failed; then
        fail "bounded rollback verification failed; the original application release was restored"
      fi
      fail "bounded rollback verification and compensation failed; manual recovery is required"
    fi
    rollback_child=
    if ! actual_images_with_bootstrap=$(collect_actual_images \
      "$active_rollback_env" "$infrastructure_release_dir/deploy"); then
      if recover_failed_rollback rollback-digest-verification-failed; then
        fail "rollback digest verification failed; the original application release was restored"
      fi
      fail "rollback digest verification and compensation failed; manual recovery is required"
    fi
    actual_images=$(printf '%s' "$actual_images_with_bootstrap" | jq -c 'del(.BOOTSTRAPPER_IMAGE)')
    preserved_bootstrap=$(awk -F= '$1 == "BOOTSTRAPPER_IMAGE" {print substr($0,index($0,"=")+1)}' "$active_rollback_env")
    install -m 0600 "$active_rollback_env" "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp"
    mv -f "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env.tmp" "$HOOK2STREAM_RELEASE_STATE_DIR/last-successful.env"
    rollback_mutated=false
    trap - HUP
    trap - INT
    trap - TERM
    rm -f "$rollback_original_env" "$rollback_restore_env"
    if [ "$environment" = staging ]; then
      rolled_back_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
      jq -cn --arg commit "$identifier" --arg rolledBackAt "$rolled_back_at" \
        '{schema:"hook2stream-current-rollback-v1",environment:"staging",commitSha:$commit,rolledBackAt:$rolledBackAt}' \
        > "$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json.tmp"
      chmod 0600 "$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json.tmp"
      mv -f "$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json.tmp" \
        "$HOOK2STREAM_RELEASE_STATE_DIR/current-candidate.json"
    fi
    rollback_receipt=$(jq -cn --arg environment "$environment" --arg sha "$identifier" --arg minimum "$MIN_ROLLBACK_RELEASE_SHA" --arg bootstrap "$preserved_bootstrap" --argjson images "$actual_images" '{schemaVersion:1,kind:"hook2stream-remote-rollback-result",environment:$environment,result:"success",releaseSha:$sha,storageFormat:"H2SEv1",minimumRollbackReleaseSha:$minimum,actualRunningImages:$images,preservedBootstrapImage:$bootstrap,checks:["target-recorded-success","storage-format-compatible","application-images-only","infrastructure-unchanged","no-migrations","smoke","bounded-e2e-reverification","digest-verification"]}')
    printf 'HOOK2STREAM_ROLLBACK_RECEIPT=%s\n' "$(printf '%s' "$rollback_receipt" | base64 | tr -d '\n')"
    ;;
  *) fail "operation is not allowed" ;;
esac
