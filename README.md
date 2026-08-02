# Hook2Stream

> **One song. Three weeks of ready-to-post lyric shorts.**

Hook2Stream — SaaS для независимых и AI-музыкантов, который превращает один MP3 в готовую 21-дневную кампанию коротких вертикальных видео. Транскрипт, обложка, визуальный набор и storyboard создаются автоматически, а пользователь подтверждает и при необходимости редактирует результат.

## Результат

Один `Release Pack` содержит ровно 18 роликов длительностью 10–30 секунд:

- 12 вариаций трёх музыкальных hooks: припев, эмоциональная строка и инструментальный drop;
- 2 teaser, 2 countdown и 2 out-now ролика;
- синхронизированный с вокалом текст;
- описания, CTA и календарь публикаций;
- единый ZIP для TikTok, YouTube Shorts, Instagram Reels и VK Clips.

Hook2Stream продаёт не генерацию отдельного видео, а готовую контент-кампанию на весь релиз.

## Что загружает артист

Для основного сценария нужен только финальный MP3. Перед upload пользователь подтверждает права и разрешает обработку через OpenRouter в режиме Zero Data Retention. После загрузки Hook2Stream запускает media ingest, детерминированный музыкальный анализ и RU/EN-транскрибацию через OpenRouter; перед campaign generation пользователь проверяет актуальные транскрипт и обложку.

Все прежние источники остаются расширенными overrides:

- WAV вместо MP3;
- готовый lyrics-текст или отметка `Instrumental`;
- собственная обложка;
- собственные изображения и короткие видео;
- фирменные цвета, шрифты, персонаж, CTA и ссылки.

По умолчанию система предлагает через OpenRouter три AI-варианта обложки и после выбора создаёт три согласованных вертикальных фона. Artist/title наносятся детерминированно и остаются редактируемыми. Видео собирается локальным FFmpeg renderer из утверждённых assets и template spec: OpenRouter video generation в MVP не используется, потому что она несовместима со строгим ZDR.

## Тарифная модель

| Тариф | Цена | Результат |
|---|---:|---|
| Preview | Бесплатно | Один low-resolution ролик с watermark и storyboard остальных вариантов |
| Clean Cover | $2 | Утверждённая clean-обложка 3000×3000 |
| Mini Release | $5 | Ровно 6 выбранных clean-роликов |
| Release Pack | $9.90 | Ровно 18 clean-роликов, copy, CTA и календарь |
| Active Artist | $29/мес. | Один Release Pack за billing period без rollover, brand kit и история релизов |
| Artwork Generations | $1 | Пять дополнительных полных artwork generations |

В проект включены initial artwork operation и две полные regeneration. Пакет за $1 добавляет пять workspace generations. Одна generation создаёт новый batch из трёх cover-кандидатов и, после утверждения обложки, трёх согласованных backgrounds; технический retry не расходует generation.

## Граница MVP

В MVP входят:

- MP3-first quick upload и опциональные advanced overrides;
- автоматическая RU/EN-транскрибация и phrase-level ручная проверка;
- BPM, song structure и energy analysis;
- три AI cover-кандидата, контролируемый artwork editor и три backgrounds;
- три редактируемых hook;
- четыре семейства шаблонов;
- campaign storyboard;
- один бесплатный watermarked preview;
- пакетный render 1080×1920;
- copy, CTA, календарь и ZIP export;
- отдельные покупки clean cover, Mini Release, Release Pack, artwork generations или подписка.

В MVP не входят:

- собственная или внешняя text-to-video генерация;
- автопубликация в социальные сети;
- Spotify analytics;
- полноценный multi-track видеоредактор;
- рекламные кабинеты;
- команды, роли, white label и публичный API.

## Tech stack

MVP stack: Next.js, React, TypeScript, ASP.NET Core API/workers, .NET Aspire, PostgreSQL, S3-compatible storage, OpenRouter API, Stripe Checkout и FFmpeg/ffprobe. В production-пути нет локальных neural-model weights и прямых интеграций с отдельными AI-провайдерами.

## Документация

- [Продуктовый и технический план](doc/base/Hook2Stream_Product_Plan.md)
- [Технологический стек](doc/base/tech-stack.md)
- [Функциональные требования](doc/func-requirements/README.md)
- [Нефункциональные требования](doc/non-func-requirements/README.md)
- [Архитектура и запуск исходного кода](src/README.md)

## Реализовано

В `src` уже находится первый исполняемый срез self-service MVP:

- англоязычный landing, Google onboarding, dashboard, release setup и brand kit;
- ASP.NET Core API с tenant isolation, ETag concurrency, rate limiting и безопасными ошибками;
- PostgreSQL schema и additive migration для workspaces, releases, revisioned workflow, assets, uploads, jobs, outbox/inbox и audit events;
- прямые single/multipart uploads в S3-compatible storage;
- MP3-first quick-upload API, workflow lanes, ETag/idempotency gates и editors для transcript/artwork/hooks/campaign;
- durable PostgreSQL job queue с capability routing, lease fencing и workers для FFmpeg/ffprobe ingest и fixture/external providers;
- проверка magic bytes, duration, dimensions и создание browser-safe derivatives;
- SSE progress с polling fallback;
- .NET Aspire topology для PostgreSQL, MinIO, bootstrapper, API, worker и Next.js;
- OpenAPI contract и сгенерированные TypeScript-типы;
- unit, API integration, AppHost topology и Playwright smoke tests.

Текущий срез реализует MP3-first orchestration, revision contracts, UI review surfaces, OpenRouter adapters для transcription/artwork/campaign, детерминированный audio analysis и FFmpeg clean render, а также валидируемую ZIP assembly. Production требует `OPENROUTER_API_KEY` со строгим ZDR, Stripe catalog/webhooks, infrastructure configuration и staging/golden-media validation. Локальные fixture providers используются только в development/tests и никогда не подменяют production AI-результат. Deployment bundle также содержит изолированный single-user MinIO-профиль для временного VPS staging; он не заменяет внешний S3 в публичном или production-контуре.

## Быстрый запуск

Требуются .NET SDK 10, Node.js 24, Docker и доступные в `PATH` `ffmpeg`/`ffprobe`.

```bash
dotnet tool restore
npm ci --prefix src/web
./scripts/run.sh
```

Локальный профиль использует fixtures и не требует AI key. Для production worker задайте `OPENROUTER_API_KEY` через secret store и включите режимы из `deploy/providers/appsettings.Production.example.json`; ключ должен быть настроен на Zero Data Retention.

Скрипт сохраняет HTTPS для Aspire, если локальный .NET development certificate доверен. Если сертификат не доверен, скрипт автоматически включает unsecured transport только для локального запуска Aspire; явно заданный `ASPIRE_ALLOW_UNSECURED_TRANSPORT` имеет приоритет. Чтобы использовать HTTPS, доверьте сертификат командой `dotnet dev-certs https --trust`.

Aspire автоматически создаёт PostgreSQL и MinIO, генерирует и сохраняет их локальные credentials в .NET user secrets и повторно использует volumes с именами, привязанными к пути AppHost. Поэтому разные clones/worktrees не делят одну базу или object storage. CORS для прямой browser upload в MinIO задаётся AppHost.

При запуске через AppHost без `Google:ClientId`/`Google:ClientSecret` автоматически включается фиксированный локальный пользователь. Режим доступен только в Development, принимает запросы только через loopback и использует новый случайный bearer token при каждом запуске. Workspace и данные сохраняются между запусками. Чтобы проверить настоящий Google OAuth flow, задайте оба ключа:

```bash
dotnet user-secrets --project src/Hook2Stream.AppHost set "Google:ClientId" "your-google-client-id.apps.googleusercontent.com"
dotnet user-secrets --project src/Hook2Stream.AppHost set "Google:ClientSecret" "your-google-client-secret"
```

Если задан только один из Google-ключей, AppHost завершит запуск с подсказкой исправить конфигурацию. Отдельный `npm run dev --prefix src/web` без AppHost не включает Local Auth и показывает экран настройки.

Не удаляйте и не меняйте `Parameters:postgres-password` или `Parameters:minio-password`, сохраняя соответствующие volumes: PostgreSQL применяет пароль только при первой инициализации хранилища. Если использовалась ранняя версия AppHost с глобальными volumes, остановите AppHost и удалите устаревшие `hook2stream-postgres-data` и `hook2stream-minio-data`; при следующем запуске Aspire создаст чистые path-scoped volumes.

Основные проверки:

```bash
dotnet test src/Hook2Stream.slnx
npm run check --prefix src/web
npm run lint --prefix src/web
npm run build --prefix src/web
npm run test:e2e --prefix src/web
```

После изменения API сначала пересоберите его, затем обновите frontend contract:

```bash
dotnet build src/Hook2Stream.Api/Hook2Stream.Api.csproj
npm run generate:api --prefix src/web
```

Commercial validation идёт параллельно с разработкой: три demo packs на треках NEЯСЫТЬ, первые self-service Mini pilots по $5 и минимум пять оплат до масштабирования тяжёлого render pipeline.
