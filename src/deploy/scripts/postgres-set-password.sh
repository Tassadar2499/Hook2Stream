#!/bin/sh
set -eu

fail() {
    printf '%s\n' "postgres password update: $*" >&2
    exit 1
}

: "${POSTGRES_USER:?POSTGRES_USER is required}"
: "${POSTGRES_DB:?POSTGRES_DB is required}"

new_password=
IFS= read -r new_password || [ -n "$new_password" ]
[ -n "$new_password" ] || fail "new password is empty"
unexpected_line=
if IFS= read -r unexpected_line || [ -n "$unexpected_line" ]; then
    unset new_password unexpected_line
    fail "new password must contain exactly one line"
fi

escaped_password=$(printf '%s' "$new_password" | sed "s/'/''/g")
quoted_role=$(printf '%s' "$POSTGRES_USER" | sed 's/"/""/g')
unset new_password

printf "ALTER ROLE \"%s\" WITH PASSWORD '%s';\n" \
    "$quoted_role" "$escaped_password" \
    | psql --no-psqlrc --quiet --set ON_ERROR_STOP=1 \
        --username "$POSTGRES_USER" --dbname "$POSTGRES_DB"

unset escaped_password quoted_role
