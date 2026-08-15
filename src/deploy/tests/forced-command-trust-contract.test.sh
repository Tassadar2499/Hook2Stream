#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
. "$deployment_dir/scripts/lib/forced-command-trust.sh"

fail() {
    printf '%s\n' "forced-command trust contract test: $*" >&2
    exit 1
}

scratch=$(mktemp -d)
cleanup() { rm -rf "$scratch"; }
trap cleanup EXIT HUP INT TERM
owner_group=$(id -u):$(id -g)

mkdir "$scratch/private"
chmod 0700 "$scratch/private"
hook2stream_trusted_directory "$scratch/private" "$owner_group" 700 \
    || fail "a private directory with exact ownership was rejected"
chmod 0770 "$scratch/private"
if hook2stream_trusted_directory "$scratch/private" "$owner_group" 700; then
    fail "a group-writable trusted directory was accepted"
fi
chmod 0700 "$scratch/private"
ln -s "$scratch/private" "$scratch/linked-directory"
if hook2stream_trusted_directory "$scratch/linked-directory" "$owner_group" 700; then
    fail "a symlink trusted directory was accepted"
fi

printf '%s\n' marker > "$scratch/marker"
chmod 0600 "$scratch/marker"
hook2stream_trusted_file "$scratch/marker" "$owner_group" 600 \
    || fail "a private file with exact ownership was rejected"
chmod 0660 "$scratch/marker"
if hook2stream_trusted_file "$scratch/marker" "$owner_group" 600; then
    fail "a group-writable trusted file was accepted"
fi
chmod 0600 "$scratch/marker"
if hook2stream_trusted_file "$scratch/marker" 424242:424242 600; then
    fail "a file owned by the wrong identity was accepted"
fi
ln -s "$scratch/marker" "$scratch/linked-file"
if hook2stream_trusted_file "$scratch/linked-file" "$owner_group" 600; then
    fail "a symlink trusted file was accepted"
fi

app_wrapper=$deployment_dir/scripts/deploy-forced-command.sh
app_launcher=$deployment_dir/scripts/deploy-forced-launcher.sh
storage_launcher=$deployment_dir/storage/scripts/storage-deploy-launcher.sh
candidate_validator=$deployment_dir/scripts/validate-candidate.sh

for required in \
    'hook2stream_trusted_directory "$HOOK2STREAM_RELEASES_DIR" 0:0 700' \
    'hook2stream_trusted_file "$HOOK2STREAM_ENV_FILE" 0:0 600' \
    'hook2stream_trusted_file "$release_dir/.deploy-bundle.sha256" 0:0 600' \
    'hook2stream_trusted_file "$HOOK2STREAM_STAGING_SIGNERS" 0:0 600'; do
    grep -F "$required" "$app_wrapper" >/dev/null \
        || fail "app wrapper omitted trust boundary: $required"
done
grep -F 'stat -c '\''%u:%g:%a'\'' "$signers"' "$candidate_validator" >/dev/null \
    || fail "candidate validator does not independently protect allowed signers"
for launcher in "$app_launcher" "$storage_launcher"; do
    grep -F "stat -c '%u:%g:%a'" "$launcher" >/dev/null \
        || fail "$launcher does not validate exact root owner, group, and mode"
    grep -F 'trusted_program' "$launcher" >/dev/null \
        || fail "$launcher does not validate its installed gate program set"
done
for app_gate in \
    'deploy-forced-command.sh' \
    'validate-candidate.sh' \
    'lib/forced-command-trust.sh'; do
    grep -F "$app_gate" "$app_launcher" >/dev/null \
        || fail "app launcher omitted installed gate component $app_gate"
done
for storage_gate in \
    'storage-forced-command.sh' \
    'validate-candidate.sh' \
    'validate-production-approval.sh' \
    'lib/storage-common.sh'; do
    grep -F "$storage_gate" "$storage_launcher" >/dev/null \
        || fail "storage launcher omitted installed gate component $storage_gate"
done

printf '%s\n' \
    "forced-command trust contract test: writable, wrong-owner, and symlink paths are rejected"
