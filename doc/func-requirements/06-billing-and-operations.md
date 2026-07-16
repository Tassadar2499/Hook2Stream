# 06. Billing и operations

Источник: [Product Plan, §§ 7–11](../base/Hook2Stream_Product_Plan.md).

Billing продаёт понятный результат: preview, 6-item Mini или 18-item Pack. Внутренние compute units используются для защиты маржи и telemetry, но не заменяют пользовательское описание продукта.

## BIL — products, payment и usage

| ID | Требование | Проверка |
|---|---|---|
| `FR-BIL-001` | Launch catalog должен содержать versioned products: `Preview $0`, `Mini Release $19 one-time`, `Release Pack $39 one-time` и `Active Artist $29/month`; checkout должен показывать актуальную цену и тип оплаты. | Catalog API/UI возвращает четыре products; изменение цены создаёт новую catalog version и не меняет завершённую purchase. |
| `FR-BIL-002` | Preview entitlement должен разрешать один low-resolution watermarked full-video preview для текущей campaign plan revision и storyboard остальных items, но не clean download. | Бесплатный пользователь получает один preview URL; clean render/export отклоняется; повторная команда не создаёт второй usage charge. |
| `FR-BIL-003` | Mini Release purchase должна быть привязана к одному project и разрешать ровно шесть выбранных campaign item IDs; selection фиксируется при запуске первого paid render. | До render пользователь меняет selection; после начала operation manifest сохраняет шесть IDs, а седьмой clean item не разрешается. |
| `FR-BIL-004` | Release Pack purchase должна быть привязана к одному project и разрешать все 18 items выбранной plan revision, включая copy и calendar. | Успешная one-time purchase активирует entitlement только для указанного project; другой project не может использовать его ID. |
| `FR-BIL-005` | Active Artist должен выдавать один Release Pack entitlement на billing period, сохранять brand kit/history, расходоваться при запуске первого paid render выбранного project и не переноситься в следующий period. | В новом period доступен один entitlement; consumed entitlement не используется второй раз; unused entitlement истекает на boundary без rollover. |
| `FR-BIL-006` | Hosted checkout должен явно различать one-time и recurring products, связывать session с user/workspace/project/product и не активировать entitlement только по browser redirect. | Cancelled checkout ничего не активирует; success page до webhook показывает pending; подписка требует явного recurring label/consent. |
| `FR-BIL-007` | Billing webhooks должны проверять signature, обрабатываться идемпотентно и поддерживать paid, failed, refunded, cancelled и subscription period events с `CFG-BILLING-GRACE`. | Duplicate event не создаёт второй purchase/entitlement; invalid signature отклоняется; refund обновляет доступ по зафиксированной policy и audit. |
| `FR-BIL-008` | Система должна вести immutable usage ledger с `reserve`, `consume`, `release` и `refund` для analysis, preview, paid render и export operations. | Worker failure освобождает неиспользованный reserve; successful operation consumes фактическое usage; одинаковый idempotency key не изменяет balance дважды. |
| `FR-BIL-009` | Generative video backgrounds должны быть недоступны в MVP catalog; при будущем включении они обязаны использовать отдельный credit product/ledger и не расходовать базовый Pack незаметно. | MVP API/UI не предлагает generative background; feature flag без отдельного entitlement не разрешает generation operation. |

## OPS — jobs, безопасность и эксплуатация

| ID | Требование | Проверка |
|---|---|---|
| `FR-OPS-001` | Analysis, preview, render, export и cleanup должны выполняться durable jobs со состояниями `Queued`, `Running`, `Succeeded`, `Failed`, `Cancelled` и историей attempts. | Restart process не теряет logical job; terminal state и timestamps доступны API/UI; retry создаёт новую attempt. |
| `FR-OPS-002` | Каждая дорогая или внешне наблюдаемая операция должна иметь idempotency key, а duplicate delivery не должна создавать второй plan, output, bundle, purchase consumption или usage transaction. | Параллельная доставка одинаковой команды завершается одним canonical result; loser получает ссылку на него. |
| `FR-OPS-003` | UI должен получать stage/progress через SSE или polling fallback, показывать, что страницу можно закрыть, и давать конкретное следующее действие при recoverable failure. | Disconnect/reconnect восстанавливает актуальный state; progress не откатывается из terminal state в running. |
| `FR-OPS-004` | Project и batch operations должны сохранять partial success: готовые hooks/items/renders/exports не удаляются из-за независимого failure. | Искусственная ошибка одного render оставляет остальные ready/downloadable и предлагает targeted retry. |
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
- User-initiated content revision после успешного paid render может требовать новый render allowance по `CFG-PAID-RERENDER-*`; UI обязан показать применяемое правило до запуска.
- Исправление platform-independent технического дефекта output выполняется без повторной покупки.

## Operational guardrails

- Production SLA не зависит от домашнего GPU worker.
- PostgreSQL является source of truth для business/job state.
- Object storage lifecycle не заменяет logical authorization checks.
- Secrets, payment payloads и presigned URLs не попадают в export manifest или пользовательские logs.
- Social publishing может рассматриваться только после подтверждённого repeat use export-first продукта.
