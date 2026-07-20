# 02. Release project и media assets

Источник: [Product Plan, §§ 4.1, 5, 7, 8](../base/Hook2Stream_Product_Plan.md).

Release project связывает входные материалы, analysis, hooks, campaign plan, purchases, renders и export history.

## REL — release project

| ID | Требование | Проверка |
|---|---|---|
| `FR-REL-001` | Система должна одним MP3-first command создать в персональном workspace release project, audio asset и upload session. Новый project имеет `FlowKind=Mp3First`, `Mode=Unscheduled` и уникальный ID. | Повтор с тем же `Idempotency-Key` и payload возвращает те же IDs; другое тело с тем же ключом получает `409`; project недоступен другому workspace. |
| `FR-REL-002` | Project должен хранить имя артиста, название трека, RU/EN language, internal notes и brand kit snapshot. ID3/filename используются только как предложения и не перезаписывают пользовательское значение. | Один MP3 можно загрузить до metadata; перед первым external artwork request система требует подтверждённые artist/title/language/release timing. |
| `FR-REL-003` | До campaign generation пользователь должен заменить начальный `Unscheduled` на `Upcoming` с будущей release date либо `Released` с фактической release date и будущей/сегодняшней campaign start date. | `Unscheduled` разрешает ingest/analysis; Upcoming и Released валидируются раздельно; без schedule не создаются artwork/campaign по production policy. |
| `FR-REL-004` | До отправки MP3 пользователь должен подтвердить права на audio/lyrics/performance и разрешить external AI processing через OpenRouter с Zero Data Retention. Только детерминированные ingest/analysis не требуют внешнего provider call; transcription, artwork и campaign проверяют актуальный consent до и после каждого вызова. | Без двух quick-upload flags API отклоняет запрос; revoke/replacement отменяет AI stage и не сохраняет поздний provider result; attestation хранит actor, timestamp, policy version, bound audio asset и fingerprint. |
| `FR-REL-005` | Система должна отдавать reload-safe workflow с lanes `Audio`, `Analysis`, `Transcript`, `Artwork`, `Hooks`, `Campaign`, `Preview`, `FinalRender`, progress, blockers и next action; coarse project state остаётся производным представлением. | Перезагрузка/другое устройство восстанавливает канонические lanes; failure одного item не переводит весь project в общий необратимый `Failed`. |
| `FR-REL-006` | Пользователь должен иметь возможность переименовать internal project label, архивировать, восстановить из архива и удалить project. | Archive скрывает project из active list без удаления; restore возвращает его; delete немедленно закрывает доступ и запускает configured cleanup. |
| `FR-REL-007` | Система должна хранить immutable dependency revisions/fingerprints и помечать downstream artifacts stale после изменения audio, transcript approval, hook, approved cover, background, release timing или item controls. | Замена audio инвалидирует analysis/transcript/artwork/campaign; transcript edit сохраняет cover; hook/background/item edit инвалидирует только ссылающиеся items/renders и export revision. |

## AST — media assets

| ID | Требование | Проверка |
|---|---|---|
| `FR-AST-001` | Главный flow должен принимать один финальный MP3 в пределах `CFG-AUDIO-*`; WAV поддерживается как advanced replacement. | Валидный MP3 создаёт draft/upload session; WAV не предлагается основной dropzone, но принимается Sources-панелью; unsupported/corrupt media отклоняется с точной причиной. |
| `FR-AST-002` | User cover необязательна: система создаёт три AI cover-кандидата либо принимает собственную cover и создаёт browser-safe proxy/thumbnail. | Analysis не зависит от cover; campaign generation блокируется до approval ровно одной current cover revision. |
| `FR-AST-003` | Пользовательские visuals необязательны и могут содержать 0–10 поддерживаемых изображений/коротких видео; после cover approval система создаёт три согласованных campaign backgrounds. | Проект без custom visuals доходит до storyboard; одиннадцатый custom visual не активируется; manual sources и generated backgrounds различимы по origin/purpose. |
| `FR-AST-004` | Upload должен выполняться напрямую в object storage через ограниченную session, показывать progress и поддерживать безопасное возобновление там, где размер требует multipart. | Обрыв multipart upload можно продолжить без duplicate asset; отменённая session не считается активным входом. |
| `FR-AST-005` | До analysis система должна проверить audio magic/container, codec, duration, наличие audio stream и отсутствие повреждения; image/video checks выполняются до их выбора в artwork/campaign. | Переименованный либо повреждённый файл отклоняется по фактическому содержимому; UI показывает исправимое действие. |
| `FR-AST-006` | Система должна создать normalized audio, image/video proxies, metadata и content hashes, не изменяя original bytes. | Analysis и browser preview используют derived assets; original hash остаётся неизменным и доступен в internal manifest. |
| `FR-AST-007` | Пользователь должен иметь возможность заменить audio/cover, добавить, удалить и переупорядочить optional visuals. | Замена audio инвалидирует analysis, transcript approval, hooks, artwork approval, campaign и renders; изменение visual инвалидирует только зависимые items. |
| `FR-AST-008` | Asset keys, metadata, proxies и downloads должны быть изолированы workspace; project deletion должен применяться к original и derived assets по `CFG-RETENTION-*`. | Чужой key или URL не даёт доступ; после logical delete новые URLs не выдаются, а physical cleanup подтверждается audit event. |

## Gates MP3-first flow

```text
start + local analysis: 1 × MP3
first external artwork: processed audio + artist + track + RU/EN + release timing + rights
campaign generation: approved transcript OR confirmed Instrumental
                     + approved cover + 3 generated/uploaded backgrounds
                     + exactly 3 editable hooks
advanced overrides: WAV + prepared lyrics + own cover + 0..10 visuals
```
