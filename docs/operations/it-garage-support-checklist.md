# IT-Garage pre-order support checklist

Keep the written response with the infrastructure change record. Do not order
or enable production payments from an informal assumption.

Ask IT-Garage to confirm for RZ-W-4, RZ-W-8, FI-MX-4, and FI-MX-5:

1. The displayed price's billing period, VAT treatment, current stock, and
   whether one customer can keep all four plans concurrently.
2. Ubuntu 24.04 amd64 availability and KVM access to `/dev/loop*`, device mapper,
   LUKS2, `/dev/net/tun`, and Tailscale/WireGuard.
3. A stable public IPv4 for both app plans, IPv6 behavior, address persistence
   across reboot/reinstall, and whether inbound TCP 80/443 plus UDP 443 is
   permitted.
4. Web/serial/VNC console access that remains usable before the encrypted data
   filesystem and Docker start, plus rescue/reinstall behavior.
5. Permission for bursty FFmpeg rendering and a 60-minute soak, the actual CPU
   share/steal policy, NVMe IOPS limits, and the escalation path before a
   throttle or suspension.
6. The exact traffic Fair Use threshold, measurement window, likely reduced
   speed, regional routing between Germany and Finland, and whether private
   Tailscale traffic is counted differently.
7. Outbound access to GitHub, GHCR, Tailscale, Google, Stripe, and OpenRouter,
   and any SMTP/port/content restrictions that could affect the application.
8. Available provider firewall, snapshots, backups, audit logs, scoped panel
   users/API tokens, 2FA, payment alerts, and incident notification, even if
   those features are not publicly documented.
9. Legal entity, physical datacenter/operator, data-processing terms, and
   whether Germany and Finland are independent failure domains or use an
   upstream dependency that defeats the intended placement.

Record the answer date, support ticket ID, responder, exact SKUs, accepted
exceptions, and the next revalidation date. A negative answer on console access,
TUN/Tailscale, stable app IPv4, or permitted FFmpeg/network workload blocks the
four-VPS design.
