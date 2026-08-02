#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
temporary_dir=$(mktemp -d)

cleanup() {
    rm -rf "$temporary_dir"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

fail_test() {
    printf '%s\n' "release-state preflight test: $*" >&2
    exit 1
}

environment_file=${temporary_dir}/test.env
printf '%s\n' 'STORAGE_MODE=external' > "$environment_file"
deployment_program=release-state-preflight-test
. "$deployment_dir/scripts/lib/deployment-common.sh"

valid_state_dir=${temporary_dir}/valid-state
(
    release_state_dir=$valid_state_dir
    deployment_acquire_lock
    [ "$(stat -c '%u:%a' "$valid_state_dir")" = "$(id -u):700" ]
    [ -f "$valid_state_dir/deploy.lock" ]
) || fail_test "a new private state directory was not accepted"

wrong_mode_dir=${temporary_dir}/wrong-mode
mkdir "$wrong_mode_dir"
chmod 0755 "$wrong_mode_dir"
if (release_state_dir=$wrong_mode_dir; deployment_acquire_lock) >/dev/null 2>&1; then
    fail_test "a state directory with mode 0755 was accepted"
fi
[ "$(stat -c '%a' "$wrong_mode_dir")" = 755 ] \
    || fail_test "preflight silently repaired an unsafe state-directory mode"

symlink_target=${temporary_dir}/symlink-target
symlink_state=${temporary_dir}/symlink-state
mkdir "$symlink_target"
chmod 0755 "$symlink_target"
ln -s "$symlink_target" "$symlink_state"
if (release_state_dir=$symlink_state; deployment_acquire_lock) >/dev/null 2>&1; then
    fail_test "a symlink release-state directory was accepted"
fi
[ "$(stat -c '%a' "$symlink_target")" = 755 ] \
    || fail_test "preflight followed and chmodded a state-directory symlink"

real_parent=${temporary_dir}/real-parent
linked_parent=${temporary_dir}/linked-parent
mkdir "$real_parent"
chmod 0700 "$real_parent"
ln -s "$real_parent" "$linked_parent"
if (release_state_dir=$linked_parent/state; deployment_acquire_lock) >/dev/null 2>&1; then
    fail_test "a symlink parent component was accepted"
fi
[ ! -e "$real_parent/state" ] \
    || fail_test "preflight followed a symlink parent while creating state"

lock_state=${temporary_dir}/lock-state
lock_victim=${temporary_dir}/lock-victim
mkdir "$lock_state"
chmod 0700 "$lock_state"
printf '%s\n' keep-this-content > "$lock_victim"
ln -s "$lock_victim" "$lock_state/deploy.lock"
if (release_state_dir=$lock_state; deployment_acquire_lock) >/dev/null 2>&1; then
    fail_test "a symlink deployment lock was accepted"
fi
grep -qx 'keep-this-content' "$lock_victim" \
    || fail_test "preflight followed and truncated a deployment-lock symlink"

ownership_state=${temporary_dir}/ownership-state
ownership_stub_bin=${temporary_dir}/ownership-bin
mkdir "$ownership_state" "$ownership_stub_bin"
chmod 0700 "$ownership_state"
cat > "$ownership_stub_bin/id" <<'EOF'
#!/bin/sh
printf '%s\n' 0
EOF
cat > "$ownership_stub_bin/stat" <<'EOF'
#!/bin/sh
printf '%s\n' '424242:700'
EOF
chmod 0700 "$ownership_stub_bin/id" "$ownership_stub_bin/stat"
if (PATH=$ownership_stub_bin:$PATH; release_state_dir=$ownership_state; deployment_acquire_lock) \
    >/dev/null 2>&1; then
    fail_test "a non-root-owned state directory was accepted for a sudo/root run"
fi

unsafe_parent=${temporary_dir}/unsafe-privileged-parent
unsafe_state=${unsafe_parent}/state
unsafe_stub_bin=${temporary_dir}/unsafe-bin
mkdir "$unsafe_parent" "$unsafe_state" "$unsafe_stub_bin"
chmod 0777 "$unsafe_parent"
chmod 0700 "$unsafe_state"
cat > "$unsafe_stub_bin/id" <<'EOF'
#!/bin/sh
printf '%s\n' 0
EOF
cat > "$unsafe_stub_bin/stat" <<'EOF'
#!/bin/sh
set -eu
target=
for argument in "$@"; do target=$argument; done
case "$target" in
    "$UNSAFE_PRIVILEGED_PARENT") printf '%s\n' '0:777' ;;
    "$UNSAFE_PRIVILEGED_STATE") printf '%s\n' '0:700' ;;
    *) printf '%s\n' '0:755' ;;
esac
EOF
chmod 0700 "$unsafe_stub_bin/id" "$unsafe_stub_bin/stat"
if (PATH=$unsafe_stub_bin:$PATH \
    UNSAFE_PRIVILEGED_PARENT=$unsafe_parent \
    UNSAFE_PRIVILEGED_STATE=$unsafe_state \
    export PATH UNSAFE_PRIVILEGED_PARENT UNSAFE_PRIVILEGED_STATE
    release_state_dir=$unsafe_state
    deployment_acquire_lock) >/dev/null 2>&1; then
    fail_test "a privileged state path below a writable ancestor was accepted"
fi

printf '%s\n' \
    "release-state preflight test: path, ancestry, ownership, mode, and lock safety passed"
