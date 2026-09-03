# Runtime secret files

Create the required extensionless scalar files below the environment's
root-owned encrypted secrets directory, or point `SECRETS_DIR` to it. Deployed
Servers.Guru hosts use only the production-path rows; MinIO bootstrap/root files are
for disposable local/CI profiles and must not exist as production credentials.

| File | Consumer / scope |
|---|---|
| `postgres_password` | PostgreSQL, PgBouncer, applications, backup |
| `s3_runtime_access_key` | API, workers, storage probe and janitor; Storj media bucket only |
| `s3_runtime_secret_key` | matching least-privilege Storj media secret |
| `google_client_secret` | API OAuth client |
| `stripe_secret_key` | staging API Stripe test client; forbidden when `BILLING_MODE=disabled` |
| `stripe_webhook_secret` | staging API test-webhook verification; forbidden when `BILLING_MODE=disabled` |
| `openrouter_api_key` | control worker only |
| `media_keyring` | API/workers; environment-specific H2SE v1 KEK keyring |
| `invited_emails` | API invite allowlist; newline-delimited, `#` comments allowed |
| `backup_s3_access_key` | backup sidecar; matching Storj backup bucket only |
| `backup_s3_secret_key` | no-Delete Storj backup writer secret |
| `backup_age_recipient` | public `age1...` recipient; private recovery identity is off-host |
| `s3_bootstrap_access_key` | local/CI MinIO one-shot bootstrap only |
| `s3_bootstrap_secret_key` | local/CI MinIO bucket/CORS/lifecycle bootstrap only |
| `minio_root_user` | local/CI MinIO server/initializer only |
| `minio_root_password` | local/CI MinIO server/initializer only |

Use one value per file; one trailing newline is allowed. Set `SECRETS_GID` to a
dedicated numeric group (default `2000`), make the directory
`root:<SECRETS_GID>` mode `0750`, and every consumed file
`root:<SECRETS_GID>` mode `0640`. Files must be regular non-symlinks. Compose
preserves host ownership and joins containers to the supplemental group; no
host login or deploy user may join it.

Staging sets `BILLING_MODE=stripe` and requires both Stripe files. Production
sets `BILLING_MODE=disabled`; both Stripe files must be absent, no Stripe price
IDs are configured, and Compose does not declare or mount them.

For a deployed host, create the directory only after LUKS is mounted:

```sh
sudo install -d -o root -g 2000 -m 0750 \
  /srv/hook2stream/secrets/current
```

Write values through a root shell without echoing them to history or logs.
Generate at least 32 random bytes for PostgreSQL/session-class secrets. Supply
externally issued values unchanged. The repository `.dockerignore` excludes
this directory as defense in depth, but deployed values must live outside the
checkout and never be copied into images, candidates, provider state, or
GitHub.

## Storj credentials

Staging and production use separate Storj projects, full project access grants,
and encryption passphrases. For each environment derive and register only:

- media runtime: Read, Write, List, Delete on that environment's fixed media
  bucket;
- backup writer: Read, Write, List, no Delete on its fixed backup bucket, with
  maximum object TTL 168 hours for staging or 840 hours for production;
- restore read-only: backup bucket only, held by the operator off-host.

The reviewed command shapes are:

```sh
uplink share --access <environment-root-grant> \
  --readonly=false --register sj://<environment-media-bucket>/
uplink share --access <environment-root-grant> \
  --readonly=false --disallow-deletes --max-object-ttl <168h-or-840h> \
  --register sj://<environment-backup-bucket>/
uplink share --access <environment-root-grant> \
  --readonly --register sj://<environment-backup-bucket>/
```

Run them only inside an encrypted operator session and capture registered
credentials directly into the encrypted store. The placeholders above are not
real grants; never paste real values into Git, history, logs, provider state, or CI.

Only the registered media and backup S3 access-key/secret-key pairs are written
to the VPS scalar files. Full project grants, encryption passphrases,
bootstrap/root credentials, and restore credentials stay in encrypted operator
escrow outside Servers.Guru, Storj, GitHub, and Vault's host-renderer records.

The media credential also runs the authenticated marker probe and aborts stale
temporary multipart uploads. It does not receive access to the backup bucket.
The backup credential uploads single age-encrypted objects, reads/lists its own
bucket, receives version IDs, and cannot delete. Retention is enforced by the
grant's maximum object TTL rather than S3 lifecycle or a delete job.

## H2SE and backup recovery material

The `media_keyring` is unrelated between staging and production, contains an
active 256-bit KEK plus retained read keys, and fails closed when absent or
invalid. Rotate the active key every 90 days and keep encrypted escrow outside
both Servers.Guru and Storj. Storj receives only H2SE ciphertext.

Generate the age identity on the operator recovery device. Copy only its public
recipient into `backup_age_recipient`; never install the private identity on a
VPS or upload it to Storj. A restore drill temporarily combines the off-host
restore grant, age identity, and escrowed H2SE keyring under explicit operator
control.

## Local/CI MinIO exception

When `STORAGE_MODE=minio`, create distinct random MinIO root and bootstrap
values and use `StorageProvisioningMode=Manage`. The initializer may create the
two disposable buckets and their scoped identities. Applications and the backup
sidecar still use runtime/bootstrap/backup test credentials and never mount the
root secret directly.

The MinIO console stays disabled. Changing a local access-key ID creates a new
test identity, so explicitly remove a retired one after validating the cutover.
None of these root/bootstrap values may be reused for Storj, staging, or
production. Local MinIO is not a deployed backup or durability boundary.

## Deployment E2E credentials

Authenticated release-gate inputs are deliberately outside `SECRETS_DIR`
because no application container consumes them. Store the environment-specific
OAuth cookie jar, expected-email scalar, licensed MP3, and staging soak baseline
below `/srv/hook2stream/e2e` as
`root:root` mode `0400` or `0600`; keep the directory `0700`. The checked-in
`host/authenticated-e2e.sh` rejects symlinks, other owners, looser modes and
files outside this encrypted directory. Never copy these inputs into a release
candidate, GitHub secret/artifact, container mount, provider metadata, command
argument, or deploy log. Refresh the short-lived OAuth cookie jar through an
operator-controlled browser session before it expires.

## Optional Vault rendering

The default MVP may use `SECRET_PROVIDER=file`. The optional one-shot Vault
renderer materializes the same production scalar files; containers remain
unaware of the provider. Access-key IDs are files so an ID/secret pair is
promoted atomically. See [`../vault/README.md`](../vault/README.md). The
OpenRouter value must be a current `sk-or-v1-` inference key whose account/key
guardrail enforces Zero Data Retention; Compose mounts it only into `control`.
