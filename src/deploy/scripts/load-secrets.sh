#!/bin/sh
set -eu

fail() {
    printf '%s\n' "hook2stream secret loader: $*" >&2
    exit 1
}

read_secret() {
    secret_path=$1
    [ -f "$secret_path" ] || fail "required secret file is missing: $secret_path"
    [ -r "$secret_path" ] || fail "required secret file is not readable: $secret_path"

    secret_value=$(sed -e 's/[[:space:]]*$//' "$secret_path")
    [ -n "$secret_value" ] || fail "required secret file is empty: $secret_path"
    printf '%s' "$secret_value"
}

export_secret() {
    secret_path=$1
    variable_name=$2
    secret_value=$(read_secret "$secret_path")
    export "$variable_name=$secret_value"
    unset secret_value
}

if [ -n "${DB_PASSWORD_FILE:-}" ]; then
    db_password=$(read_secret "$DB_PASSWORD_FILE")
    escaped_db_password=$(printf '%s' "$db_password" | sed 's/"/""/g')
    : "${DB_HOST:?DB_HOST is required when DB_PASSWORD_FILE is set}"
    : "${POSTGRES_DB:=hook2stream}"
    : "${POSTGRES_USER:=hook2stream}"
    : "${DB_PORT:=5432}"
    : "${DB_SSL_MODE:=Disable}"
    : "${DB_MAX_POOL_SIZE:=10}"
    : "${DB_CONNECT_TIMEOUT_SECONDS:=15}"
    : "${DB_COMMAND_TIMEOUT_SECONDS:=180}"
    : "${OTEL_SERVICE_NAME:=hook2stream}"

    export ConnectionStrings__hook2stream="Host=${DB_HOST};Port=${DB_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=\"${escaped_db_password}\";SSL Mode=${DB_SSL_MODE};Pooling=true;Minimum Pool Size=0;Maximum Pool Size=${DB_MAX_POOL_SIZE};Timeout=${DB_CONNECT_TIMEOUT_SECONDS};Command Timeout=${DB_COMMAND_TIMEOUT_SECONDS};Keepalive=30;Include Error Detail=false;Application Name=${OTEL_SERVICE_NAME}"
    unset db_password escaped_db_password
fi

if [ -n "${STORAGE_ACCESS_KEY_FILE:-}" ]; then
    export_secret "$STORAGE_ACCESS_KEY_FILE" Storage__AccessKey
fi

if [ -n "${STORAGE_SECRET_FILE:-}" ]; then
    export_secret "$STORAGE_SECRET_FILE" Storage__SecretKey
fi

if [ -n "${GOOGLE_CLIENT_SECRET_FILE:-}" ]; then
    export_secret "$GOOGLE_CLIENT_SECRET_FILE" Google__ClientSecret
fi

if [ -n "${STRIPE_SECRET_KEY_FILE:-}" ]; then
    export_secret "$STRIPE_SECRET_KEY_FILE" Stripe__SecretKey
fi

if [ -n "${STRIPE_WEBHOOK_SECRET_FILE:-}" ]; then
    export_secret "$STRIPE_WEBHOOK_SECRET_FILE" Stripe__WebhookSecret
fi

if [ -n "${OPENROUTER_API_KEY_FILE:-}" ]; then
    export_secret "$OPENROUTER_API_KEY_FILE" OpenRouter__ApiKey
fi

if [ -n "${BACKUP_S3_ACCESS_KEY_FILE:-}" ]; then
    export_secret "$BACKUP_S3_ACCESS_KEY_FILE" BACKUP_S3_ACCESS_KEY
fi

[ "$#" -gt 0 ] || fail "no application command was supplied"
exec "$@"
