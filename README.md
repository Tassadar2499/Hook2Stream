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

## Стадия

Текущая стадия — спецификация и подготовка concierge validation. До полноценной self-service разработки планируется:

1. сделать три демонстрационных Release Pack на треках NEЯСЫТЬ;
2. запустить англоязычный landing;
3. продать минимум пять пилотов по $19;
4. подтвердить, что клиенты публикуют ролики, экономят не менее двух часов и хотят вернуться со следующим релизом.
