#!/bin/sh
set -eu

config=/etc/hook2stream/deploy.conf
wrapper=/usr/local/libexec/hook2stream/deploy-forced-command.sh
[ "$(id -u)" -eq 0 ] || { printf '%s\n' "deploy launcher: root is required" >&2; exit 1; }
[ -f "$config" ] && [ ! -L "$config" ] && [ "$(stat -c '%u:%a' "$config")" = 0:600 ] \
    || { printf '%s\n' "deploy launcher: $config must be root-owned mode 0600" >&2; exit 1; }
[ -x "$wrapper" ] && [ ! -L "$wrapper" ] \
    || { printf '%s\n' "deploy launcher: wrapper installation is invalid" >&2; exit 1; }
set -a
. "$config"
set +a
exec "$wrapper"
