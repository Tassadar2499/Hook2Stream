#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
healthcheck_script=${deployment_dir}/scripts/http-healthcheck.sh
caddyfile=${deployment_dir}/Caddyfile
temporary_dir=$(mktemp -d)
server_pid=

cleanup() {
    if [ -n "$server_pid" ]; then
        kill "$server_pid" >/dev/null 2>&1 || true
        wait "$server_pid" >/dev/null 2>&1 || true
    fi
    rm -rf "$temporary_dir"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

fail() {
    printf '%s\n' "http healthcheck test: $*" >&2
    exit 1
}

start_server() {
    response_status=$1
    port_file=${temporary_dir}/port
    request_file=${temporary_dir}/request
    rm -f "$port_file" "$request_file"

    node - "$port_file" "$request_file" "$response_status" <<'NODE' &
const fs = require("node:fs");
const net = require("node:net");

const [portFile, requestFile, responseStatus] = process.argv.slice(2);
const server = net.createServer((socket) => {
    let request = "";
    socket.setEncoding("utf8");
    socket.on("error", (error) => {
        if (error.code !== "ECONNRESET") {
            throw error;
        }
    });
    socket.on("data", (chunk) => {
        request += chunk;
        if (!request.includes("\r\n\r\n")) {
            return;
        }

        fs.writeFileSync(requestFile, request);
        socket.end(
            `HTTP/1.1 ${responseStatus} Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n`,
        );
        server.close();
    });
});

server.listen(0, "127.0.0.1", () => {
    fs.writeFileSync(portFile, String(server.address().port));
});
NODE
    server_pid=$!

    attempts=0
    while [ ! -s "$port_file" ]; do
        attempts=$((attempts + 1))
        [ "$attempts" -lt 100 ] || fail "test server did not start"
        sleep 0.05
    done
    server_port=$(cat "$port_file")
}

wait_for_server() {
    wait "$server_pid"
    server_pid=
}

start_server 200
HEALTHCHECK_HOST=127.0.0.1 \
HEALTHCHECK_PORT=$server_port \
HEALTHCHECK_PATH=/health/ready \
    sh "$healthcheck_script"
wait_for_server
grep -F 'GET /health/ready HTTP/1.1' "$request_file" >/dev/null \
    || fail "healthcheck sent the wrong request path"
grep -F 'Host: 127.0.0.1' "$request_file" >/dev/null \
    || fail "healthcheck did not default the Host header to the connection host"

start_server 200
HEALTHCHECK_HOST=127.0.0.1 \
HEALTHCHECK_PORT=$server_port \
HEALTHCHECK_PATH=/health/ready \
HEALTHCHECK_HOST_HEADER=app.example.com \
    sh "$healthcheck_script"
wait_for_server
grep -F 'Host: app.example.com' "$request_file" >/dev/null \
    || fail "healthcheck did not send HEALTHCHECK_HOST_HEADER"

start_server 503
if HEALTHCHECK_HOST=127.0.0.1 \
    HEALTHCHECK_PORT=$server_port \
    HEALTHCHECK_HOST_HEADER=app.example.com \
    sh "$healthcheck_script"; then
    fail "healthcheck accepted a non-200 response"
fi
wait_for_server

node - "$caddyfile" <<'NODE'
const fs = require("node:fs");

const caddyfile = fs.readFileSync(process.argv[2], "utf8");
const publicReady = caddyfile.indexOf("@api_ready path /health/api-ready");
const apiRoutes = caddyfile.indexOf("@api path /api/*");
const webFallback = caddyfile.lastIndexOf("handle {");

if (publicReady < 0 || apiRoutes < 0 || webFallback < 0) {
    throw new Error("expected public readiness, API, and web fallback routes");
}
if (!(publicReady < apiRoutes && apiRoutes < webFallback)) {
    throw new Error("health and API routes must precede the web fallback");
}
if (!/handle @api_ready \{[\s\S]*?rewrite \* \/health\/ready[\s\S]*?header_up Host \{\$APP_DOMAIN\}[\s\S]*?respond "ready" 200[\s\S]*?respond "not ready" 503[\s\S]*?\n\t\}/.test(caddyfile)) {
    throw new Error("public API readiness must proxy safely and normalize its response");
}
if (!/health_uri \/health\/ready\s+health_headers \{\s+Host \{\$APP_DOMAIN\}\s+\}/.test(caddyfile)) {
    throw new Error("Caddy API healthcheck must use APP_DOMAIN as its Host header");
}
NODE

printf '%s\n' "http healthcheck test: passed"
