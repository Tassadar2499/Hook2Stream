#!/bin/sh
set -eu

fail() {
    printf '%s\n' "pgbouncer entrypoint: $*" >&2
    exit 1
}

: "${POSTGRES_HOST:=postgres}"
: "${POSTGRES_PORT:=5432}"
: "${POSTGRES_DB:=hook2stream}"
: "${POSTGRES_USER:=hook2stream}"
: "${POSTGRES_PASSWORD_FILE:?POSTGRES_PASSWORD_FILE is required}"
: "${PGBOUNCER_CONFIG:=/etc/pgbouncer/pgbouncer.ini}"

[ -r "$POSTGRES_PASSWORD_FILE" ] || fail "PostgreSQL password secret is not readable"
postgres_password=$(sed -e 's/[[:space:]]*$//' "$POSTGRES_PASSWORD_FILE")
[ -n "$postgres_password" ] || fail "PostgreSQL password secret is empty"

case "$POSTGRES_USER" in
    *[!A-Za-z0-9_]*|'') fail "POSTGRES_USER contains unsupported characters" ;;
esac

escaped_password=$(printf '%s' "$postgres_password" | sed 's/"/""/g')
umask 077
printf '"%s" "%s"\n' "$POSTGRES_USER" "$escaped_password" > /tmp/userlist.txt
unset postgres_password escaped_password

export POSTGRES_HOST POSTGRES_PORT POSTGRES_DB
exec pgbouncer "$PGBOUNCER_CONFIG"
