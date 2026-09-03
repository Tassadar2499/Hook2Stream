#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
base_compose=$deployment_dir/compose.yaml
stripe_overlay=$deployment_dir/compose.billing-stripe.yaml
common=$deployment_dir/scripts/lib/deployment-common.sh
release=$deployment_dir/scripts/deploy-release.sh
fail() { printf '%s\n' "billing mode deployment contract: $*" >&2; exit 1; }

[ -f "$stripe_overlay" ] && [ ! -L "$stripe_overlay" ] \
    || fail "Stripe Compose overlay is missing or unsafe"
grep -Fq 'Stripe__Mode: Disabled' "$base_compose" \
    || fail "base Compose does not fail closed with Stripe disabled"
if grep -Eq 'STRIPE_(SECRET|WEBHOOK)|Stripe__PriceIds__|stripe_(secret_key|webhook_secret)' "$base_compose"; then
    fail "base Compose still references Stripe secrets or price identifiers"
fi
if grep -F 'Stripe__' "$base_compose" | grep -Fvx '      Stripe__Mode: Disabled' >/dev/null; then
    fail "base Compose retains Stripe configuration beyond the explicit disabled mode"
fi
for stripe_contract in \
    'Stripe__Mode: Stripe' \
    'STRIPE_SECRET_KEY_FILE: /run/secrets/stripe_secret_key' \
    'STRIPE_WEBHOOK_SECRET_FILE: /run/secrets/stripe_webhook_secret' \
    'Stripe__PriceIds__art_credits_5:' \
    'Stripe__PriceIds__mini_release:' \
    'Stripe__PriceIds__release_pack:' \
    'Stripe__PriceIds__clean_cover:' \
    'Stripe__PriceIds__active_artist:'; do
    grep -Fq "$stripe_contract" "$stripe_overlay" \
        || fail "Stripe overlay omits $stripe_contract"
done

grep -Fxq 'BILLING_MODE=stripe' "$deployment_dir/environments/staging.env.example" \
    || fail "staging does not select Stripe billing"
grep -Fxq 'BILLING_MODE=disabled' "$deployment_dir/environments/production.env.example" \
    || fail "production does not select disabled billing"
if grep -Eq '^STRIPE_PRICE_' "$deployment_dir/environments/production.env.example"; then
    fail "production template retains Stripe price identifiers"
fi
grep -Fq 'deployment_validate_environment_billing_mode' "$release" \
    || fail "forward deploy does not invoke the billing environment gate"
grep -Fq 'production must use BILLING_MODE=disabled' "$common" \
    || fail "shared environment gate does not enforce disabled production billing"
grep -Fq 'staging must use BILLING_MODE=stripe' "$common" \
    || fail "shared environment gate does not enforce Stripe staging billing"

scratch=$(mktemp -d)
cleanup() { rm -rf "$scratch"; }
trap cleanup EXIT HUP INT TERM
mkdir "$scratch/bin"
cat > "$scratch/bin/docker" <<'EOF'
#!/bin/sh
set -eu
printf '%s\n' "$@" > "${TEST_DOCKER_ARGS:?}"
EOF
chmod 0700 "$scratch/bin/docker"

deployment_program=billing-mode-deployment-contract
DOCKER_CONFIG=$scratch/docker-config
export DOCKER_CONFIG

assert_compose_selection() {
    selected_environment=$1
    selected_billing_mode=$2
    expected_overlay=$3
    environment_file=$scratch/$selected_environment.env
    cat > "$environment_file" <<EOF
DEPLOYMENT_ENVIRONMENT=$selected_environment
SECRET_PROVIDER=file
STORAGE_MODE=external
BILLING_MODE=$selected_billing_mode
EOF
    HOOK2STREAM_ENV_FILE=$environment_file
    export HOOK2STREAM_ENV_FILE
    # shellcheck disable=SC1090
    . "$common"
    TEST_DOCKER_ARGS=$scratch/$selected_environment.args
    export TEST_DOCKER_ARGS
    PATH="$scratch/bin:$PATH" compose config
    grep -Fxq "$deployment_dir/compose.yaml" "$TEST_DOCKER_ARGS" \
        || fail "$selected_environment omitted base Compose"
    case "$expected_overlay" in
        present)
            grep -Fxq "$stripe_overlay" "$TEST_DOCKER_ARGS" \
                || fail "$selected_environment omitted Stripe overlay"
            ;;
        absent)
            if grep -Fxq "$stripe_overlay" "$TEST_DOCKER_ARGS"; then
                fail "$selected_environment unexpectedly selected Stripe overlay"
            fi
            ;;
    esac
}

assert_compose_selection staging stripe present
assert_compose_selection production disabled absent

printf '%s\n' \
    "billing mode deployment contract: conditional Compose, secrets, identifiers, and environment mapping passed"
