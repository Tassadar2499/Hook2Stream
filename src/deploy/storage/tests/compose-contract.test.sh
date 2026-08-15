#!/bin/sh
set -eu

storage_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
compose=$storage_dir/compose.yaml
caddy=$storage_dir/Caddyfile
fail() { printf '%s\n' "storage compose contract: $*" >&2; exit 1; }

grep -F 'host_ip: ${TAILSCALE_IPV4:?TAILSCALE_IPV4 is required}' "$compose" >/dev/null || fail "HTTPS is not bound to the Tailscale IPv4"
test "$(grep -c 'published: "443"' "$compose")" -eq 1 || fail "exactly one host TCP port must be published"
grep -F 'protocol: tcp' "$compose" >/dev/null || fail "TCP 443 is absent"
! grep -F 'protocol: udp' "$compose" >/dev/null || fail "UDP 443 must not be published"
! grep -E 'published: "?(9000|9001)"?' "$compose" >/dev/null || fail "MinIO API or console is exposed on the host"
grep -F 'MINIO_BROWSER: "off"' "$compose" >/dev/null || fail "MinIO console is not disabled"
grep -F 'STORAGE_MODE: minio' "$compose" >/dev/null || fail "published source-built MinIO image will fail its entrypoint gate"
grep -F 'user: "10001:10001"' "$compose" >/dev/null || fail "MinIO does not use its dedicated service identity"
grep -F 'user: "10002:10002"' "$compose" >/dev/null || fail "Caddy can collide with an operator UID"
grep -F 'user: "10003:10003"' "$compose" >/dev/null || fail "initializer can collide with an operator UID"
for service_id in 10001 10002 10003; do
    grep -F "uid=$service_id,gid=$service_id,mode=0700" "$compose" >/dev/null \
        || fail "service $service_id lacks a private writable tmpfs"
done
grep -F 'target: /run/tls/tls.crt' "$compose" >/dev/null || fail "TLS certificate is not mounted"
grep -F 'target: /run/tls/tls.key' "$compose" >/dev/null || fail "TLS key is not mounted"
grep -F 'MANAGED_IDENTITY_INVENTORY_SOURCE: stdin' "$compose" >/dev/null \
    || fail "initializer does not require the root-streamed managed-identity inventory"
! grep -F 'managed_identity_inventory:' "$compose" >/dev/null \
    || fail "root-only managed-identity inventory was weakened into a container bind mount"
test "$(grep -c 'read_only: true' "$compose")" -ge 7 || fail "read-only runtime/mount contracts are missing"
grep -F 'source: ./policies' "$compose" >/dev/null || fail "policy directory is not a bind mount"
grep -F 'source: ./lifecycle' "$compose" >/dev/null || fail "lifecycle directory is not a bind mount"
grep -F 'source: ./markers' "$compose" >/dev/null || fail "marker directory is not a bind mount"
! grep -A3 '^configs:' "$compose" | grep -E 'policies|lifecycle|markers' >/dev/null || fail "directory was incorrectly declared as a Compose config"
grep -F 'protocols h1 h2' "$caddy" >/dev/null || fail "Caddy TCP protocols are not fixed"
! grep -F 'h3' "$caddy" >/dev/null || fail "Caddy HTTP/3 contradicts TCP-only firewall policy"
grep -F 'path /.well-known/hook2stream-storage-protocol' "$caddy" >/dev/null || fail "storage protocol endpoint is absent"
grep -F 'respond "1" 200' "$caddy" >/dev/null || fail "storage protocol body is not exact v1"
grep -F '@private path /minio /minio/*' "$caddy" >/dev/null \
    || fail "the complete internal MinIO namespace is not blocked"
! grep -F '@private path /minio/admin/*' "$caddy" >/dev/null \
    || fail "MinIO protection relies on an incomplete internal-route allowlist"
grep -F 'header "Host: $${STORAGE_TLS_SERVER_NAME}"' "$compose" >/dev/null || fail "Caddy healthcheck lacks the configured Host header"
grep -F '${STORAGE_TLS_SERVER_NAME:?STORAGE_TLS_SERVER_NAME is required}=127.0.0.1' "$compose" >/dev/null \
    || fail "Caddy health hostname lacks a loopback-only resolver entry"
grep -F 'https://$${STORAGE_TLS_SERVER_NAME}:443/healthz' "$compose" >/dev/null \
    || fail "Caddy healthcheck TLS SNI does not match the configured certificate"
! grep -E '(^|[[:space:]])log[[:space:]]*\{' "$caddy" >/dev/null || fail "S3 object keys would enter Caddy access logs"
printf '%s\n' "storage compose contract: PASS"
