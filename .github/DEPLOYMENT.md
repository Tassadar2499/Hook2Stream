# Hook2Stream release workflow setup

The workflows are fail-closed until the GitHub and host controls below exist. No application or infrastructure secret is included in a release candidate.

## Repository controls

Protect `main`: require the `CI` workflow checks, require pull requests and approval, dismiss stale approvals, require conversation resolution, block force pushes and deletion, and do not permit bypass. Configure every deployment Environment (`staging`, `production`, `storage-staging`, and `storage-production`) to allow only the protected `main` branch and no tags. The production environments must require two reviewers and prevent self-review; lack of either the branch restriction or reviewer protection blocks live payments.

Create GitHub Environments named `staging` and `production`. Each environment has its own values for:

- `DEPLOY_HOST` secret: Tailscale DNS name or IP of that environment's host.
- `DEPLOY_SSH_PRIVATE_KEY` secret: environment-specific ED25519 deploy key.
- `DEPLOY_SSH_KNOWN_HOSTS` secret: pinned ED25519 host-key record for `DEPLOY_HOST`.
- `TS_OAUTH_CLIENT_ID` and `TS_AUDIENCE` secrets: Tailscale workload-identity federation values.
- `MIN_ROLLBACK_RELEASE_SHA` variable: full SHA of the release that activates the encrypted-storage rollback floor. Configure the same value in the host wrapper environment.

The staging environment also has `STAGING_RECEIPT_SIGNING_KEY`, a dedicated ED25519 private key used only to sign successful staging receipts. Add the corresponding public key as the repository variable `STAGING_RECEIPT_ALLOWED_SIGNERS`, in OpenSSH allowed-signers format:

```text
hook2stream-staging ssh-ed25519 AAAA...
```

The Tailscale identities need writable `auth_keys` federation and must be restricted by ACL to:

- `tag:hook2stream-ci-staging` to the staging host on TCP 22 only.
- `tag:hook2stream-ci-production` to the production host on TCP 22 only.

Do not configure a Tailscale OAuth secret: these workflows deliberately use GitHub OIDC federation and ephemeral tagged nodes.

## Storage workflow controls

Create two additional protected GitHub Environments: `storage-staging` and
`storage-production`. Keep their deploy keys, host keys, Tailscale workload
identities, receipt keys, and variables distinct from the app environments.
Each storage Environment supplies:

- `STORAGE_DEPLOY_HOST`: the corresponding storage host's Tailscale DNS name;
- `STORAGE_DEPLOY_SSH_PRIVATE_KEY`: an environment-specific ED25519 key;
- `STORAGE_DEPLOY_SSH_KNOWN_HOSTS`: the exact pinned ED25519 record;
- `STORAGE_TS_OAUTH_CLIENT_ID` and `STORAGE_TS_AUDIENCE`:
  workload-identity federation values.

`storage-staging` also has the dedicated
`STORAGE_STAGING_RECEIPT_SIGNING_KEY` secret. Store its public counterpart as
the repository variable `STORAGE_STAGING_RECEIPT_ALLOWED_SIGNERS`, in OpenSSH
allowed-signers format with identity `hook2stream-storage-staging`.
`storage-production` requires the same two-reviewer/no-self-review protection
as app production. The minimum protocol, object format, and MinIO security
sequence plus the last-applied release/source commit are persisted by the
root-owned host wrapper on the encrypted storage mount; they are not mutable
GitHub variables.

The release-independent MinIO approval policy comes only from current protected
`main`. Install the reviewed file at
`/etc/hook2stream-storage/minio-security-policy.json` on each storage host as
root:root mode `0600`, record its source commit and SHA-256, and configure the
exact path in the root-owned deploy configuration. Never install the policy
copy carried by an old candidate. The current policy intentionally has an
empty `approvedSourceReleases` array because the final OSS MinIO release has
four unresolved High advisories; consequently Storage CI and both host deploys
remain fail-closed until a supported storage choice is reviewed.

Tailscale ACLs allow `tag:hook2stream-storage-ci-staging` only to staging
storage TCP 22 and `tag:hook2stream-storage-ci-production` only to production
storage TCP 22. They do not grant CI direct access to MinIO, app hosts,
databases, or the other environment. Storage deployment also uses OIDC and
ephemeral nodes; no reusable Tailscale auth key is stored in GitHub.

Storage candidates and receipts are independent of app candidates. Production
promotion accepts a successful main-branch storage CI run ID, proves the exact
candidate passed storage staging, and streams those same digest-only files to
the storage forced command without rebuilding. Before and after production
approval it reapplies the policy from current protected `main` and rescans all
three exact image digests with the current vulnerability database. A
format-floor or MinIO security-sequence downgrade fails before Docker mutation
and requires a forward fix.

## Host command protocol

`hook2stream-deploy` has no Docker or secrets access. Its `authorized_keys` entry uses `restrict` and a root-owned forced command. The client command is exactly `deploy <candidate-id>` or `rollback <40-character-sha> H2SEv1`.

Deploy receives an uncompressed tar stream on stdin. It contains `candidate/` with the four immutable candidate files; production also contains `approval/staging-receipt.json` and `approval/staging-receipt.sig`. The host validates checksums, metadata schema, repository and image allowlists, archive traversal, staging signature, and the actual running digests. Its final success line is a base64-encoded `hook2stream-remote-deploy-result` JSON record prefixed with `HOOK2STREAM_REMOTE_RECEIPT=`.

Every successful deployment records a root-owned capability document for its release SHA. Rollback accepts only a release recorded as successful on that same host whose capability document explicitly includes `H2SEv1`; the SHA is treated only as an identity and is never compared lexicographically. `MIN_ROLLBACK_RELEASE_SHA` activates this fail-closed floor on both the GitHub Environment and host. Once an H2SE object exists, a target without the H2SE reader capability is ineligible even if it was previously successful.

Rollback is application-image-only. The host starts with the current successful
environment, replaces only `API_IMAGE`, `WORKER_IMAGE`, `WEB_IMAGE` and
`RELEASE_VERSION` from the selected target, and preserves the current
bootstrapper and infrastructure digests. Before mutation it proves that Caddy,
PostgreSQL, PgBouncer, the backup sidecar and all three egress proxies still run
those current digests. It then pulls and recreates only API, every worker and
web with `--no-deps`. It never runs the bootstrapper or migrations and never
pulls/recreates Caddy, PostgreSQL, PgBouncer, backup or egress services.

The target schema must therefore be expand/contract compatible with the
currently migrated database; incompatible rollback remains blocked in favor of
a forward fix or a separately approved write-stop/restore. After health, public
smoke, mandatory authenticated E2E and exact app-plus-infrastructure digest
checks, the host atomically makes the synthesized environment current and emits
`HOOK2STREAM_ROLLBACK_RECEIPT=` with a base64-encoded
`hook2stream-remote-rollback-result`. Its checks are
`target-recorded-success`, `storage-format-compatible`,
`application-images-only`, `infrastructure-unchanged`, `no-migrations`,
`smoke`, `e2e`, and `digest-verification`.
