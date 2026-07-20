# 01. Аккаунт и brand kit

Источник: [Product Plan, §§ 2, 4.2, 5, 6](../base/Hook2Stream_Product_Plan.md).

Документ описывает public entry point, персональный workspace и визуальные defaults. В MVP нет команд, ролей и нескольких брендов в одном workspace.

## ACC — аккаунт, landing и workspace

| ID | Требование | Проверка |
|---|---|---|
| `FR-ACC-001` | Система должна позволять посетителю зарегистрироваться поддерживаемым managed-auth способом, принять действующие Terms/Privacy и создать одну пользовательскую identity. | Новый посетитель с валидными данными завершает регистрацию и получает авторизованную сессию; без принятия обязательных документов регистрация не завершается. |
| `FR-ACC-002` | Система должна поддерживать повторный вход, выход и восстановление доступа без создания новой identity или потери ранее созданных релизов. | После logout и recovery пользователь входит в ту же identity и видит прежние projects, purchases и exports. |
| `FR-ACC-003` | При первой регистрации система должна создать персональный workspace и сделать пользователя его единственным владельцем. | Первый вход создаёт ровно один workspace; повторный вход не создаёт дополнительный workspace. |
| `FR-ACC-004` | Система должна проверять принадлежность workspace для каждого project, asset, job, brand kit, purchase и export и не раскрывать существование чужого ресурса по известному ID. | Запрос собственного ресурса разрешён; запрос ID другого workspace возвращает безопасный отказ без данных, filename, status или download URL. |
| `FR-ACC-005` | Public landing должен показывать оффер `One song. Three weeks of ready-to-post lyric shorts`, основной вход «один MP3», автоматические transcript/artwork/video drafts, ручной review, 18 роликов, тарифы и отсутствие обещания вирусности. | До регистрации понятны MP3-first flow, 21-дневный результат, один бесплатный preview и цены; landing не требует готовую cover/visuals и не рекламирует автопубликацию/text-to-video. |
| `FR-ACC-006` | Авторизованный dashboard должен позволять создать релиз, видеть recent projects, progress, готовые items, текущий entitlement и историю export bundles; пустой workspace должен показывать first-run guidance. | Workspace с данными показывает актуальные counts и переходы; новый workspace показывает пример и `New release`, а не пустую техническую таблицу. |

## BRD — brand kit

| ID | Требование | Проверка |
|---|---|---|
| `FR-BRD-001` | Система должна позволять сохранить display name артиста, primary/secondary/accent colors, heading/body fonts, стандартный CTA, smart link и tone restrictions. | Валидные значения сохраняются, повторно открываются без потери и используются как defaults нового release project. |
| `FR-BRD-002` | Система должна позволять добавить необязательные изображения персонажа или маскота и явно отключить character layer. | Проект без character assets остаётся валидным; при добавлении asset он доступен для поддерживаемых templates, а при отключении не попадает в compositions. |
| `FR-BRD-003` | Если пользователь не задал palette/fonts, система должна использовать доступные безопасные defaults, затем предложить редактируемую palette из утверждённой обложки и поддерживаемого font allowlist. | MP3-only draft остаётся валидным до cover; после approval получает читаемую palette/fonts до campaign generation, каждый default редактируем. |
| `FR-BRD-004` | Система должна валидировать colors, контраст, font availability, asset type и лицензионно разрешённый allowlist и заменять недоступное значение с явным предупреждением. | Недопустимый font или нечитаемая комбинация не запускают скрытый fallback: UI показывает проблему и выбранную замену до preview. |
| `FR-BRD-005` | Сохранённый brand kit переиспользуется в следующих релизах; его изменение не мутирует уже созданные revisions, а автоматически supersede-ит current plan и запускает новый plan с новым snapshot. | Новый project получает актуальные defaults; исторический/оплаченный plan остаётся воспроизводимым по старому snapshot, а current workflow явно показывает regeneration. |
| `FR-BRD-006` | При создании или обновлении campaign plan система должна фиксировать immutable `BrandKitSnapshot` с version, values и asset references. | Manifest и каждый `CompositionSpec` ссылаются на конкретную snapshot version; удаление исходного global setting не меняет готовый render. |

## Инварианты

- Один MVP workspace принадлежит одному пользователю.
- Один workspace имеет один активный reusable brand kit.
- Brand kit не является обязательным входом: безопасные defaults обязательны.
- Template никогда не загружает произвольный font или executable asset из пользовательского ввода.
