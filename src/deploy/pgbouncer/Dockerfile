ARG BUILD_IMAGE=docker.io/library/alpine:3.24@sha256:28bd5fe8b56d1bd048e5babf5b10710ebe0bae67db86916198a6eec434943f8b
ARG RUNTIME_IMAGE=docker.io/library/alpine:3.24@sha256:28bd5fe8b56d1bd048e5babf5b10710ebe0bae67db86916198a6eec434943f8b
ARG PGBOUNCER_VERSION=1.25.2
ARG PGBOUNCER_SOURCE_SHA256=924ad35113fd0a71c8e2dbe85b5d03445532e2b7b37a9f8a48983beea238b332

FROM ${BUILD_IMAGE} AS build

ARG PGBOUNCER_VERSION
ARG PGBOUNCER_SOURCE_SHA256

RUN apk add --no-cache \
        autoconf \
        automake \
        curl \
        gcc \
        libc-dev \
        libevent-dev \
        libtool \
        make \
        openssl-dev \
        pkgconf
RUN curl --fail --location --proto '=https' --tlsv1.2 \
        --output /tmp/pgbouncer.tar.gz \
        "https://github.com/pgbouncer/pgbouncer/releases/download/pgbouncer_1_25_2/pgbouncer-${PGBOUNCER_VERSION}.tar.gz" \
    && echo "${PGBOUNCER_SOURCE_SHA256}  /tmp/pgbouncer.tar.gz" | sha256sum -c - \
    && mkdir /source \
    && tar --extract --gzip --file /tmp/pgbouncer.tar.gz --directory /source --strip-components=1

WORKDIR /source
RUN ./configure --prefix=/usr \
    && make -j"$(getconf _NPROCESSORS_ONLN)" pgbouncer \
    && strip pgbouncer \
    && ./pgbouncer --version | grep -F "PgBouncer ${PGBOUNCER_VERSION}"

FROM ${RUNTIME_IMAGE} AS runtime

ARG PGBOUNCER_VERSION
ARG PGBOUNCER_SOURCE_SHA256

LABEL org.opencontainers.image.title="Hook2Stream PgBouncer" \
      org.opencontainers.image.description="Pinned PgBouncer source build on a patched minimal Alpine runtime" \
      org.opencontainers.image.source="https://github.com/pgbouncer/pgbouncer" \
      org.opencontainers.image.licenses="ISC" \
      com.hook2stream.pgbouncer.source-version="${PGBOUNCER_VERSION}" \
      com.hook2stream.pgbouncer.source-sha256="${PGBOUNCER_SOURCE_SHA256}"

RUN apk add --no-cache \
        libcrypto3=3.5.8-r0 \
        libevent=2.1.13-r0 \
        libssl3=3.5.8-r0 \
        postgresql17-client=17.11-r0 \
    && addgroup -S -g 10001 pgbouncer \
    && adduser -S -D -H -u 10001 -G pgbouncer pgbouncer

COPY --from=build /source/pgbouncer /usr/bin/pgbouncer
COPY --from=build /source/COPYRIGHT /licenses/PgBouncer-ISC.txt
RUN chmod 0555 /usr/bin/pgbouncer \
    && chmod 0444 /licenses/PgBouncer-ISC.txt

USER 10001:10001
EXPOSE 6432
ENTRYPOINT ["/usr/bin/pgbouncer"]
