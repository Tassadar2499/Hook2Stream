#!/bin/sh
set -eu

export HEALTHCHECK_HOST=${HEALTHCHECK_HOST:-127.0.0.1}
export HEALTHCHECK_PORT=${HEALTHCHECK_PORT:-8080}
export HEALTHCHECK_PATH=${HEALTHCHECK_PATH:-/health/ready}
export HEALTHCHECK_HOST_HEADER=${HEALTHCHECK_HOST_HEADER:-${HEALTHCHECK_HOST}}

exec /bin/bash -ec '
    exec 3<>"/dev/tcp/${HEALTHCHECK_HOST}/${HEALTHCHECK_PORT}"
    printf "GET %s HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n" \
        "${HEALTHCHECK_PATH}" "${HEALTHCHECK_HOST_HEADER}" >&3
    IFS=" " read -r protocol status remainder <&3
    test "${status}" = "200"
'
