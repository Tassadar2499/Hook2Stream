#!/bin/sh
set -eu

marker=${BACKUP_SUCCESS_MARKER:-/tmp/last-successful-backup}
max_age=${BACKUP_MAX_AGE_SECONDS:-7200}
: "${BACKUP_AGE_RECIPIENT_FILE:?BACKUP_AGE_RECIPIENT_FILE is required}"

[ -s "$marker" ] || exit 1
[ -r "$BACKUP_AGE_RECIPIENT_FILE" ] || exit 1
case "$max_age" in
    *[!0-9]*|'') exit 1 ;;
esac

last_success=$(sed -n '1p' "$marker")
last_key_id=$(sed -n '2p' "$marker")
current_recipient=$(sed -e 's/[[:space:]]*$//' "$BACKUP_AGE_RECIPIENT_FILE")
current_key_id=$(printf '%s' "$current_recipient" | sha256sum | cut -c1-16)
case "$last_success" in
    *[!0-9]*|'') exit 1 ;;
esac
[ -n "$last_key_id" ] && [ "$last_key_id" = "$current_key_id" ] || exit 1

now=$(date -u +%s)
age=$((now - last_success))
[ "$age" -ge 0 ] && [ "$age" -le "$max_age" ]
