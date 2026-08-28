#!/bin/sh
set -eu

marker=${MEDIA_JANITOR_SUCCESS_MARKER:-/tmp/last-successful-media-janitor}
max_age=${MEDIA_JANITOR_MAX_AGE_SECONDS:-93600}
[ -s "$marker" ] || exit 1
case "$max_age" in *[!0-9]*|'') exit 1 ;; esac
last_success=$(sed -n '1p' "$marker")
case "$last_success" in *[!0-9]*|'') exit 1 ;; esac
now=$(date -u +%s)
age=$((now - last_success))
[ "$age" -ge 0 ] && [ "$age" -le "$max_age" ]
