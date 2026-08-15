#!/usr/bin/env bash
set -euo pipefail

ci_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

fixture_repo="$scratch/repository"
storage_dir="$fixture_repo/src/deploy/storage"
fragments="$scratch/fragments"
candidate="$scratch/candidate"
mkdir -p "$storage_dir/scripts" "$fragments" "$candidate"

cat > "$storage_dir/compose.yaml" <<'EOF'
services:
  minio:
    image: ${MINIO_IMAGE:?digest required}
EOF
cat > "$storage_dir/storage-release.json" <<'EOF'
{"schemaVersion":1,"kind":"hook2stream-storage-runtime","protocolVersion":1,"storageFormatVersion":1,"objectFormat":"H2SEv1","minioRelease":"RELEASE.2025-10-15T17-29-55Z","minioSourceCommit":"9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a"}
EOF
cat > "$storage_dir/scripts/deploy-storage.sh" <<'EOF'
#!/bin/sh
set -eu
printf '%s\n' 'fixture storage deployment'
EOF
chmod 0555 "$storage_dir/scripts/deploy-storage.sh"

git -C "$fixture_repo" init -q
git -C "$fixture_repo" add src/deploy/storage
git -C "$fixture_repo" -c user.name=storage-test -c user.email=storage-test@example.invalid \
  commit -q -m 'storage fixture'
sha="$(git -C "$fixture_repo" rev-parse HEAD)"
repository=example/Hook2Stream
run_id=24680
run_attempt=3

printf 'MINIO_IMAGE=ghcr.io/example/hook2stream-minio@sha256:%064d\n' 1 > "$fragments/minio.env"
printf 'MINIO_MC_IMAGE=minio/mc@sha256:%064d\n' 2 > "$fragments/minio-mc.env"
printf 'CADDY_IMAGE=caddy@sha256:%064d\n' 3 > "$fragments/caddy.env"

node "$ci_dir/storage-candidate.mjs" create \
  --output "$candidate" \
  --fragments "$fragments" \
  --storage-dir "$storage_dir" \
  --repository "$repository" \
  --sha "$sha" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt"

node "$ci_dir/storage-candidate.mjs" validate \
  --candidate "$candidate" \
  --repository "$repository" \
  --sha "$sha" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt"

artifact_name="$(node -p 'JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).artifactName' \
  "$candidate/storage-metadata.json")"
test "$artifact_name" = "storage-candidate-${sha}-${run_id}-${run_attempt}"
if tar -tzf "$candidate/storage-bundle.tar.gz" | grep -Ev '^storage(/|$)' >/dev/null; then
  echo "storage bundle contains an entry outside storage/" >&2
  exit 1
fi

must_reject() {
  local expected=$1
  shift
  if "$@" >"$scratch/rejected.out" 2>&1; then
    echo "invalid storage contract unexpectedly validated: $expected" >&2
    exit 1
  fi
  grep -F "$expected" "$scratch/rejected.out" >/dev/null || {
    echo "storage validation did not fail for the expected reason: $expected" >&2
    cat "$scratch/rejected.out" >&2
    exit 1
  }
}

refresh_checksums() {
  local directory=$1
  (
    cd "$directory"
    sha256sum storage-bundle.tar.gz storage-images.env storage-metadata.json > SHA256SUMS
  )
}

cp -a "$candidate" "$scratch/unknown-key"
printf 'UNEXPECTED_IMAGE=caddy@sha256:%064d\n' 9 >> "$scratch/unknown-key/storage-images.env"
refresh_checksums "$scratch/unknown-key"
must_reject "storage-images.env must contain exactly" \
  node "$ci_dir/storage-candidate.mjs" validate --candidate "$scratch/unknown-key"

cp -a "$candidate" "$scratch/tagged-image"
sed -i 's#^MINIO_IMAGE=.*#MINIO_IMAGE=ghcr.io/example/hook2stream-minio:latest#' \
  "$scratch/tagged-image/storage-images.env"
refresh_checksums "$scratch/tagged-image"
must_reject "MINIO_IMAGE is not a digest-only image reference" \
  node "$ci_dir/storage-candidate.mjs" validate --candidate "$scratch/tagged-image"

cp -a "$candidate" "$scratch/unapproved-repository"
sed -i 's#ghcr.io/example/hook2stream-minio@#ghcr.io/example/unapproved-minio@#' \
  "$scratch/unapproved-repository/storage-images.env"
node - "$scratch/unapproved-repository/storage-metadata.json" <<'JS'
const fs = require("fs");
const path = process.argv[2];
const value = JSON.parse(fs.readFileSync(path));
value.images.MINIO_IMAGE = value.images.MINIO_IMAGE.replace("hook2stream-minio@", "unapproved-minio@");
fs.writeFileSync(path, `${JSON.stringify(value, null, 2)}\n`);
JS
refresh_checksums "$scratch/unapproved-repository"
must_reject "MINIO_IMAGE repository is outside the storage allowlist" \
  node "$ci_dir/storage-candidate.mjs" validate --candidate "$scratch/unapproved-repository"

cp -a "$candidate" "$scratch/traversal"
python3 - "$scratch/traversal/storage-bundle.tar.gz" <<'PY'
import io
import tarfile
import sys

with tarfile.open(sys.argv[1], "w:gz") as archive:
    payload = b"escape"
    info = tarfile.TarInfo("storage/../../escape")
    info.size = len(payload)
    archive.addfile(info, io.BytesIO(payload))
PY
node - "$scratch/traversal" <<'JS'
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const directory = process.argv[2];
const digest = (file) => crypto.createHash("sha256").update(fs.readFileSync(path.join(directory, file))).digest("hex");
const metadataPath = path.join(directory, "storage-metadata.json");
const metadata = JSON.parse(fs.readFileSync(metadataPath));
metadata.storageBundle.sha256 = digest("storage-bundle.tar.gz");
fs.writeFileSync(metadataPath, `${JSON.stringify(metadata, null, 2)}\n`);
JS
refresh_checksums "$scratch/traversal"
must_reject "unsafe storage bundle path" \
  node "$ci_dir/storage-candidate.mjs" validate --candidate "$scratch/traversal"

cp -a "$candidate" "$scratch/extra-file"
touch "$scratch/extra-file/unexpected"
must_reject "candidate must contain exactly" \
  node "$ci_dir/storage-candidate.mjs" validate --candidate "$scratch/extra-file"

cp -a "$candidate" "$scratch/symlink-candidate"
mv "$scratch/symlink-candidate/storage-metadata.json" "$scratch/symlinked-metadata.json"
ln -s "$scratch/symlinked-metadata.json" "$scratch/symlink-candidate/storage-metadata.json"
must_reject "storage-metadata.json must be a regular non-symlink file" \
  node "$ci_dir/storage-candidate.mjs" validate --candidate "$scratch/symlink-candidate"

cp -a "$candidate" "$scratch/format-downgrade"
python3 - "$scratch/format-downgrade/storage-bundle.tar.gz" <<'PY'
import io
import json
import tarfile
import sys

source = sys.argv[1]
members = []
with tarfile.open(source, "r:gz") as archive:
    for member in archive.getmembers():
        payload = archive.extractfile(member).read() if member.isfile() else None
        if member.name == "storage/storage-release.json":
            payload = json.dumps({
                "schemaVersion": 1,
                "kind": "hook2stream-storage-runtime",
                "protocolVersion": 1,
                "storageFormatVersion": 1,
                "objectFormat": "Plaintext",
                "minioRelease": "RELEASE.2025-10-15T17-29-55Z",
                "minioSourceCommit": "9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a",
            }).encode()
            member.size = len(payload)
        members.append((member, payload))
with tarfile.open(source, "w:gz") as archive:
    for member, payload in members:
        archive.addfile(member, io.BytesIO(payload) if payload is not None else None)
PY
node - "$scratch/format-downgrade" <<'JS'
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const directory = process.argv[2];
const digest = (file) => crypto.createHash("sha256").update(fs.readFileSync(path.join(directory, file))).digest("hex");
const metadataPath = path.join(directory, "storage-metadata.json");
const metadata = JSON.parse(fs.readFileSync(metadataPath));
metadata.storageBundle.sha256 = digest("storage-bundle.tar.gz");
fs.writeFileSync(metadataPath, `${JSON.stringify(metadata, null, 2)}\n`);
JS
refresh_checksums "$scratch/format-downgrade"
must_reject "storage/storage-release.json does not declare the supported H2SEv1 runtime and MinIO source pin" \
  node "$ci_dir/storage-candidate.mjs" validate --candidate "$scratch/format-downgrade"

cp -a "$candidate" "$scratch/minio-release-drift"
python3 - "$scratch/minio-release-drift/storage-bundle.tar.gz" <<'PY'
import io
import json
import tarfile
import sys

source = sys.argv[1]
members = []
with tarfile.open(source, "r:gz") as archive:
    for member in archive.getmembers():
        payload = archive.extractfile(member).read() if member.isfile() else None
        if member.name == "storage/storage-release.json":
            manifest = json.loads(payload)
            manifest["minioRelease"] = "RELEASE.unreviewed"
            payload = json.dumps(manifest).encode()
            member.size = len(payload)
        members.append((member, payload))
with tarfile.open(source, "w:gz") as archive:
    for member, payload in members:
        archive.addfile(member, io.BytesIO(payload) if payload is not None else None)
PY
node - "$scratch/minio-release-drift" <<'JS'
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const directory = process.argv[2];
const digest = (file) => crypto.createHash("sha256").update(fs.readFileSync(path.join(directory, file))).digest("hex");
const metadataPath = path.join(directory, "storage-metadata.json");
const metadata = JSON.parse(fs.readFileSync(metadataPath));
metadata.storageBundle.sha256 = digest("storage-bundle.tar.gz");
fs.writeFileSync(metadataPath, `${JSON.stringify(metadata, null, 2)}\n`);
JS
refresh_checksums "$scratch/minio-release-drift"
must_reject "storage/storage-release.json does not declare the supported H2SEv1 runtime and MinIO source pin" \
  node "$ci_dir/storage-candidate.mjs" validate --candidate "$scratch/minio-release-drift"

cp -a "$candidate" "$scratch/minio-source-drift"
python3 - "$scratch/minio-source-drift/storage-bundle.tar.gz" <<'PY'
import io
import json
import tarfile
import sys

source = sys.argv[1]
members = []
with tarfile.open(source, "r:gz") as archive:
    for member in archive.getmembers():
        payload = archive.extractfile(member).read() if member.isfile() else None
        if member.name == "storage/storage-release.json":
            manifest = json.loads(payload)
            manifest["minioSourceCommit"] = "0" * 40
            payload = json.dumps(manifest).encode()
            member.size = len(payload)
        members.append((member, payload))
with tarfile.open(source, "w:gz") as archive:
    for member, payload in members:
        archive.addfile(member, io.BytesIO(payload) if payload is not None else None)
PY
node - "$scratch/minio-source-drift" <<'JS'
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const directory = process.argv[2];
const digest = (file) => crypto.createHash("sha256").update(fs.readFileSync(path.join(directory, file))).digest("hex");
const metadataPath = path.join(directory, "storage-metadata.json");
const metadata = JSON.parse(fs.readFileSync(metadataPath));
metadata.storageBundle.sha256 = digest("storage-bundle.tar.gz");
fs.writeFileSync(metadataPath, `${JSON.stringify(metadata, null, 2)}\n`);
JS
refresh_checksums "$scratch/minio-source-drift"
must_reject "storage/storage-release.json does not declare the supported H2SEv1 runtime and MinIO source pin" \
  node "$ci_dir/storage-candidate.mjs" validate --candidate "$scratch/minio-source-drift"

must_reject "candidate repository does not match --repository" \
  node "$ci_dir/storage-candidate.mjs" validate --candidate "$candidate" --repository other/Repository
must_reject "candidate commit does not match --sha" \
  node "$ci_dir/storage-candidate.mjs" validate --candidate "$candidate" --sha "$(printf '%040d' 9)"
must_reject "candidate run does not match --run-id" \
  node "$ci_dir/storage-candidate.mjs" validate --candidate "$candidate" --run-id 999

remote_result="$scratch/storage-remote-result.json"
node - "$candidate" "$remote_result" <<'JS'
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const [candidate, output] = process.argv.slice(2);
const metadata = JSON.parse(fs.readFileSync(path.join(candidate, "storage-metadata.json")));
const digest = (name) => crypto.createHash("sha256").update(fs.readFileSync(path.join(candidate, name))).digest("hex");
fs.writeFileSync(output, `${JSON.stringify({
  schemaVersion: 1,
  kind: "hook2stream-storage-remote-deploy-result",
  environment: "storage-staging",
  result: "success",
  candidateArtifact: metadata.artifactName,
  commitSha: metadata.commitSha,
  storageImagesSha256: digest("storage-images.env"),
  storageBundleSha256: digest("storage-bundle.tar.gz"),
  actualImages: metadata.images,
  checks: [
    "policy-verification",
    "quota-verification",
    "versioning-verification",
    "lifecycle-verification",
    "digest-verification",
  ],
}, null, 2)}\n`);
JS

node "$ci_dir/storage-receipt.mjs" validate-remote \
  --candidate "$candidate" \
  --result "$remote_result" \
  --environment storage-staging

cp "$remote_result" "$scratch/storage-production-result.json"
sed -i 's/"storage-staging"/"storage-production"/' "$scratch/storage-production-result.json"
node "$ci_dir/storage-receipt.mjs" validate-remote \
  --candidate "$candidate" \
  --result "$scratch/storage-production-result.json" \
  --environment storage-production

receipt="$scratch/storage-staging-receipt.json"
node "$ci_dir/storage-receipt.mjs" create \
  --candidate "$candidate" \
  --remote-result "$remote_result" \
  --output "$receipt"
node "$ci_dir/storage-receipt.mjs" validate \
  --candidate "$candidate" \
  --receipt "$receipt" \
  --repository "$repository" \
  --sha "$sha" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt"

ssh-keygen -q -t ed25519 -N '' -f "$scratch/signing-key"
ssh-keygen -Y sign -f "$scratch/signing-key" -n hook2stream-storage-staging-receipt "$receipt" >/dev/null
printf 'hook2stream-storage-staging %s\n' "$(cat "$scratch/signing-key.pub")" > "$scratch/allowed-signers"
ssh-keygen -Y verify \
  -f "$scratch/allowed-signers" \
  -I hook2stream-storage-staging \
  -n hook2stream-storage-staging-receipt \
  -s "$receipt.sig" < "$receipt" >/dev/null

cp "$remote_result" "$scratch/incomplete-remote-result.json"
node - "$scratch/incomplete-remote-result.json" <<'JS'
const fs = require("fs");
const path = process.argv[2];
const value = JSON.parse(fs.readFileSync(path));
value.checks = value.checks.filter((check) => check !== "lifecycle-verification");
fs.writeFileSync(path, `${JSON.stringify(value, null, 2)}\n`);
JS
must_reject "remote storage result does not bind the required verified deployment state" \
  node "$ci_dir/storage-receipt.mjs" validate-remote \
    --candidate "$candidate" \
    --result "$scratch/incomplete-remote-result.json" \
    --environment storage-staging

echo "storage candidate, remote result, and signed receipt contract tests passed"
