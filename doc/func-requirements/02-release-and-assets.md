# 02. Release project и media assets

Источник: [Product Plan, §§ 4.1, 5, 7, 8](../base/Hook2Stream_Product_Plan.md).

Release project связывает входные материалы, analysis, hooks, campaign plan, purchases, renders и export history.

## REL — release project

| ID | Требование | Проверка |
|---|---|---|
| `FR-REL-001` | Система должна позволять создать release project в персональном workspace с уникальным ID и состоянием `Draft`. | После создания project доступен только владельцу и отображается в dashboard без обязательного запуска analysis. |
| `FR-REL-002` | Project должен хранить имя артиста, название трека, язык, internal notes и выбранный brand kit snapshot; имя артиста и трека обязательны до analysis. | Попытка запустить analysis без обязательной metadata блокируется с указанием отсутствующих полей; валидные значения сохраняются. |
| `FR-REL-003` | Пользователь должен выбрать `Upcoming` с будущей release date либо `Released` с фактической release date и датой начала новой кампании. | Upcoming не принимает прошедшую release date; Released не требует будущую дату и сохраняет отдельный campaign start. |
| `FR-REL-004` | До analysis пользователь должен подтвердить права на audio, lyrics, cover и visual assets и указать synthetic/altered-content status. | Без rights attestation analysis не запускается; подтверждение сохраняет actor, timestamp, policy version и flags. |
| `FR-REL-005` | Система должна вычислять и показывать каноническое состояние project: `Draft`, `Analyzing`, `HookReview`, `CampaignReady`, `PreviewReady`, `Rendering`, `Ready`, `PartiallyReady` или `Archived`. | Состояние соответствует фактическим artifacts/jobs; failure одного item не переводит весь project в несуществующий общий `Failed`. |
| `FR-REL-006` | Пользователь должен иметь возможность переименовать internal project label, архивировать, восстановить из архива и удалить project. | Archive скрывает project из active list без удаления; restore возвращает его; delete немедленно закрывает доступ и запускает configured cleanup. |
| `FR-REL-007` | Система должна хранить dependency versions и помечать downstream artifacts устаревшими после изменения audio, lyrics, timing, approved hook, brand snapshot или composition controls. | Замена audio инвалидирует analysis и plan; изменение одного campaign item инвалидирует только его affected render и export bundle revision. |

## AST — media assets

| ID | Требование | Проверка |
|---|---|---|
| `FR-AST-001` | Project должен принимать один обязательный финальный audio asset в MP3 или WAV в пределах `CFG-AUDIO-*`. | Валидный MP3/WAV сохраняется; unsupported type, превышение bytes или duration отклоняются до analysis с точной причиной. |
| `FR-AST-002` | Project должен принимать одну обязательную cover image и создавать browser-safe proxy/thumbnail. | Без cover analysis не запускается; валидная cover отображается в setup, brand defaults и animated-cover template. |
| `FR-AST-003` | Project должен содержать от 3 до 10 активных visual assets, каждый из которых является поддерживаемым изображением или коротким видео. | При двух assets запуск блокируется; 3 и 10 принимаются; одиннадцатый не активируется без удаления или замены существующего. |
| `FR-AST-004` | Upload должен выполняться напрямую в object storage через ограниченную session, показывать progress и поддерживать безопасное возобновление там, где размер требует multipart. | Обрыв multipart upload можно продолжить без duplicate asset; отменённая session не считается активным входом. |
| `FR-AST-005` | До analysis система должна проверить MIME/container, codec, duration, dimensions, наличие audio stream и отсутствие повреждения, применяя `CFG-AUDIO-*` и `CFG-VISUAL-*`. | Переименованный либо повреждённый файл отклоняется по фактическому содержимому; UI показывает исправимое действие. |
| `FR-AST-006` | Система должна создать normalized audio, image/video proxies, metadata и content hashes, не изменяя original bytes. | Analysis и browser preview используют derived assets; original hash остаётся неизменным и доступен в internal manifest. |
| `FR-AST-007` | Пользователь должен иметь возможность заменить cover/audio, добавить, удалить и переупорядочить visuals, сохраняя правило 3–10 перед запуском campaign generation. | Замена обновляет dependency revision; удаление, оставляющее менее трёх active visuals, разрешено в draft, но блокирует generation с объяснением. |
| `FR-AST-008` | Asset keys, metadata, proxies и downloads должны быть изолированы workspace; project deletion должен применяться к original и derived assets по `CFG-RETENTION-*`. | Чужой key или URL не даёт доступ; после logical delete новые URLs не выдаются, а physical cleanup подтверждается audit event. |

## Обязательный набор для analysis

```text
audio: 1 × MP3/WAV
lyrics: text OR Instrumental
cover: 1 image
visuals: 3..10 image/video assets
metadata: artist + track + language + release timing
rights: accepted
```
