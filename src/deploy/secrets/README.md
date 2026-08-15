# Runtime secret files

Create the following extensionless files here, or point `SECRETS_DIR` at an
equivalent root-owned directory outside the checkout:

| File | Consumer |
|---|---|
| `postgres_password` | PostgreSQL, PgBouncer, applications, backup |
| `s3_runtime_access_key` | API and workers; media access-key ID |
| `s3_runtime_secret_key` | API and workers; least-privilege media access |
| `s3_bootstrap_access_key` | one-shot bootstrap access-key ID |
| `s3_bootstrap_secret_key` | one-shot bucket/CORS/lifecycle bootstrap |
| `google_client_secret` | API OAuth client |
| `stripe_secret_key` | API Stripe client |
| `stripe_webhook_secret` | API webhook verification |
| `openrouter_api_key` | control worker only |
| `media_keyring` | API and workers; H2SE v1 KEK keyring, JSON, fail-closed |
| `invited_emails` | API invite allowlist; newline-delimited emails, `#` comments allowed |
| `backup_s3_access_key` | PostgreSQL backup access-key ID |
| `backup_s3_secret_key` | PostgreSQL backup bucket only |
| `backup_age_recipient` | public `age1...` recipient whose private recovery key is off-host |
| `backup_heartbeat_url` | PostgreSQL backup success monitor; may be empty to disable |
| `minio_root_user` | MinIO server and one-shot initializer only; required when `STORAGE_MODE=minio` |
| `minio_root_password` | MinIO server and one-shot initializer only; required when `STORAGE_MODE=minio` |

Use one value per file; a trailing newline is allowed. Set `SECRETS_GID` to a
dedicated numeric group (the default is `2000`), make the directory
`root:<SECRETS_GID>` mode `0750`, and make every file `root:<SECRETS_GID>` mode
`0640`. Compose file-backed secrets retain host ownership rather than applying
the long-syntax `uid`, `gid`, or `mode`, so the containers join this supplemental
group explicitly. No host login user should be a member of it.

For the default alpha path:

```sh
sudo install -d -o root -g 2000 -m 0750 /srv/hook2stream/secrets/current
sudo install -o root -g 2000 -m 0640 /dev/null \
  /srv/hook2stream/secrets/current/backup_heartbeat_url
```

Create the other files with the same ownership and mode, then write their values
through a root shell without echoing them to terminal history. Generate local
values with a cryptographic RNG: use at least 32 random bytes for the PostgreSQL
password. Generate the age identity on the operator recovery
device, copy only its public recipient to the host, and keep the private identity
outside the VPS and Object Storage.
Supply externally issued provider secrets unchanged. Never commit or copy these
files into an image.

When `STORAGE_MODE=minio`, generate a distinct random root username and at
least 32 random bytes for `minio_root_password`. The initializer uses the root
identity only to create the two buckets and their scoped service identities.
Applications and the PostgreSQL backup sidecar continue to use the existing
runtime/bootstrap/backup S3 files and never mount either MinIO root secret.
The MinIO console is disabled and the root identity must not be used from a
browser or stored in `.env`.
Re-running the initializer updates a secret key when its access-key ID stays
the same. Changing an access-key ID creates a new MinIO user but cannot safely
infer which old ID should be revoked; after a credential cutover, verify the
new identity and explicitly remove the retired user with the administrative
client.
The repository-root `.dockerignore` excludes this entire directory from every
application build context; keep secret files outside the checkout in production
and treat that exclusion as defense in depth.

The closed alpha sets `SECRET_PROVIDER=file` and creates all scalar files
directly. The optional Vault renderer can materialize the same contract later;
containers remain unaware of the provider. Access-key IDs are files as well so
an ID/secret pair is promoted and rolled back atomically.

Set `backup_heartbeat_url` to the secret HTTPS heartbeat URL issued by the
external monitor. Leave the file empty to disable delivery. The URL is never
logged, and a monitoring outage does not turn a completed backup into a failed
backup.

The OpenRouter key must be a current `sk-or-v1-` inference key and its account or
key guardrail must enforce Zero Data Retention. The compose configuration only
mounts this key into the `control` worker.

For AWS-style S3 policies, scope every credential to its named bucket/prefix:

- runtime media: bucket listing plus object read, write, delete, and multipart
  completion/abort operations;
- media bootstrap: runtime access plus bucket creation (when the bucket does not
  already exist), CORS configuration, and lifecycle configuration;
- PostgreSQL backup: object upload, version listing, and permanent version/delete
  marker deletion under `BACKUP_S3_PREFIX`.

Use a separate break-glass read credential for restore drills. Do not give the
running backup sidecar account-wide bucket administration permissions.
