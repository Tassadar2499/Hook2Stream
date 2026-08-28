ARG POSTGRES_BASE_IMAGE=docker.io/library/postgres:17.11-alpine3.24@sha256:18cfe3ef5e6815560c98237d6216d1e5119702fb0f3894c8785dd58b8bbe5d73
FROM ${POSTGRES_BASE_IMAGE}

LABEL org.opencontainers.image.title="Hook2Stream PostgreSQL" \
      org.opencontainers.image.description="PostgreSQL 17.11 with patched Alpine runtime and su-exec privilege drop"

USER root
RUN apk add --no-cache --upgrade \
        libcrypto3=3.5.8-r0 \
        libssl3=3.5.8-r0 \
    && apk add --no-cache su-exec \
    && rm -f /usr/local/bin/gosu \
    && ln -s /sbin/su-exec /usr/local/bin/gosu \
    && test "$(gosu postgres id -u)" = "$(id -u postgres)"
