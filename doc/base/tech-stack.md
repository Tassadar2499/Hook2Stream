# Технологический стек ClipForge

Источник: `doc/base/AI_Content_Factory_SaaS_Plan.md`, раздел 9.1.

| Категория | Технология | Роль |
|---|---|---|
| Frontend | Next.js | Веб-интерфейс |
| Backend API | ASP.NET Core | REST API, бизнес-логика |
| Worker | ASP.NET Core Worker | Фоновые задачи (рендер, анализ, публикация) |
| Архитектура | Модульный монолит | API и Worker в одном solution |
| Локальная разработка / оркестрация | .NET Aspire (AppHost + ServiceDefaults) | Оркестрация API, Worker и зависимостей (PostgreSQL, S3/R2); ServiceDefaults: OpenTelemetry, health checks, resilience; dev dashboard. Redis-ресурс — позже, после MVP |
| База данных | PostgreSQL | Source of truth, доменные сущности |
| Очередь задач | Durable PostgreSQL-backed job queue | Персистентные фоновые задачи |
| Object storage | S3-compatible (Cloudflare R2) | Хранение медиа и экспортов |
| Media processing | FFmpeg, ffprobe | Proxy, рендер, валидация, waveform |
| Realtime / progress | SSE или SignalR | Прогресс обработки |
| Auth | Managed auth + OAuth (YouTube, TikTok, VK OAuth 2.1) | Регистрация, вход, социальные интеграции |
| AI / Analysis | Adapters: transcription (ASR/Whisper), LLM, platform APIs | Распознавание текста, скоринг, публикация |
| Observability | OpenTelemetry, structured logs, error tracking | Мониторинг и трейсинг (через Aspire ServiceDefaults) |
| Тестирование | Unit, integration (PostgreSQL/S3), Playwright E2E | Покрытие доменной логики и сценариев |
| UI / Design | Geist или Inter; тёмная тема | Визуальная система |
| Инфраструктура | CI/CD, migrations, secrets, dev/stage/prod, health checks; .NET Aspire — local dev orchestration | Развертывание и эксплуатация |

## Опционально / позже

| Технология | Когда |
|---|---|
| Redis | Cache, locks, realtime — после MVP; подключается как Aspire-ресурс |
| Kubernetes | Только после измеренной необходимости масштабирования |
| Smart reframe | V1, после Core MVP |
