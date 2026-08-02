ARG POSTGRES_IMAGE=postgres:17.10-alpine3.24
FROM ${POSTGRES_IMAGE}

USER root
RUN apk add --no-cache aws-cli coreutils curl jq openssl

COPY src/deploy/scripts/postgres-backup.sh /usr/local/bin/postgres-backup
COPY src/deploy/scripts/postgres-backup-healthcheck.sh /usr/local/bin/postgres-backup-healthcheck
RUN chmod 0555 /usr/local/bin/postgres-backup /usr/local/bin/postgres-backup-healthcheck

USER postgres
ENTRYPOINT ["/usr/local/bin/postgres-backup"]
CMD ["daemon"]
