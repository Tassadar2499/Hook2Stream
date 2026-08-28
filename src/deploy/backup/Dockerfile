ARG POSTGRES_IMAGE=postgres:17.10-alpine3.24
FROM ${POSTGRES_IMAGE}

USER root
RUN apk add --no-cache age aws-cli bash coreutils jq

COPY src/deploy/scripts/postgres-backup.sh /usr/local/bin/postgres-backup
COPY src/deploy/scripts/postgres-backup-healthcheck.sh /usr/local/bin/postgres-backup-healthcheck
COPY src/deploy/scripts/storj-media-janitor.sh /usr/local/bin/storj-media-janitor
COPY src/deploy/scripts/storj-media-janitor-healthcheck.sh /usr/local/bin/storj-media-janitor-healthcheck
RUN chmod 0555 \
    /usr/local/bin/postgres-backup \
    /usr/local/bin/postgres-backup-healthcheck \
    /usr/local/bin/storj-media-janitor \
    /usr/local/bin/storj-media-janitor-healthcheck

USER postgres
ENTRYPOINT ["/usr/local/bin/postgres-backup"]
CMD ["daemon"]
