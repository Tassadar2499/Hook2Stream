#!/bin/sh
set -eu
set -f

[ "$#" -eq 3 ] || { printf '%s\n' "usage: validate-candidate.sh CANDIDATE_DIR ARTIFACT REPOSITORY" >&2; exit 2; }
candidate=$1
expected_artifact=$2
expected_repository=$3
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/lib/storage-common.sh"

storage_require_command jq
storage_require_command sha256sum
storage_require_command tar
[ -d "$candidate" ] && [ ! -L "$candidate" ] || storage_fail "candidate directory is invalid"
actual_files=$(find "$candidate" -mindepth 1 -maxdepth 1 -printf '%f\n' | sort)
expected_files=$(printf '%s\n' SHA256SUMS storage-bundle.tar.gz storage-images.env storage-metadata.json | sort)
[ "$actual_files" = "$expected_files" ] || storage_fail "candidate contains missing or unknown files"
for name in SHA256SUMS storage-bundle.tar.gz storage-images.env storage-metadata.json; do
    [ -f "$candidate/$name" ] && [ ! -L "$candidate/$name" ] || storage_fail "candidate file is unsafe: $name"
done
[ "$(wc -c < "$candidate/storage-bundle.tar.gz")" -le 67108864 ] || storage_fail "storage bundle exceeds 64 MiB"

checksum_names=$(awk '
    !/^[0-9a-f]{64}  [A-Za-z0-9][A-Za-z0-9._-]*$/ { exit 1 }
    { print $2 }
' "$candidate/SHA256SUMS") || storage_fail "SHA256SUMS syntax is invalid"
[ "$checksum_names" = "$(printf '%s\n' storage-bundle.tar.gz storage-images.env storage-metadata.json)" ] \
    || storage_fail "SHA256SUMS must cover the exact candidate files in canonical order"
(cd "$candidate" && sha256sum --strict -c SHA256SUMS >/dev/null) || storage_fail "candidate checksum verification failed"

metadata=$candidate/storage-metadata.json
jq -e '
    type == "object" and
    (keys | sort) == (["schemaVersion","kind","protocolVersion","sourceRef","sourceEvent","workflow","workflowName","artifactName","repository","commitSha","ciRunId","ciRunAttempt","createdAt","images","storageBundle"] | sort) and
    .schemaVersion == 1 and .kind == "hook2stream-storage-candidate" and
    .protocolVersion == "storage-forced-command-v1" and
    .sourceRef == "refs/heads/main" and .sourceEvent == "push" and
    .workflow == ".github/workflows/storage-ci.yml" and .workflowName == "Storage CI" and
    (.commitSha | type == "string" and test("^[0-9a-f]{40}$")) and
    (.ciRunId | type == "number" and . >= 1 and floor == .) and
    (.ciRunAttempt | type == "number" and . >= 1 and floor == .) and
    (.createdAt | type == "string") and
    (.images | type == "object") and
    (.storageBundle | type == "object")
' "$metadata" >/dev/null || storage_fail "storage metadata schema is invalid"

artifact=$(jq -r .artifactName "$metadata")
repository=$(jq -r .repository "$metadata")
commit=$(jq -r .commitSha "$metadata")
run_id=$(jq -r .ciRunId "$metadata")
run_attempt=$(jq -r .ciRunAttempt "$metadata")
[ "$artifact" = "$expected_artifact" ] || storage_fail "candidate artifact differs from SSH command"
[ "$repository" = "$expected_repository" ] || storage_fail "candidate repository differs from host configuration"
[ "$artifact" = "storage-candidate-$commit-$run_id-$run_attempt" ] \
    || storage_fail "candidate artifact identity is not canonical"
case "$repository" in [A-Za-z0-9_.-]*/[A-Za-z0-9_.-]*) ;; *) storage_fail "repository is invalid" ;; esac

storage_validate_strict_env "$candidate/storage-images.env"
image_line_count=$(awk 'NF && $0 !~ /^#/ {count++} END {print count + 0}' "$candidate/storage-images.env")
[ "$image_line_count" -eq 4 ] || storage_fail "storage-images.env must have exactly four assignments"
for key in STORAGE_RELEASE_VERSION MINIO_IMAGE MINIO_MC_IMAGE CADDY_IMAGE; do
    storage_env_value "$candidate/storage-images.env" "$key" >/dev/null
done
release_version=$(storage_env_value "$candidate/storage-images.env" STORAGE_RELEASE_VERSION)
minio_image=$(storage_env_value "$candidate/storage-images.env" MINIO_IMAGE)
mc_image=$(storage_env_value "$candidate/storage-images.env" MINIO_MC_IMAGE)
caddy_image=$(storage_env_value "$candidate/storage-images.env" CADDY_IMAGE)
[ "$release_version" = "$commit" ] || storage_fail "storage release version differs from metadata"
storage_validate_digest_image MINIO_IMAGE "$minio_image"
storage_validate_digest_image MINIO_MC_IMAGE "$mc_image"
storage_validate_digest_image CADDY_IMAGE "$caddy_image"
owner=$(printf '%s' "${repository%%/*}" | tr '[:upper:]' '[:lower:]')
case "$minio_image" in "ghcr.io/$owner/hook2stream-minio@sha256:"*) ;; *) storage_fail "MINIO_IMAGE repository is outside the allowlist" ;; esac
case "$mc_image" in minio/mc@sha256:*|docker.io/minio/mc@sha256:*) ;; *) storage_fail "MINIO_MC_IMAGE repository is outside the allowlist" ;; esac
case "$caddy_image" in caddy@sha256:*|docker.io/library/caddy@sha256:*) ;; *) storage_fail "CADDY_IMAGE repository is outside the allowlist" ;; esac
jq -e --arg minio "$minio_image" --arg mc "$mc_image" --arg caddy "$caddy_image" '
    (.images | keys | sort) == (["MINIO_IMAGE","MINIO_MC_IMAGE","CADDY_IMAGE"] | sort) and
    .images == {MINIO_IMAGE:$minio, MINIO_MC_IMAGE:$mc, CADDY_IMAGE:$caddy}
' "$metadata" >/dev/null || storage_fail "metadata images differ from storage-images.env"

bundle_sha=$(sha256sum "$candidate/storage-bundle.tar.gz" | awk '{print $1}')
jq -e --arg sha "$bundle_sha" '
    (.storageBundle | keys | sort) == (["file","sha256"] | sort) and
    .storageBundle.file == "storage-bundle.tar.gz" and .storageBundle.sha256 == $sha
' "$metadata" >/dev/null || storage_fail "metadata bundle identity is invalid"

tar -tzf "$candidate/storage-bundle.tar.gz" | while IFS= read -r member; do
    case "$member" in storage|storage/|storage/*) ;; *) storage_fail "bundle member is outside storage/" ;; esac
    case "$member" in /*|../*|*/../*|*/..|*\\*) storage_fail "bundle path traversal detected" ;; esac
done
if tar -tvzf "$candidate/storage-bundle.tar.gz" | awk '$1 !~ /^[d-]/ {bad=1} END {exit bad ? 0 : 1}'; then
    storage_fail "bundle links and special files are forbidden"
fi
tar -tvzf "$candidate/storage-bundle.tar.gz" \
    | awk '{total += $3} END {exit total <= 268435456 ? 0 : 1}' \
    || storage_fail "expanded storage bundle exceeds 256 MiB"
tar -xOzf "$candidate/storage-bundle.tar.gz" storage/storage-release.json > "$candidate/.storage-release.json.tmp" \
    || storage_fail "bundle lacks storage-release.json"
jq -e '
    type == "object" and
    (keys | sort) == (["schemaVersion","kind","protocolVersion","storageFormatVersion","objectFormat","minioRelease","minioSourceCommit"] | sort) and
    .schemaVersion == 1 and .kind == "hook2stream-storage-runtime" and
    .protocolVersion == 1 and .storageFormatVersion == 1 and
    .objectFormat == "H2SEv1" and
    (.minioRelease | type == "string" and
        test("^RELEASE\\.[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}-[0-9]{2}-[0-9]{2}Z$")) and
    (.minioSourceCommit | type == "string" and test("^[0-9a-f]{40}$"))
' "$candidate/.storage-release.json.tmp" >/dev/null || storage_fail "bundle storage compatibility manifest is invalid"
rm -f "$candidate/.storage-release.json.tmp"
printf '%s\n' "$commit"
