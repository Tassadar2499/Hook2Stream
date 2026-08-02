#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
password_script=${deployment_dir}/scripts/postgres-set-password.sh
temporary_dir=$(mktemp -d)

cleanup() {
    rm -rf "$temporary_dir"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

fail() {
    printf '%s\n' "postgres password rotation test: $*" >&2
    exit 1
}

stub_bin=${temporary_dir}/bin
mkdir -p "$stub_bin"
cat > "${stub_bin}/psql" <<'EOF'
#!/bin/sh
set -eu
printf '%s\n' "$@" > "${TEST_STATE_DIR}/psql-arguments"
cat > "${TEST_STATE_DIR}/sql"
EOF
chmod 0700 "${stub_bin}/psql"

password_file=${temporary_dir}/password
printf "%s\n" "p'ass\\word" > "$password_file"
PATH="${stub_bin}:${PATH}" \
TEST_STATE_DIR=$temporary_dir \
POSTGRES_USER='role"name' \
POSTGRES_DB=hook2stream \
    sh "$password_script" < "$password_file" \
    >"${temporary_dir}/stdout" 2>"${temporary_dir}/stderr"

expected_sql=${temporary_dir}/expected-sql
printf "%s\n" "ALTER ROLE \"role\"\"name\" WITH PASSWORD 'p''ass\\word';" \
    > "$expected_sql"
cmp -s "$expected_sql" "${temporary_dir}/sql" \
    || fail "password or role identifier was not quoted as one SQL value"
[ ! -s "${temporary_dir}/stdout" ] && [ ! -s "${temporary_dir}/stderr" ] \
    || fail "successful rotation exposed unexpected output"
grep -Fx -- '--set' "${temporary_dir}/psql-arguments" >/dev/null \
    || fail "psql was not configured to stop on SQL errors"
grep -Fx -- 'ON_ERROR_STOP=1' "${temporary_dir}/psql-arguments" >/dev/null \
    || fail "psql ON_ERROR_STOP was not enabled"

: > "$password_file"
if PATH="${stub_bin}:${PATH}" \
    TEST_STATE_DIR=$temporary_dir \
    POSTGRES_USER=hook2stream \
    POSTGRES_DB=hook2stream \
    sh "$password_script" < "$password_file" \
    >"${temporary_dir}/empty-output" 2>&1; then
    fail "empty password was accepted"
fi

printf '%s\n%s\n' first second > "$password_file"
if PATH="${stub_bin}:${PATH}" \
    TEST_STATE_DIR=$temporary_dir \
    POSTGRES_USER=hook2stream \
    POSTGRES_DB=hook2stream \
    sh "$password_script" < "$password_file" \
    >"${temporary_dir}/multiline-output" 2>&1; then
    fail "multi-line password was accepted"
fi

printf '%s\n%s' first second > "$password_file"
if PATH="${stub_bin}:${PATH}" \
    TEST_STATE_DIR=$temporary_dir \
    POSTGRES_USER=hook2stream \
    POSTGRES_DB=hook2stream \
    sh "$password_script" < "$password_file" \
    >"${temporary_dir}/unterminated-multiline-output" 2>&1; then
    fail "multi-line password without a final newline was accepted"
fi

printf '%s\n' "postgres password rotation test: SQL quoting is safe"
