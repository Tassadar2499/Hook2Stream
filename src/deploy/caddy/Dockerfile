ARG GO_IMAGE=docker.io/library/golang:1.27.0-alpine3.24@sha256:4c9fe60190a2a3350ddc51de80d0224b8a6698d12bdfc999fee45ea9d6c46dbc
ARG CADDY_RELEASE=v2.11.4
ARG CADDY_COMMIT=e2eee6a7fce366321294c9c2a79f3146891dcbdf

FROM ${GO_IMAGE} AS build

ARG CADDY_RELEASE
ARG CADDY_COMMIT

RUN apk add --no-cache git
RUN git init /source \
    && cd /source \
    && git remote add origin https://github.com/caddyserver/caddy.git \
    && git fetch --depth=1 origin "refs/tags/${CADDY_RELEASE}:refs/tags/${CADDY_RELEASE}" \
    && git checkout --detach "${CADDY_RELEASE}" \
    && test "$(git rev-parse HEAD)" = "${CADDY_COMMIT}" \
    && test "$(git tag --points-at HEAD)" = "${CADDY_RELEASE}"

WORKDIR /source
RUN go get \
        github.com/go-chi/chi/v5@v5.3.0 \
        github.com/klauspost/compress@v1.18.7 \
        go.opentelemetry.io/otel@v1.44.0 \
        go.opentelemetry.io/otel/sdk@v1.44.0 \
        go.opentelemetry.io/otel/trace@v1.44.0 \
        golang.org/x/crypto@v0.55.0 \
        golang.org/x/net@v0.57.0 \
        golang.org/x/text@v0.41.0 \
        google.golang.org/grpc@v1.83.1 \
    && test "$(go list -m -f '{{.Version}}' golang.org/x/crypto)" = "v0.55.0" \
    && test "$(go list -m -f '{{.Version}}' google.golang.org/grpc)" = "v1.83.1" \
    && go mod verify \
    && CGO_ENABLED=0 GOOS=linux go build \
        -buildvcs=false \
        -trimpath \
        -ldflags="-s -w -X github.com/caddyserver/caddy/v2.CustomVersion=${CADDY_RELEASE}" \
        -o /out/caddy \
        ./cmd/caddy \
    && test "$(/out/caddy version)" = "${CADDY_RELEASE}" \
    && go version -m /out/caddy \
        | grep -Eq '^[[:space:]]+dep[[:space:]]+golang.org/x/crypto[[:space:]]+v0\.55\.0[[:space:]]' \
    && go version -m /out/caddy \
        | grep -Eq '^[[:space:]]+dep[[:space:]]+google.golang.org/grpc[[:space:]]+v1\.83\.1[[:space:]]'
RUN mkdir -p /runtime-root/data /runtime-root/config \
    && chown -R 10001:10001 /runtime-root/data /runtime-root/config \
    && chmod 0700 /runtime-root/data /runtime-root/config

FROM scratch AS runtime

ARG CADDY_RELEASE
ARG CADDY_COMMIT

LABEL org.opencontainers.image.title="Hook2Stream Caddy" \
      org.opencontainers.image.description="Pinned Caddy source build with reviewed security dependency updates" \
      org.opencontainers.image.source="https://github.com/caddyserver/caddy" \
      org.opencontainers.image.licenses="Apache-2.0" \
      com.hook2stream.caddy.source-release="${CADDY_RELEASE}" \
      com.hook2stream.caddy.source-commit="${CADDY_COMMIT}"

COPY --from=build /out/caddy /usr/bin/caddy
COPY --from=build /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt
COPY --from=build /source/LICENSE /licenses/Caddy-Apache-2.0.txt
COPY --from=build /runtime-root/ /

ENV PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    XDG_CONFIG_HOME=/config \
    XDG_DATA_HOME=/data

EXPOSE 80 443 443/udp
USER 10001:10001
CMD ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
