# External Vault renderer

Production uses one short-lived host renderer rather than giving application
containers Vault identities. `vault-renderer` authenticates to the external
Vault with AppRole, reads seven exact KV v2 records, writes versioned candidate
JSON files, and exits. The deployment reconciler validates and atomically
promotes those candidates into the scalar files consumed by `compose.yaml`.

## External Vault records

The KV v2 engine must be mounted at `hook2stream-kv`. Create these records:

| API path | Required fields | Candidate |
|---|---|---|
| `hook2stream-kv/data/production/foundation` | `postgres_password` | `foundation.json` |
| `hook2stream-kv/data/production/runtime-s3` | `access_key_id`, `secret_access_key` | `runtime-s3.json` |
| `hook2stream-kv/data/production/bootstrap-s3` | `access_key_id`, `secret_access_key` | `bootstrap-s3.json` |
| `hook2stream-kv/data/production/api` | `google_client_secret`, `stripe_secret_key`, `stripe_webhook_secret` | `api.json` |
| `hook2stream-kv/data/production/control` | `openrouter_api_key` | `control.json` |
| `hook2stream-kv/data/production/backup-s3` | `access_key_id`, `secret_access_key`, `heartbeat_url` | `backup-s3.json` |
| `hook2stream-kv/data/production/backup-encryption/current` | `key_id`, `passphrase` | `backup-encryption.json` |

Every candidate has exactly `kv_version` and `secrets` at its root. Candidate
field names are the Vault schema; the reconciler maps them to the scalar names
documented in `../secrets/README.md`.
Keep `heartbeat_url` present as a string; use an empty string when the optional
backup success heartbeat is disabled.

Apply `policies/host-renderer.hcl` to one production host-renderer AppRole. Set
its token use count to unlimited (`token_num_uses=0`) so Vault Agent auto-auth
can complete all template reads. Store only `role_id` and `secret_id` in
`VAULT_AUTH_DIR`; mode `0700` on the directory and `0400` on both files is
recommended. The Agent intentionally keeps the SecretID file because its bind
mount is read-only. Scope and rotate that bootstrap credential independently.

`VAULT_ADDR` must use HTTPS. `VAULT_CACERT` points to the host CA bundle, which
is mounted as `/vault/tls/ca.pem`; TLS verification is never disabled. Create
`VAULT_CANDIDATE_DIR` before rendering as `root:<SECRETS_GID>` with mode `0750`.
It is the only writable host mount in the renderer.

Run the one-shot renderer through both Compose files:

```sh
docker compose --env-file .env \
  -f compose.yaml -f compose.vault.yaml \
  --profile tools run --rm vault-renderer
```

The Vault Agent image must be pinned by digest. The default renderer UID is root
and its GID matches `SECRETS_GID`, because the reconciler creates root-owned,
group-readable scalar files for the non-root runtime containers. Change
`VAULT_RENDER_UID` or `VAULT_RENDER_GID` only together with that reviewed
ownership contract.
Compose invokes `/bin/vault` directly so the vendor image entrypoint cannot run
`setcap` on the read-only filesystem or replace the reviewed `0:2000` identity.

## Backup encryption history

Before changing `backup-encryption/current`, create an immutable copy at
`hook2stream-kv/data/production/backup-encryption/keys/<key_id>` with KV CAS set
to zero, then update `current` using its observed KV version as the CAS value.
The writer policy grants only `create` on historical key-ID paths, so Vault also
rejects attempts to overwrite an existing archive record.
The host renderer cannot read historical keys. Grant
`policies/backup-restore.hcl` only to the break-glass restore identity, and grant
`policies/backup-encryption-writer.hcl` only to the controlled rotation writer.
