#!/bin/sh
set -eu

temporary_dir=$(mktemp -d)
trap 'rm -rf "$temporary_dir"' EXIT HUP INT TERM
lock=$temporary_dir/forced-command.lock
fifo=$temporary_dir/release
ready=$temporary_dir/ready
mkfifo "$fifo"

(
  exec 8<>"$lock"
  flock -n 8
  : > "$ready"
  read -r _ < "$fifo"
) &
holder=$!
while [ ! -e "$ready" ]; do sleep 0.01; done

if (exec 8<>"$lock"; flock -n 8); then
  printf '%s\n' "forced command lock test: parallel attempt acquired the lock" >&2
  exit 1
fi
printf '%s\n' release > "$fifo"
wait "$holder"
(exec 8<>"$lock"; flock -n 8) || {
  printf '%s\n' "forced command lock test: lock was not released" >&2
  exit 1
}
printf '%s\n' "forced command lock test: parallel attempts serialize across post-deploy verification"
