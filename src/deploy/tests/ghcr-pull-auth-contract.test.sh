#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
. "$deployment_dir/scripts/lib/forced-command-trust.sh"

fail_test() {
    printf '%s\n' "GHCR pull-auth contract: $*" >&2
    exit 1
}

scratch=$(mktemp -d)
cleanup() { rm -rf "$scratch"; }
trap cleanup EXIT HUP INT TERM
mkdir "$scratch/bin"
cat > "$scratch/bin/jq" <<'EOF'
#!/usr/bin/env node
const fs = require("fs");
const args = process.argv.slice(2);
const path = args.at(-1);
let parsed;
try { parsed = JSON.parse(fs.readFileSync(path, "utf8")); } catch { process.exit(1); }
const auth = parsed?.auths?.["ghcr.io"]?.auth;
if (args.includes("-jr")) {
  if (typeof auth !== "string") process.exit(1);
  process.stdout.write(auth);
  process.exit(0);
}
const usernameIndex = args.indexOf("--arg");
const username = usernameIndex >= 0 && args[usernameIndex + 1] === "username"
  ? args[usernameIndex + 2] : "";
const exact = parsed && Object.keys(parsed).length === 1 &&
  Object.keys(parsed.auths ?? {}).length === 1 &&
  Object.keys(parsed.auths?.["ghcr.io"] ?? {}).length === 1 &&
  typeof auth === "string" && /^[A-Za-z0-9+/]+={0,2}$/.test(auth);
let credential = "";
if (exact) {
  try {
    credential = Buffer.from(auth, "base64").toString("utf8");
    if (Buffer.from(credential, "utf8").toString("base64") !== auth) process.exit(1);
  } catch { process.exit(1); }
}
const valid = exact && credential.startsWith(`${username}:`) &&
  Buffer.from(credential, "utf8").toString("base64") === auth &&
  credential.length > username.length + 1 && credential.split(":").length === 2 &&
  !/[\r\n\0]/.test(credential);
process.exit(valid ? 0 : 1);
EOF
chmod 0755 "$scratch/bin/jq"
PATH=$scratch/bin:$PATH
export PATH
owner_group=$(id -u):$(id -g)
auth_dir=$scratch/registry-auth
username=hook2stream-staging-pull
environment=staging
credential_identity=hook2stream-staging-0123456789abcdef0123456789abcdef
token=fixture-read-packages-only
encoded_auth=$(printf '%s' "$username:$token" | base64 | tr -d '\n')
mkdir -m 0700 "$auth_dir"
write_config() {
    printf '%s\n' "{\"auths\":{\"ghcr.io\":{\"auth\":\"$encoded_auth\"}}}" \
        > "$auth_dir/config.json"
    chmod 0600 "$auth_dir/config.json"
}
write_identity() {
    printf '%s\n' \
        'schema=hook2stream-ghcr-pull-identity-v1' \
        "environment=$environment" \
        "username=$username" \
        "credential_identity=$credential_identity" \
        'operator_attests_read_packages_only=true' \
        'operator_attests_environment_exclusive=true' \
        'scope_verification=provider-unavailable' \
        > "$auth_dir/identity.attestation"
    chmod 0600 "$auth_dir/identity.attestation"
}
auth_sha256() {
    printf '%s' "$encoded_auth" | sha256sum | awk '{ print $1 }'
}
write_config
write_identity
identity_sha256=$(sha256sum "$auth_dir/identity.attestation" | awk '{ print $1 }')

hook2stream_validate_ghcr_pull_auth \
    "$auth_dir" "$username" "$(auth_sha256)" "$owner_group" \
    || fail_test "exact root-private single-host Docker auth was rejected"
hook2stream_validate_ghcr_identity_attestation \
    "$auth_dir" "$environment" "$username" "$credential_identity" \
    "$identity_sha256" "$owner_group" \
    || fail_test "exact environment credential identity attestation was rejected"

chmod 0750 "$auth_dir"
if hook2stream_validate_ghcr_pull_auth \
    "$auth_dir" "$username" "$(auth_sha256)" "$owner_group"; then
    fail_test "group-accessible auth directory was accepted"
fi
chmod 0700 "$auth_dir"
chmod 0640 "$auth_dir/config.json"
if hook2stream_validate_ghcr_pull_auth \
    "$auth_dir" "$username" "$(auth_sha256)" "$owner_group"; then
    fail_test "group-readable Docker config was accepted"
fi
chmod 0600 "$auth_dir/config.json"

printf '%s\n' unexpected > "$auth_dir/extra"
if hook2stream_validate_ghcr_pull_auth \
    "$auth_dir" "$username" "$(auth_sha256)" "$owner_group"; then
    fail_test "an extra registry-auth directory entry was accepted"
fi
rm "$auth_dir/extra"

cp "$auth_dir/config.json" "$scratch/config-target"
rm "$auth_dir/config.json"
ln -s "$scratch/config-target" "$auth_dir/config.json"
if hook2stream_validate_ghcr_pull_auth \
    "$auth_dir" "$username" "$(auth_sha256)" "$owner_group"; then
    fail_test "a symlinked Docker config was accepted"
fi
rm "$auth_dir/config.json"
write_config

for invalid_config in \
    "{\"auths\":{\"ghcr.io\":{\"auth\":\"$encoded_auth\"},\"docker.io\":{\"auth\":\"$encoded_auth\"}}}" \
    "{\"auths\":{\"ghcr.io\":{\"auth\":\"$encoded_auth\"}},\"credsStore\":\"pass\"}" \
    '{"auths":{"ghcr.io":{"auth":"not-base64!"}}}' \
    '{"auths":{}}'; do
    printf '%s\n' "$invalid_config" > "$auth_dir/config.json"
    if hook2stream_validate_ghcr_pull_auth \
        "$auth_dir" "$username" "$(auth_sha256)" "$owner_group"; then
        fail_test "malformed, multi-registry, or helper-based auth config was accepted"
    fi
done
write_config

if hook2stream_validate_ghcr_identity_attestation \
    "$auth_dir" production "$username" "$credential_identity" \
    "$identity_sha256" "$owner_group"; then
    fail_test "staging credential identity attestation was accepted for production"
fi
if hook2stream_validate_ghcr_identity_attestation \
    "$auth_dir" "$environment" "$username" \
    hook2stream-staging-ffffffffffffffffffffffffffffffff \
    "$identity_sha256" "$owner_group"; then
    fail_test "a different credential identity pin was accepted"
fi

# A killed rotation may leave only known, root-private unique temporaries. The
# next rotation removes those, but refuses symlinks or other untrusted debris.
stale_config=$auth_dir/.config.json.tmp.crash
stale_identity=$auth_dir/.identity.attestation.tmp.crash
: > "$stale_config"; : > "$stale_identity"
chmod 0600 "$stale_config" "$stale_identity"
hook2stream_remove_stale_ghcr_auth_temporaries "$auth_dir" "$owner_group" \
    || fail_test "safe interrupted-rotation temporaries were not recoverable"
[ ! -e "$stale_config" ] && [ ! -e "$stale_identity" ] \
    || fail_test "interrupted-rotation temporaries survived recovery"
ln -s "$auth_dir/config.json" "$stale_config"
if hook2stream_remove_stale_ghcr_auth_temporaries "$auth_dir" "$owner_group"; then
    fail_test "symlinked interrupted-rotation temporary was accepted"
fi
rm "$stale_config"

if hook2stream_validate_ghcr_pull_auth \
    "$auth_dir" wrong-user "$(auth_sha256)" "$owner_group"; then
    fail_test "credential for a different username was accepted"
fi
if hook2stream_validate_ghcr_pull_auth \
    "$auth_dir" "$username" aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa \
    "$owner_group"; then
    fail_test "credential differing from its pinned auth digest was accepted"
fi
if hook2stream_validate_ghcr_pull_auth \
    "$auth_dir" "$username" "$(auth_sha256)" 424242:424242; then
    fail_test "registry auth owned by the wrong identity was accepted"
fi

operator_script=$deployment_dir/../../deploy/providers/serversguru/configure-ghcr-pull-auth.sh
launcher=$deployment_dir/scripts/deploy-forced-launcher.sh
wrapper=$deployment_dir/scripts/deploy-forced-command.sh
common=$deployment_dir/scripts/lib/deployment-common.sh
deploy=$deployment_dir/scripts/deploy-release.sh
rollback=$deployment_dir/scripts/rollback-application.sh
host_validator=$deployment_dir/scripts/validate-host.sh
deploy_config=$deployment_dir/host/deploy.conf.example

[ -x "$operator_script" ] || fail_test "operator-only credential installer is missing"
for script in "$operator_script" "$launcher" "$wrapper" "$common" "$deploy" "$rollback"; do
    sh -n "$script" || fail_test "invalid shell syntax: $script"
done
grep -Fq -- '--password-stdin' "$operator_script" \
    || fail_test "operator login does not use password-stdin"
if grep -Eq -- '--password([=[:space:]])|GHCR_(TOKEN|PAT)=|echo .*token|printf .*token.*docker' \
    "$operator_script"; then
    fail_test "operator installer may expose the PAT through argv, environment, or output"
fi
grep -Fq '>/dev/null 2>&1' "$operator_script" \
    || fail_test "docker login diagnostics are not suppressed"
grep -Fq 'trust_library=/usr/local/libexec/hook2stream/lib/forced-command-trust.sh' \
    "$operator_script" \
    || fail_test "operator installer does not pin the installed trust helper"
for mutable_trust_source in \
    'repository_root=' \
    'src/deploy/scripts/lib/forced-command-trust.sh'; do
    if grep -Fq "$mutable_trust_source" "$operator_script"; then
        fail_test "operator installer sources mutable repository trust code"
    fi
done
grep -Fq 'stat -c '\''%u:%g:%a'\'' "$trust_library"' "$operator_script" \
    || fail_test "installed trust helper owner and mode are not verified"
grep -Fq 'no_extended_acl "$trust_library"' "$operator_script" \
    || fail_test "installed trust helper ACLs are not rejected"
grep -Fq '(($credential | @base64) == $encoded)' \
    "$deployment_dir/scripts/lib/forced-command-trust.sh" \
    || fail_test "Docker auth does not require canonical Base64 round-trip"
grep -Fq 'hook2stream_trusted_directory "$auth_dir" 0:0 700' "$operator_script" \
    || fail_test "operator installer can follow an unsafe registry-auth path"
grep -Fq '[ ! -L "$lock_file" ]' "$operator_script" \
    || fail_test "operator installer can follow a credential-rotation lock symlink"
grep -Fq 'unset CDPATH ENV BASH_ENV DOCKER_CONFIG' "$launcher" \
    || fail_test "launcher accepts an inherited Docker config path"
grep -Fxq 'DOCKER_CONFIG=/srv/hook2stream/registry-auth' "$deploy_config" \
    || fail_test "deploy config omits the encrypted canonical Docker config"
for gate in \
    'hook2stream_validate_ghcr_pull_auth' \
    'hook2stream_validate_ghcr_identity_attestation' \
    'HOOK2STREAM_GHCR_USERNAME' \
    'HOOK2STREAM_GHCR_AUTH_SHA256' \
    'HOOK2STREAM_GHCR_CREDENTIAL_IDENTITY' \
    'HOOK2STREAM_GHCR_IDENTITY_SHA256'; do
    grep -Fq "$gate" "$wrapper" || fail_test "forced wrapper omits GHCR gate: $gate"
    grep -Fq "$gate" "$host_validator" || fail_test "host validator omits GHCR gate: $gate"
done
grep -Fq 'scope_verification=provider-unavailable' "$operator_script" \
    && grep -Fq 'GitHub does not expose PAT scopes to this installer.' "$operator_script" \
    || fail_test "operator installer falsely implies that GitHub PAT scopes are technically verified"
grep -Fq 'hook2stream_remove_stale_ghcr_auth_temporaries "$auth_dir" 0:0' "$operator_script" \
    && grep -Fq 'mktemp "$auth_dir/.config.json.tmp.XXXXXX"' "$operator_script" \
    || fail_test "operator installer lacks unique-tempfile crash recovery"
grep -Fq 'deployment_validate_ghcr_pull_auth' "$deploy" \
    || fail_test "forward deploy does not validate GHCR auth before pull"
grep -Fq 'docker --config "$DOCKER_CONFIG" image pull' "$rollback" \
    || fail_test "rollback pull does not select the reviewed Docker config"
pull_line=$(grep -n 'docker --config "$DOCKER_CONFIG" image pull' "$rollback" | cut -d: -f1)
publish_line=$(grep -n 'mv -f "$active_environment_tmp" "$active_environment_file"' "$rollback" | cut -d: -f1)
[ -n "$pull_line" ] && [ -n "$publish_line" ] && [ "$pull_line" -lt "$publish_line" ] \
    || fail_test "rollback publishes its active environment before authenticated pulls finish"

printf '%s\n' "GHCR pull-auth contract: exact private config, pins, and pull-before-publish rollback passed"
