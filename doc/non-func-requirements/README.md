# Нефункциональные требования Hook2Stream

> Статус: нормативная спецификация MVP
> Версия: 2.0
> Дата: 20 июля 2026 года
> Основные источники: [Hook2Stream Product Plan](../base/Hook2Stream_Product_Plan.md), [функциональные требования](../func-requirements/README.md), [технологический стек](../base/tech-stack.md)

## 1. Назначение

Документ задаёт измеримые свойства self-service MVP Hook2Stream:

- производительность и capacity;
- доступность и устойчивость media pipeline;
- сохранность, recovery и сроки хранения данных;
- безопасность и privacy;
- качество итоговых media;
- observability и эксплуатационную поддержку;
- accessibility и client compatibility;
- сопровождаемость, delivery и экономические guardrails.

Требования являются внутренними целями платной beta, а не договорным SLA. Технология или provider сами по себе не считаются выполнением требования: выполнение подтверждается наблюдаемым результатом и проверкой.

Общее количество требований: **64**.

## 2. Нормативные соглашения

### 2.1. Идентификаторы

Требование имеет стабильный ID `NFR-<DOMAIN>-NNN`.

| Код | Область |
|---|---|
| `PERF` | Производительность и capacity |
| `REL` | Availability, reliability и production topology |
| `DATA` | Durability, backup, recovery и retention |
| `SEC` | Security и privacy |
| `MEDIA` | Качество preview и final media |
| `OBS` | Observability и supportability |
| `UX` | Accessibility, responsive UI и browser support |
| `ENG` | Maintainability, delivery и cost control |

Термины `должна`, `не должна`, `не более`, `не менее` и `обязательно` нормативны. Значения `P50`, `P75`, `P95` и `P99` вычисляются по завершённым операциям за скользящее 30-дневное окно, если в требовании не указан другой период.

### 2.2. Среды и конфигурация

- Требования применяются к production paid beta.
- Staging должна воспроизводить contracts, security controls и topology production, но может иметь меньшую capacity.
- Значения `CFG-*` являются versioned deployment configuration.
- Environment может ужесточить target, но не может ослабить его без изменения версии этой спецификации.
- Synthetic, load и recovery tests не включаются в продуктовые percentile, если они явно маркированы как test traffic.

### 2.3. Reference workload

Processing и capacity targets измеряются на валидном reference project:

- один поддерживаемый MP3 длительностью 3–5 минут;
- automatic RU/EN transcript либо подтверждённый `Instrumental`;
- три AI cover candidates, одна approved cover и три generated backgrounds;
- без обязательных пользовательских visual assets;
- три hooks, один preview и Release Pack из 18 роликов;
- без text-to-video и неподдерживаемых codecs.

Reference load длится не менее 30 минут и включает одновременно:

- 100 активных web sessions;
- 20 прямых uploads в object storage;
- три analysis workflows;
- два paid Release Pack workflows.

## 3. PERF — производительность и capacity

| ID | Требование | Проверка |
|---|---|---|
| `NFR-PERF-001` | Синхронные control-plane API requests должны иметь P95 не более 500 ms и P99 не более 1.5 s. Время передачи media bytes и подтверждённая задержка внешнего auth/payment provider не включаются, но orchestration overhead Hook2Stream включается. | Нагрузочный тест основных metadata, project, campaign, job-status и entitlement endpoints под reference load подтверждает percentile и error rate. |
| `NFR-PERF-002` | Public landing, dashboard и Create Release должны на P75 реальных поддерживаемых клиентов соблюдать Core Web Vitals: LCP не более 2.5 s, INP не более 200 ms и CLS не более 0.1. | RUM dashboard и Lighthouse CI для mobile/desktop profiles не превышают targets; regression блокирует release до documented exception. |
| `NFR-PERF-003` | Суммарное machine-processing время MP3-first project от `upload complete` до storyboard должно иметь P50 не более 10 минут и P95 не более 20 минут; интервалы `WaitingUser` для metadata/rights/transcript/cover review исключаются. | Synthetic project автоматически удовлетворяет gates, включает queue/provider time и отдельно публикует wall-clock и paused user time. |
| `NFR-PERF-004` | Один watermarked preview для готового campaign plan должен завершаться с P95 не более 10 минут от принятия preview command. | Preview operation под reference load завершается `Ready` в target; cache hit и новый render измеряются раздельно. |
| `NFR-PERF-005` | Полный Release Pack должен завершаться с P50 не более 30 минут и P95 не более 60 минут от подтверждения entitlement до готового валидного ZIP. | End-to-end operation включает queue, 18 renders, validation и bundle assembly; invalid input и user cancellation исключаются с зафиксированной причиной. |
| `NFR-PERF-006` | Изменение project lane/job state должно появляться в UI не позднее 5 секунд после commit; после reconnect через `Last-Event-ID` либо workflow polling актуальный snapshot восстанавливается не позднее 10 секунд. | Project SSE и polling-fallback tests разрывают соединение, меняют state и измеряют время до согласованного UI. |
| `NFR-PERF-007` | Reference load не должен нарушать `NFR-PERF-001..006`; доля HTTP 5xx/timeout по причине capacity должна быть менее 1%. | Повторяемый 30-минутный load test публикует latency, queue age, saturation и error report для каждого release candidate. |

## 4. REL — availability, reliability и production topology

`Control plane` включает public landing, авторизованный web/API, создание и просмотр jobs, запуск checkout и выдачу signed download URL. Завершение media jobs измеряется отдельными processing SLO.

| ID | Требование | Проверка |
|---|---|---|
| `NFR-REL-001` | Месячная доступность control plane должна быть не ниже 99.5%. Доступность означает успешный synthetic сценарий login/session check → projects read → job status read. | External probe выполняет сценарий не реже одного раза в минуту; monthly report хранит raw checks, exclusions и итоговый процент. |
| `NFR-REL-002` | Плановые работы могут исключаться из availability только при уведомлении не менее чем за 24 часа и суммарной длительности не более 2 часов в месяц. Подтверждённые outages внешних providers учитываются отдельно как dependency incidents. | Incident report классифицирует каждое окно; незаявленная или превышенная maintenance считается downtime Hook2Stream. |
| `NFR-REL-003` | Доля успешно валидированных paid render outputs после разрешённых automatic retries должна быть не ниже 97% за скользящие 30 дней при выборке не менее 100 outputs. | Dashboard считает только валидные inputs; при меньшей выборке показывает абсолютное число failures и не скрывает показатель. |
| `NFR-REL-004` | Потерянный worker lease должен быть обнаружен, а retryable operation возвращена в очередь не позднее 5 минут; fencing token должен запрещать поздний canonical commit прежней attempt. | Chaos test завершает media/analysis/artwork/render worker и затем доставляет stale result: logical job восстанавливается, stale result отклоняется. |
| `NFR-REL-005` | Restart, duplicate delivery и concurrent retry не должны создавать повторный entitlement consumption, usage transaction, render object, export bundle или billing side effect. | Fault-injection test параллельно доставляет одну idempotency key и подтверждает один canonical result и один расход. |
| `NFR-REL-006` | Ошибка одного campaign item или необязательного adapter не должна удалять независимые успешные результаты; готовые outputs должны оставаться доступными, а retry — быть targeted. | Batch test вызывает failure одного render и одного copy adapter, затем проверяет partial state, downloads и повтор только failed scope. |
| `NFR-REL-007` | Production paid beta должна использовать single-region topology с минимум двумя control-plane instances, managed PostgreSQL с PITR и S3-compatible storage. `media/analysis`, `control/OpenRouter`, `render` и `export` capabilities развёртываются изолированными pools. | Worker не leases неподдерживаемый job; отключение OpenRouter/render pool не нарушает API/control readiness и сохранённые user approvals. |
| `NFR-REL-008` | Production capacity и SLO не должны зависеть от домашнего компьютера, домашнего GPU или вручную запущенного worker. Kubernetes не является обязательным условием. | Отключение всех developer machines не меняет readiness production; inventory перечисляет только управляемые production resources, а локальные neural models отсутствуют. |

## 5. DATA — durability, backup, recovery и retention

| ID | Требование | Проверка |
|---|---|---|
| `NFR-DATA-001` | PostgreSQL должен быть единственным source of truth для business state, job state, entitlements и manifests; наличие object не должно само по себе предоставлять доступ или менять state. | Удалённый/чужой DB record закрывает выдачу URL даже при существующем object; orphan scan обнаруживает bytes без canonical record. |
| `NFR-DATA-002` | Originals, generated artwork, derived assets, renders и bundles должны иметь content hash и проверяться после upload/provider staging/render/copy/restore. Sidecar manifest не меняет business state до проверки. | Corruption/dimension/magic mismatch в staging отклоняется; только валидированный artifact продвигается в immutable canonical key и `Ready`. |
| `NFR-DATA-003` | Для business metadata и job state должны выполняться RPO не более 1 часа и RTO не более 4 часов. | Recovery drill восстанавливает последнюю допустимую точку, запускает API/workers и подтверждает state/entitlements в пределах targets. |
| `NFR-DATA-004` | PostgreSQL backups/PITR и используемый object-storage protection mechanism должны быть зашифрованы и обеспечивать 35-дневное recovery window; отсутствие свежей recovery point более 2 часов считается incident. | Backup monitor проверяет freshness ежедневно и после deploy; выборочная копия восстанавливается с checksum verification. |
| `NFR-DATA-005` | Полный restore drill должен выполняться не реже одного раза в месяц и перед migration с массовым data rewrite или сменой storage/provider. | Runbook сохраняет дату, recovery point, фактические RPO/RTO, найденные дефекты и владельца corrective action. |
| `NFR-DATA-006` | Provider staging удаляется через 24 часа; superseded/unselected artwork — через 30 дней. Неоплаченный project/assets/free preview удаляются после 30 дней неактивности; paid originals/proxies/preview — через 90 дней, paid finals/manifests/bundles — через 365 дней. | Time-shift tests проверяют boundaries; current approved cover не удаляется как unselected; UI/Terms показывают необратимый cleanup. |
| `NFR-DATA-007` | User deletion должна немедленно блокировать доступ, удалить primary copies не позднее 7 дней и сделать данные невосстановимыми из rolling backups не позднее 35 дней. Restore process обязан повторно применить deletion tombstones до открытия доступа. | Delete/restore drill подтверждает отсутствие новых signed URLs, cleanup primary storage и повторное применение tombstones к восстановленной БД. |
| `NFR-DATA-008` | Billing, tax и обязательный audit metadata должны храниться отдельно по опубликованной legal policy и не должны содержать media bytes, lyrics, secrets или presigned URLs. | Data inventory и sampled records подтверждают purpose, retention owner и отсутствие запрещённых полей. |

## 6. SEC — security и privacy

| ID | Требование | Проверка |
|---|---|---|
| `NFR-SEC-001` | Весь внешний traffic должен использовать TLS 1.2 или выше; HTTP перенаправляется на HTTPS, production включает HSTS и secure transport к managed dependencies. | TLS scanner и integration test отклоняют insecure protocol, mixed content и plaintext service connection. |
| `NFR-SEC-002` | PostgreSQL, object storage, backups и persistent job data должны быть зашифрованы at rest provider-managed или customer-managed keys. | Infrastructure audit подтверждает encryption flags и запрещает создание незашифрованного production resource policy-as-code check. |
| `NFR-SEC-003` | Secrets OpenRouter/Stripe/storage и других providers должны поступать только из approved runtime secret store и не попадать в repository, images, logs, traces, browser или export. Long-lived credentials ротируются не реже одного раза в 90 дней. | Secret scanning и rotation drill меняют credential без rebuild/потери jobs; browser bundle и manifests не содержат provider key. |
| `NFR-SEC-004` | Authorization должна проверять workspace ownership для каждого project, asset, job, entitlement и export и не раскрывать существование чужого ресурса. | Обязательный cross-workspace suite перебирает read/write/download operations и получает безопасный одинаковый отказ без metadata leakage. |
| `NFR-SEC-005` | Media/analysis/render workers должны работать non-root в изолированном container context с read-only base filesystem, temporary workdir, resource/time limits и deny-by-default egress. Только control adapters имеют allowlisted OpenRouter egress; media/render не имеют внешнего AI egress. | Sandbox test блокирует запись/host/egress вне allowlist; process/provider failure не раскрывает media URL и не нарушает control plane. |
| `NFR-SEC-006` | Media должна проверяться по MIME, magic bytes, container, codec, duration и dimensions до processing. User input не должен попадать в shell interpolation, executable path, arbitrary template или font loading. | Malicious filenames, traversal, polyglot files, malformed containers и command payloads не меняют process arguments или object keys. |
| `NFR-SEC-007` | Browser auth/session должна использовать `Secure`, `HttpOnly` и подходящий `SameSite` policy либо эквивалентную managed-auth защиту; state-changing requests защищаются от CSRF, credential stuffing и brute force rate limits. | Security integration tests проверяют cookie/token flags, CSRF rejection, logout invalidation и throttling повторных auth attempts. |
| `NFR-SEC-008` | Object keys должны генерироваться сервером. Presigned upload URL действует не более 60 минут, download URL — не более 15 минут и выдаётся только после повторной authorization check. | Clock-controlled tests подтверждают expiry, scope по exact object/method и невозможность получить новый URL после delete/revocation. |
| `NFR-SEC-009` | Hook2Stream не должен принимать или хранить card data; checkout размещается у payment provider, а billing webhooks требуют signature, timestamp tolerance и idempotency. | Architecture/data-flow review не находит card fields; invalid, replayed и duplicate webhook fixtures безопасно отклоняются или переиспользуют canonical event. |
| `NFR-SEC-010` | Release candidate не должен содержать известных Critical или High vulnerabilities в runtime dependencies/images без documented risk acceptance. Critical устраняется или изолируется не позднее 24 часов, High — не позднее 7 дней. | CI выполняет NuGet/npm/pip/container scanning; exception содержит owner, compensating control и expiry. |
| `NFR-SEC-011` | Security baseline должна покрывать OWASP ASVS Level 1 для всего приложения и применимые Level 2 controls для authentication, authorization, cryptography, file processing и stored data. | Перед paid beta и каждым major release заполняется traceable ASVS checklist со ссылками на tests, configuration и принятые exceptions. |
| `NFR-SEC-012` | Все AI-вызовы MVP должны идти только через OpenRouter endpoints, совместимые с Zero Data Retention, с `data_collection=deny` и required-parameter routing. До и после вызова worker повторно проверяет consent, audio binding и fingerprint; raw prompts, lyrics и base64 запрещены в logs/audit. | Production startup отклоняет unsafe config; revoked-consent race test не сохраняет provider output; egress audit не находит direct-provider или generative-video calls. |

## 7. MEDIA — качество preview и final media

| ID | Требование | Проверка |
|---|---|---|
| `NFR-MEDIA-001` | Paid final video должен использовать 1080×1920, constant 30 FPS, H.264 High@4.1, `yuv420p`, target video bitrate 8 Mbit/s, max 12 Mbit/s, GOP 60 frames и MP4 fast start. | `ffprobe`/`mediainfo` каждого output подтверждает profile; mismatch блокирует `Ready` и export. |
| `NFR-MEDIA-002` | Paid final audio должен использовать AAC-LC, 48 kHz, stereo и target 192 kbit/s. Loudness master сохраняется; upward normalization запрещена, attenuation применяется только для ограничения true peak до −1 dBTP. | Golden fixtures сравнивают integrated loudness/true peak source и output; codec/profile mismatch или clipping отклоняется. |
| `NFR-MEDIA-003` | Free preview должен использовать 540×960, constant 30 FPS, H.264/AAC, target video bitrate 2 Mbit/s, fast start и заметный service watermark. | Automated profile check и representative-frame inspection подтверждают dimensions, codecs, bitrate range и watermark. |
| `NFR-MEDIA-004` | Output должен пройти decode, duration, stream, checksum и truncation validation до `Ready`; невалидный файл не должен попадать в bundle. | Набор truncated, zero-byte, wrong-codec и corrupt fixtures завершается `Failed` с безопасным diagnostic code. |
| `NFR-MEDIA-005` | Разница длительности audio/video streams должна быть не более 100 ms, а timing render events относительно `CompositionSpec` — не более одного frame. | Golden corpus автоматически измеряет stream duration и marker/phrase timing для vocal и instrumental fixtures. |
| `NFR-MEDIA-006` | Browser preview и server render одного `CompositionSpec` должны иметь SSIM не ниже 0.98 на утверждённых representative frames после выравнивания resolution и исключения service watermark region. | Golden render suite сравнивает frames в закреплённых browser/font/render environments и публикует diff artifacts. |
| `NFR-MEDIA-007` | Final MP4 должен начинать воспроизведение без полного скачивания и корректно декодироваться в поддерживаемых desktop/mobile browsers и системных players без изменения файла. | Compatibility suite проверяет progressive playback, audio presence, duration и seek на browser matrix из `NFR-UX-006`. |

## 8. OBS — observability и supportability

| ID | Требование | Проверка |
|---|---|---|
| `NFR-OBS-001` | OpenTelemetry trace должен связывать browser/API, outbox/job, .NET orchestrator, deterministic analysis/render, OpenRouter calls и storage operation единым trace context либо explicit async link. | Synthetic MP3-first project открывается как связный trace через queue/provider boundaries и сохраняет logical operation ID. |
| `NFR-OBS-002` | Application logs не должны содержать lyrics, artwork prompts, media/base64, credentials, payment payloads, tokens или presigned URLs. Разрешены hashes, safe codes и provider request IDs. | Sentinel redaction test проходит .NET/OpenRouter/FFmpeg errors; log scan не находит запрещённые значения. |
| `NFR-OBS-003` | Operational dashboard должен показывать control-plane availability, API P95/P99, queue age, processing P50/P95, worker saturation, retry rate и paid render success. | Каждый metric имеет owner, unit, labels и ссылку на соответствующий NFR; synthetic incident изменяет ожидаемые panels. |
| `NFR-OBS-004` | Cost telemetry должна учитывать analysed seconds, analysis compute, rendered seconds, storage byte-days, delivery bytes, LLM usage, retries и cost per Pack без включения пользовательского контента в metric labels. | Один reference Pack даёт reconciled cost breakdown; сумма raw units совпадает с job/usage ledger в допустимой rounding tolerance. |
| `NFR-OBS-005` | Alert должен создаваться при нарушении availability target, queue age более 10 минут, отсутствии recovery point более 2 часов или paid render success ниже 97% при выборке не менее 100 outputs. | Alert tests подают synthetic series, проверяют routing, deduplication, recovery notification и ссылку на runbook. |
| `NFR-OBS-006` | Каждая безопасно отображаемая ошибка API/job должна иметь stable error code и correlation/trace ID; stack trace и provider secrets пользователю не возвращаются. | Contract tests подтверждают error envelope, поиск события по correlation ID и отсутствие внутренних exception details. |
| `NFR-OBS-007` | Каждый service должен предоставлять internal `/health/live` и `/health/ready`; liveness проверяет process, readiness — способность выполнять назначенную роль и обязательные dependency checks. | Dependency fault переводит readiness в unhealthy без restart loop; health responses не раскрывают connection strings, paths или credentials. |

## 9. UX — accessibility, responsive UI и browser support

| ID | Требование | Проверка |
|---|---|---|
| `NFR-UX-001` | Весь пользовательский интерфейс MVP, включая onboarding, upload, timing editor, storyboard, checkout и downloads, должен соответствовать WCAG 2.2 Level AA. | Accessibility acceptance checklist покрывает все route/state combinations, включая loading, empty, validation и failure states. |
| `NFR-UX-002` | Все операции должны выполняться с клавиатуры без timing-dependent gestures; timing editor обязан иметь доступные числовые поля in/out как альтернативу drag controls. | Keyboard-only test проходит полный flow без focus trap; manual hook и phrase timing сохраняются без pointer. |
| `NFR-UX-003` | Controls должны иметь programmatic name/role/state, errors — связь с полем и понятное исправление, а async progress — screen-reader announcement без повторяющегося шума. | NVDA и VoiceOver корректно озвучивают labels, validation summary, job state change и completion. |
| `NFR-UX-004` | Text и controls должны соблюдать AA contrast. UI motion учитывает `prefers-reduced-motion`; media preview запускается только явным действием пользователя и имеет pause/stop controls. | Contrast scanner и manual reduced-motion test подтверждают отсутствие autoplay и необязательной анимации. |
| `NFR-UX-005` | Полный workflow должен работать при viewport от 360 px, в portrait/landscape и с touch input без потери данных или desktop-only review. | Responsive Playwright suite проходит signup → MP3 → transcript/artwork review → storyboard → checkout → download на mobile/tablet/desktop. |
| `NFR-UX-006` | Поддерживаются две последние стабильные major versions Chrome, Edge, Firefox, Safari, iOS Safari и Android Chrome на дату release. | Browser matrix выполняет smoke и основной end-to-end flow; unsupported browser получает понятное предупреждение без ложной потери данных. |
| `NFR-UX-007` | Интерактивные targets должны быть не меньше 24×24 CSS px или иметь эквивалентный spacing; функция не должна зависеть только от hover, color или multi-touch gesture. | Automated geometry checks и manual touch test покрывают timeline handles, asset controls, campaign cards и dialogs. |
| `NFR-UX-008` | CI не должен допускать axe violations уровней `critical` или `serious`; перед каждым production release выполняется ручная проверка NVDA на Windows и VoiceOver на iOS/macOS. | Release evidence содержит axe report и подписанный manual checklist; найденное нарушение блокирует release либо имеет owner/expiry exception. |

## 10. ENG — maintainability, delivery и cost control

| ID | Требование | Проверка |
|---|---|---|
| `NFR-ENG-001` | Analysis/transcript/artwork/campaign revisions, `CompositionSpec`, provider manifest и export manifest должны иметь explicit schema/handler version. Workers поддерживают текущую и предыдущую production version для in-flight jobs. | Contract fixtures обеих versions читаются новым deployment; неизвестная version отклоняется до side effect. |
| `NFR-ENG-002` | Database migrations должны использовать expand/contract для несовместимых изменений и позволять rollback application deployment не более чем за 15 минут без потери уже подтверждённых данных. | Staging rehearsal выполняет old → mixed → new → rollback sequence и подтверждает чтение/запись на каждом этапе. |
| `NFR-ENG-003` | CI release gate должен включать unit, integration с PostgreSQL/S3, OpenRouter HTTP contract tests, deterministic FFmpeg golden-media smoke, security scans и основной Playwright flow. | Protected branch не создаёт deployable artifact при failure обязательного suite; report хранит версии tools и fixtures. |
| `NFR-ENG-004` | .NET Aspire AppHost и ServiceDefaults должны воспроизводить локальную service topology: API, workers, PostgreSQL, object storage, health checks, service discovery и OpenTelemetry. | Новый developer по documented command запускает solution и видит healthy resources/traces без ручного создания connection strings. |
| `NFR-ENG-005` | Deployment configuration должна иметь versioned группы `CFG-SLO-*`, `CFG-PERF-*`, `CFG-WORKER-*`, `CFG-RETENTION-*`, `CFG-SIGNED-URL-*` и `CFG-EXPORT-*`; startup должен валидировать обязательные значения и безопасные ranges. | Missing/invalid production setting завершает startup до принятия traffic; effective non-secret config version видна diagnostics/audit. |
| `NFR-ENG-006` | Build artifacts и media-worker images должны быть immutable, dependency versions pinned, а один release identifier — присутствовать в API, worker telemetry и manifests. | Повторный build из lockfiles даёт эквивалентный dependency graph; trace одного Pack показывает единые либо явно совместимые release versions. |
| `NFR-ENG-007` | При не менее чем 20 paid operations за скользящие 30 дней direct variable compute должен оставаться не выше 15% net revenue, а contribution margin — не ниже 70% по каждому paid product. Нарушение создаёт operational alert, но не меняет завершённую цену пользователя. | Monthly unit-economics report reconciles provider invoices и usage ledger; до достижения выборки показатели помечаются informational, а threshold breach создаёт owner/action без retroactive billing. |
| `NFR-ENG-008` | Изменение NFR target, production contract или security exception должно обновлять tests/runbook и сохранять traceability по NFR-ID; ID не переиспользуется для другого смысла. | Documentation check на release подтверждает ссылки из test plan/ADR/runbook и changelog для изменённого нормативного значения. |

## 11. Канонические configuration defaults

| Группа | MVP default |
|---|---|
| `CFG-SLO-*` | 99.5% monthly control-plane availability; planned maintenance ≤ 2 h/month |
| `CFG-PERF-*` | Storyboard P50/P95 10/20 min; preview P95 10 min; Pack P50/P95 30/60 min |
| `CFG-WORKER-*` | Lost lease recovery ≤ 5 min; capability pools/fencing; concurrency/resource limits отдельно для media/analysis/control/render/export |
| `CFG-RETENTION-*` | Draft 30 days; originals/proxies 90 days; finals/manifests/bundles 365 days; primary deletion ≤ 7 days; backup expiry ≤ 35 days |
| `CFG-SIGNED-URL-*` | Upload URL ≤ 60 min; download URL ≤ 15 min |
| `CFG-EXPORT-*` | Final 1080×1920 H.264/AAC 30 FPS; preview 540×960 H.264/AAC 30 FPS; параметры детализированы в `NFR-MEDIA-001..003` |

Количество повторных выдач нового download URL после expiry остаётся product/abuse configuration и должно быть закрыто до paid beta. Уже выданный URL не продлевается.

## 12. Внутренние эксплуатационные интерфейсы

- `GET /health/live` возвращает только факт работоспособности process.
- `GET /health/ready` проверяет обязательные dependencies конкретной роли и не раскрывает детали конфигурации.
- API error envelope содержит stable `code`, безопасный `message` и `traceId`.
- Metrics используют low-cardinality labels: environment, service, operation/job type, status, provider class и release version.
- Workspace, project и job IDs могут использоваться только в access-controlled structured logs/traces, но не как metric labels.
- Health, metrics и tracing endpoints не являются public product API.

## 13. Обязательные verification suites

| Suite | Покрытие |
|---|---|
| Performance/load | Reference workload, API percentile, Core Web Vitals, processing time, SSE reconnect |
| Resilience | Worker kill/restart, lease expiry, duplicate delivery, partial render failure, dependency timeout |
| Recovery | Backup freshness, monthly restore, RPO/RTO, deletion tombstones после restore |
| Security | Tenant isolation, malicious media, command injection, URL expiry, auth/session, webhook replay, secret/log scanning |
| Media | ffprobe/mediainfo profiles, corruption, A/V sync, phrase timing, SSIM и progressive playback |
| Accessibility/client | axe, keyboard-only, NVDA, VoiceOver, viewport 360+, touch и browser matrix |
| Delivery/contracts | Current/previous schema contracts, migration rollback, Aspire startup и release-version traceability |

## 14. Правила сопровождения

- Новое измеримое системное свойство получает NFR-ID в профильном разделе.
- Функциональное поведение остаётся в [FR-комплекте](../func-requirements/README.md); NFR не дублирует product entitlement или campaign recipe.
- Изменение target требует причины, новых acceptance checks и оценки влияния на стоимость.
- Временное исключение содержит owner, compensating control, expiry и ссылку на risk acceptance.
- Production incident, нарушивший NFR, должен приводить к корректировке test, alert или runbook, если существующая защита не обнаружила причину.

## 15. Покрытие первым implementation increment

Этот раздел не меняет нормативные targets и не объявляет paid-beta NFR выполненными. Он фиксирует, какие механизмы уже присутствуют в `src`.

| Область | Реализовано сейчас | Следующая проверяемая граница |
|---|---|---|
| `PERF` | Media bytes идут напрямую в object storage; API остаётся control plane; progress передаётся через SSE с polling fallback. | Load/RUM измерения и end-to-end targets analysis/render/export. |
| `REL` | Capability-routed PostgreSQL queue, lease/fencing, outbox dispatch, recovery и bounded retry; replacement активируется после successful ingest. | Chaos tests на реальных PostgreSQL/MinIO, stale lease commit и partial render recovery. |
| `DATA` | PostgreSQL — source of truth; server-generated keys; SHA-256; immutable workflow revisions; provider staging manifests. | Backup/PITR, staging promotion/cleanup, retention workers, tombstones и restore drills. |
| `SEC` | Google OAuth + собственный JWT validation, tenant-safe `404`, rate limiting, ETag concurrency, direct presigned upload, magic-byte/media validation и safe process arguments. | Production TLS/secret manager, worker container sandbox, ASVS evidence и signed downloads. |
| `MEDIA` | ffprobe validation; normalized audio preview, image proxy/thumbnail и H.264 `yuv420p` video proxy. | Golden corpus, final 1080×1920 profiles, A/V sync, SSIM и bundle validation. |
| `OBS` | ServiceDefaults, OpenTelemetry, structured errors с `traceId`, live/ready health checks и durable job events. | Production dashboards, alerts, cost telemetry и cross-runtime traces. |
| `UX` | MP3 quick upload, workflow hub и transcript/artwork/campaign review surfaces with reload-safe progress. | axe/browser matrix, timing editor accessibility и complete paid flow. |
| `ENG` | Strict .NET build, pinned dependencies, EF migration, Aspire topology test, generated OpenAPI TypeScript contract, unit/integration/Playwright suites. | CI release gate, contract-version fixtures, media golden tests and deploy artifacts. |
