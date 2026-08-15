#!/bin/sh
set -eu
set -f

fail() { printf '%s\n' "storage forced deploy: $*" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || fail "wrapper must run as root through the exact sudoers rule"
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/lib/storage-common.sh"

: "${STORAGE_ENV_FILE:?STORAGE_ENV_FILE is required}"
: "${STORAGE_RELEASES_DIR:?STORAGE_RELEASES_DIR is required}"
: "${STORAGE_STATE_DIR:?STORAGE_STATE_DIR is required}"
: "${STORAGE_REPOSITORY:?STORAGE_REPOSITORY is required}"
: "${STORAGE_MINIO_SECURITY_POLICY:?STORAGE_MINIO_SECURITY_POLICY is required}"
[ "$STORAGE_RELEASES_DIR" = /srv/hook2stream-storage/releases ] || fail "releases directory is not canonical"
[ "$STORAGE_STATE_DIR" = /srv/hook2stream-storage/release-state ] || fail "state directory is not canonical"
[ "$STORAGE_MINIO_SECURITY_POLICY" = /etc/hook2stream-storage/minio-security-policy.json ] \
    || fail "MinIO security policy path is not canonical"
[ -f "$STORAGE_MINIO_SECURITY_POLICY" ] && [ ! -L "$STORAGE_MINIO_SECURITY_POLICY" ] \
    && [ "$(stat -c '%u:%g:%a' "$STORAGE_MINIO_SECURITY_POLICY")" = 0:0:600 ] \
    || fail "MinIO security policy must be root:root mode 0600"
for directory in "$STORAGE_RELEASES_DIR" "$STORAGE_STATE_DIR"; do
    [ -d "$directory" ] && [ ! -L "$directory" ] && [ "$(stat -c '%u:%a' "$directory")" = 0:700 ] \
        || fail "$directory must be a root-owned non-symlink directory mode 0700"
done
[ -f "$STORAGE_ENV_FILE" ] && [ ! -L "$STORAGE_ENV_FILE" ] \
    && [ "$(stat -c '%u:%a' "$STORAGE_ENV_FILE")" = 0:600 ] \
    || fail "storage environment must be root-owned mode 0600"
storage_validate_strict_env "$STORAGE_ENV_FILE"
environment=$(storage_env_value "$STORAGE_ENV_FILE" DEPLOYMENT_ENVIRONMENT)
case "$environment" in staging|production) ;; *) fail "environment must be staging or production" ;; esac

lock_file=$STORAGE_STATE_DIR/forced-command.lock
if [ ! -e "$lock_file" ]; then (umask 077 && : > "$lock_file"); fi
[ -f "$lock_file" ] && [ ! -L "$lock_file" ] && [ "$(stat -c '%u:%a' "$lock_file")" = 0:600 ] \
    || fail "forced-command lock must be root-owned mode 0600"
exec 8<> "$lock_file"
flock -n 8 || fail "another storage deployment is already running"

old_ifs=$IFS
IFS=' '
set -- ${SSH_ORIGINAL_COMMAND:-}
IFS=$old_ifs
[ "$#" -eq 2 ] && [ "$1" = deploy-storage ] \
    || fail "only 'deploy-storage storage-candidate-SHA-RUN-ATTEMPT' is allowed"
artifact=$2
case "$artifact" in storage-candidate-[0-9a-f]*-[1-9]*-[1-9]*) ;; *) fail "candidate artifact name is invalid" ;; esac

incoming=$(mktemp -d "$STORAGE_STATE_DIR/incoming.XXXXXX")
cleanup() { rm -rf "$incoming"; }
trap cleanup EXIT HUP INT TERM
envelope=$incoming/envelope.tar
dd iflag=fullblock bs=1048576 count=129 of="$envelope" 2>/dev/null
[ "$(wc -c < "$envelope")" -le 134217728 ] || fail "deployment envelope exceeds 128 MiB"
tar -tf "$envelope" | while IFS= read -r member; do
    case "$member" in
        .|./|candidate|candidate/*|./candidate|./candidate/*|approval|approval/*|./approval|./approval/*) ;;
        *) fail "deployment envelope path is not allowed" ;;
    esac
    case "$member" in /*|../*|*/../*|*/..|*\\*) fail "deployment envelope path traversal detected" ;; esac
done
if tar -tvf "$envelope" | awk '$1 !~ /^[d-]/ {bad=1} END {exit bad ? 0 : 1}'; then
    fail "deployment envelope links and special files are forbidden"
fi
tar -tvf "$envelope" | awk '{total += $3} END {exit total <= 402653184 ? 0 : 1}' \
    || fail "expanded deployment envelope exceeds 384 MiB"
tar -xf "$envelope" --no-same-owner --no-same-permissions -C "$incoming"
[ -d "$incoming/candidate" ] || fail "deployment envelope lacks candidate/"

commit=$("$script_dir/validate-candidate.sh" "$incoming/candidate" "$artifact" "$STORAGE_REPOSITORY")
case "$commit" in *[!0-9a-f]*|'') fail "candidate validator returned an invalid commit" ;; esac
[ "${#commit}" -eq 40 ] || fail "candidate validator returned an invalid commit"
if [ "$environment" = production ]; then
    : "${STORAGE_STAGING_SIGNERS:?STORAGE_STAGING_SIGNERS is required for production}"
    "$script_dir/validate-production-approval.sh" \
        "$incoming/candidate" "$incoming/approval" "$STORAGE_STAGING_SIGNERS"
else
    [ ! -e "$incoming/approval" ] || fail "staging envelope must not contain production approval"
fi

bundle_sha=$(sha256sum "$incoming/candidate/storage-bundle.tar.gz" | awk '{print $1}')
release_dir=$STORAGE_RELEASES_DIR/$commit
if [ -e "$release_dir" ]; then
    [ -d "$release_dir" ] && [ ! -L "$release_dir" ] \
        && [ -f "$release_dir/.storage-bundle.sha256" ] && [ ! -L "$release_dir/.storage-bundle.sha256" ] \
        || fail "existing release path is unsafe"
    [ "$(cat "$release_dir/.storage-bundle.sha256")" = "$bundle_sha" ] \
        || fail "existing release has a conflicting storage bundle"
else
    release_tmp=$(mktemp -d "$STORAGE_RELEASES_DIR/.${commit}.XXXXXX")
    tar -xzf "$incoming/candidate/storage-bundle.tar.gz" \
        --no-same-owner --no-same-permissions -C "$release_tmp"
    [ -x "$release_tmp/storage/scripts/deploy-storage.sh" ] \
        && [ -f "$release_tmp/storage/storage-release.json" ] \
        || fail "storage release lacks its deploy entrypoint or compatibility manifest"
    printf '%s\n' "$bundle_sha" > "$release_tmp/.storage-bundle.sha256"
    chmod 0600 "$release_tmp/.storage-bundle.sha256"
    mv "$release_tmp" "$release_dir" || fail "could not atomically publish storage release"
fi

manifest=$release_dir/storage/storage-release.json
jq -e '
    type == "object" and
    (keys | sort) == (["schemaVersion","kind","protocolVersion","storageFormatVersion","objectFormat","minioRelease","minioSourceCommit"] | sort) and
    .schemaVersion == 1 and .kind == "hook2stream-storage-runtime" and
    .protocolVersion == 1 and .storageFormatVersion == 1 and
    .objectFormat == "H2SEv1" and
    (.minioRelease | type == "string" and
        test("^RELEASE\\.[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}-[0-9]{2}-[0-9]{2}Z$")) and
    (.minioSourceCommit | type == "string" and test("^[0-9a-f]{40}$"))
' "$manifest" >/dev/null || fail "release compatibility manifest is invalid"
candidate_protocol=$(jq -r .protocolVersion "$manifest")
candidate_storage_format=$(jq -r .storageFormatVersion "$manifest")
candidate_object_format=$(jq -r .objectFormat "$manifest")
candidate_minio_release=$(jq -r .minioRelease "$manifest")
candidate_minio_source_commit=$(jq -r .minioSourceCommit "$manifest")

# The approval policy is release-independent, root-owned host state. Never
# trust a candidate-bundled approval list: the operator must install the
# reviewed current policy before deployment. Empty, unknown, old, or future
# source pins fail closed before format-floor or Docker/MinIO mutation.
candidate_minio_security_sequence=$(storage_validate_minio_security_policy \
    "$STORAGE_MINIO_SECURITY_POLICY" "$candidate_minio_release" "$candidate_minio_source_commit")

format_marker=$STORAGE_STATE_DIR/storage-format-floor.json
data_dir=$(storage_env_value "$STORAGE_ENV_FILE" STORAGE_DATA_DIR)
last_successful_release_sha=
if [ -e "$format_marker" ]; then
    [ -f "$format_marker" ] && [ ! -L "$format_marker" ] \
        && [ "$(stat -c '%u:%a' "$format_marker")" = 0:600 ] \
        || fail "storage format floor is unsafe"
    jq -e --arg environment "$environment" '
        type == "object" and
        (keys | sort) == (["schemaVersion","kind","environment","minimumProtocolVersion","minimumStorageFormatVersion","minimumMinioSecuritySequence","objectFormat","minioRelease","minioSourceCommit","pendingReleaseSha","lastSuccessfulReleaseSha"] | sort) and
        .schemaVersion == 1 and .kind == "hook2stream-storage-format-floor" and
        .environment == $environment and
        (.minimumProtocolVersion | type == "number" and . >= 1 and floor == .) and
        (.minimumStorageFormatVersion | type == "number" and . >= 1 and floor == .) and
        (.minimumMinioSecuritySequence | type == "number" and . >= 1 and floor == .) and
        .objectFormat == "H2SEv1" and
        (.minioRelease | type == "string" and
            test("^RELEASE\\.[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}-[0-9]{2}-[0-9]{2}Z$")) and
        (.minioSourceCommit | type == "string" and test("^[0-9a-f]{40}$")) and
        (.pendingReleaseSha == null or (.pendingReleaseSha | type == "string" and test("^[0-9a-f]{40}$"))) and
        (.lastSuccessfulReleaseSha == null or (.lastSuccessfulReleaseSha | type == "string" and test("^[0-9a-f]{40}$")))
    ' "$format_marker" >/dev/null || fail "storage format floor schema is invalid"
    minimum_protocol=$(jq -r .minimumProtocolVersion "$format_marker")
    minimum_storage_format=$(jq -r .minimumStorageFormatVersion "$format_marker")
    minimum_minio_security_sequence=$(jq -r .minimumMinioSecuritySequence "$format_marker")
    floor_object_format=$(jq -r .objectFormat "$format_marker")
    floor_minio_release=$(jq -r .minioRelease "$format_marker")
    floor_minio_source_commit=$(jq -r .minioSourceCommit "$format_marker")
    last_successful_release_sha=$(jq -r '.lastSuccessfulReleaseSha // ""' "$format_marker")
    [ "$candidate_protocol" -ge "$minimum_protocol" ] || fail "storage protocol downgrade is forbidden"
    [ "$candidate_storage_format" -ge "$minimum_storage_format" ] || fail "MinIO on-disk format downgrade is forbidden"
    storage_validate_minio_security_transition \
        "$candidate_minio_security_sequence" "$candidate_minio_release" "$candidate_minio_source_commit" \
        "$minimum_minio_security_sequence" "$floor_minio_release" "$floor_minio_source_commit"
    [ "$candidate_object_format" = "$floor_object_format" ] || fail "object format change is forbidden"
else
    [ -d "$data_dir" ] && [ ! -L "$data_dir" ] || fail "storage data directory is unavailable"
    [ -z "$(find "$data_dir" -mindepth 1 -print -quit)" ] \
        || fail "non-empty storage without a format floor requires operator recovery"
fi

active_env=$STORAGE_STATE_DIR/candidate-$commit.env
release_keys=' STORAGE_RELEASE_VERSION MINIO_IMAGE MINIO_MC_IMAGE CADDY_IMAGE '
awk -F= -v keys="$release_keys" 'index(keys, " " $1 " ") == 0 {print}' "$STORAGE_ENV_FILE" > "$active_env.tmp"
cat "$incoming/candidate/storage-images.env" >> "$active_env.tmp"
chmod 0600 "$active_env.tmp"
mv -f "$active_env.tmp" "$active_env"

minimum_protocol=${minimum_protocol:-$candidate_protocol}
minimum_storage_format=${minimum_storage_format:-$candidate_storage_format}
minimum_minio_security_sequence=${minimum_minio_security_sequence:-$candidate_minio_security_sequence}
[ "$candidate_protocol" -le "$minimum_protocol" ] || minimum_protocol=$candidate_protocol
[ "$candidate_storage_format" -le "$minimum_storage_format" ] || minimum_storage_format=$candidate_storage_format
[ "$candidate_minio_security_sequence" -le "$minimum_minio_security_sequence" ] \
    || minimum_minio_security_sequence=$candidate_minio_security_sequence

# Persist the raised compatibility floor before the first Docker pull, start,
# or MinIO mutation. A failed format-changing attempt therefore leaves a
# forward-fix-only floor instead of making the old release eligible again.
storage_write_format_floor "$format_marker" "$environment" \
    "$minimum_protocol" "$minimum_storage_format" "$minimum_minio_security_sequence" \
    "$candidate_object_format" "$candidate_minio_release" "$candidate_minio_source_commit" \
    "$commit" "$last_successful_release_sha"

"$release_dir/storage/scripts/deploy-storage.sh" "$release_dir" "$active_env" >&2

# Clear the pending attempt only after the runtime, IAM, topology, lifecycle,
# protocol, and digest checks have all succeeded.
storage_write_format_floor "$format_marker" "$environment" \
    "$minimum_protocol" "$minimum_storage_format" "$minimum_minio_security_sequence" \
    "$candidate_object_format" "$candidate_minio_release" "$candidate_minio_source_commit" \
    "" "$commit"
install -m 0600 "$active_env" "$STORAGE_STATE_DIR/last-successful.env.tmp"
mv -f "$STORAGE_STATE_DIR/last-successful.env.tmp" "$STORAGE_STATE_DIR/last-successful.env"
ln -sfn "$release_dir" "$STORAGE_STATE_DIR/current.tmp"
mv -Tf "$STORAGE_STATE_DIR/current.tmp" "$STORAGE_STATE_DIR/current"

STORAGE_RELEASE_DIR=$release_dir
STORAGE_ACTIVE_ENV_FILE=$active_env
DEPLOYMENT_ENVIRONMENT=$environment
export STORAGE_RELEASE_DIR STORAGE_ACTIVE_ENV_FILE DEPLOYMENT_ENVIRONMENT
minio_container=$(storage_compose ps -q minio)
caddy_container=$(storage_compose ps -q caddy)
[ -n "$minio_container" ] && [ -n "$caddy_container" ] || fail "deployed containers are unavailable"
actual_minio=$(docker inspect --format '{{.Config.Image}}' "$minio_container")
actual_caddy=$(docker inspect --format '{{.Config.Image}}' "$caddy_container")
actual_mc=$(storage_env_value "$active_env" MINIO_MC_IMAGE)
[ "$actual_minio" = "$(storage_env_value "$active_env" MINIO_IMAGE)" ] || fail "MinIO digest changed after verification"
[ "$actual_caddy" = "$(storage_env_value "$active_env" CADDY_IMAGE)" ] || fail "Caddy digest changed after verification"
images_sha=$(sha256sum "$incoming/candidate/storage-images.env" | awk '{print $1}')
receipt_environment=storage-$environment
receipt=$(jq -cn \
    --arg environment "$receipt_environment" --arg artifact "$artifact" --arg commit "$commit" \
    --arg imagesSha "$images_sha" --arg bundleSha "$bundle_sha" \
    --arg minio "$actual_minio" --arg mc "$actual_mc" --arg caddy "$actual_caddy" \
    '{schemaVersion:1,kind:"hook2stream-storage-remote-deploy-result",environment:$environment,result:"success",candidateArtifact:$artifact,commitSha:$commit,storageImagesSha256:$imagesSha,storageBundleSha256:$bundleSha,actualImages:{MINIO_IMAGE:$minio,MINIO_MC_IMAGE:$mc,CADDY_IMAGE:$caddy},checks:["policy-verification","quota-verification","versioning-verification","lifecycle-verification","digest-verification"]}')
printf 'HOOK2STREAM_STORAGE_REMOTE_RECEIPT=%s\n' "$(printf '%s' "$receipt" | base64 | tr -d '\n')"
