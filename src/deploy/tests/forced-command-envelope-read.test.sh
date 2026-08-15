#!/bin/sh
set -eu

deploy_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
app_wrapper=$deploy_dir/scripts/deploy-forced-command.sh
storage_wrapper=$deploy_dir/storage/scripts/storage-forced-command.sh

fail() {
    printf '%s\n' "forced-command envelope read test: $*" >&2
    exit 1
}

grep -F 'dd iflag=fullblock bs=1048576 count=257 of="$envelope"' "$app_wrapper" >/dev/null \
    || fail "app wrapper can truncate a chunked SSH stream"
grep -F 'dd iflag=fullblock bs=1048576 count=129 of="$envelope"' "$storage_wrapper" >/dev/null \
    || fail "storage wrapper can truncate a chunked SSH stream"

command -v node >/dev/null 2>&1 || fail "Node.js is required for the chunked writer fixture"
scratch=$(mktemp -d)
cleanup() { rm -rf "$scratch"; }
trap cleanup EXIT HUP INT TERM

write_chunked_payload() {
    node -e '
        const chunk = Buffer.alloc(4096, 0x61);
        let remaining = 512;
        function write() {
          if (remaining-- === 0) return;
          if (process.stdout.write(chunk)) setImmediate(write);
          else process.stdout.once("drain", write);
        }
        write();
    '
}

for limit in 257 129; do
    output=$scratch/envelope-$limit.tar
    write_chunked_payload | dd iflag=fullblock bs=1048576 count="$limit" of="$output" 2>/dev/null
    [ "$(wc -c < "$output")" -eq 2097152 ] \
        || fail "full-block reader truncated the 2 MiB payload for limit $limit"
done

printf '%s\n' "forced-command envelope read test: chunked SSH streams are read in full"
