#!/bin/sh
# Pure host-profile and policy helpers. This file is safe to source from tests.

hook2stream_host_profile() {
    hook2stream_profile_role=$1
    hook2stream_profile_environment=$2

    case "${hook2stream_profile_role}:${hook2stream_profile_environment}" in
        app:staging)
            hook2stream_profile_minimum_gib=112
            ;;
        app:production)
            hook2stream_profile_minimum_gib=176
            ;;
        storage:staging)
            hook2stream_profile_minimum_gib=64
            ;;
        storage:production)
            hook2stream_profile_minimum_gib=256
            ;;
        *)
            return 1
            ;;
    esac

    case "$hook2stream_profile_role" in
        app)
            hook2stream_profile_backing_file=/var/lib/hook2stream-data.luks
            hook2stream_profile_mapper=hook2stream-data
            hook2stream_profile_mount=/srv/hook2stream
            ;;
        storage)
            hook2stream_profile_backing_file=/var/lib/hook2stream-storage.luks
            hook2stream_profile_mapper=hook2stream-storage
            hook2stream_profile_mount=/srv/hook2stream-storage
            ;;
    esac
}

hook2stream_required_secret_files() {
    hook2stream_secret_role=$1
    case "$hook2stream_secret_role" in
        app)
            printf '%s\n' \
                postgres_password \
                s3_runtime_access_key \
                s3_runtime_secret_key \
                s3_bootstrap_access_key \
                s3_bootstrap_secret_key \
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
        storage)
            printf '%s\n' \
                minio_root_user \
                minio_root_password \
                s3_runtime_access_key \
                s3_runtime_secret_key \
                s3_bootstrap_access_key \
                s3_bootstrap_secret_key \
                backup_s3_access_key \
                backup_s3_secret_key \
                storage-tls.crt \
                storage-tls.key
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

    printf '%s\n' "$hook2stream_ufw_status" | grep -qx 'Status: active' \
        || return 1
    printf '%s\n' "$hook2stream_ufw_status" \
        | grep -Eq '^Default:[[:space:]]+deny \(incoming\),[[:space:]]+allow \(outgoing\),[[:space:]]+(deny|disabled) \(routed\)[[:space:]]*$' \
        || return 1

    printf '%s\n' "$hook2stream_ufw_status" | awk -v role="$hook2stream_ufw_role" '
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

            if (role == "app" &&
                (target == "80/tcp" || target == "443/tcp" || target == "443/udp")) {
                if (tailscale || line !~ /Anywhere/) invalid = 1
                else record_public_web(target, ipv6)
                next
            }

            if (role == "storage" && target == "443/tcp") {
                if (!tailscale) invalid = 1
                else if (!ipv6) tailscale_storage_https_v4 = 1
                next
            }

            invalid = 1
        }
        END {
            if (invalid || !tailscale_ssh_v4) exit 1
            if (role == "app") {
                if (!public_v4["80/tcp"] || !public_v4["443/tcp"] ||
                    !public_v4["443/udp"]) exit 1
                saw_v6 = public_v6["80/tcp"] || public_v6["443/tcp"] ||
                    public_v6["443/udp"]
                if (saw_v6 && (!public_v6["80/tcp"] ||
                    !public_v6["443/tcp"] || !public_v6["443/udp"])) exit 1
            } else if (role == "storage") {
                if (!tailscale_storage_https_v4) exit 1
            } else {
                exit 1
            }
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

hook2stream_validate_storage_https_listeners() {
    hook2stream_https_listener_table=$1
    hook2stream_https_tailscale_ipv4=$2
    printf '%s\n' "$hook2stream_https_listener_table" | awk \
        -v expected="$hook2stream_https_tailscale_ipv4:443" '
        $4 ~ /:443$/ {
            count++
            if ($4 != expected) invalid = 1
        }
        END { exit invalid ? 1 : 0 }
    '
}

hook2stream_validate_docker_bindings() {
    hook2stream_binding_role=$1
    hook2stream_binding_environment=$2
    hook2stream_binding_tailscale_ipv4=$3
    hook2stream_binding_table=$4

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

            if (role == "app") {
                if (project != "hook2stream-" environment || service != "caddy") {
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
            } else if (role == "storage") {
                if (project != "hook2stream-storage-" environment ||
                    service != "caddy" || container_port != "443/tcp" ||
                    host_ip != tailscale_ipv4 || host_port != "443") {
                    invalid = 1
                }
            } else {
                invalid = 1
            }
        }
        END { exit invalid ? 1 : 0 }
    '
}

hook2stream_subpath_mount_matches() {
    hook2stream_expected_source=$1
    hook2stream_expected_target=$2
    hook2stream_actual_source=$3
    hook2stream_actual_target=$4
    [ "$hook2stream_actual_source" = "$hook2stream_expected_source" ] \
        && [ "$hook2stream_actual_target" = "$hook2stream_expected_target" ]
}

hook2stream_service_identity_matches() {
    hook2stream_identity_record=$1
    hook2stream_identity_name=$2
    hook2stream_identity_uid=$3
    hook2stream_identity_gid=$4
    printf '%s\n' "$hook2stream_identity_record" | awk -F: \
        -v name="$hook2stream_identity_name" \
        -v uid="$hook2stream_identity_uid" \
        -v gid="$hook2stream_identity_gid" '
        NF == 7 && $1 == name && $3 == uid && $4 == gid &&
            $7 == "/usr/sbin/nologin" { valid = 1 }
        END { exit valid ? 0 : 1 }
    '
}

hook2stream_gid_list_contains() {
    hook2stream_gid_list=$1
    hook2stream_gid_expected=$2
    for hook2stream_gid_entry in $hook2stream_gid_list; do
        [ "$hook2stream_gid_entry" != "$hook2stream_gid_expected" ] || return 0
    done
    return 1
}

hook2stream_gid_list_is_exact() {
    hook2stream_exact_gid_list=$1
    hook2stream_exact_gid=$2
    [ "$hook2stream_exact_gid_list" = "$hook2stream_exact_gid" ]
}
