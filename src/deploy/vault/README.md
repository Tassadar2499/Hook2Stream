# Optional external Vault renderer

The one-shot host renderer is an optional source for the production scalar
secret contract. Application containers never receive Vault identities.
`vault-renderer` authenticates with AppRole, reads seven exact KV v2 records,
writes versioned candidate JSON files, and exits. The deployment reconciler
validates and atomically promotes those candidates into files consumed by
`compose.yaml`.

Storj full project grants, project encryption passphrases, bootstrap/root S3
credentials, and restore-read grants are deliberately outside this renderer.
Keep them in encrypted operator escrow. The renderer receives only the
production media runtime and no-Delete backup writer S3 pairs.

## External Vault records

Mount the KV v2 engine at `hook2stream-kv` and create:

| API path | Exact fields | Candidate |
|---|---|---|
| `hook2stream-kv/data/production/foundation` | `postgres_password` | `foundation.json` |
| `hook2stream-kv/data/production/runtime-s3` | `access_key_id`, `secret_access_key` | `runtime-s3.json` |
| `hook2stream-kv/data/production/api` | always `google_client_secret`; add `stripe_secret_key`, `stripe_webhook_secret` only for `BILLING_MODE=stripe` | `api.json` |
| `hook2stream-kv/data/production/control` | `openrouter_api_key` | `control.json` |
| `hook2stream-kv/data/production/backup-s3` | `access_key_id`, `secret_access_key` | `backup-s3.json` |
| `hook2stream-kv/data/production/media-security` | `media_keyring`, `invited_emails` | `media-security.json` |
| `hook2stream-kv/data/production/backup-encryption` | `age_recipient` | `backup-encryption.json` |

Every candidate has exactly `kv_version` and `secrets` at its root. Candidate
field names are strict and the reconciler maps them to scalar names documented
in `../secrets/README.md`. The API template receives the explicit
`BILLING_MODE`: disabled production rejects Stripe fields and materializes only
the Google secret, while Stripe staging requires all three API fields.
`backup-s3` accepts only `access_key_id` and
`secret_access_key`; a legacy payload containing `heartbeat_url` or any other
extra field is rejected. There is no `bootstrap-s3` renderer record because
deployed Storj bootstrap credentials remain off-host; local/CI MinIO uses its
own file secrets.

All values must be non-empty strings without NUL, carriage return, or leading
or trailing whitespace. Only `invited_emails` may contain internal LF
characters; use them to separate allowlist entries. `media_keyring` must itself
be a one-line JSON object string, for example
`{"activeKeyId":"k1","keys":{"k1":"<base64-32-byte-KEK>"}}`. The
`age_recipient` record contains only the public `age1...` X25519 recipient.
Private age identities are never installed on the VPS or stored in Vault.

Apply `policies/host-renderer.hcl` to the production host-renderer AppRole. Set
`token_num_uses=0` so Vault Agent auto-auth can complete all template reads.
Store only `role_id` and `secret_id` in `VAULT_AUTH_DIR`; directory mode `0700`
and file mode `0400` are recommended. The read-only mount intentionally retains
the SecretID file, so scope and rotate this bootstrap credential separately.

`VAULT_ADDR` must be an HTTPS origin. `VAULT_CACERT` points to the host CA bundle
mounted as `/vault/tls/ca.pem`; TLS verification is never disabled. Create
`VAULT_CANDIDATE_DIR` as `root:<SECRETS_GID>` mode `0750` before rendering. It is
the renderer's only writable host mount.

Run the billing-disabled one-shot renderer through the base and Vault files:

```sh
docker compose --env-file .env \
  -f compose.yaml -f compose.vault.yaml \
  --profile tools run --rm vault-renderer
```

For `BILLING_MODE=stripe`, include `compose.billing-stripe.yaml` between the
base and Vault files. The deployment helper selects this exact file set and
rejects any environment/mode mismatch.

The Vault Agent image must be pinned by digest. The default identity is root
with GID `SECRETS_GID`, allowing the reconciler to create root-owned,
group-readable scalar files for non-root containers. Change `VAULT_RENDER_UID`
or `VAULT_RENDER_GID` only together with that reviewed ownership contract.
Compose invokes `/bin/vault` directly so the vendor entrypoint cannot mutate the
read-only filesystem or replace the reviewed identity.

## Backup age-recipient rotation

Generate the new age identity on an operator-controlled device and escrow its
private identity outside Vault, the VPS, Storj, and GitHub. Update only the
public `age_recipient` field, then run:

```sh
sudo /opt/hook2stream/rotate-backup-age-recipient.sh
```

The rotation renders a complete candidate generation, refuses unrelated secret
drift, atomically switches `current`, and runs `postgres-backup backup-once`.
The backup manifest and object name carry the new recipient fingerprint, so a
successful proving backup demonstrates that the active public recipient was
used. Only then is the long-running backup daemon recreated. On any proof
failure the prior `current`/`previous` links and daemon are restored. Vault does
not retain passphrases, encryption key IDs, private identities, or backup-key
history.

Routine changes use `rotate-vault-secrets.sh`; PostgreSQL uses
`rotate-postgres-password.sh`. Both scripts refuse backup recipient drift and
direct the operator to the specialized command above.
