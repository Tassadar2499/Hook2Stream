ARG POSTGRES_BASE_IMAGE=postgres:17.11-alpine3.24
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
