#!/bin/sh
set -eu
set -f

config=/etc/hook2stream-storage/deploy.conf
wrapper=/usr/local/libexec/hook2stream-storage/storage-forced-command.sh
wrapper_dir=/usr/local/libexec/hook2stream-storage
[ "$(id -u)" -eq 0 ] || { printf '%s\n' "storage deploy launcher: root is required" >&2; exit 1; }
[ -f "$config" ] && [ ! -L "$config" ] && [ "$(stat -c '%u:%g:%a' "$config")" = 0:0:600 ] \
    || { printf '%s\n' "storage deploy launcher: config must be root:root mode 0600" >&2; exit 1; }
for trusted_parent in /usr/local /usr/local/libexec; do
    [ -d "$trusted_parent" ] && [ ! -L "$trusted_parent" ] \
        && [ "$(stat -c '%u:%g:%a' "$trusted_parent")" = 0:0:755 ] \
        || { printf '%s\n' "storage deploy launcher: untrusted parent $trusted_parent" >&2; exit 1; }
done
[ -d "$wrapper_dir" ] && [ ! -L "$wrapper_dir" ] \
    && [ "$(stat -c '%u:%g:%a' "$wrapper_dir")" = 0:0:755 ] \
    || { printf '%s\n' "storage deploy launcher: wrapper directory must be root:root mode 0755" >&2; exit 1; }
[ -d "$wrapper_dir/lib" ] && [ ! -L "$wrapper_dir/lib" ] \
    && [ "$(stat -c '%u:%g:%a' "$wrapper_dir/lib")" = 0:0:755 ] \
    || { printf '%s\n' "storage deploy launcher: wrapper library directory must be root:root mode 0755" >&2; exit 1; }
for trusted_program in \
    "$wrapper" \
    "$wrapper_dir/validate-candidate.sh" \
    "$wrapper_dir/validate-production-approval.sh" \
    "$wrapper_dir/lib/storage-common.sh"; do
    [ -f "$trusted_program" ] && [ ! -L "$trusted_program" ] \
        && [ "$(stat -c '%u:%g:%a' "$trusted_program")" = 0:0:555 ] \
        || { printf '%s\n' "storage deploy launcher: untrusted program $trusted_program" >&2; exit 1; }
done
set -a
. "$config"
set +a
exec "$wrapper"
