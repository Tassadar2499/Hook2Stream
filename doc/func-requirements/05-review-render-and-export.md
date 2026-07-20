# 05. Review, preview, render и export

Источник: [Product Plan, §§ 5, 6.7–6.9, 7, 8](../base/Hook2Stream_Product_Plan.md).

Preview и final render используют один composition contract. Разница определяется quality/watermark profile, а не отдельной ручной реализацией интерфейса.

## REN — preview и render

| ID | Требование | Проверка |
|---|---|---|
| `FR-REN-001` | Browser preview и server render должны читать один versioned `CompositionSpec` и одинаково интерпретировать timing, assets, fit/fill, focal point, text и template controls. | Representative-frame comparison для одного spec находится в утверждённой tolerance; unsupported control отклоняется до render, а не игнорируется. |
| `FR-REN-002` | Paid final output должен иметь canvas 1080×1920, H.264 video и AAC audio; FPS, bitrate, loudness и GOP определяются `CFG-EXPORT-*`. | ffprobe подтверждает обязательные codec/dimensions и configured values; файл воспроизводится с audio duration в допустимой tolerance. |
| `FR-REN-003` | Для image/video asset система должна поддерживать `Fit` с безопасным фоном, `Fill` и ручной focal point/pan/zoom в пределах template controls. | Landscape, portrait и ultra-wide fixtures дают валидный portrait output; focal point сохраняется после reload и final render. |
| `FR-REN-004` | Kinetic composition должна отображать только утверждённый text, соблюдать phrase timing, safe margins, line breaks и выбранный allowlisted font; instrumental variant использует title/CTA. | Vocal fixture синхронизирует phrases с audio; instrumental fixture не содержит lyric layer; text не выходит за configured safe area. |
| `FR-REN-005` | До оплаты система должна выбрать один лучший eligible item по deterministic preview ranking и отрендерить полный low-resolution preview с заметным сервисным watermark; остальные 17 items остаются storyboard/poster-only. Успешный preview расходуется один раз на project, а не на revision. | Повтор команды идемпотентен; после transcript/artwork/item edit старый preview помечается stale, но второй бесплатный server render не выдаётся. Failed technical attempt не расходует allowance. |
| `FR-REN-006` | Paid items должны рендериться отдельными durable jobs с общим progress, per-item status, cancellation, retry и partial success. | Failure item 7 не удаляет ready items 1–6/8; retry запускается только для failed revision; cancel не маркирует незавершённый output как ready. |
| `FR-REN-007` | Система должна вычислять composition hash по spec, source hashes, template version и render profile и переиспользовать валидированный output для идентичного paid render. | Повторная доставка одной команды возвращает существующий `RenderVersion`, не создаёт новый object и не списывает usage повторно. |
| `FR-REN-008` | Каждый output должен пройти ffprobe и integrity validation до состояния `Ready`; невалидный или неполный файл должен считаться failed и не попадать в export. | Truncated file, неверный codec/dimension или sync вне tolerance отклоняются; причина доступна retry/support. |

## EXP — entitlement и export bundle

| ID | Требование | Проверка |
|---|---|---|
| `FR-EXP-001` | Clean video export разрешает только items, покрытые Mini Release, Release Pack или Active Artist; clean cover требует отдельный `Clean Cover` entitlement. Preview не разрешает clean download. | Запрос clean item/cover проверяет соответствующий entitlement и связывает его с project/operation; video entitlement не открывает cover автоматически. |
| `FR-EXP-002` | Mini Release должен требовать явный выбор ровно шести из 18 items; система должна предложить редактируемый default top-6. | Пять или семь selections не подтверждаются; замена default item возможна до paid render; итоговый manifest содержит ровно шесть IDs. |
| `FR-EXP-003` | Release Pack и Active Artist monthly entitlement должны разрешать все 18 items текущей выбранной campaign revision. | Batch operation создаёт или переиспользует 18 paid render versions; manifest не пропускает item без явного failed status. |
| `FR-EXP-004` | Paid video product должен выдавать clean MP4 для покрытых items, а `Clean Cover $2` — approved cover 3000×3000; service watermark, URL или рекламная плашка не добавляются. | Pixel/text inspection соответствующего output не находит service overlay; без cover entitlement artwork остаётся protected. |
| `FR-EXP-005` | Файлы должны получать стабильные безопасные имена с order, day, phase, hook/template slug и revision без raw path/control characters. | Повторная сборка той же revision даёт те же filenames; Unicode metadata корректно slugifies, не создавая collision или traversal. |
| `FR-EXP-006` | Video bundle должен содержать `videos`, `copy/campaign.csv`, `copy/campaign.txt`, `calendar/calendar.csv`, `calendar/calendar.ics` и `manifest.json`. Clean cover не копируется в video ZIP: она выдаётся отдельно по короткоживущей signed URL только пока активен `$2` entitlement. | Mini содержит ровно 6 MP4, Pack — ровно 18; ZIP никогда не открывает cover в обход её ACL/refund; manifest перечисляет каждый included file/hash. |
| `FR-EXP-007` | Bundle должен хранить один platform-neutral MP4 на campaign item и не дублировать его для TikTok, Shorts, Reels и VK Clips; destination variants хранятся только в copy. | Release Pack ZIP содержит 18 MP4, а не 72; copy records содержат четыре destination payloads на соответствующий item ID. |
| `FR-EXP-008` | Готовый bundle должен сохраняться как immutable `ExportBundle`, отображаться в history и скачиваться через ограниченный signed URL по `CFG-DOWNLOAD-*`. | Повторное открытие history показывает product, revision, item count и created time; expired URL не работает, новый выдаётся только владельцу. |

## Preview ranking

Default preview item выбирается детерминированно:

1. vocal project: сначала `Chorus · KineticLyrics`;
2. instrumental project: сначала `EnergyPeak · VisualLoopA`;
3. если preferred item имеет blocking warning, выбирается следующий ready item по campaign order;
4. пользователь может изменить выбранный preview item до первого preview render;
5. после первого успешного render смена plan revision только помечает preview stale и обновляет posters.

## Export validation

Bundle считается готовым, когда:

- все включённые MP4 имеют валидный `RenderVersion`;
- item count соответствует entitlement;
- calendar и copy ссылаются только на включённые campaign IDs;
- hashes manifest совпадают с фактическими bytes;
- ZIP можно открыть и проверить до выдачи signed URL.

Clean cover валидируется отдельно: она должна соответствовать купленным approved artwork revision/composition, profile 3000×3000 sRGB и активному cover entitlement.
