# Функциональные требования Hook2Stream

> Статус: спецификация MVP
> Версия: 2.0
> Дата: 16 июля 2026 года
> Основной источник: [Hook2Stream Product Plan](../base/Hook2Stream_Product_Plan.md)

## 1. Назначение

Комплект описывает наблюдаемое поведение первого self-service продукта:

```text
one song
→ analysis and three hooks
→ 18-item campaign
→ one free preview
→ paid render
→ ZIP and calendar
```

Нормативный scope ограничен MVP. Автопубликация, social analytics, команды, white label, generative video и публичный API упоминаются только как исключения и не создают обязательств реализации.

Общее количество требований: **97**.

## 2. Нормативные соглашения

### 2.1. Идентификаторы

Требование имеет стабильный ID `FR-<DOMAIN>-NNN`.

| Код | Область |
|---|---|
| `ACC` | Аккаунт, landing и персональный workspace |
| `BRD` | Brand kit |
| `REL` | Release project |
| `AST` | Media assets |
| `LYR` | Lyrics и phrase timing |
| `ANL` | Music analysis и hooks |
| `CAM` | Campaign plan, copy и calendar |
| `REN` | Preview и render |
| `EXP` | Entitled export |
| `BIL` | Products, payment и usage |
| `OPS` | Jobs, безопасность и эксплуатация |

Термины `должна`, `не должна`, `ровно`, `только` и `обязательно` нормативны.

### 2.2. Acceptance

В таблицах профильных документов:

- `Требование` задаёт обязательное поведение;
- `Проверка` задаёт минимальный acceptance scenario;
- значения `CFG-*` означают deployment configuration, которую нужно закрыть до beta.

Если требование меняет данные, проверка должна включать повторное чтение сохранённого результата. Если действие дорогое или асинхронное, проверка также должна учитывать retry/idempotency.

## 3. Акторы

| Актор | Назначение |
|---|---|
| Посетитель | Просматривает landing и начинает создание release pack |
| Пользователь | Управляет своим brand kit, релизами, оплатой и exports |
| Платёжный провайдер | Создаёт checkout и передаёт подписанные billing events |
| Analysis worker | Выполняет normalization, WhisperX и Essentia analysis |
| Render worker | Выполняет Remotion/FFmpeg render и validation |
| Сотрудник поддержки | Диагностирует jobs и выполняет разрешённые retry/refund |
| Система | Проверяет права, состояния, entitlements и инварианты |

В MVP один пользователь является единственным владельцем персонального workspace. Командных ролей нет.

## 4. Карта документов

| Документ | Диапазоны | Количество |
|---|---|---:|
| [Аккаунт и brand kit](01-account-and-brand-kit.md) | `FR-ACC-001..006`, `FR-BRD-001..006` | 12 |
| [Release и assets](02-release-and-assets.md) | `FR-REL-001..007`, `FR-AST-001..008` | 15 |
| [Lyrics, analysis и hooks](03-lyrics-analysis-and-hooks.md) | `FR-LYR-001..007`, `FR-ANL-001..011` | 18 |
| [Campaign generation](04-campaign-generation.md) | `FR-CAM-001..017` | 17 |
| [Review, render и export](05-review-render-and-export.md) | `FR-REN-001..008`, `FR-EXP-001..008` | 16 |
| [Billing и operations](06-billing-and-operations.md) | `FR-BIL-001..009`, `FR-OPS-001..010` | 19 |

## 5. Сквозной сценарий

```mermaid
flowchart LR
    A["Landing and signup"] --> B["Release project"]
    B --> C["Audio, lyrics, cover, 3–10 visuals"]
    C --> D["Alignment and song analysis"]
    D --> E["Approve three hooks"]
    E --> F["18-item campaign storyboard"]
    F --> G["One watermarked preview"]
    G --> H["Mini / Release / subscription entitlement"]
    H --> I["Paid batch render"]
    I --> J["ZIP, copy, CSV, ICS, manifest"]
```

## 6. Общие состояния

### 6.1. Release project

```text
Draft
→ Analyzing
→ HookReview
→ CampaignReady
→ PreviewReady
→ Rendering
→ Ready | PartiallyReady
→ Archived
```

- `Draft` — обязательные входы ещё не подтверждены.
- `Analyzing` — существует активный ingest/alignment/analysis job.
- `HookReview` — доступны три hook suggestions или допустимые fallbacks.
- `CampaignReady` — сохранён versioned plan из 18 items.
- `PreviewReady` — доступен один валидированный watermarked preview.
- `Rendering` — создаются paid outputs.
- `Ready` — готовы все items, разрешённые entitlement.
- `PartiallyReady` — часть разрешённых items готова, часть завершилась ошибкой или ожидает retry.
- `Archived` — пользователь исключил проект из активного списка; данные не считаются удалёнными.

Логическое удаление является отдельным lifecycle-признаком и немедленно закрывает доступ независимо от состояния.

### 6.2. Job

```text
Queued → Running → Succeeded
Queued → Cancelled
Running → Failed | Cancelled
```

Retry создаёт новую attempt одной логической операции. Idempotency key запрещает второй расход entitlement, duplicate output или повторное billing consumption.

### 6.3. Campaign item

```text
Planned → Previewable → RenderQueued → Rendering → Ready | Failed
```

Изменение content controls создаёт новую revision item. Готовый `RenderVersion` остаётся immutable.

## 7. Глобальные инварианты

1. Валидный `CampaignPlan` содержит ровно 18 items.
2. В плане присутствуют ровно три утверждённых hooks.
3. Каждый hook имеет четыре composition variants: kinetic, animated cover и два visual loops.
4. План содержит два teaser, два countdown и два out-now items; в режиме `Released` два countdown заменяются post-release variants.
5. Каждый ролик длится от 10 до 30 секунд включительно.
6. Upcoming calendar использует восемь pre-release, два release-day и восемь post-release slots внутри окна `-10..+10`.
7. Бесплатно рендерится один полный low-resolution preview с watermark.
8. Mini Release разрешает ровно шесть явно выбранных clean outputs.
9. Release Pack и Active Artist entitlement разрешают все 18 clean outputs.
10. Paid output не содержит сервисный watermark.
11. Один MP4 не дублируется в ZIP по числу платформ.
12. Instrumental mode не создаёт вымышленные lyrics.
13. Ошибка одного item не удаляет успешные items.
14. Social publication не является частью MVP и не блокирует export.

## 8. Канонический export bundle

```text
hook2stream-{artist}-{track}/
├── videos/
│   └── 01-...mp4
├── copy/
│   ├── campaign.csv
│   └── campaign.txt
├── calendar/
│   ├── calendar.csv
│   └── calendar.ics
└── manifest.json
```

`manifest.json` содержит product, project revision, campaign plan version, выбранные item IDs, render hashes, filenames и timestamps. Он не содержит secrets, presigned URLs или внутренние provider credentials.

## 9. Открытые конфигурационные решения

| ID | Решение | Требуемо |
|---|---|---|
| `DEC-001` | `CFG-AUDIO-MAX-BYTES`, `CFG-AUDIO-MAX-DURATION` и точные codec limits | До upload beta |
| `DEC-002` | Максимальный размер, duration и resolution visual asset | До upload beta |
| `DEC-003` | Поддерживаемый allowlist fonts и правила лицензирования | До первого paid render |
| `DEC-004` | FPS, bitrate, loudness target и GOP final output | До golden corpus validation |
| `DEC-005` | Payment provider, налоги/MoR и billing grace | До checkout beta |
| `DEC-006` | Retention original, proxy, preview, render и export | До публичных Terms |
| `DEC-007` | Download URL TTL и число повторных скачиваний | До paid beta |
| `DEC-008` | Low-confidence thresholds WhisperX/Essentia | До hook quality benchmark |
| `DEC-009` | Поддерживаемые языки copy generation | До public landing |
| `DEC-010` | Канал support и полномочия admin операций | До первых внешних пользователей |
| `DEC-011` | Число и правила user-initiated paid re-renders после успешного output | До paid beta |

Открытое значение не разрешает его игнорировать: до закрытия используется явно заданная environment configuration и тестируются обе стороны границы.

## 10. Граница функциональных требований

Отдельными нефункциональными спецификациями должны быть зафиксированы:

- P50/P95 processing time;
- availability и recovery targets;
- resource isolation;
- encryption и secret management;
- backup/restore;
- accessibility;
- browser support;
- production topology.

Технологические решения перечислены в [tech stack](../base/tech-stack.md), но библиотека или provider не заменяет наблюдаемое требование.

## 11. Правила сопровождения

- Новое поведение добавляется в профильный документ и карту этого README.
- ID не переиспользуются для другого смысла.
- Изменение фиксированного recipe, тарифного entitlement или schedule требует новой версии product plan и обновления acceptance tests.
- Template, analysis и composition contracts версионируются.
- Тесты и ADR ссылаются на FR-ID.
- Future roadmap не добавляется в MVP как скрытое обязательство.
