#!/bin/sh
set -eu

tests_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
storage_dir=$(CDPATH= cd -- "$tests_dir/.." && pwd)
fail() { printf '%s\n' "storage deployment validation: $*" >&2; exit 1; }

find "$storage_dir" -type f -name '*.sh' -print | sort | while IFS= read -r script; do
    sh -n "$script" || fail "shell syntax failed: $script"
done
for json in $(find "$storage_dir" -type f -name '*.json' -print | sort); do
    node -e 'JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"))' "$json" \
        || fail "JSON syntax failed: $json"
done
for test_script in "$tests_dir"/*.test.sh; do sh "$test_script"; done
printf '%s\n' "storage deployment validation: PASS"
