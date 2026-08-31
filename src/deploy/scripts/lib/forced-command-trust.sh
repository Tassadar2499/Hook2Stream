#!/bin/sh

hook2stream_no_extended_acl() {
    [ "$#" -eq 1 ] || return 1
    command -v getfacl >/dev/null 2>&1 || return 1
    LC_ALL=C getfacl -cp -- "$1" 2>/dev/null | awk '
      /^$/ { next }
      /^user::[rwx-][rwx-][rwx-]$/ { users++; next }
      /^group::[rwx-][rwx-][rwx-]$/ { groups++; next }
      /^other::[rwx-][rwx-][rwx-]$/ { others++; next }
      { invalid = 1 }
      END { exit (users == 1 && groups == 1 && others == 1 && !invalid) ? 0 : 1 }
    '
}

hook2stream_trusted_directory() {
    [ "$#" -eq 3 ] || return 1
    trusted_path=$1
    trusted_owner_group=$2
    trusted_mode=$3
    [ -d "$trusted_path" ] && [ ! -L "$trusted_path" ] \
        && [ "$(stat -c '%u:%g:%a' "$trusted_path")" = \
            "$trusted_owner_group:$trusted_mode" ] \
        && hook2stream_no_extended_acl "$trusted_path"
}

hook2stream_trusted_file() {
    [ "$#" -eq 3 ] || return 1
    trusted_path=$1
    trusted_owner_group=$2
    trusted_mode=$3
    [ -f "$trusted_path" ] && [ ! -L "$trusted_path" ] \
        && [ "$(stat -c '%u:%g:%a' "$trusted_path")" = \
            "$trusted_owner_group:$trusted_mode" ] \
        && hook2stream_no_extended_acl "$trusted_path"
}

hook2stream_validate_ghcr_pull_auth() {
    [ "$#" -eq 4 ] || return 1
    hook2stream_registry_dir=$1
    hook2stream_registry_username=$2
    hook2stream_registry_auth_sha256=$3
    hook2stream_registry_owner_group=$4
    printf '%s\n' "$hook2stream_registry_username" \
        | grep -Eq '^[A-Za-z0-9]([A-Za-z0-9-]{0,37}[A-Za-z0-9])?$' \
        || return 1
    printf '%s\n' "$hook2stream_registry_auth_sha256" \
        | grep -Eq '^[0-9a-f]{64}$' || return 1
    hook2stream_trusted_directory \
        "$hook2stream_registry_dir" "$hook2stream_registry_owner_group" 700 \
        || return 1
    hook2stream_registry_config=$hook2stream_registry_dir/config.json
    hook2stream_trusted_file \
        "$hook2stream_registry_config" "$hook2stream_registry_owner_group" 600 \
        || return 1
    hook2stream_registry_entries=$(printf '%s\n' config.json identity.attestation)
    [ "$(find "$hook2stream_registry_dir" -mindepth 1 -maxdepth 1 -printf '%f\n' \
        | LC_ALL=C sort)" = "$hook2stream_registry_entries" ] || return 1
    jq -e --arg username "$hook2stream_registry_username" '
      (keys | sort) == ["auths"] and
      (.auths | type == "object" and (keys | sort) == ["ghcr.io"]) and
      (.auths["ghcr.io"] | type == "object" and (keys | sort) == ["auth"]) and
      (.auths["ghcr.io"].auth | type == "string" and length > 0) and
      (.auths["ghcr.io"].auth as $encoded |
       ($encoded | test("^[A-Za-z0-9+/]+={0,2}$")) and
       (($encoded | @base64d) as $credential |
        (($credential | @base64) == $encoded) and
        ($credential | startswith($username + ":")) and
        ($credential | length) > (($username | length) + 1) and
        (($credential | split(":")) | length) == 2 and
        ($credential | test("[\r\n\u0000]") | not))
       )
    ' "$hook2stream_registry_config" >/dev/null 2>&1 || return 1
    hook2stream_registry_actual_sha256=$(jq -jr '.auths["ghcr.io"].auth' \
        "$hook2stream_registry_config" | sha256sum | awk '{ print $1 }') \
        || return 1
    [ "$hook2stream_registry_actual_sha256" = \
        "$hook2stream_registry_auth_sha256" ]
}

hook2stream_validate_ghcr_identity_attestation() {
    [ "$#" -eq 6 ] || return 1
    hook2stream_identity_registry_dir=$1
    hook2stream_identity_environment=$2
    hook2stream_identity_username=$3
    hook2stream_identity_id=$4
    hook2stream_identity_sha256=$5
    hook2stream_identity_owner_group=$6
    case "$hook2stream_identity_environment" in staging|production) ;; *) return 1 ;; esac
    printf '%s\n' "$hook2stream_identity_username" \
        | grep -Eq '^[A-Za-z0-9]([A-Za-z0-9-]{0,37}[A-Za-z0-9])?$' \
        || return 1
    printf '%s\n' "$hook2stream_identity_id" \
        | grep -Eq "^hook2stream-${hook2stream_identity_environment}-[0-9a-f]{32}$" \
        || return 1
    printf '%s\n' "$hook2stream_identity_sha256" \
        | grep -Eq '^[0-9a-f]{64}$' || return 1
    hook2stream_identity_file=$hook2stream_identity_registry_dir/identity.attestation
    hook2stream_trusted_file \
        "$hook2stream_identity_file" "$hook2stream_identity_owner_group" 600 \
        || return 1
    hook2stream_expected_attestation=$(printf '%s\n' \
        'schema=hook2stream-ghcr-pull-identity-v1' \
        "environment=$hook2stream_identity_environment" \
        "username=$hook2stream_identity_username" \
        "credential_identity=$hook2stream_identity_id" \
        'operator_attests_read_packages_only=true' \
        'operator_attests_environment_exclusive=true' \
        'scope_verification=provider-unavailable')
    [ "$(cat "$hook2stream_identity_file")" = "$hook2stream_expected_attestation" ] \
        || return 1
    hook2stream_identity_actual_sha256=$(sha256sum "$hook2stream_identity_file" \
        | awk '{ print $1 }') || return 1
    [ "$hook2stream_identity_actual_sha256" = "$hook2stream_identity_sha256" ]
}

hook2stream_remove_stale_ghcr_auth_temporaries() {
    [ "$#" -eq 2 ] || return 1
    hook2stream_stale_registry_dir=$1
    hook2stream_stale_owner_group=$2
    for hook2stream_stale_path in \
        "$hook2stream_stale_registry_dir"/.config.json.tmp.* \
        "$hook2stream_stale_registry_dir"/.identity.attestation.tmp.*; do
        [ -e "$hook2stream_stale_path" ] || [ -L "$hook2stream_stale_path" ] || continue
        hook2stream_trusted_file "$hook2stream_stale_path" \
            "$hook2stream_stale_owner_group" 600 || return 1
        rm -f -- "$hook2stream_stale_path" || return 1
    done
}

hook2stream_validate_rollback_capability() {
    [ "$#" -eq 4 ] || return 1
    hook2stream_capability_file=$1
    hook2stream_capability_sha=$2
    hook2stream_capability_protocol=$3
    hook2stream_capability_owner_group=$4
    printf '%s\n' "$hook2stream_capability_sha" | grep -Eq '^[0-9a-f]{40}$' \
        || return 1
    [ "$hook2stream_capability_protocol" = hook2stream-application-rollback-v2 ] \
        || return 1
    hook2stream_trusted_file "$hook2stream_capability_file" \
        "$hook2stream_capability_owner_group" 600 || return 1
    jq -e --arg sha "$hook2stream_capability_sha" \
        --arg protocol "$hook2stream_capability_protocol" 'select(
      (keys | sort) == ["releaseSha","rollbackProtocol","schemaVersion","storageFormats"] and
      .schemaVersion == 2 and .releaseSha == $sha and
      .rollbackProtocol == $protocol and .storageFormats == ["H2SEv1"]
    )' "$hook2stream_capability_file" >/dev/null 2>&1
}

hook2stream_validate_exact_allowed_signer() {
    [ "$#" -eq 2 ] || return 1
    hook2stream_signers_path=$1
    hook2stream_signers_principal=$2
    [ -f "$hook2stream_signers_path" ] && [ ! -L "$hook2stream_signers_path" ] \
        || return 1
    hook2stream_no_extended_acl "$hook2stream_signers_path" || return 1
    awk -v expected_principal="$hook2stream_signers_principal" '
      /^[[:space:]]*($|#)/ { next }
      {
        records++
        if ($1 != expected_principal || $2 != "ssh-ed25519" || NF != 3) {
          invalid = 1
        }
      }
      END { exit (records == 1 && !invalid) ? 0 : 1 }
    ' "$hook2stream_signers_path"
}

hook2stream_allowed_signer_key_material() {
    [ "$#" -eq 2 ] || return 1
    hook2stream_key_signers_path=$1
    hook2stream_key_signers_principal=$2
    hook2stream_validate_exact_allowed_signer \
        "$hook2stream_key_signers_path" "$hook2stream_key_signers_principal" \
        || return 1
    awk '
      /^[[:space:]]*($|#)/ { next }
      { print $2 " " $3 }
    ' "$hook2stream_key_signers_path"
}

hook2stream_validate_distinct_allowed_signers() {
    [ "$#" -eq 4 ] || return 1
    hook2stream_first_signer_key=$(hook2stream_allowed_signer_key_material "$1" "$2") \
        || return 1
    hook2stream_second_signer_key=$(hook2stream_allowed_signer_key_material "$3" "$4") \
        || return 1
    [ "$hook2stream_first_signer_key" != "$hook2stream_second_signer_key" ]
}

hook2stream_validate_distinct_ed25519_fingerprints() {
    [ "$#" -eq 2 ] || return 1
    printf '%s\n' "$1" | grep -Eq '^SHA256:[A-Za-z0-9+/]{43}$' || return 1
    printf '%s\n' "$2" | grep -Eq '^SHA256:[A-Za-z0-9+/]{43}$' || return 1
    [ "$1" != "$2" ]
}

hook2stream_validate_exact_authorized_key() {
    [ "$#" -eq 3 ] || return 1
    hook2stream_authorized_keys_path=$1
    hook2stream_authorized_key_role=$2
    hook2stream_authorized_key_fingerprint=$3
    [ -f "$hook2stream_authorized_keys_path" ] \
        && [ ! -L "$hook2stream_authorized_keys_path" ] \
        || return 1
    hook2stream_no_extended_acl "$hook2stream_authorized_keys_path" || return 1
    case "$hook2stream_authorized_key_role" in operator|deploy) ;; *) return 1 ;; esac
    printf '%s\n' "$hook2stream_authorized_key_fingerprint" \
        | grep -Eq '^SHA256:[A-Za-z0-9+/]{43}$' || return 1
    awk -v role="$hook2stream_authorized_key_role" '
      /^[[:space:]]*($|#)/ { next }
      {
        records++
        if (role == "operator") {
          if ($1 != "ssh-ed25519" || (NF != 2 && NF != 3) ||
              (NF == 3 && $3 !~ /^[A-Za-z0-9_.@+-]+$/)) invalid = 1
          next
        }
        prefix = "restrict,command=\"/usr/bin/sudo -n /usr/local/sbin/hook2stream-deploy-launcher\" ssh-ed25519 "
        if (index($0, prefix) != 1) {
          invalid = 1
          next
        }
        key = substr($0, length(prefix) + 1)
        if (key == "" || key ~ /[[:space:]]/ || key !~ /^[A-Za-z0-9+\/=]+$/) invalid = 1
      }
      END { exit (records == 1 && !invalid) ? 0 : 1 }
    ' "$hook2stream_authorized_keys_path" || return 1
    hook2stream_authorized_key_details=$(ssh-keygen -lf \
        "$hook2stream_authorized_keys_path" -E sha256 2>/dev/null) || return 1
    [ "$(printf '%s\n' "$hook2stream_authorized_key_details" | awk 'NF { count++ } END { print count + 0 }')" -eq 1 ] \
        || return 1
    [ "$(printf '%s\n' "$hook2stream_authorized_key_details" | awk '{ print $1 }')" = 256 ] \
        && [ "$(printf '%s\n' "$hook2stream_authorized_key_details" | awk '{ print $2 }')" = \
            "$hook2stream_authorized_key_fingerprint" ] \
        && [ "$(printf '%s\n' "$hook2stream_authorized_key_details" | awk '{ print $NF }')" = '(ED25519)' ]
}

hook2stream_validate_exact_deploy_sudoers() {
    [ "$#" -eq 1 ] || return 1
    hook2stream_sudoers_path=$1
    [ -f "$hook2stream_sudoers_path" ] && [ ! -L "$hook2stream_sudoers_path" ] \
        || return 1
    hook2stream_no_extended_acl "$hook2stream_sudoers_path" || return 1
    hook2stream_expected_sudoers=$(printf '%s\n' \
        'Defaults:hook2stream-deploy env_keep += "SSH_ORIGINAL_COMMAND"' \
        'hook2stream-deploy ALL=(root) NOPASSWD: /usr/local/sbin/hook2stream-deploy-launcher')
    [ "$(cat "$hook2stream_sudoers_path")" = "$hook2stream_expected_sudoers" ]
}

hook2stream_validate_effective_deploy_sudoers() {
    [ "$#" -eq 1 ] || return 1
    hook2stream_effective_sudoers=$(printf '%s\n' "$1" | awk '
      continued { sub(/^[[:space:]]+/, ""); continued = 0 }
      sub(/[[:space:]]*\\$/, "") { printf "%s ", $0; continued = 1; next }
      { print }
      END { if (continued) exit 1 }
    ') || return 1
    [ "$hook2stream_effective_sudoers" = \
        'hook2stream-deploy ALL = (root) NOPASSWD: /usr/local/sbin/hook2stream-deploy-launcher' ]
}
