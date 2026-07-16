# 03. Lyrics, music analysis и hooks

Источник: [Product Plan, §§ 4.3, 5, 6.5, 8](../base/Hook2Stream_Product_Plan.md).

Пользовательский текст является источником lyrics. WhisperX отвечает за alignment и confidence, но не должен молча переписывать авторский текст.

## LYR — lyrics и phrase timing

| ID | Требование | Проверка |
|---|---|---|
| `FR-LYR-001` | Для vocal project система должна принимать lyrics через paste или поддерживаемый plain-text upload и сохранять исходный порядок строк и stanza breaks. | Кириллический и латинский текст повторно открывается без потери символов, пустых разделителей и line order. |
| `FR-LYR-002` | Пользователь должен иметь возможность явно выбрать `Instrumental`; этот режим заменяет обязательность lyrics и запрещает генерацию вымышленных строк. | Instrumental project запускает analysis без lyrics; campaign payload содержит только title/CTA либо пустой lyric layer. |
| `FR-LYR-003` | Project должен хранить выбранный язык lyrics/audio; система может предложить detected language, но пользовательское подтверждение является каноническим. | Несовпадение detection показывает suggestion, не меняя язык автоматически; подтверждённый язык передаётся alignment и copy adapters. |
| `FR-LYR-004` | Analysis worker должен выровнять пользовательский текст с audio на phrase/word level через versioned WhisperX adapter и сохранить timestamps. | Для golden fixture возвращаются ordered phrases и words в пределах audio duration; provider output без валидных timestamps отклоняется. |
| `FR-LYR-005` | Система должна показывать confidence, unmatched phrases и подозрительные gaps/repetitions и не представлять low-confidence alignment как точный. | Phrase ниже `CFG-LYRICS-LOW-CONFIDENCE` визуально помечена и доступна в списке требующих проверки. |
| `FR-LYR-006` | Пользователь должен иметь возможность редактировать phrase text, line breaks, start/end и объединять или разделять phrases до утверждения hooks. | Сохранённая правка воспроизводится после reload; end не может быть раньше start или выходить за duration; пересечения явно валидируются. |
| `FR-LYR-007` | Каждое изменение lyrics/timing должно создавать новую `LyricsDocument` revision и инвалидировать только зависящие analysis, hooks, plans и renders. | После правки прежний immutable render остаётся в history, но новый export не использует stale timing без явного восстановления старой revision. |

## ANL — music analysis и hooks

| ID | Требование | Проверка |
|---|---|---|
| `FR-ANL-001` | Analysis worker должен вычислить BPM, beat grid и meter/confidence через versioned Essentia adapter. | BPM и beat timestamps находятся внутри audio duration; отсутствие уверенного meter создаёт warning, а не завершает весь project ошибкой. |
| `FR-ANL-002` | Система должна выделить ordered song sections и вероятные boundaries, включая доступные intro, verse, chorus, bridge, drop, solo и outro labels. | Sections покрывают допустимые ranges, не имеют отрицательной duration и показывают confidence; неизвестный участок может иметь label `Unknown`. |
| `FR-ANL-003` | Система должна вычислить energy curve, onsets и заметные transitions, пригодные для поиска build-up, drop, riff, solo и peak. | Golden fixture с известным drop возвращает transition рядом с эталоном в заданной tolerance; отсутствие peak даёт fallback warning. |
| `FR-ANL-004` | Для vocal project система должна предложить один `Chorus` hook на основе repeat/section, energy, phrase completeness и clean boundaries. | Предложение имеет duration 10–30 секунд, explanation и не обрывает слово без warning. |
| `FR-ANL-005` | Для vocal project система должна предложить один `EmotionalLyric` hook на основе законченной сильной фразы, повторяемости и музыкального контекста. | Hook ссылается на существующие phrase IDs и отображает точный excerpt; LLM не имеет права придумывать timestamp или lyric text. |
| `FR-ANL-006` | Система должна предложить один `InstrumentalDrop` hook на основе energy transition, riff/solo/drop section и чистых музыкальных boundaries. | Hook остаётся доступным при отсутствии lyrics и содержит музыкальное, а не lyric explanation. |
| `FR-ANL-007` | В режиме `Instrumental` система должна сформировать три неперекрывающихся либо минимально перекрывающихся hooks с ролями `PrimarySection`, `SecondarySection` и `EnergyPeak`, используя только structure/energy features. | Instrumental fixture получает ровно три hooks без lyric excerpts; если distinct sections недостаточно, overlap и low-diversity warning отображаются пользователю. |
| `FR-ANL-008` | Каждый suggested или пользовательский hook должен иметь duration от 10 до 30 секунд включительно, находиться внутри audio и стремиться к beat/phrase boundaries. | Значение 9.99 или 30.01 секунды не утверждается; ручной boundary snap можно отключить только с явным warning о cut. |
| `FR-ANL-009` | Пользователь должен иметь возможность изменить in/out, выбрать другой suggested candidate или создать ручной hook для любого из трёх slots. | После замены slot сохраняет unique ID/revision и новый timestamp; остальные утверждённые slots не меняются. |
| `FR-ANL-010` | Система должна сохранять versioned `SongAnalysis` и для каждого suggestion показывать role, timestamp, duration, confidence, features и краткое explanation. | Reload возвращает ту же analysis version; explanation опирается только на сохранённые sections, energy events и phrases. |
| `FR-ANL-011` | Analysis должен выполняться как durable job с progress, cancellation, retry и partial-result preservation; повторный запуск для того же input hash/version не должен создавать двойное списание. | Worker restart продолжает или безопасно повторяет stage; готовые alignment/features сохраняются при failure hook ranking; retry идемпотентен. |

## Hook Review gate

Campaign generation разрешается только когда:

- существует актуальный `SongAnalysis`;
- lyrics alignment подтверждён либо project находится в `Instrumental`;
- заполнены ровно три hook slots;
- каждый hook имеет валидные boundaries;
- пользователь явно подтвердил предложенные или изменённые hooks.
