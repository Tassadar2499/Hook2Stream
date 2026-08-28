ARG GO_IMAGE=golang:1.27.0-alpine3.24
ARG POSTGRES_BASE_IMAGE=postgres:17.11-alpine3.24

FROM ${GO_IMAGE} AS storage-tool-build

WORKDIR /source
COPY src/deploy/backup/storage-tool/go.mod src/deploy/backup/storage-tool/go.sum ./
RUN go mod download && go mod verify
COPY src/deploy/backup/storage-tool/ ./
RUN go list -deps ./... > /tmp/storage-tool-dependencies \
    && ! grep -Eq '^golang.org/x/crypto/openpgp(/|$)' /tmp/storage-tool-dependencies \
    && rm -f /tmp/storage-tool-dependencies \
    && go test ./... \
    && CGO_ENABLED=0 go build \
        -buildvcs=false \
        -trimpath \
        -ldflags='-s -w' \
        -o /out/hook2stream-storage-tool \
        .

FROM ${POSTGRES_BASE_IMAGE}

USER root
RUN apk add --no-cache --upgrade \
        libcrypto3=3.5.8-r0 \
        libssl3=3.5.8-r0 \
    && apk add --no-cache bash coreutils jq \
    && rm -f /usr/local/bin/gosu

COPY --from=storage-tool-build /out/hook2stream-storage-tool /usr/local/bin/hook2stream-storage-tool
COPY src/deploy/scripts/postgres-backup.sh /usr/local/bin/postgres-backup
COPY src/deploy/scripts/postgres-backup-healthcheck.sh /usr/local/bin/postgres-backup-healthcheck
COPY src/deploy/scripts/storj-media-janitor.sh /usr/local/bin/storj-media-janitor
COPY src/deploy/scripts/storj-media-janitor-healthcheck.sh /usr/local/bin/storj-media-janitor-healthcheck
RUN chmod 0555 \
    /usr/local/bin/hook2stream-storage-tool \
    /usr/local/bin/postgres-backup \
    /usr/local/bin/postgres-backup-healthcheck \
    /usr/local/bin/storj-media-janitor \
    /usr/local/bin/storj-media-janitor-healthcheck

USER postgres
ENTRYPOINT ["/usr/local/bin/postgres-backup"]
CMD ["daemon"]
