#!/bin/sh
# Backward-compatible alias for the original staging-only firewall helper.

if ! command -v hook2stream_validate_ufw_status >/dev/null 2>&1; then
    staging_host_common_base=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
    for staging_host_common_candidate in \
        "$staging_host_common_base/lib/host-validation-common.sh" \
        "$staging_host_common_base/../scripts/lib/host-validation-common.sh"; do
        if [ -r "$staging_host_common_candidate" ]; then
            . "$staging_host_common_candidate"
            break
        fi
    done
fi
command -v hook2stream_validate_ufw_status >/dev/null 2>&1 \
    || return 1

staging_host_validate_ufw_status() {
    hook2stream_validate_ufw_status app "$1"
}
