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
