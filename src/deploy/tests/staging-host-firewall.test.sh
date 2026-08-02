#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
. "$deployment_dir/scripts/lib/staging-host-common.sh"

fail_test() {
    printf '%s\n' "staging firewall test: $*" >&2
    exit 1
}

valid_v4_status='Status: active
Logging: on (low)
Default: deny (incoming), allow (outgoing), disabled (routed)

To                         Action      From
--                         ------      ----
22/tcp                     ALLOW IN    203.0.113.40/32
80/tcp                     ALLOW IN    Anywhere
443/tcp                    ALLOW IN    Anywhere
443/udp                    ALLOW IN    Anywhere'

staging_host_validate_ufw_status "$valid_v4_status" \
    || fail_test "the exact IPv4 staging policy was rejected"

valid_dual_stack_status="${valid_v4_status}
80/tcp (v6)                ALLOW IN    Anywhere (v6)
443/tcp (v6)               ALLOW IN    Anywhere (v6)
443/udp (v6)               ALLOW IN    Anywhere (v6)"
staging_host_validate_ufw_status "$valid_dual_stack_status" \
    || fail_test "the mirrored dual-stack staging policy was rejected"

assert_rejected() {
    rejected_name=$1
    rejected_status=$2
    if staging_host_validate_ufw_status "$rejected_status"; then
        fail_test "$rejected_name policy was accepted"
    fi
}

inactive_status=$(printf '%s\n' "$valid_v4_status" | sed 's/^Status: active$/Status: inactive/')
assert_rejected inactive "$inactive_status"

allow_default_status=$(printf '%s\n' "$valid_v4_status" \
    | sed 's/Default: deny (incoming)/Default: allow (incoming)/')
assert_rejected default-allow "$allow_default_status"

broad_ssh_status=$(printf '%s\n' "$valid_v4_status" \
    | sed 's#203\.0\.113\.40/32#Anywhere#')
assert_rejected broad-ssh "$broad_ssh_status"

universal_cidr_ssh_status=$(printf '%s\n' "$valid_v4_status" \
    | sed 's#203\.0\.113\.40/32#0.0.0.0/0#')
assert_rejected universal-cidr-ssh "$universal_cidr_ssh_status"

missing_udp_status=$(printf '%s\n' "$valid_v4_status" \
    | sed '/^443\/udp[[:space:]]/d')
assert_rejected missing-public-udp "$missing_udp_status"

unexpected_port_status="${valid_v4_status}
9000/tcp                   ALLOW IN    203.0.113.40/32"
assert_rejected unexpected-private-port "$unexpected_port_status"

allow_routed_default_status=$(printf '%s\n' "$valid_v4_status" \
    | sed 's/disabled (routed)/allow (routed)/')
assert_rejected routed-default-allow "$allow_routed_default_status"

forwarded_private_port_status="${valid_v4_status}
9000/tcp                   ALLOW FWD   Anywhere"
assert_rejected forwarded-private-port "$forwarded_private_port_status"

partial_ipv6_status="${valid_v4_status}
80/tcp (v6)                ALLOW IN    Anywhere (v6)"
assert_rejected partial-ipv6 "$partial_ipv6_status"

printf '%s\n' \
    "staging firewall test: default-deny, restricted SSH, and exact web rules passed"
