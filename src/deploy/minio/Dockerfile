ARG GO_IMAGE=docker.io/library/golang:1.25.12-alpine3.23@sha256:f128118f1f3a7f38949c57f03d29d13e4afdb06b86f840f8f43e8031e6c1a73a
ARG ALPINE_IMAGE=docker.io/library/alpine:3.22.5@sha256:7c8cb692ae09657cbc4a3f3cbd0e8d5a2690ba38386aaaf252dbb060bf5eb2e6
ARG MINIO_RELEASE=RELEASE.2025-10-15T17-29-55Z
ARG MINIO_COMMIT=9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a
FROM ${GO_IMAGE} AS build

ARG MINIO_RELEASE
ARG MINIO_COMMIT

RUN apk add --no-cache git
RUN git init /source \
    && cd /source \
    && git remote add origin https://github.com/minio/minio.git \
    && git fetch --depth=1 origin \
        "refs/tags/${MINIO_RELEASE}:refs/tags/${MINIO_RELEASE}" \
    && git checkout --detach "${MINIO_RELEASE}" \
    && test "$(git rev-parse HEAD)" = "${MINIO_COMMIT}" \
    && test "$(git tag --points-at HEAD)" = "${MINIO_RELEASE}"

WORKDIR /source
RUN mkdir -p /out \
    && ldflags="$(MINIO_RELEASE=RELEASE go run buildscripts/gen-ldflags.go)" \
    && CGO_ENABLED=0 GOOS=linux go build \
        -tags kqueue \
        -trimpath \
        -ldflags "${ldflags}" \
        -o /out/minio . \
    && /out/minio --version | grep -F "${MINIO_RELEASE}"

FROM ${ALPINE_IMAGE} AS runtime

ARG MINIO_RELEASE
ARG MINIO_COMMIT

LABEL org.opencontainers.image.title="Hook2Stream local/CI MinIO" \
      org.opencontainers.image.description="Source build of the final MinIO community release; deployment security gate enforced separately" \
      org.opencontainers.image.source="https://github.com/minio/minio" \
      com.hook2stream.minio.source-release="${MINIO_RELEASE}" \
      com.hook2stream.minio.source-commit="${MINIO_COMMIT}" \
      org.opencontainers.image.licenses="AGPL-3.0-only"

RUN addgroup -S -g 10001 minio \
    && adduser -S -D -H -u 10001 -G minio minio \
    && mkdir -p /data /licenses \
    && chown minio:minio /data \
    && chmod 0750 /data

COPY --from=build /out/minio /usr/local/bin/minio
COPY --from=build /source/LICENSE /licenses/MinIO-AGPL-3.0.txt
COPY src/deploy/minio/minio-entrypoint.sh /usr/local/bin/minio-entrypoint
RUN chmod 0555 /usr/local/bin/minio /usr/local/bin/minio-entrypoint \
    && chmod 0444 /licenses/MinIO-AGPL-3.0.txt

ENV HOME=/tmp
USER 10001:10001
EXPOSE 9000
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:9000/minio/health/ready || exit 1

ENTRYPOINT ["/usr/local/bin/minio-entrypoint"]
CMD ["server", "--address", ":9000", "/data"]
