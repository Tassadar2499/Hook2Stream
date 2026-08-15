# Production pipeline providers

Deployment and provider operations for the two Hetzner environments are defined
in `docs/operations/hook2stream-mvp-runbook.md`. This directory contains only
application-provider examples; it does not authorize shared staging/production
credentials.

The MVP uses one external AI gateway: OpenRouter. Copy `appsettings.Production.example.json`, inject `OPENROUTER_API_KEY` through the runtime secret store, and never commit the key to an appsettings file. The key must enforce Zero Data Retention; startup and request routing reject a production OpenRouter configuration that cannot satisfy the ZDR policy.

Pinned production routes:

- transcription — `openai/whisper-large-v3` through OpenRouter audio transcription;
- artwork — `bytedance-seed/seedream-4.5` through OpenRouter image generation;
- campaign/copy — `openai/gpt-oss-120b` through OpenRouter chat completions with structured output;
- audio analysis — local deterministic FFmpeg/DSP implementation, with no model weights;
- preview/final video — local deterministic FFmpeg template renderer, with no generative video model.

OpenRouter video generation is intentionally disabled: its current API cannot be used with Zero Data Retention. The worker records hashes, model/provider IDs, generation IDs, usage and safe failure codes, but never logs raw prompts, lyrics, audio/base64, presigned URLs or credentials.

`Fixture` mode is allowed only in development and automated tests. Production should use `OpenRouter` for transcription/artwork/campaign and `Deterministic` for analysis/rendering. The legacy `ExternalProcess` adapter remains a compatibility seam, not the supported MVP deployment path.

The example selects the `control` pool because that is the only pool that loads OpenRouter. Deploy the same worker image separately for `media`, `analysis`, `control`, `render`, and `export`, overriding `Worker__Capabilities__0` with exactly one of those values for each deployment. The old `artwork` and `campaign` capability names are no longer valid; both belong to `control`.
