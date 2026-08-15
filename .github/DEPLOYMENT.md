# Hook2Stream release workflow setup

The workflows are fail-closed until the GitHub and host controls below exist. No application or infrastructure secret is included in a release candidate.

## Repository controls

Protect `main`: require the `CI` workflow checks, require pull requests and approval, dismiss stale approvals, require conversation resolution, block force pushes and deletion, and do not permit bypass. The production environment must require two reviewers and prevent self-review; lack of this protection blocks live payments.

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
