# Рефакторинг BorderlessWindow + фиксы «зазор при движении границы» и «призрак левой границы»

> Утверждённый пользователем план. Статус выполнения — см. `AGENT_LOG.md` рядом с этим файлом.

## Контекст

- Исходник: `refactor/original/BorderlessWindow.cs` (5546 строк) — один класс, десятки подсистем, много мёртвых экспериментов и диагностики.
- Стенд пользователя: Win11, 4K @150%, `net10.0-windows`. **В песочнице нет компилятора C#** — проверка только чтением/балансом скобок; сборку и поведение проверяет пользователь.
- Решения пользователя (подтверждены): partial classes; удалить все мёртвые эксперименты и путь ControlzEx; удалить диагностику TsLog/SnapLog полностью; новые фиксы — за const-флагами, по умолчанию ВКЛ.
- Жёсткие ограничения (из handoff'ов): НЕ ломать рабочее (unsnap-restore Variant A/A+/A++, DivGrip joint-resize, PassiveFollow, tile-group, free-edge grip, edge-watcher, маски/анимации); НЕ повторять доказанные тупики (fill/mask/overlay для зазора; WVR_VALIDRECTS/NOCOPYBITS/frame-sync/deferred/софт-рендер для призрака); НЕ менять `CaptionUnsnapRestoreThresholdDip=20.0`.

## Часть 1 — Разбиение на partial classes

Создать в `refactor/Controls/` (namespace `ControlPanel`, класс `BorderlessWindow : Window`):

| Файл | Содержимое |
|---|---|
| `BorderlessWindow.cs` | Ядро: константы-флаги (единая карта), конструктор, `OnSourceInitialized`, `OnContentRendered`, `OnStateChanged`, `OnDpiChanged`, mouse-overrides, `OnClosing`, `WindowProc` (диспетчер сообщений) |
| `BorderlessWindow.Interop.cs` | Все P/Invoke, структуры (RECT, POINT, WINDOWPOS, NCCALCSIZE_PARAMS, MONITORINFO...), WM/HT/SWP/DWM константы, общие хелперы (`TryGetVisibleBounds`, `TryGetWorkArea`, `Near`, `RectEquals`...) |
| `BorderlessWindow.Chrome.cs` | `EnsureResizeStyles`, `ApplyThemedSystemFrame`, `ApplyThemedBorderMetrics`, `ThemedFrameNcCalcSize`, `ThemedHitTest`, `AdjustMaximizedBounds`, `IsInDraggableCaption`, `IsOverTitleInteractive`, `UpdateSnapDwmBorderColor` |
| `BorderlessWindow.SnapResize.cs` | DivGrip-оверлеи, `UpdateDividerJointResize(V)`, `ApplyDividerBatch`, co-tiles, free-edge grip, `FindSnapNeighbors(V)`, `TryGetSnapInternalEdges`, `FollowFrameResizeNeighbors`, `AnchorUnsnapResize`, snap-follow таймер, PassiveFollow (+поиск соседей, tile-group), `ApplySnapLayoutGapFix`, joint-resize cursor |
| `BorderlessWindow.Unsnap.cs` | Caption-unsnap-restore (`TryBeginCaptionUnsnapRestoreDrag` → `EndCaptionUnsnapRestoreDrag`, manual move, handoff to shell), WinEvent-hook (`MaybeSuppressUnsnapResnapFrame`, `OnUnsnapWinEvent`, `FinishUnsnapWinEventRestore`), restore-cache |
| `BorderlessWindow.Animation.cs` | Cloak/uncloak, скриншот-маска (`StartScreenshotCrossfade`, `ScreenshotFadeLoop`, `UpdateShotAlpha`, `CleanupScreenshotMask`, `TryGetMaskRect`, `EndMask`), `StartMaskReveal`, `StartRestoreReveal`, `AnimateToWindowState`, `AnimateMinimize`, `SmoothStep` |
| `BorderlessWindow.Taskbar.cs` | Edge-watcher (auto-hide панель), `ShrinkBottom`, gap-mask, курсор-драйв, `RestorePlacement`/`SavePlacement`, `CenterOnPrimaryScreen`, `GetMonitorSignature` |

Реализация: прочитать исходник целиком по кускам, переносить методы 1:1 (кроме удалений и фиксов ниже), сверять баланс скобок каждого файла. XAML не трогается (среда его не поддерживает; точки соприкосновения — только `TitleBar`/кнопки через SystemCommands + `WindowChrome.IsHitTestVisibleInChrome`, они завязаны на `ThemedHitTest`/`IsOverTitleInteractive` — сигнатуры сохраняются).

## Часть 2 — Удаления (только confirmed-dead)

1. **Диагностика полностью**: `EnableTroubleshootLog`, `TsLog`, `SnapLog`, `LogIncoming`, `LogHit`, `LogFirstUpdate`, `LogNearestRejectedNeighbor`, `LogPassiveCandidates`, `RectStr`, `WorkStr`, `HitName`, `GetWndClass` (если используется только логами), поля `_lastPf*LogTick`, `_lastDivMoveLogTick`, `_lastLoggedHit`, `_snapDiagLock`, `_captionUnsnapMoveTickCount` и все call-site'ы.
2. **ControlzEx**: `AttachControlzExChrome`, `WindowChromeBehavior`-attach, `UseThemedSystemFrame` инлайнится как единственный режим (флаг оставить как const=true документально или убрать ветки `if (UseThemedSystemFrame)` → всегда true). Пометка пользователю: убрать PackageReference из csproj.
3. **Мёртвые эксперименты** (флаг + код): `EnableDividerGuideLine` (+`EnsureDivGuide*`, `ShowDivGuideAt`, `HideDivGuide`, поля/консты), `EnableDividerDeferredResize` (+`_divFsPending*`), `EnableDividerFrameSync`/`...DwmFlush` (+`DivFsEnsureHook/DivFsUnhook/DivFsOnRender`, `_divFs*`), `EnableDividerSingleBatch`, `EnableDividerNoCopyBits`, `TryBuildAlignedValidRects` + ветка `EnableFullRedrawOnOriginMove=false` (WVR_REDRAW становится безусловным), `ShrinkThemedFrame`-остатки, `IncludeCaptionForSnap`/`DisableDwmNcRendering` toolkit (`SetWindowThemeAttribute`, `WTA_OPTIONS`, `WTNCA_*`, `DWMNCRP_*` — если не используются живым кодом).
4. **Старая серая маска**: `StartMaskCrossfade`, `MaskFadeLoop`, `Ease` (handoff подтверждает — не вызываются), `_captionUnsnapWatchdog` (handoff: бесполезен) — предварительно перепроверить по коду, что реально нет вызовов.
5. Неиспользуемые P/Invoke после чисток (проверить каждый грэпом по итоговым файлам).

НЕ удалять: двухпроходный `ApplyDividerBatch` (остаётся как fallback-путь под флагом нового фикса), `EnableSuppressResizeBitBlt`, `AllowBitBltForOffscreenResize`, `EdgeClampMaxOverhang`, `ncLargeOverhang`, `NcRedrawSkip`, весь unsnap Variant A/A+/A++.

## Часть 3 — Фикс 1: зазор при движении границы (`EnableSeamGapFix = true`)

**Диагноз по коду+логам**: зазор = межкадровое расхождение двух РАЗДЕЛЬНЫХ коммитов геометрии:
- путь DivGrip: предиктивный `ApplyDividerBatch(moveOur=false)` реально двигает соседей → отдельный кадр → потом финальный batch двигает нас;
- путь frame-resize (наш край тянется OS-циклом): `FollowFrameResizeNeighbors` двигает соседа ПОСЛЕ `WM_WINDOWPOSCHANGED` нашего окна → сосед отстаёт на кадр; в логе `refactor/logs/snap-joint-resize-gap.log` видно CacheSet-поток с дельтами до ~60px/кадр — это и есть ширина зазора.

**��ешение — «grower-first, shrinker-second» + один атомарный batch (геометрия/тайминг, без рисования)**:
1. **DivGrip-путь**: заменить предиктивный реальный сдвиг соседей на запрос `WM_GETMINMAXINFO` соседа (клампим `effDiv` по его `ptMinTrackSize` заранее, без движения) → остаётся ОДИН атомарный `BeginDefer/EndDeferWindowPos` на кадр (наше окно + соседи + co-tiles вместе). Нет двойного транзакта → нет межкадрового шва. Двойного move нашего окна тоже нет (effDiv уже скорректирован) → ghost-trail регрессия исключена по построению.
   - Fallback: при `EnableSeamGapFix=false` работает прежняя двухпроходная схема (код сохраняется).
   - Страховка от «наложения на мин. ширине» (причина отказа от старого single-batch): если после коммита фактический край соседа ≠ предсказанному (редкий кламп ОС сверх MINMAXINFO), на СЛЕДУЮЩЕМ кадре effDiv корректируется по факту — так уже работает существующий realign (`ArmDivReleaseRealign` на отпускании остаётся).
2. **Frame-resize путь**: перенести догон соседа из `WM_WINDOWPOSCHANGED` в `WM_WINDOWPOSCHANGING`: по pending-rect считаем новый край соседа и коммитим его ДО того, как применится наш кадр, НО только когда сосед **растёт** (наш край отступает). Когда сосед сжимается (мы растём) — оставляем догон после (наше растущее окно само накрывает шов). Инвариант: любое переходное состояние = перекрытие (невидимо, оба окна непрозрачны), никогда не зазор.

## Часть 4 — Фикс 2: призрак при движении левой границы (`EnableLeftEdgeGhostGuard = true`)

**Опора на доказанный факт** (resize-ghost handoff): «как только между движущимся краем и клиентом есть рамка — призрака НЕТ», и в коде это уже РАБОТАЕТ для верха: `ThemedFrameInset=1` («ghost-guard сверху»). Призрак остался только на левом крае — потому что слева client доходит до края окна (full-bleed). Симметричный фикс никто не пробовал.

**Решение**: в `ThemedFrameNcCalcSize` добавить такой же 1px-inset на ЛЕВОМ крае. NC-полоса красится DWM в `ThemeBorderColorRef=0x00403432` — это ровно цвет `BorderBrush` XAML, т.е. визуально ничего не меняется (левый 1px границы просто становится DWM-owned вместо WPF-owned). Растяг redirection-bitmap перестаёт доставать до движущегося края — по той же механике, что уже спасла верх.
- ~~Компенсация двойной границы: при активном флаге выставлять `BorderThickness.Left=0` из кода (`ApplyThemedBorderMetrics`), чтобы суммарно осталось 1px.~~ **ОТМЕНЕНО АУДИТОМ (2026-07-05)**: предпосылка «NC-полоса красится DWM в `ThemeBorderColorRef`» верна только для SNAPPED-окна с внутренним разделителем; у floating `UpdateSnapDwmBorderColor` ставит `DWMWA_COLOR_NONE` → полоса не красится, и при `Left=0` floating-окно теряло бы видимую левую линию рамки. Правильно — точная симметрия с верхним guard'ом (там inset и top-бордер 1px сосуществуют): `BorderThickness.Left` ОСТАЁТСЯ 1. Итоговое поведение подтверждено пользователем как желаемое: боковые границы 1 физ. px у floating, 2 физ. px при снапе (WPF-бордер + окрашенная DWM-полоса).
- Вариант-эскалация (в IDEAS.md, не включать): если 1px не хватит — динамический inset 2px только на время активного left-drag (`_divDragging`/`_sizingEdge`), со снятием через `SWP_FRAMECHANGED` на отпускании.
- Учет взаимодействий: `ApplySnapLayoutGapFix`, клампы к work-area и `EdgeClampMaxOverhang` в NCCALCSIZE применяются к client ПОСЛЕ inset'а — порядок сохранить как у верхнего inset'а; hit-test не меняется (левая зона ресайза и так снаружи client).

## Часть 5 — Порядок работ и верификация

1. Скопировать исходник (сделано: `refactor/original/BorderlessWindow.cs`), прочитать полностью по кускам.
2. Написать 7 partial-файлов с переносом кода 1:1 + удаления Части 2.
3. Внедрить фиксы Частей 3–4 за флагами (default true), старые пути сохранить как fallback при false.
4. Самопроверка: баланс `{}`/`()` каждого файла; грэп на каждый удалённый символ (0 вхождений в живом коде); грэп на каждый вызываемый метод (объявление существует); сверка списка const-флагов с handoff-картой.
5. Написать `IDEAS.md` — нереализованные мысли/предложения (эскалация ghost-guard, транзитивная связность 4-оконной группы, удаление ControlzEx из csproj, возможный перенос флагов в настройки и т.п.).
6. Итоговое резюме для пользователя: что удалено, что перенесено, как выключить каждый новый фикс, сценарии ручного теста (7 сценариев: snap+joint-resize, 3 колонки, free-edge grip, unsnap протяжкой, off-screen, курсор ↔, старт/минимизация/фуллскрин-анимации).

## Риски

- Нет компилятора: возможны опечатки при переносе → митигируется механическим 1:1 переносом и грэп-сверками.
- Удаление `UseThemedSystemFrame`-веток: делать консервативно — если ветка сомнительна, оставить флаг const=true вместо инлайна.
- Фикс 1 меняет структуру батча — старый путь остаётся под флагом, откат одной константой.
- Фикс 2 меняет client-rect на 1px слева — вся логика, читающая client (маски, грипы), использует window/visible bounds, не client; перепроверить по коду при реализации.
