# 03. Lyrics, music analysis и hooks

Источник: [Product Plan, §§ 4.4, 5, 6.5, 8](../base/Hook2Stream_Product_Plan.md).

Для основного flow транскрипт автоматически создаётся из MP3. Пользователь подтверждает его после phrase-level review; импортированный авторский текст становится отдельным canonical source и не переписывается моделью молча.

## LYR — lyrics и phrase timing

| ID | Требование | Проверка |
|---|---|---|
| `FR-LYR-001` | Для vocal MP3 система должна автоматически создать RU/EN transcript с ordered phrases, internal word timing и confidence; paste/UTF-8 text остаются optional replacement source. | Vocal MP3 без lyrics получает reviewable transcript; imported text повторно открывается без потери Unicode/order, а automatic revision остаётся в history. |
| `FR-LYR-002` | Система может предложить instrumental candidate, но только пользовательское подтверждение переводит project в `Instrumental`; режим запрещает вымышленные строки. | Instrumental detection не проходит gate автоматически; после подтверждения transcript revision содержит ноль phrases, а campaign использует title/CTA/beat-reactive layers. |
| `FR-LYR-003` | Project должен хранить выбранный язык lyrics/audio; система может предложить detected language, но пользовательское подтверждение является каноническим. | Несовпадение detection показывает suggestion, не меняя язык автоматически; подтверждённый язык передаётся alignment и copy adapters. |
| `FR-LYR-004` | Transcription stage должен отправлять исходный MP3 в `openai/whisper-large-v3` только через OpenRouter с обязательным ZDR и запрашивать phrase/word timestamps. Локальная vocal separation и локальная speech model в MVP запрещены. | Contract fixture возвращает ordered phrases/words внутри audio; provider provenance фиксирует requested/resolved model, generation ID, usage и safe hashes без raw audio/lyrics. |
| `FR-LYR-005` | Система должна показывать confidence, unmatched phrases, gaps и repetitions не только цветом; low-confidence phrase должна быть исправлена либо явно acknowledged перед approval. | Неподтверждённая phrase ниже threshold блокирует approve; после edit/acknowledge та же revision либо новая revision проходит gate. |
| `FR-LYR-006` | Пользователь должен иметь возможность редактировать phrase text, line breaks, start/end и объединять или разделять phrases до утверждения hooks. | Сохранённая правка воспроизводится после reload; end не может быть раньше start или выходить за duration; пересечения явно валидируются. |
| `FR-LYR-007` | Каждое сохранение lyrics/timing должно создавать immutable `TranscriptRevision`; approval привязывается к точной revision и source-audio fingerprint. Новая approved revision инвалидирует hooks, campaign, renders и export, но не утверждённую cover. | История сохраняется; stale approval не применяется к новой revision; artwork остаётся current и предлагает необязательную regeneration. |

## ANL — music analysis и hooks

| ID | Требование | Проверка |
|---|---|---|
| `FR-ANL-001` | Analysis worker должен детерминированно вычислить BPM, beat grid и meter/confidence через versioned FFmpeg/DSP adapter параллельно transcription stage, без neural model weights. | BPM и beat timestamps находятся внутри audio duration; одинаковые bytes/version дают одинаковый результат; отсутствие уверенного meter создаёт warning, а не завершает весь project ошибкой. |
| `FR-ANL-002` | Система должна выделить ordered song sections и вероятные boundaries, включая доступные intro, verse, chorus, bridge, drop, solo и outro labels. | Sections покрывают допустимые ranges, не имеют отрицательной duration и показывают confidence; неизвестный участок может иметь label `Unknown`. |
| `FR-ANL-003` | Система должна вычислить energy curve, onsets и заметные transitions, пригодные для поиска build-up, drop, riff, solo и peak. | Golden fixture с известным drop возвращает transition рядом с эталоном в заданной tolerance; отсутствие peak даёт fallback warning. |
| `FR-ANL-004` | Для vocal project система должна предложить один `Chorus` hook на основе repeat/section, energy, phrase completeness и clean boundaries. | Предложение имеет duration 10–30 секунд, explanation и не обрывает слово без warning. |
| `FR-ANL-005` | Для vocal project система должна предложить один `EmotionalLyric` hook на основе законченной сильной фразы, повторяемости и музыкального контекста. | Hook ссылается на существующие phrase IDs и отображает точный excerpt; LLM не имеет права придумывать timestamp или lyric text. |
| `FR-ANL-006` | Система должна предложить один `InstrumentalDrop` hook на основе energy transition, riff/solo/drop section и чистых музыкальных boundaries. | Hook остаётся доступным при отсутствии lyrics и содержит музыкальное, а не lyric explanation. |
| `FR-ANL-007` | В режиме `Instrumental` система должна сформировать три неперекрывающихся либо минимально перекрывающихся hooks с ролями `PrimarySection`, `SecondarySection` и `EnergyPeak`, используя только structure/energy features. | Instrumental fixture получает ровно три hooks без lyric excerpts; если distinct sections недостаточно, overlap и low-diversity warning отображаются пользователю. |
| `FR-ANL-008` | Каждый suggested или пользовательский hook должен иметь duration от 10 до 30 секунд включительно, находиться внутри audio и стремиться к beat/phrase boundaries. | Значение 9.99 или 30.01 секунды не утверждается; ручной boundary snap можно отключить только с явным warning о cut. |
| `FR-ANL-009` | Пользователь должен иметь возможность изменить in/out, выбрать другой suggested candidate или создать ручной hook для любого из трёх slots; hook edit не является отдельным blocking approval. | После замены slot сохраняет unique ID/revision и новый timestamp; остальные slots не меняются, а зависимые campaign items становятся stale. |
| `FR-ANL-010` | Система должна сохранять versioned `SongAnalysis` и для каждого suggestion показывать role, timestamp, duration, confidence, features и краткое explanation. | Reload возвращает ту же analysis version; explanation опирается только на сохранённые sections, energy events и phrases. |
| `FR-ANL-011` | Analysis должен выполняться как durable job с progress, cancellation, retry и partial-result preservation; повторный запуск для того же input hash/version не должен создавать двойное списание. | Worker restart продолжает или безопасно повторяет stage; готовые alignment/features сохраняются при failure hook ranking; retry идемпотентен. |

## Transcript и campaign gates

Transcript approval разрешается только когда:

- подтверждён RU/EN language;
- исправлены либо acknowledged все blocking confidence warnings;
- ranges и порядок phrases валидны;
- либо пользователь явно подтвердил `Instrumental`.

Campaign generation разрешается только когда:

- существует актуальный `SongAnalysis`;
- утверждена актуальная `TranscriptRevision` либо `Instrumental` revision;
- заполнены ровно три hook slots;
- каждый hook имеет валидные boundaries;
- hooks принадлежат текущей transcript revision;
- утверждена актуальная cover revision и готовы три backgrounds.
