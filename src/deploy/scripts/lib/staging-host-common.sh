#!/bin/sh
# Pure validation helpers shared by the staging-host preflight and its tests.

staging_host_validate_ufw_status() {
    staging_host_ufw_status=$1

    printf '%s\n' "$staging_host_ufw_status" \
        | grep -qx 'Status: active' || return 1
    printf '%s\n' "$staging_host_ufw_status" \
        | grep -Eq '^Default:[[:space:]]+deny \(incoming\),[[:space:]]+allow \(outgoing\),[[:space:]]+(deny|disabled) \(routed\)[[:space:]]*$' \
        || return 1

    printf '%s\n' "$staging_host_ufw_status" | awk '
        function is_public_web_target(target) {
            return target == "80/tcp" ||
                   target == "443/tcp" ||
                   target == "443/udp"
        }

        {
            target = $1
            ipv6_rule = ($2 == "(v6)")
            offset = ipv6_rule ? 1 : 0
            action = $(2 + offset)
            direction = $(3 + offset)
            source = $(4 + offset)

            if (action != "ALLOW" && action != "LIMIT") {
                next
            }

            if (direction == "FWD") {
                invalid_rule = 1
                next
            }

            if (direction != "IN") {
                next
            }

            if (ipv6_rule) {
                saw_ipv6_rule = 1
            }

            if (target == "22/tcp") {
                if (source == "Anywhere" || source == "" ||
                    source == "0.0.0.0/0" || source == "::/0" ||
                    source == "0/0") {
                    invalid_rule = 1
                } else {
                    restricted_ssh = 1
                }
                next
            }

            if (!is_public_web_target(target)) {
                invalid_rule = 1
                next
            }

            if (action == "ALLOW" && source == "Anywhere") {
                if (ipv6_rule) {
                    public_v6[target] = 1
                } else {
                    public_v4[target] = 1
                }
            }
        }

        END {
            if (invalid_rule || !restricted_ssh) {
                exit 1
            }
            if (!public_v4["80/tcp"] ||
                !public_v4["443/tcp"] ||
                !public_v4["443/udp"]) {
                exit 1
            }
            if (saw_ipv6_rule &&
                (!public_v6["80/tcp"] ||
                 !public_v6["443/tcp"] ||
                 !public_v6["443/udp"])) {
                exit 1
            }
        }
    '
}
