#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
. "$deployment_dir/scripts/lib/host-validation-common.sh"

fail_test() {
    printf '%s\n' "host firewall test: $*" >&2
    exit 1
}

app_status='Status: active
Logging: on (low)
Default: deny (incoming), allow (outgoing), disabled (routed)

To                         Action      From
--                         ------      ----
22/tcp on tailscale0       ALLOW IN    Anywhere
80/tcp                     ALLOW IN    Anywhere
443/tcp                    ALLOW IN    Anywhere
443/udp                    ALLOW IN    Anywhere'

hook2stream_validate_ufw_status app "$app_status" \
    || fail_test "the exact IPv4 app policy was rejected"

app_dual_stack_status="${app_status}
22/tcp (v6) on tailscale0  ALLOW IN    Anywhere (v6)
80/tcp (v6)                ALLOW IN    Anywhere (v6)
443/tcp (v6)               ALLOW IN    Anywhere (v6)
443/udp (v6)               ALLOW IN    Anywhere (v6)"
hook2stream_validate_ufw_status app "$app_dual_stack_status" \
    || fail_test "the mirrored dual-stack app policy was rejected"

storage_status='Status: active
Logging: on (low)
Default: deny (incoming), allow (outgoing), deny (routed)

To                         Action      From
--                         ------      ----
22/tcp on tailscale0       ALLOW IN    Anywhere
443/tcp on tailscale0      ALLOW IN    Anywhere'

hook2stream_validate_ufw_status storage "$storage_status" \
    || fail_test "the Tailscale-only storage policy was rejected"

storage_dual_stack_status="${storage_status}
22/tcp (v6) on tailscale0  ALLOW IN    Anywhere (v6)
443/tcp (v6) on tailscale0 ALLOW IN    Anywhere (v6)"
hook2stream_validate_ufw_status storage "$storage_dual_stack_status" \
    || fail_test "the Tailscale-only dual-stack storage policy was rejected"

assert_rejected() {
    rejected_role=$1
    rejected_name=$2
    rejected_status=$3
    if hook2stream_validate_ufw_status "$rejected_role" "$rejected_status"; then
        fail_test "$rejected_role $rejected_name policy was accepted"
    fi
}

inactive_status=$(printf '%s\n' "$app_status" | sed 's/^Status: active$/Status: inactive/')
assert_rejected app inactive "$inactive_status"

allow_default_status=$(printf '%s\n' "$app_status" \
    | sed 's/Default: deny (incoming)/Default: allow (incoming)/')
assert_rejected app default-allow "$allow_default_status"

public_ssh_status=$(printf '%s\n' "$app_status" \
    | sed 's#22/tcp on tailscale0#22/tcp             #')
assert_rejected app public-ssh "$public_ssh_status"

missing_udp_status=$(printf '%s\n' "$app_status" \
    | sed '/^443\/udp[[:space:]]/d')
assert_rejected app missing-public-udp "$missing_udp_status"

unexpected_port_status="${app_status}
9000/tcp                   ALLOW IN    Anywhere"
assert_rejected app unexpected-private-port "$unexpected_port_status"

allow_routed_status=$(printf '%s\n' "$app_status" \
    | sed 's/disabled (routed)/allow (routed)/')
assert_rejected app routed-default-allow "$allow_routed_status"

forwarded_status="${app_status}
9000/tcp                   ALLOW FWD   Anywhere"
assert_rejected app forwarded-private-port "$forwarded_status"

partial_ipv6_status="${app_status}
80/tcp (v6)                ALLOW IN    Anywhere (v6)"
assert_rejected app partial-ipv6 "$partial_ipv6_status"

public_storage_https=$(printf '%s\n' "$storage_status" \
    | sed 's#443/tcp on tailscale0#443/tcp             #')
assert_rejected storage public-https "$public_storage_https"

storage_with_http="${storage_status}
80/tcp                     ALLOW IN    Anywhere"
assert_rejected storage public-http "$storage_with_http"

storage_v6_only=$(printf '%s\n' "$storage_dual_stack_status" \
    | sed -e '/^22\/tcp on tailscale0[[:space:]]*ALLOW/d' \
        -e '/^443\/tcp on tailscale0[[:space:]]*ALLOW/d')
assert_rejected storage missing-tailscale-ipv4 "$storage_v6_only"

printf '%s\n' \
    "host firewall test: app public web and Tailscale-only app/storage administration passed"
