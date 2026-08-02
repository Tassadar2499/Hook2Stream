# Hook2Stream source

`src` содержит исполняемый MP3-first срез Hook2Stream: от регистрации и загрузки одного MP3 до revision-based review транскрипта, artwork, hooks и 18-item campaign. В локальном профиле внешние AI/media providers заменяются детерминированными fixtures.

## Topology

```mermaid
flowchart LR
    WEB["Next.js web"] --> API["ASP.NET Core API"]
    API --> DB["PostgreSQL"]
    WEB --> S3["MinIO / S3 upload"]
    API --> S3
    API --> JOBS["PostgreSQL durable jobs + outbox"]
    JOBS --> CONTROL["Control worker"]
    JOBS --> MEDIA["Media / FFmpeg"]
    JOBS --> ANALYSIS["Deterministic DSP analysis"]
    JOBS --> RENDER["Deterministic FFmpeg render"]
    CONTROL --> AI["OpenRouter: STT / image / campaign"]
    MEDIA --> S3
    ANALYSIS --> S3
    RENDER --> S3
    BOOT["Bootstrapper"] --> DB
    BOOT --> S3
    ASPIRE["Aspire AppHost"] --> WEB
    ASPIRE --> API
    ASPIRE --> WORKER
    ASPIRE --> BOOT
```

## Projects

| Project | Responsibility |
|---|---|
| `Hook2Stream.Domain` | Entities, immutable revisions, workflow lanes and domain enums |
| `Hook2Stream.Application` | API/provider contracts, validation, media policy and ports |
| `Hook2Stream.Infrastructure` | EF Core/PostgreSQL, S3, capability-routed queue, FFmpeg and fixture/external providers |
| `Hook2Stream.Api` | Authenticated `/api/v1`, MP3 quick upload, workflow/review, assets, jobs and SSE |
| `Hook2Stream.Worker` | Leased background execution, outbox dispatch and capability handlers |
| `Hook2Stream.Bootstrapper` | Database migrations and object-storage bucket/CORS bootstrap |
| `Hook2Stream.ServiceDefaults` | OpenTelemetry, service discovery and health endpoints |
| `Hook2Stream.AppHost` | Local Aspire topology: PostgreSQL, MinIO, backend and web |
| `web` | Next.js landing and authenticated release setup |
| `tests` | Unit, API integration and AppHost topology tests |

## Local development

```bash
dotnet tool restore
npm ci --prefix src/web
dotnet run --project src/Hook2Stream.AppHost
```

The AppHost generates and persists local PostgreSQL and MinIO passwords in .NET user secrets, then reuses path-scoped data volumes across restarts. The volume names include the AppHost path identity, so parallel clones and worktrees do not share local state. MinIO CORS for direct browser uploads is configured by the container environment; the storage adapter can apply bucket CORS for compatible S3 providers.

When both Google OAuth settings are absent, the Development AppHost automatically supplies a fixed local user and a per-run bearer token to the API and web app. The local scheme only accepts loopback requests and is rejected outside Development and Testing. Configure both settings to exercise the real Google sign-in flow:

```bash
dotnet user-secrets --project src/Hook2Stream.AppHost set "Google:ClientId" "your-google-client-id.apps.googleusercontent.com"
dotnet user-secrets --project src/Hook2Stream.AppHost set "Google:ClientSecret" "your-google-client-secret"
```

A partial Google configuration fails fast. Running the Next.js app by itself does not enable local authentication.

### Local OpenRouter smoke

The Development profile uses fixtures by default. The dedicated transcription and image endpoints do not support per-request ZDR routing. Before enabling them, configure OpenRouter **Settings > Privacy** or a guardrail assigned to the inference key so ZDR is enforced for both OpenAI and non-frontier model groups. Keep **Private Input & Output Logging** and **OpenRouter Use of Inputs/Outputs** disabled as well. `AccountOrGuardrailZdrEnforced` is a local fail-fast acknowledgement of the remote ZDR policy; it does not change the OpenRouter account.

Keep the plaintext inference key (`sk-or-v1-` followed by 64 hexadecimal characters) in the Worker user-secrets store and switch only the AI stages:

```bash
read -rs hook2stream_openrouter_key
dotnet user-secrets set --project src/Hook2Stream.Worker "OpenRouter:ApiKey" "$hook2stream_openrouter_key"
unset hook2stream_openrouter_key
dotnet user-secrets set --project src/Hook2Stream.Worker "OpenRouter:AccountOrGuardrailZdrEnforced" "true"
dotnet user-secrets set --project src/Hook2Stream.Worker "PipelineProviders:AudioAnalysis:Mode" "Deterministic"
dotnet user-secrets set --project src/Hook2Stream.Worker "PipelineProviders:Transcription:Mode" "OpenRouter"
dotnet user-secrets set --project src/Hook2Stream.Worker "PipelineProviders:Artwork:Mode" "OpenRouter"
dotnet user-secrets set --project src/Hook2Stream.Worker "PipelineProviders:CampaignPlanning:Mode" "OpenRouter"
dotnet user-secrets set --project src/Hook2Stream.Worker "PipelineProviders:VideoRendering:Mode" "Deterministic"
```

Restart AppHost after changing secrets, upload a short MP3 you are allowed to process, and confirm the external-AI rights gate in the UI. The pinned defaults are `openai/whisper-large-v3`, `bytedance-seed/seedream-4.5`, and `openai/gpt-oss-120b`. The campaign adapter additionally enforces ZDR, denies provider data collection, and requires declared parameter support per request; the dedicated endpoints rely on the account or key guardrail asserted above. Do not put the API key in `appsettings*.json` or commit it.

Keep each generated storage password paired with its volume. PostgreSQL only applies `POSTGRES_PASSWORD` while initializing an empty data directory, so changing or deleting `Parameters:postgres-password` while retaining its volume prevents the dependency gate from becoming ready. Legacy installs can remove the unused `hook2stream-postgres-data` and `hook2stream-minio-data` volumes after stopping AppHost; the next run creates clean path-scoped replacements.

The media worker expects `ffmpeg` and `ffprobe` in `PATH`. Originals are never rewritten; derivatives and provider staging objects use versioned keys. Default development configuration uses deterministic fixture providers. Production uses separate capability pools without local neural models:

- `media` — FFmpeg/ffprobe ingest and normalization;
- `analysis` — deterministic FFmpeg/DSP beat, section and energy extraction;
- `control` — pipeline reconciliation and OpenRouter transcription, artwork and campaign planning;
- `render` — deterministic FFmpeg template rendering;
- `export` — validation and immutable ZIP assembly.

Workers lease only matching capabilities. A lease token fences late attempts; only .NET commits canonical business state after validating a sidecar manifest.

Production images use the repository root as their build context so `Directory.Build.props` is applied, for example `docker build -f src/Hook2Stream.Api/Dockerfile .`. The Dockerfiles pin an available .NET 10 container SDK independently of the workstation-only `global.json`. Separate API, bootstrapper, worker and standalone Next.js Dockerfiles run as non-root users; deploy the same worker image once per capability pool.

The production web image uses same-origin `/api/*` requests by default, so one
immutable image can move between environments without rebuilding it for a
hostname. Set `NEXT_PUBLIC_API_BASE_URL` only for split-origin deployments;
`NEXT_PUBLIC_AUTH_MODE` remains a build-time choice.

Set exactly one `Worker__Capabilities__0` value per worker deployment (`media`, `analysis`, `control`, `render`, or `export`). The bootstrapper owns bucket setup; when `Storage__ConfigureBucketCors=true`, provide every public browser origin through `Storage__BrowserUploadOrigins__N` (HTTPS is required in Production). Providers that do not support the S3 `AbortIncompleteMultipartUpload` lifecycle action must set `Storage__ConfigureMultipartAbortLifecycle=false`; staging-object expiration remains controlled independently by `Storage__ConfigureBucketLifecycle`.

## MP3-first API flow

1. `POST /api/v1/releases/audio-uploads` with `Idempotency-Key`, rights confirmation and external-AI consent creates an `Unscheduled` project, bound attestation, audio asset and direct-upload session atomically.
2. Existing upload completion starts ingest through the outbox. Deterministic analysis and OpenRouter transcription begin once the audio master is ready and consent remains current.
3. `PUT /api/v1/releases/{id}/setup` confirms metadata; the existing rights endpoint confirms rights before artwork generation.
4. `/transcript`, `/artwork`, `/hooks` and `/campaign` expose immutable revisions. Mutations require current `If-Match`; asynchronous generation commands also require `Idempotency-Key`.
5. `GET /api/v1/releases/{id}/workflow` is the reload-safe aggregate of lane progress, blockers and next action. `GET /api/v1/releases/{id}/events` streams ordered project events, resumes from `Last-Event-ID` (or `?after=`), and uses workflow polling as fallback; job-level SSE remains available for focused diagnostics.

Automatic transcription has a supported quality baseline for `en` and `ru`. WAV, prepared lyrics, user cover and custom visuals remain optional advanced sources; they are not required by the main MP3 flow.

## Contracts

The API contract is generated explicitly so ordinary builds never rewrite tracked files. Regenerate the OpenAPI document and checked-in frontend schema after an API contract change:

```bash
src/ci/generate-contracts.sh
```

`web/src/lib/api-schema.d.ts` is generated and must not be edited manually. CI reruns the generator and fails when either checked-in artifact drifts.

## Verification

```bash
src/ci/verify.sh
npm run test:e2e --prefix src/web
```

## Production deployment

The provider-neutral, budget single-node deployment bundle lives in
[`deploy/`](deploy/README.md). It includes Caddy TLS termination, PgBouncer,
isolated worker pools, encrypted off-host PostgreSQL backups, Docker secrets,
health checks, resource limits, and an ordered release/rollback runbook. It is
intended for a closed alpha; the runbook identifies the managed PostgreSQL and
multi-instance upgrades required for paid beta. Backup manifests bind every
encrypted recovery point to an immutable key ID; historical passphrases remain
available for the 35-day recovery window plus a 14-day restore margin.

## Implementation boundary

The repository contains the MP3-first domain/API/UI foundation, revision invalidation, provider ports, durable orchestration, OpenRouter adapters and deterministic media processors. A fixture can prove contracts and the exact 18-item recipe, but is never selected as a production AI provider.

The repository includes deterministic analysis, clean FFmpeg render and validated ZIP assembly. Production readiness still requires an OpenRouter key configured for Zero Data Retention, a real-provider staging smoke for the pinned models, deployed Stripe Checkout/catalog/webhooks, infrastructure configuration, golden-media evaluation and the staged rollout described in the product requirements. No local neural-model image or sidecar is required.
