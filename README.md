# Hook2Stream

> **One song. Three weeks of ready-to-post lyric shorts.**

Hook2Stream — SaaS для независимых и AI-музыкантов, который превращает одну песню и небольшой набор визуальных материалов в готовую 21-дневную кампанию коротких вертикальных видео.

## Результат

Один `Release Pack` содержит ровно 18 роликов длительностью 10–30 секунд:

- 12 вариаций трёх музыкальных hooks: припев, эмоциональная строка и инструментальный drop;
- 2 teaser, 2 countdown и 2 out-now ролика;
- синхронизированный с вокалом текст;
- описания, CTA и календарь публикаций;
- единый ZIP для TikTok, YouTube Shorts, Instagram Reels и VK Clips.

Hook2Stream продаёт не генерацию отдельного видео, а готовую контент-кампанию на весь релиз.

## Что загружает артист

Обязательно:

- финальный MP3 или WAV;
- текст песни либо отметку `Instrumental`;
- обложку;
- от 3 до 10 изображений или видео;
- название артиста и трека;
- дату релиза либо дату начала кампании для уже выпущенного трека.

Опционально:

- фирменные цвета и шрифты;
- изображения персонажа или маскота;
- стандартные CTA и ссылки.

Если brand kit не заполнен, безопасные цвета и стили формируются из обложки и остаются редактируемыми.

## Тарифная модель

| Тариф | Цена | Результат |
|---|---:|---|
| Preview | Бесплатно | Один low-resolution ролик с watermark и storyboard остальных вариантов |
| Mini Release | $19 | Любые 6 clean-роликов из кампании |
| Release Pack | $39 | Все 18 clean-роликов, copy, CTA и календарь |
| Active Artist | $29/мес. | Один Release Pack за billing period, brand kit и история релизов |

Generative video backgrounds не входят в MVP. Если они появятся позже, то будут оплачиваться отдельными credits.

## Граница MVP

В MVP входят:

- audio-first upload;
- phrase-level lyrics alignment;
- BPM, song structure и energy analysis;
- три редактируемых hook;
- четыре семейства шаблонов;
- campaign storyboard;
- один бесплатный watermarked preview;
- пакетный render 1080×1920;
- copy, CTA, календарь и ZIP export;
- оплата за Mini Release, Release Pack или подписку.

В MVP не входят:

- собственная text-to-video модель;
- автопубликация в социальные сети;
- Spotify analytics;
- полноценный multi-track видеоредактор;
- рекламные кабинеты;
- команды, роли, white label и публичный API.

## Tech stack

MVP stack: Next.js, React, TypeScript, ASP.NET Core API/Worker, .NET Aspire AppHost + ServiceDefaults, PostgreSQL, S3-compatible storage, Remotion, FFmpeg/ffprobe, Python sidecar with WhisperX and Essentia.

## Документация

- [Продуктовый и технический план](doc/base/Hook2Stream_Product_Plan.md)
- [Технологический стек](doc/base/tech-stack.md)
- [Функциональные требования](doc/func-requirements/README.md)
- [Нефункциональные требования](doc/non-func-requirements/README.md)
- [Архитектура и запуск исходного кода](src/README.md)

## Реализовано

В `src` уже находится первый исполняемый срез self-service MVP:

- англоязычный landing, Clerk onboarding, dashboard, release setup и brand kit;
- ASP.NET Core API с tenant isolation, ETag concurrency, rate limiting и безопасными ошибками;
- PostgreSQL schema и миграция для workspaces, releases, assets, uploads, jobs и audit events;
- прямые single/multipart uploads в S3-compatible storage;
- durable PostgreSQL job queue и worker для FFmpeg/ffprobe media ingest;
- проверка magic bytes, duration, dimensions и создание browser-safe derivatives;
- SSE progress с polling fallback;
- .NET Aspire topology для PostgreSQL, MinIO, bootstrapper, API, worker и Next.js;
- OpenAPI contract и сгенерированные TypeScript-типы;
- unit, API integration, AppHost topology и Playwright smoke tests.

Текущая граница реализации заканчивается на валидированном наборе входных материалов релиза. WhisperX/Essentia analysis, выбор hooks, 18-item campaign, Remotion render, preview, billing и ZIP export остаются следующими инкрементами.

## Быстрый запуск

Требуются .NET SDK 10, Node.js 24, Docker и доступные в `PATH` `ffmpeg`/`ffprobe`.

```bash
dotnet tool restore
npm ci --prefix src/web
dotnet run --project src/Hook2Stream.AppHost
```

Aspire автоматически создаёт PostgreSQL и MinIO, генерирует и сохраняет их локальные credentials в .NET user secrets и повторно использует volumes с именами, привязанными к пути AppHost. Поэтому разные clones/worktrees не делят одну базу или object storage. CORS для прямой browser upload в MinIO задаётся AppHost. Без Clerk-ключей landing запускается, а защищённая часть показывает экран настройки. Для полного auth-flow:

```bash
dotnet user-secrets --project src/Hook2Stream.AppHost set "Clerk:Issuer" "https://your-instance.clerk.accounts.dev"
dotnet user-secrets --project src/Hook2Stream.AppHost set "Clerk:PublishableKey" "pk_test_..."
```

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

Commercial validation идёт параллельно с разработкой: три demo packs на треках NEЯСЫТЬ, первые пилоты по $19 и минимум пять оплат до масштабирования тяжёлого render pipeline.
