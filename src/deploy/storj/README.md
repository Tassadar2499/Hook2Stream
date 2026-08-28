# Storj operator bootstrap

Staging and production use separate Storj Standard/global projects. The four
bucket names, versioning mode, marker schema, and retention thresholds are fixed
by `bootstrap-buckets.sh`; deployed application hosts run only in `VerifyOnly`
mode and must never receive the project/root access grant.

For each environment, create one full project access grant with a unique
encryption passphrase and escrow both outside the VPS and GitHub. Derive and
register three restricted grants with `uplink share`:

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

Use `--register` to obtain S3 gateway credentials. Capture its output directly
in the operator's encrypted secret store; do not paste grants, passphrases,
access keys, or secret keys into shell history, GitHub, Terraform state, or CI
logs. Both restricted grants from one environment must descend from the same
escrowed project grant/passphrase; production and staging never share one.

## Trusted operator workstation

Install the AWS CLI, `curl`, `jq`, and the standard GNU tools from a root-owned
OS package (or the root-installed AWS CLI v2 bundle). Every executable and its
canonical parent directories must be owned by UID 0 and not writable by group
or other users. User-local `pip`, `pipx`, Homebrew, npm, shell-function, and
`PATH` shims are ignored; any such tool found inside the fixed system path is
rejected by ownership and mode checks. The scripts replace the caller's
`PATH` with `/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin`, pin
each canonical executable, and run AWS CLI and curl through a minimal `env -i`
environment. Proxy, custom CA, AWS-profile, Python, and loader variables cannot
reach a credential-bearing provider process.

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
  STORJ_S3_ACCESS_KEY_FILE="$secret_root/staging-bootstrap-access-key" \
  STORJ_S3_SECRET_KEY_FILE="$secret_root/staging-bootstrap-secret-key" \
  /bin/sh "$repository_root/src/deploy/storj/bootstrap-buckets.sh"
```

Internally the bootstrap copies the two values into a private, mode-`0600` AWS
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
`NoSuchBucket`, `NotFound`, or the AWS CLI HEAD mapping `404`; `AccessDenied`,
`403`, network errors, and 5xx errors stop without `CreateBucket`.
`GetBucketCors` accepts only `NoSuchCORSConfiguration` as proof that CORS is
absent. A generic `404`, authorization failure, network error, or 5xx response
blocks bootstrap.

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
