ARG ALPINE_IMAGE=docker.io/library/alpine:3.24@sha256:28bd5fe8b56d1bd048e5babf5b10710ebe0bae67db86916198a6eec434943f8b
FROM ${ALPINE_IMAGE}

ARG SQUID_VERSION=7.6-r0

LABEL org.opencontainers.image.title="Hook2Stream egress proxy" \
      org.opencontainers.image.description="Minimal patched Squid allowlist proxy" \
      org.opencontainers.image.source="https://gitlab.alpinelinux.org/alpine/aports/-/tree/3.24-stable/main/squid" \
      org.opencontainers.image.licenses="GPL-2.0-or-later" \
      com.hook2stream.squid.package-version="${SQUID_VERSION}"

RUN apk add --no-cache \
        libcrypto3=3.5.8-r0 \
        libssl3=3.5.8-r0 \
        squid="${SQUID_VERSION}" \
    && squid -v | grep -F 'Version 7.6'

USER 31:31
EXPOSE 3128
ENTRYPOINT ["/usr/sbin/squid"]
CMD ["--foreground", "-f", "/etc/squid/squid.conf"]
