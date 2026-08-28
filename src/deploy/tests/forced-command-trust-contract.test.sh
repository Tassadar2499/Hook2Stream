#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
. "$deployment_dir/scripts/lib/forced-command-trust.sh"

fail() {
    printf '%s\n' "forced-command trust contract test: $*" >&2
    exit 1
}

scratch=$(mktemp -d)
cleanup() { rm -rf "$scratch"; }
trap cleanup EXIT HUP INT TERM
owner_group=$(id -u):$(id -g)

mkdir "$scratch/private"
chmod 0700 "$scratch/private"
hook2stream_trusted_directory "$scratch/private" "$owner_group" 700 \
    || fail "a private directory with exact ownership was rejected"
chmod 0770 "$scratch/private"
if hook2stream_trusted_directory "$scratch/private" "$owner_group" 700; then
    fail "a group-writable trusted directory was accepted"
fi
chmod 0700 "$scratch/private"
ln -s "$scratch/private" "$scratch/linked-directory"
if hook2stream_trusted_directory "$scratch/linked-directory" "$owner_group" 700; then
    fail "a symlink trusted directory was accepted"
fi

printf '%s\n' marker > "$scratch/marker"
chmod 0600 "$scratch/marker"
hook2stream_trusted_file "$scratch/marker" "$owner_group" 600 \
    || fail "a private file with exact ownership was rejected"
chmod 0660 "$scratch/marker"
if hook2stream_trusted_file "$scratch/marker" "$owner_group" 600; then
    fail "a group-writable trusted file was accepted"
fi
chmod 0600 "$scratch/marker"
if hook2stream_trusted_file "$scratch/marker" 424242:424242 600; then
    fail "a file owned by the wrong identity was accepted"
fi
ln -s "$scratch/marker" "$scratch/linked-file"
if hook2stream_trusted_file "$scratch/linked-file" "$owner_group" 600; then
    fail "a symlink trusted file was accepted"
fi

release_sha=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
rollback_protocol=hook2stream-application-rollback-v2
capability=$scratch/release.capabilities.json
mkdir "$scratch/bin"
cat > "$scratch/bin/jq" <<'EOF'
#!/usr/bin/env node
const fs = require("fs");
const args = process.argv.slice(2);
const parsed = JSON.parse(fs.readFileSync(args.at(-1), "utf8"));
const values = new Map();
for (let i = 0; i < args.length - 2; i++) {
  if (args[i] === "--arg") values.set(args[i + 1], args[i + 2]);
}
const exactKeys = Object.keys(parsed).sort().join(",") ===
  "releaseSha,rollbackProtocol,schemaVersion,storageFormats";
const valid = exactKeys && parsed.schemaVersion === 2 &&
  parsed.releaseSha === values.get("sha") &&
  parsed.rollbackProtocol === values.get("protocol") &&
  Array.isArray(parsed.storageFormats) && parsed.storageFormats.length === 1 &&
  parsed.storageFormats[0] === "H2SEv1";
process.exit(valid ? 0 : 1);
EOF
chmod 0755 "$scratch/bin/jq"
PATH=$scratch/bin:$PATH
export PATH
printf '%s\n' \
    "{\"schemaVersion\":1,\"releaseSha\":\"$release_sha\",\"storageFormats\":[\"H2SEv1\"]}" \
    > "$capability"
chmod 0600 "$capability"
if hook2stream_validate_rollback_capability \
    "$capability" "$release_sha" "$rollback_protocol" "$owner_group"; then
    fail "an adversarial pre-v2 successful-release capability was accepted for rollback"
fi
printf '%s\n' \
    "{\"schemaVersion\":2,\"releaseSha\":\"$release_sha\",\"storageFormats\":[\"H2SEv1\"],\"rollbackProtocol\":\"$rollback_protocol\"}" \
    > "$capability"
hook2stream_validate_rollback_capability \
    "$capability" "$release_sha" "$rollback_protocol" "$owner_group" \
    || fail "the exact rollback protocol v2 capability was rejected"
if command -v setfacl >/dev/null 2>&1 && id nobody >/dev/null 2>&1; then
    printf '%s\n' acl-marker > "$scratch/acl-marker"
    chmod 0640 "$scratch/acl-marker"
    if setfacl -m u:nobody:r--,m::r-- "$scratch/acl-marker" 2>/dev/null; then
        [ "$(stat -c '%a' "$scratch/acl-marker")" = 640 ] \
            || fail "ACL fixture unexpectedly changed the visible mode"
        if hook2stream_trusted_file "$scratch/acl-marker" "$owner_group" 640; then
            fail "a trusted file with a named POSIX ACL was accepted"
        fi
    fi
fi

signers=$scratch/staging-allowed-signers
printf '%s\n' \
    '# exactly one release authority is permitted' \
    'hook2stream-staging ssh-ed25519 AAAATEST' > "$signers"
hook2stream_validate_exact_allowed_signer "$signers" hook2stream-staging \
    || fail "the exact staging ED25519 signer was rejected"
printf '%s\n' 'hook2stream-staging ssh-ed25519 AAAASTALE' >> "$signers"
if hook2stream_validate_exact_allowed_signer "$signers" hook2stream-staging; then
    fail "an extra stale staging signer was accepted"
fi
printf '%s\n' '* ssh-ed25519 AAAATEST' > "$signers"
if hook2stream_validate_exact_allowed_signer "$signers" hook2stream-staging; then
    fail "a wildcard staging signer principal was accepted"
fi
printf '%s\n' 'hook2stream-staging ssh-rsa AAAATEST' > "$signers"
if hook2stream_validate_exact_allowed_signer "$signers" hook2stream-staging; then
    fail "an RSA staging signer was accepted"
fi
printf '%s\n' 'cert-authority hook2stream-staging ssh-ed25519 AAAATEST' > "$signers"
if hook2stream_validate_exact_allowed_signer "$signers" hook2stream-staging; then
    fail "an allowed-signers record with extra fields was accepted"
fi
printf '%s\n' 'hook2stream-staging ssh-ed25519 AAAASTAGING' > "$signers"
first_fingerprint=SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
second_fingerprint=SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
hook2stream_validate_distinct_ed25519_fingerprints \
    "$first_fingerprint" "$second_fingerprint" \
    || fail "different ED25519 fingerprints were rejected"
if hook2stream_validate_distinct_ed25519_fingerprints \
    "$first_fingerprint" "$first_fingerprint"; then
    fail "one ED25519 key was accepted for two independent trust roles"
fi

ssh-keygen -q -t ed25519 -N '' -f "$scratch/operator-key"
operator_fingerprint=$(ssh-keygen -lf "$scratch/operator-key.pub" -E sha256 | awk '{print $2}')
cp "$scratch/operator-key.pub" "$scratch/operator-authorized-keys"
hook2stream_validate_exact_authorized_key \
    "$scratch/operator-authorized-keys" operator "$operator_fingerprint" \
    || fail "the exact operator ED25519 key was rejected"
cat "$scratch/operator-key.pub" >> "$scratch/operator-authorized-keys"
if hook2stream_validate_exact_authorized_key \
    "$scratch/operator-authorized-keys" operator "$operator_fingerprint"; then
    fail "an extra operator SSH key was accepted"
fi

deploy_key_blob=$(awk '{print $2}' "$scratch/operator-key.pub")
printf '%s %s\n' \
    'restrict,command="/usr/bin/sudo -n /usr/local/sbin/hook2stream-deploy-launcher" ssh-ed25519' \
    "$deploy_key_blob" > "$scratch/deploy-authorized-keys"
hook2stream_validate_exact_authorized_key \
    "$scratch/deploy-authorized-keys" deploy "$operator_fingerprint" \
    || fail "the exact restricted deploy ED25519 key was rejected"
printf '%s %s\n' 'ssh-ed25519' "$deploy_key_blob" > "$scratch/deploy-authorized-keys"
if hook2stream_validate_exact_authorized_key \
    "$scratch/deploy-authorized-keys" deploy "$operator_fingerprint"; then
    fail "an unrestricted deploy SSH key was accepted"
fi
printf '%s %s\n' \
    'restrict,no-port-forwarding,command="/usr/bin/sudo -n /usr/local/sbin/hook2stream-deploy-launcher" ssh-ed25519' \
    "$deploy_key_blob" > "$scratch/deploy-authorized-keys"
if hook2stream_validate_exact_authorized_key \
    "$scratch/deploy-authorized-keys" deploy "$operator_fingerprint"; then
    fail "unexpected deploy authorized_keys options were accepted"
fi
printf '%s %s\n' \
    'restrict,command="/usr/bin/sudo -n /usr/local/sbin/hook2stream-deploy-launcher" ssh-ed25519' \
    "$deploy_key_blob" > "$scratch/deploy-authorized-keys"
if hook2stream_validate_exact_authorized_key \
    "$scratch/deploy-authorized-keys" deploy 'SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'; then
    fail "a deploy SSH key with the wrong configured fingerprint was accepted"
fi

printf '%s\n' \
    'Defaults:hook2stream-deploy env_keep += "SSH_ORIGINAL_COMMAND"' \
    'hook2stream-deploy ALL=(root) NOPASSWD: /usr/local/sbin/hook2stream-deploy-launcher' \
    > "$scratch/hook2stream-deploy.sudoers"
hook2stream_validate_exact_deploy_sudoers "$scratch/hook2stream-deploy.sudoers" \
    || fail "the exact deploy sudoers rule was rejected"
printf '%s\n' 'hook2stream-deploy ALL=(ALL) NOPASSWD: ALL' \
    >> "$scratch/hook2stream-deploy.sudoers"
if hook2stream_validate_exact_deploy_sudoers "$scratch/hook2stream-deploy.sudoers"; then
    fail "an additional unrestricted deploy sudoers grant was accepted"
fi

effective_sudoers='hook2stream-deploy ALL = (root) NOPASSWD:\
    /usr/local/sbin/hook2stream-deploy-launcher'
hook2stream_validate_effective_deploy_sudoers "$effective_sudoers" \
    || fail "the exact effective deploy sudoers policy was rejected"
if hook2stream_validate_effective_deploy_sudoers "$effective_sudoers
hook2stream-deploy ALL = (ALL) NOPASSWD: ALL"; then
    fail "an additional effective deploy sudoers command was accepted"
fi
if hook2stream_validate_effective_deploy_sudoers \
    '%hook2stream-deploy ALL = (root) NOPASSWD: /usr/local/sbin/hook2stream-deploy-launcher'; then
    fail "an effective deploy sudoers group grant was accepted"
fi

app_wrapper=$deployment_dir/scripts/deploy-forced-command.sh
app_launcher=$deployment_dir/scripts/deploy-forced-launcher.sh
candidate_validator=$deployment_dir/scripts/validate-candidate.sh

for required in \
    'hook2stream_trusted_directory "$HOOK2STREAM_RELEASES_DIR" 0:0 700' \
    'hook2stream_trusted_file "$HOOK2STREAM_ENV_FILE" 0:0 600' \
    'hook2stream_trusted_file "$release_dir/.deploy-bundle.sha256" 0:0 600' \
    'hook2stream_trusted_file "$HOOK2STREAM_STAGING_SIGNERS" 0:0 600'; do
    grep -F "$required" "$app_wrapper" >/dev/null \
        || fail "app wrapper omitted trust boundary: $required"
done
if grep -Eq 'provider[_-](window|lifecycle|deadline)|HOOK2STREAM_PROVIDER' "$app_wrapper"; then
    fail "permanent Servers.Guru staging still depends on ephemeral provider-window state"
fi
for exact_host_access_gate in \
    'hook2stream_validate_exact_authorized_key' \
    'hook2stream_validate_exact_deploy_sudoers' \
    'hook2stream_validate_effective_deploy_sudoers' \
    'cvtsudoers -f sudoers -e -M -p' \
    '/etc/sudoers.d/hook2stream-deploy'; do
    grep -F "$exact_host_access_gate" "$deployment_dir/scripts/validate-host.sh" >/dev/null \
        || fail "host validator omitted exact SSH/sudo gate: $exact_host_access_gate"
done
grep -F 'operator and forced-command deploy accounts must use different ED25519 keys' \
    "$deployment_dir/scripts/validate-host.sh" >/dev/null \
    || fail "host validator permits operator and deploy to reuse one SSH key"
for fixed_path_gate in "$app_launcher" "$app_wrapper"; do
    grep -Fq 'PATH=/usr/sbin:/usr/bin:/sbin:/bin' "$fixed_path_gate" \
        || fail "$fixed_path_gate does not pin root command resolution"
    grep -Fq 'unset CDPATH ENV BASH_ENV' "$fixed_path_gate" \
        || fail "$fixed_path_gate does not clear shell startup path injection"
done
for sustained_gate in \
    '"$HOOK2STREAM_E2E_HOOK" staging "$current_env" "$commit" soak-60m' \
    'hook2stream_trusted_file "$current_candidate" 0:0 600' \
    'cmp -s "$release_env" "$current_env"' \
    'timeout --signal=TERM --kill-after=5s 3890s' \
    '2> "$soak_dir/hook.stderr"'; do
    grep -F "$sustained_gate" "$app_wrapper" >/dev/null \
        || fail "trusted sustained soak boundary is missing: $sustained_gate"
done
[ "$(grep -Fc "trap 'exit 130' HUP INT TERM" "$app_wrapper")" -eq 2 ] \
    || fail "deploy and sustained soak do not exit explicitly on HUP/INT/TERM"
if grep -Eq "trap .*EXIT HUP INT TERM" "$app_wrapper"; then
    fail "cleanup-only signal traps may resume deployment or soak after interruption"
fi
grep -F 'stat -c '\''%u:%g:%a'\'' "$signers"' "$candidate_validator" >/dev/null \
    || fail "candidate validator does not independently protect allowed signers"
grep -F 'hook2stream_validate_exact_allowed_signer "$signers" hook2stream-staging' \
    "$candidate_validator" >/dev/null \
    || fail "candidate validator does not enforce one exact staging ED25519 authority"
if grep -Eq 'provider[_-](window|lifecycle|destroy|teardown)|HOOK2STREAM_PROVIDER' \
    "$candidate_validator"; then
    fail "candidate validator still requires retired provider lifecycle evidence"
fi
for launcher in "$app_launcher"; do
    grep -F "stat -c '%u:%g:%a'" "$launcher" >/dev/null \
        || fail "$launcher does not validate exact root owner, group, and mode"
    grep -F 'trusted_program' "$launcher" >/dev/null \
        || fail "$launcher does not validate its installed gate program set"
done
for app_gate in \
    'deploy-forced-command.sh' \
    'rollback-application.sh' \
    'validate-candidate.sh' \
    'lib/forced-command-trust.sh'; do
    grep -F "$app_gate" "$app_launcher" >/dev/null \
        || fail "app launcher omitted installed gate component $app_gate"
done
printf '%s\n' \
    "forced-command trust contract test: writable, wrong-owner, and symlink paths are rejected"
