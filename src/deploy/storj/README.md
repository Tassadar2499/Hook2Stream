# Storj operator bootstrap

Staging and production use separate Storj Standard/global projects. The four
bucket names, versioning mode, marker schema, and retention thresholds are fixed
by `bootstrap-buckets.sh`; deployed application hosts run only in `VerifyOnly`
mode and must never receive project/root or bootstrap access.

## Irreversible project-creation gate

Do not create either project until the operator records an explicit encryption
model decision for it. Storj does not allow changing the model later:

- `managed` is recommended for this MVP because projects created after
  2025-11-30 cannot use exhaustive S3 listing with Self-Managed encryption.
  H2SE and age still ensure Storj receives only media and backup ciphertext.
- `self-managed` keeps the Storj encryption passphrase solely with the
  operator, but requires a distinct escrowed passphrase per environment and
  live proof that every Hook2Stream list/prefix operation avoids the unsupported
  exhaustive-list path.

The user must approve the recorded choice before project creation. Pass the
same recorded value as `STORJ_ENCRYPTION_MODEL=managed|self-managed` during
bootstrap; it is bound into the signed-by-digest storage marker. Never silently
substitute one model for the other.

For each environment, create one full project access grant and escrow it outside
the VPS and GitHub. For Self-Managed projects, use and escrow that environment's
approved unique passphrase. Derive and register three restricted grants with
`uplink share`:

- media runtime: Read, Write, List, Delete, restricted to its media bucket;
- backup writer: Read, Write, List, `--disallow-deletes`, its backup bucket, and
  `--max-object-ttl 168h` for staging or `840h` for production;
- restore: `--readonly`, restricted to the backup bucket and kept off-host.

Using a locally named root access (so the serialized root grant does not appear
in shell history), the command shapes are:

```sh
uplink share --access hook2stream-staging-root --readonly=false --register \
  sj://hook2stream-com-staging-media/
uplink share --access hook2stream-staging-root --readonly=false \
  --disallow-deletes --max-object-ttl 168h --register \
  sj://hook2stream-com-staging-pg-backups/
uplink share --access hook2stream-staging-root --readonly --register \
  sj://hook2stream-com-staging-pg-backups/
```

Repeat with the production root access, production bucket names, and `840h`.

Create a fourth, temporary full-project S3 credential named
`hook2stream-<environment>-bootstrap-<date>` in the Storj Console. It is
operator-only and exists solely to create/verify the two canonical buckets,
enable backup versioning, and publish the marker. Capture it directly into the
two mode-`0600` bootstrap files used below. After bootstrap and live acceptance,
delete that access in the Storj Console and prove the old credential now receives
an exact permission denial. Never delete the escrowed root grant, because doing
so also revokes all derived runtime roles.

Use `--register` to obtain S3 gateway credentials. Capture its output directly
in the operator's encrypted secret store; do not paste grants, passphrases,
access keys, or secret keys into shell history, GitHub, Terraform state, or CI
logs. Both restricted grants from one environment must descend from the same
escrowed project grant/passphrase; production and staging never share one.

## Trusted operator workstation

Install `python3-venv`, `curl`, `jq`, and the standard GNU tools from root-owned
Ubuntu packages, then install the repository's narrow S3 client:

```sh
sudo /bin/sh src/deploy/storj/install-compatible-s3-client.sh
```

The installer supports Ubuntu 24.04 amd64/Python 3.12, uses exact
`boto3==1.35.99` and `botocore==1.35.99` wheels with `--require-hashes`, and
installs under a content-versioned
`/opt/hook2stream-storj-s3-client-v1-boto3-1.35.99-<digest-prefix>` path.
Operator scripts recursively reject non-root-owned, group/other-writable, or
unexpected-symlink entries across the venv before Python can run, then verify
the client digest and self-check before reading credentials. They invoke Python
with `-I -E -s`, allow only the required S3 operations and four canonical
buckets, and never use an arbitrary AWS CLI from `PATH`.

Every executable and canonical parent directory must be owned by UID 0 and not
writable by group or other users. The scripts replace the caller's `PATH` and
run the S3 client and curl through minimal `env -i` environments. Proxy, custom
CA, AWS-profile, Python, and loader variables cannot reach a credential-bearing
provider process.

Each source credential must be a different absolute, already-canonical path to
a regular non-symlink file. The file must be owned by the current operator, have
mode exactly `0600`, have exactly one hard link, and be at most 4096 bytes. Its
immediate directory must be owned by root or the current operator and must not
be group/other-writable. Keep that directory on the operator's encrypted disk;
do not place credentials in the repository or a shared temporary directory.
All credential files consumed by one invocation must share that directory.

Invoke the scripts by absolute path from a clean environment. The example below
assumes `repository_root` and `secret_root` have already been resolved to
absolute canonical paths. Do not add secrets themselves to the command line:

```sh
/usr/bin/env -i \
  PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
  DEPLOYMENT_ENVIRONMENT=staging \
  STORJ_PROJECT_ID=replace-with-storj-project-id \
  STORJ_ENCRYPTION_MODEL=replace-with-approved-managed-or-self-managed \
  STORJ_S3_ACCESS_KEY_FILE="$secret_root/staging-bootstrap-access-key" \
  STORJ_S3_SECRET_KEY_FILE="$secret_root/staging-bootstrap-secret-key" \
  /bin/sh "$repository_root/src/deploy/storj/bootstrap-buckets.sh"
```

Internally the bootstrap copies the two values into a private, mode-`0600` S3
credentials file under a mode-`0700` one-run directory inside the same encrypted
secret directory, and removes that directory on exit. Plaintext keys are not
exported globally, placed in process arguments, or copied to `/tmp`.

Copy only the printed `STORAGE_CONTRACT_SHA256` into the corresponding root-owned
host environment file. Then run the acceptance tool with the same clean,
absolute-path contract and the restricted media and backup writer files:

```sh
/usr/bin/env -i \
  PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
  DEPLOYMENT_ENVIRONMENT=staging \
  STORAGE_CONTRACT_SHA256=replace-with-64-lowercase-hex-digest \
  MEDIA_S3_ACCESS_KEY_FILE="$secret_root/staging-media-access-key" \
  MEDIA_S3_SECRET_KEY_FILE="$secret_root/staging-media-secret-key" \
  BACKUP_S3_ACCESS_KEY_FILE="$secret_root/staging-backup-access-key" \
  BACKUP_S3_SECRET_KEY_FILE="$secret_root/staging-backup-secret-key" \
  /bin/sh "$repository_root/src/deploy/storj/live-acceptance.sh"
```

The acceptance tool creates separate private AWS credential files for the two
roles, so a media invocation never inherits the backup secret and vice versa.
The backup acceptance object intentionally remains undeletable; verify it expires
after 168/840 hours and record that delayed check in the go-live evidence.

Bootstrap is fail-closed around ambiguous S3 and HTTP failures. A failed
`HeadBucket` triggers creation only for the exact missing-bucket codes
`NoSuchBucket`, `NotFound`, or the S3 HEAD mapping `404`; `AccessDenied`,
`403`, network errors, and 5xx errors stop without `CreateBucket`.
Storj does not implement bucket CORS read/write/delete operations, so bootstrap
does not call them. Browser S3 URLs remain disabled, and live acceptance proves
the marker is private through an unauthenticated exact `403` or `404` response.

Both bootstrap and live acceptance perform the marker privacy GET in curl's
minimal environment, without configuration or any environment proxy; curl uses
`-q --proxy '' --noproxy '*'`. The only accepted anonymous statuses are
exactly `403` or `404`. Redirects, `204`, `200`, network failure, and every other
status block go-live.

Storj's S3 compatibility layer does not support bucket lifecycle configuration.
Temporary `staging/` H2SE data and manifests receive the same absolute 24-hour
object TTL, while the daily media janitor aborts incomplete multipart uploads
older than 24 hours. Backups use single `PutObject` calls, publish their manifest
last, and rely on the access grant's maximum object TTL rather than a delete job.
The July 2026 pricing model still bills objects deleted before 30 days for the
full 30-day minimum and bills objects smaller than 50 kB as 50 kB. TTL remains a
retention/security control, not a way to reduce those minimum charges.
