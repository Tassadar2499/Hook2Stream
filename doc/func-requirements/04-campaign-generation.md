# 04. Campaign generation

Источник: [Product Plan, §§ 4.4–4.7, 5, 6.6](../base/Hook2Stream_Product_Plan.md).

Campaign generation превращает три утверждённых hooks и brand snapshot в детерминированный storyboard. Система не выбирает произвольное число результатов: валидный plan всегда содержит ровно 18 items.

## CAM — campaign plan, copy и calendar

| ID | Требование | Проверка |
|---|---|---|
| `FR-CAM-001` | Система должна разрешать generation только при валидных inputs, rights attestation, актуальном analysis, brand snapshot и ровно трёх утверждённых hooks. | Отсутствующий cover, третий hook или stale alignment блокирует generation с перечнем исправимых причин; готовые prerequisites запускают durable job. |
| `FR-CAM-002` | Успешный `CampaignPlan` должен содержать ровно 18 ordered campaign items и не считаться валидным при 17 или 19 items. | Contract validation принимает 18 уникальных IDs и отклоняет любое другое количество до storyboard/checkout. |
| `FR-CAM-003` | Система должна создать 12 hook items: для каждого из трёх hooks по одному `KineticLyrics`, `AnimatedCover`, `VisualLoopA` и `VisualLoopB`. | Matrix содержит `3 × 4` уникальных hook/template combinations без пропуска или duplicate combination. |
| `FR-CAM-004` | Система должна добавить ровно 2 `Teaser`, 2 `Countdown` и 2 `OutNow` items в режиме `Upcoming`. | Type counts равны `2/2/2`; изменение одного item не может удалить обязательный type из plan. |
| `FR-CAM-005` | `KineticLyrics` должен использовать только сохранённые phrase timings; для instrumental hook он должен переключаться на beat-reactive title/CTA без lyric payload. | Vocal preview подсвечивает существующий text; instrumental preview не содержит сгенерированных строк и остаётся валидным. |
| `FR-CAM-006` | `AnimatedCover` должен использовать project cover, brand snapshot и поддерживаемые motion controls без требования дополнительного video asset. | Project с тремя изображениями и без video assets получает валидный animated-cover item для каждого hook. |
| `FR-CAM-007` | `VisualLoopA` и `VisualLoopB` одного hook должны использовать разные active visual assets либо явно разные crop/motion treatment, если distinct assets технически непригодны. | При наличии минимум двух пригодных assets A/B используют разные IDs; fallback reuse сопровождается diversity warning и отличается composition controls. |
| `FR-CAM-008` | `Teaser`, `Countdown` и `OutNow` должны использовать versioned `CampaignCard` template с phase-specific title, date и CTA; два варианта одного type должны отличаться opening, asset assignment или CTA. | Pair comparison фиксирует хотя бы одно нормативное различие и одинаковый brand snapshot. |
| `FR-CAM-009` | Каждый item должен иметь duration от 10 до 30 секунд включительно; hook item не может выходить за утверждённые hook boundaries, кроме versioned intro/outro padding внутри audio. | Contract отклоняет 9.99/30.01 seconds и выход за audio; разрешённый padding сохраняется в composition spec. |
| `FR-CAM-010` | Upcoming plan должен применять канонические posting offsets и default assignment из таблицы ниже, сохраняя возможность пользователю изменить время внутри дня. | Новая campaign с future release date получает 8 pre-release, 2 release-day и 8 post-release items на ожидаемых offsets. |
| `FR-CAM-011` | Для `Released` plan система должна создать 18 slots в днях `0..20`, заменить оба `Countdown` на post-release CTA items и не создавать copy, будто релиз ещё не вышел. | Released fixture не содержит countdown wording; два replacement items имеют type `PostReleaseCTA`, а последняя дата не позже start +20 days. |
| `FR-CAM-012` | Каждый `CampaignItem` должен хранить day/phase, hook, template/version, asset IDs, duration, composition controls, text payload, copy variants, CTA и revision status. | Сериализация/повторное чтение возвращает все поля; item можно полностью восстановить без обращения к mutable global defaults. |
| `FR-CAM-013` | Система должна генерировать для каждого item neutral caption, emotional short caption, CTA, hashtags и destination-specific text для TikTok, YouTube Shorts, Instagram Reels и VK Clips. | Storyboard показывает редактируемые variants для четырёх destinations; ошибка одного copy adapter не блокирует video plan и помечается для retry/manual edit. |
| `FR-CAM-014` | Asset assignment должен распределять 3–10 visuals по plan, избегать длинной серии одного asset и сохранять единый brand snapshot. | Default plan не использует один visual asset более чем в трёх последовательных visual-dependent items; все items ссылаются на одну snapshot version. |
| `FR-CAM-015` | До дорогостоящего paid render система должна показать storyboard всех 18 items с day, phase, hook, template, asset, duration, text/CTA и readiness warning. | Пользователь видит 18 ordered cards до checkout; отсутствие полного video preview у 17 cards не скрывается. |
| `FR-CAM-016` | Пользователь должен иметь возможность изменить asset, supported template, opening, CTA или copy и перегенерировать один item без изменения утверждённых revisions остальных items. | Item A получает новую revision; hashes и rendered state items B–R остаются прежними. |
| `FR-CAM-017` | Campaign generation должна быть versioned и детерминированной относительно input revisions, recipe version и generation seed; повтор той же операции должен переиспользовать plan либо вернуть идентичный contract. | Повтор с теми же hashes/version/seed не создаёт второй логический plan или usage charge; изменение recipe version создаёт новую plan revision. |

## Канонический Upcoming schedule

Обозначения:

- `C` — chorus hook;
- `L` — emotional lyric hook;
- `D` — instrumental drop hook;
- `KL` — kinetic lyrics;
- `AC` — animated cover;
- `VLA/VLB` — visual loop A/B.

| Offset | Default item |
|---:|---|
| `-10` | Teaser 1 |
| `-9` | C · AC |
| `-8` | L · KL |
| `-6` | D · VLA |
| `-5` | Teaser 2 |
| `-3` | C · VLA |
| `-2` | Countdown 1 |
| `-1` | Countdown 2 |
| `0` | Out Now 1 |
| `0` | Out Now 2 |
| `+1` | C · KL |
| `+2` | L · AC |
| `+3` | D · AC |
| `+5` | L · VLA |
| `+6` | D · KL |
| `+7` | C · VLB |
| `+9` | L · VLB |
| `+10` | D · VLB |

Для instrumental project обозначения `C/L/D` заменяются тремя утверждёнными structure/energy slots, но template counts и schedule сохраняются.

## Канонический Released schedule

Offsets от выбранной campaign start:

```text
0, 0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 18, 20
```

- Два `OutNow`/reactivation items ставятся в день `0`.
- `Countdown 1/2` преобразуются в `PostReleaseCTA 1/2`.
- Остальные hook/template combinations сохраняются и распределяются в исходном относительном порядке.

## Storyboard edit boundary

MVP позволяет:

- выбрать другой active asset;
- переключить поддерживаемый template;
- выбрать разрешённый opening treatment;
- изменить fit/fill и focal point;
- исправить text, CTA и copy;
- повторно сгенерировать один item.

MVP не предоставляет arbitrary tracks, keyframes, transitions timeline или загрузку executable templates.
