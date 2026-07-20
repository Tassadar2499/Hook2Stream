# 06. Billing и operations

Источник: [Product Plan, §§ 7–11](../base/Hook2Stream_Product_Plan.md).

Billing продаёт понятные результаты: preview, clean cover, ровно 6-item Mini, ровно 18-item Pack и пакет из пяти artwork generations. Внутренние compute units используются для защиты маржи и telemetry, но не заменяют пользовательское описание продукта.

## BIL — products, payment и usage

| ID | Требование | Проверка |
|---|---|---|
| `FR-BIL-001` | Launch catalog должен содержать versioned products: `Preview $0`, `Clean Cover $2`, `Mini Release $5`, `Release Pack $9.90`, `Active Artist $29/month` и `5 Artwork Generations $1`; checkout показывает цену, exact entitlement и тип оплаты. | Catalog API/UI возвращает шесть products; изменение цены создаёт новую version и не меняет завершённую purchase. |
| `FR-BIL-002` | Preview allowance должен разрешать один успешно завершённый low-resolution watermarked full-video preview на project и storyboard/posters остальных items, но не clean download. | Бесплатный пользователь получает один preview URL; clean render/export отклоняется; retries до первого success не делают второго consume. |
| `FR-BIL-003` | `Mini Release $5` привязывается к одному project и разрешает ровно шесть выбранных campaign item IDs; selection и campaign snapshot фиксируются при создании checkout. | До checkout пользователь меняет selection; после оплаты delayed webhook не может перенести entitlement на новую campaign; manifest сохраняет ровно шесть IDs. |
| `FR-BIL-004` | `Release Pack $9.90` привязывается к одному project и разрешает ровно все 18 items выбранной plan revision, включая copy и calendar. | Успешная one-time purchase активирует entitlement только для указанного project и exactly 18 items. |
| `FR-BIL-005` | Active Artist должен выдавать один Release Pack entitlement на billing period, сохранять brand kit/history, расходоваться при запуске первого paid render выбранного project и не переноситься в следующий period. | В новом period доступен один entitlement; consumed entitlement не используется второй раз; unused entitlement истекает на boundary без rollover. |
| `FR-BIL-006` | Stripe-hosted Checkout должен явно различать `payment` и `subscription`, связывать session с workspace/project/product и не активировать entitlement только по browser redirect. | Cancelled checkout ничего не активирует; success page до webhook показывает pending; подписка требует recurring consent. |
| `FR-BIL-007` | Stripe webhooks должны проверять signature на raw body, обрабатываться через unique inbox и поддерживать paid, failed, refunded, cancelled и subscription-period events. Refund закрывает будущие signed URLs/неиспользованные allowances, но не обещает отозвать скачанные bytes; audit сохраняется. | Duplicate event не создаёт второй entitlement; invalid signature отклоняется; после refund новый URL не выдаётся, исторический audit доступен. |
| `FR-BIL-008` | Система должна вести immutable entitlement/usage/art-credit ledgers с `reserve`, `consume`, `release` и `refund`. User credit расходуется один раз на canonical artwork operation; неизвестный outcome внешнего provider не списывается повторно автоматически. | Worker failure освобождает reserve; одинаковый idempotency key не меняет balance дважды; ledger reconciles с provider usage. |
| `FR-BIL-009` | Каждый project включает initial artwork generation и две regeneration. Пакет `$1` добавляет пять workspace generations; одна generation создаёт три cover candidates и связанный batch из трёх backgrounds после approval. | Первые три generations бесплатны; четвёртая резервирует одну из пяти купленных; technical retries/manual composition не меняют balance. |
| `FR-BIL-010` | Каждый купленный campaign item должен включать один user-initiated content rerender; исправление подтверждённого технического дефекта всегда бесплатно, дальнейшая content revision требует нового allowance/entitlement. | Первый rerender проходит без доплаты и атомарно consumes allowance; второй блокируется до покупки; technical correction не расходует content allowance. |
| `FR-BIL-011` | Checkout/entitlement должны сохранять immutable campaign, MP3 fingerprint, artist/title, release mode и schedule anchor. Metadata/audio/campaign changes после checkout не меняют оплаченный output; истечение monthly period запрещает новый render, но не скрывает готовую history, пока entitlement не refunded/revoked. | Delayed payment после replacement рендерит купленные campaign/audio snapshots и исходный calendar; expired Active Artist history выдаёт новые short-lived URLs, refund — нет. |

## OPS — jobs, безопасность и эксплуатация

| ID | Требование | Проверка |
|---|---|---|
| `FR-OPS-001` | Ingest, analysis, transcription, artwork, campaign, preview, final render, export и cleanup должны выполняться durable jobs с attempts, required capability и lease fencing token. | Worker leases только поддерживаемый capability; поздний attempt со старым token не может записать success; restart безопасно повторяет stage. |
| `FR-OPS-002` | Каждая дорогая/внешняя операция должна иметь API idempotency key и canonical input fingerprint. Бизнес-состояние и enqueue связываются transactional outbox; duplicate delivery не создаёт второй artifact, plan или consume. | Параллельные delivery/commands завершаются одним canonical result; crash после commit восстанавливается dispatcher/reconciler. |
| `FR-OPS-003` | UI должен получать project-level ordered progress через SSE с `Last-Event-ID` либо polling snapshot, показывать возможность закрыть страницу и конкретное следующее действие. | Disconnect/reconnect не теряет event и не откатывает terminal lane; reload восстанавливает lanes/blockers без локального wizard state. |
| `FR-OPS-004` | Project и batch operations должны сохранять безопасный progress: artwork correction/retry остаётся одной business operation и публикует review только после полного набора из трёх; final renders допускают partial success. | Moderation correction не расходует новую user generation; incomplete artwork batch не попадает в campaign; ошибка render item оставляет остальные ready и предлагает targeted retry. |
| `FR-OPS-005` | Support tooling должно позволять по workspace/project/job ID видеть безопасный diagnostic context и выполнять разрешённые retry, refund или manual entitlement adjustment с обязательным audit. | Admin action сохраняет actor, reason, before/after и timestamp; сотрудник без разрешения не получает media/download access. |
| `FR-OPS-006` | Система должна применять versioned `CFG-RETENTION-*`: logical delete немедленно блокирует доступ, physical cleanup удаляет originals, derived assets, renders и bundles в установленный срок. | После delete новые signed URLs не выдаются; cleanup retry идемпотентен; completion фиксируется без удаления обязательного billing/audit record. |
| `FR-OPS-007` | Media processing должен использовать server-generated paths, allowlisted arguments/templates/fonts, resource limits и process timeouts; пользовательский ввод не должен попадать в shell interpolation. | Malicious filename/text не изменяет command structure или object path; timeout завершает process и переводит attempt в безопасный failure. |
| `FR-OPS-008` | Система должна записывать cost telemetry по project и job: source seconds, analysis compute, rendered seconds, storage/delivery, LLM usage, retries и variable support adjustment. | Support/admin может получить cost breakdown одного Pack; пользовательские цены не пересчитываются задним числом по telemetry. |
| `FR-OPS-009` | Risky или незавершённые capabilities должны управляться server-side feature flags по environment/workspace без публикации недоступной навигации. | Disabled capability не вызывается прямым API request; UI не показывает пустой раздел; flag change auditируется. |
| `FR-OPS-010` | MVP не должен создавать social connections, publication jobs, platform performance sync, team roles, white-label portal или public API; их отсутствие не должно мешать ZIP export. | End-to-end сценарий завершается без platform credentials; API surface и navigation не обещают исключённые capabilities. |

## Billing allocation rules

- Entitlement активируется только подтверждённым billing event или разрешённой admin operation.
- One-time purchase не создаёт recurring subscription.
- Monthly entitlement закрепляется за project при первом paid render, а не при создании draft/storyboard.
- Failed technical attempt не расходует второй Pack.
- Каждый purchased item получает ровно один включённый content rerender; UI показывает остаток до запуска.
- Исправление platform-independent технического дефекта output выполняется без повторной покупки.

## Operational guardrails

- Production SLA не зависит от домашнего компьютера или локального AI worker.
- PostgreSQL является source of truth для business/job state.
- Production workers разделены по capabilities `media`, `analysis`, `control`, `render`, `export`; недоступность OpenRouter не мешает API/control-plane и сохранённому ручному review.
- OpenRouter adapters и deterministic media providers возвращают staged artifacts/manifests, но только .NET валидирует и переводит их в canonical revisions.
- Object storage lifecycle не заменяет logical authorization checks.
- Secrets, payment payloads и presigned URLs не попадают в export manifest или пользовательские logs.
- Social publishing может рассматриваться только после подтверждённого repeat use export-first продукта.
