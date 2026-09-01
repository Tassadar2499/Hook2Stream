#!/bin/sh
# Pure host-profile and policy helpers. This file is safe to source from tests.

hook2stream_host_profile() {
    hook2stream_profile_role=$1
    hook2stream_profile_environment=$2

    case "${hook2stream_profile_role}:${hook2stream_profile_environment}" in
        app:staging)
            hook2stream_profile_minimum_gib=48
            ;;
        app:production)
            hook2stream_profile_minimum_gib=64
            ;;
        *)
            return 1
            ;;
    esac

    hook2stream_profile_backing_file=/var/lib/hook2stream-data.luks
    hook2stream_profile_mapper=hook2stream-data
    hook2stream_profile_mount=/srv/hook2stream
}

hook2stream_required_secret_files() {
    hook2stream_secret_role=$1
    case "$hook2stream_secret_role" in
        app)
            printf '%s\n' \
                postgres_password \
                s3_runtime_access_key \
                s3_runtime_secret_key \
                google_client_secret \
                stripe_secret_key \
                stripe_webhook_secret \
                openrouter_api_key \
                media_keyring \
                invited_emails \
                backup_s3_access_key \
                backup_s3_secret_key \
                backup_age_recipient
            ;;
        *)
            return 1
            ;;
    esac
}

hook2stream_validate_backing_metadata() {
    hook2stream_backing_metadata=$1
    hook2stream_backing_minimum_gib=$2
    case "$hook2stream_backing_metadata" in
        *[!0-9:]*|*:*:*:*:*:*:*) return 1 ;;
    esac

    hook2stream_backing_uid=${hook2stream_backing_metadata%%:*}
    hook2stream_backing_remainder=${hook2stream_backing_metadata#*:}
    hook2stream_backing_gid=${hook2stream_backing_remainder%%:*}
    hook2stream_backing_remainder=${hook2stream_backing_remainder#*:}
    hook2stream_backing_mode=${hook2stream_backing_remainder%%:*}
    hook2stream_backing_remainder=${hook2stream_backing_remainder#*:}
    hook2stream_backing_size=${hook2stream_backing_remainder%%:*}
    hook2stream_backing_remainder=${hook2stream_backing_remainder#*:}
    hook2stream_backing_blocks=${hook2stream_backing_remainder%%:*}
    hook2stream_backing_block_size=${hook2stream_backing_remainder#*:}

    [ "$hook2stream_backing_uid:$hook2stream_backing_gid:$hook2stream_backing_mode" = 0:0:600 ] \
        || return 1
    hook2stream_backing_minimum_bytes=$((hook2stream_backing_minimum_gib * 1024 * 1024 * 1024))
    [ "$hook2stream_backing_size" -eq "$hook2stream_backing_minimum_bytes" ] \
        || return 1
    hook2stream_backing_allocated_bytes=$((hook2stream_backing_blocks * hook2stream_backing_block_size))
    [ "$hook2stream_backing_allocated_bytes" -ge "$hook2stream_backing_size" ]
}

hook2stream_validate_active_swap_record() {
    [ "$#" -eq 4 ] || return 1
    hook2stream_swap_record=$1
    hook2stream_swap_expected_path=$2
    hook2stream_swap_nominal_bytes=$3
    hook2stream_swap_page_bytes=$4

    case "$hook2stream_swap_nominal_bytes:$hook2stream_swap_page_bytes" in
        *[!0-9:]*|:*|*:|*:*:*) return 1 ;;
    esac
    [ "$hook2stream_swap_page_bytes" -gt 0 ] \
        && [ "$hook2stream_swap_nominal_bytes" -gt "$hook2stream_swap_page_bytes" ] \
        || return 1
    hook2stream_swap_minimum_bytes=$((
        hook2stream_swap_nominal_bytes - hook2stream_swap_page_bytes
    ))

    printf '%s\n' "$hook2stream_swap_record" | awk \
        -v expected_path="$hook2stream_swap_expected_path" \
        -v minimum_bytes="$hook2stream_swap_minimum_bytes" \
        -v nominal_bytes="$hook2stream_swap_nominal_bytes" '
        NF == 0 { next }
        {
            count++
            if (NF != 2 || $1 != expected_path || $2 !~ /^[0-9]+$/ ||
                $2 < minimum_bytes || $2 > nominal_bytes) {
                invalid = 1
            }
        }
        END { exit count == 1 && !invalid ? 0 : 1 }
    '
}

hook2stream_luks_loop_from_status() {
    hook2stream_luks_status=$1
    printf '%s\n' "$hook2stream_luks_status" | awk '
        /^[[:space:]]*type:[[:space:]]*/ {
            type = $0
            sub(/^[[:space:]]*type:[[:space:]]*/, "", type)
        }
        /^[[:space:]]*device:[[:space:]]*/ {
            device = $0
            sub(/^[[:space:]]*device:[[:space:]]*/, "", device)
        }
        END {
            if (type != "LUKS2" || device !~ /^\/dev\/loop[0-9]+$/) {
                exit 1
            }
            print device
        }
    '
}

hook2stream_validate_ufw_status() {
    hook2stream_ufw_role=$1
    hook2stream_ufw_status=$2

    [ "$hook2stream_ufw_role" = app ] || return 1

    printf '%s\n' "$hook2stream_ufw_status" | grep -qx 'Status: active' \
        || return 1
    printf '%s\n' "$hook2stream_ufw_status" \
        | grep -Eq '^Default:[[:space:]]+deny \(incoming\),[[:space:]]+allow \(outgoing\),[[:space:]]+(deny|disabled) \(routed\)[[:space:]]*$' \
        || return 1

    printf '%s\n' "$hook2stream_ufw_status" | awk '
        function is_allow(action) {
            return action == "ALLOW" || action == "LIMIT"
        }
        function record_public_web(target, ipv6) {
            if (ipv6) public_v6[target] = 1
            else public_v4[target] = 1
        }
        {
            line = $0
            target = $1
            ipv6 = (line ~ /\(v6\)/)
            tailscale = (line ~ /(^|[[:space:]])on[[:space:]]+tailscale0([[:space:]]|$)/)
            action = ""
            direction = ""
            for (i = 1; i <= NF; i++) {
                if ($i == "ALLOW" || $i == "LIMIT") action = $i
                if ($i == "IN" || $i == "FWD") direction = $i
            }
            if (!is_allow(action)) next
            if (direction == "FWD" || direction != "IN") {
                invalid = 1
                next
            }

            if (target == "22/tcp") {
                if (!tailscale) invalid = 1
                else if (!ipv6) tailscale_ssh_v4 = 1
                next
            }

            if (target == "80/tcp" || target == "443/tcp" || target == "443/udp") {
                if (tailscale || line !~ /Anywhere/) invalid = 1
                else record_public_web(target, ipv6)
                next
            }

            invalid = 1
        }
        END {
            if (invalid || !tailscale_ssh_v4) exit 1
            if (!public_v4["80/tcp"] || !public_v4["443/tcp"] ||
                !public_v4["443/udp"]) exit 1
            saw_v6 = public_v6["80/tcp"] || public_v6["443/tcp"] ||
                public_v6["443/udp"]
            if (saw_v6 && (!public_v6["80/tcp"] ||
                !public_v6["443/tcp"] || !public_v6["443/udp"])) exit 1
        }
    '
}

hook2stream_validate_proc_options() {
    hook2stream_proc_options=$1
    case ",$hook2stream_proc_options," in
        *,gid=*) return 1 ;;
    esac
    case ",$hook2stream_proc_options," in
        *,hidepid=2,*|*,hidepid=invisible,*) return 0 ;;
        *) return 1 ;;
    esac
}

hook2stream_has_tcp_listener() {
    hook2stream_listener_table=$1
    hook2stream_listener_port=$2
    printf '%s\n' "$hook2stream_listener_table" | awk -v port="$hook2stream_listener_port" '
        $4 ~ (":" port "$") { found = 1 }
        END { exit found ? 0 : 1 }
    '
}

hook2stream_validate_docker_bindings() {
    hook2stream_binding_role=$1
    hook2stream_binding_environment=$2
    hook2stream_binding_tailscale_ipv4=$3
    hook2stream_binding_table=$4

    [ "$hook2stream_binding_role" = app ] || return 1

    printf '%s\n' "$hook2stream_binding_table" | awk \
        -v role="$hook2stream_binding_role" \
        -v environment="$hook2stream_binding_environment" \
        -v tailscale_ipv4="$hook2stream_binding_tailscale_ipv4" '
        NF == 0 { next }
        NF != 5 { invalid = 1; next }
        {
            project = $1
            service = $2
            container_port = $3
            host_ip = $4
            host_port = $5

            if (role != "app" || project != "hook2stream-" environment ||
                service != "caddy") {
                invalid = 1
                next
            }
            if (host_ip != "0.0.0.0" && host_ip != "::") {
                invalid = 1
                next
            }
            if (!((container_port == "80/tcp" && host_port == "80") ||
                (container_port == "443/tcp" && host_port == "443") ||
                (container_port == "443/udp" && host_port == "443"))) {
                invalid = 1
            }
        }
        END { exit invalid ? 1 : 0 }
    '
}

hook2stream_validate_sshd_effective() {
    [ "$#" -eq 1 ] || return 1
    printf '%s\n' "$1" | awk '
      BEGIN {
        required["pubkeyauthentication"] = "pubkeyauthentication yes"
        required["passwordauthentication"] = "passwordauthentication yes"
        required["kbdinteractiveauthentication"] = "kbdinteractiveauthentication no"
        required["authenticationmethods"] = "authenticationmethods any"
        required["hostbasedauthentication"] = "hostbasedauthentication no"
        required["gssapiauthentication"] = "gssapiauthentication no"
        required["kerberosauthentication"] = "kerberosauthentication no"
        required["permitemptypasswords"] = "permitemptypasswords no"
        required["permitrootlogin"] = "permitrootlogin yes"
        required["authorizedkeysfile"] = "authorizedkeysfile .ssh/authorized_keys"
        required["authorizedkeyscommand"] = "authorizedkeyscommand none"
        required["authorizedkeyscommanduser"] = "authorizedkeyscommanduser none"
        required["trustedusercakeys"] = "trustedusercakeys none"
        required["strictmodes"] = "strictmodes yes"
        required["permituserenvironment"] = "permituserenvironment no"
        required["permituserrc"] = "permituserrc no"
        required["forcecommand"] = "forcecommand none"
        required["disableforwarding"] = "disableforwarding yes"
        required["hostkey"] = "hostkey /etc/ssh/ssh_host_ed25519_key"
      }
      { key = tolower($1); normalized = $0; sub(/^[^[:space:]]+/, key, normalized) }
      key == "acceptenv" {
        if (NF != 2 || ($2 != "LANG" && $2 != "LC_*") || ++accepted[$2] != 1) invalid = 1
        next
      }
      key == "setenv" { invalid = 1; next }
      key == "allowusers" {
        if (NF < 2) {
          invalid = 1
          next
        }
        for (field = 2; field <= NF; field++) {
          user = $field
          if (user != "root" && user != "hook2stream-operator" &&
              user != "hook2stream-deploy") {
            invalid = 1
          }
          allowed_user_seen[user]++
          allowed_user_count++
        }
        next
      }
      (key in required) {
        seen[key]++
        if (normalized != required[key]) invalid = 1
      }
      END {
        for (key in required) {
          if (seen[key] != 1) invalid = 1
        }
        if (allowed_user_count != 3 || allowed_user_seen["root"] != 1 ||
            allowed_user_seen["hook2stream-operator"] != 1 ||
            allowed_user_seen["hook2stream-deploy"] != 1) invalid = 1
        exit invalid ? 1 : 0
      }
    '
}

hook2stream_validate_sshd_root_effective() {
    [ "$#" -eq 1 ] || return 1
    hook2stream_validate_sshd_effective "$1" || return 1
    printf '%s\n' "$1" | awk '
      BEGIN {
        required["passwordauthentication"] = "passwordauthentication yes"
        required["authenticationmethods"] = "authenticationmethods any"
        required["permitrootlogin"] = "permitrootlogin yes"
      }
      {
        key = tolower($1)
        normalized = $0
        sub(/^[^[:space:]]+/, key, normalized)
      }
      (key in required) {
        seen[key]++
        if (normalized != required[key]) invalid = 1
      }
      END {
        for (key in required) {
          if (seen[key] != 1) invalid = 1
        }
        exit invalid ? 1 : 0
      }
    '
}

hook2stream_validate_sshd_config_tree() {
    [ "$#" -eq 3 ] || return 1
    hook2stream_sshd_main=$1
    hook2stream_sshd_dropins=$2
    hook2stream_sshd_owner=$3
    [ -f "$hook2stream_sshd_main" ] && [ ! -L "$hook2stream_sshd_main" ] \
        && [ "$(stat -c '%u:%g:%a' "$hook2stream_sshd_main")" = "$hook2stream_sshd_owner:644" ] \
        || return 1
    hook2stream_host_no_extended_acl "$hook2stream_sshd_main" || return 1
    [ -d "$hook2stream_sshd_dropins" ] && [ ! -L "$hook2stream_sshd_dropins" ] \
        && [ "$(stat -c '%u:%g:%a' "$hook2stream_sshd_dropins")" = "$hook2stream_sshd_owner:755" ] \
        || return 1
    hook2stream_host_no_extended_acl "$hook2stream_sshd_dropins" || return 1
    awk '
      /^[[:space:]]*($|#)/ { next }
      {
        keyword = tolower($1)
        if (keyword == "include") {
          includes++
          if (NF != 2 || $2 != "/etc/ssh/sshd_config.d/*.conf") invalid = 1
        }
        if (keyword == "match") invalid = 1
      }
      END { exit (includes == 1 && !invalid) ? 0 : 1 }
    ' "$hook2stream_sshd_main" || return 1
    set -- "$hook2stream_sshd_dropins"/*
    [ "$1" != "$hook2stream_sshd_dropins/*" ] || return 1
    for hook2stream_sshd_dropin in "$@"; do
        case "$hook2stream_sshd_dropin" in *.conf) ;; *) return 1 ;; esac
        [ -f "$hook2stream_sshd_dropin" ] && [ ! -L "$hook2stream_sshd_dropin" ] \
            || return 1
        hook2stream_sshd_dropin_metadata=$(stat -c '%u:%g:%a' "$hook2stream_sshd_dropin") \
            || return 1
        case "$hook2stream_sshd_dropin_metadata" in
            "$hook2stream_sshd_owner":600|"$hook2stream_sshd_owner":640|"$hook2stream_sshd_owner":644) ;;
            *) return 1 ;;
        esac
        hook2stream_host_no_extended_acl "$hook2stream_sshd_dropin" || return 1
        awk '
          /^[[:space:]]*($|#)/ { next }
          { keyword = tolower($1); if (keyword == "include" || keyword == "match") invalid = 1 }
          END { exit invalid ? 1 : 0 }
        ' "$hook2stream_sshd_dropin" || return 1
    done
}

hook2stream_host_no_extended_acl() {
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

hook2stream_validate_tailscale_ssh_preference() {
    [ "$#" -eq 1 ] || return 1
    printf '%s\n' "$1" | awk '
      {
        preference = preference (NR > 1 ? "\n" : "") $0
      }
      END {
        disabled_literal = preference ~ /^[ \t\r\n]*false[ \t\r\n]*$/
        disabled_object = preference ~ /^[ \t\r\n]*\{[ \t\r\n]*"ssh"[ \t\r\n]*:[ \t\r\n]*false[ \t\r\n]*\}[ \t\r\n]*$/
        exit (disabled_literal || disabled_object) ? 0 : 1
      }
    '
}

hook2stream_validate_locked_password_status() {
    [ "$#" -eq 1 ] || return 1
    case "$1" in
        L|LK) return 0 ;;
        *) return 1 ;;
    esac
}

hook2stream_validate_root_password_status() {
    [ "$#" -eq 1 ] && [ "$1" = P ]
}

hook2stream_subpath_mount_matches() {
    hook2stream_expected_source=$1
    hook2stream_expected_target=$2
    hook2stream_actual_source=$3
    hook2stream_actual_target=$4
    [ "$hook2stream_actual_source" = "$hook2stream_expected_source" ] \
        && [ "$hook2stream_actual_target" = "$hook2stream_expected_target" ]
}

hook2stream_gid_list_contains() {
    hook2stream_gid_list=$1
    hook2stream_gid_expected=$2
    for hook2stream_gid_entry in $hook2stream_gid_list; do
        [ "$hook2stream_gid_entry" != "$hook2stream_gid_expected" ] || return 0
    done
    return 1
}

hook2stream_direct_https_probe() (
    [ "$#" -eq 1 ] || return 2
    unset \
        http_proxy https_proxy all_proxy ftp_proxy no_proxy \
        HTTP_PROXY HTTPS_PROXY ALL_PROXY FTP_PROXY NO_PROXY \
        AWS_CA_BUNDLE CURL_CA_BUNDLE GIT_SSL_CAINFO REQUESTS_CA_BUNDLE \
        SSL_CERT_FILE SSL_CERT_DIR
    curl -q \
        --proxy '' \
        --noproxy '*' \
        --silent \
        --show-error \
        --output /dev/null \
        --connect-timeout 10 \
        --max-time 20 \
        --max-redirs 0 \
        --proto '=https' \
        --tlsv1.2 \
        "$1"
)
