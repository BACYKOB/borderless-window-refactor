# BorderlessWindow — HANDOFF (В РАБОТЕ, НЕ release-ready — см. «СЕССИЯ 2026-07-02»)

> СТАТУС НА 2026-07-02: из двух ранее открытых багов **BUG2** (joint-resize/тайлинг, задачи 1/2/5) РЕШЁН
> в этой сессии новым подходом (оверлеи-грипы `DivGrip` + passive-follow + атомарный batch —
> см. «СЕССИЯ 2026-07-02»); финальный тест пользователя пройден, кроме двух остаточных
> дефектов (4-оконный диагональный отрыв и косметический зазор — описаны в конце).
> Задачи 1.1, 1.2, 4 решены и подтверждены.
> Разделы ниже с пометкой ⚠️ УСТАРЕЛО — это история, а не текущее состояние.

> 🆕 СЕССИЯ 2026-07-02: крупная новая работа (passive-follow, tear-off, tile-group, repaint-фиксы, +~2000 строк,
> файл теперь 5022 строки) задокументирована в конце — раздел «СЕССИЯ 2026-07-02».

## Статус задач

| # | Задача | Статус | Механизм |
|---|---|---|---|
| 1 | Joint-resize ползунком в Snap-группе (Win11) | ✅ РЕШЕНО (сессия 2026-07-02) | переписано через прозрачные оверлеи-грипы `DivGrip` над внутренними разделителями + атомарный `ApplyDividerBatch` — см. «СЕССИЯ 2026-07-02» §0,§5 |
| 1.1 | Анти-скачок при un-snap-ресайзе | ✅ РЕШЕНО | `EnableAnchorUnsnapResize` + `AnchorUnsnapResize` (WM_SIZING + якорь) |
| 1.2 | Широкий хват свободного края snapped-окна | ✅ РЕШЕНО | `EnableFreeEdgeGrip` + внешний прозрачный layered-оверлей (`FreeEdgeGripPx=12`, было 7) |
| 2 | 3-колоночная раскладка (joint-resize центра) | ✅ РЕШЕНО (сессия 2026-07-02) | `DivGrip` + `ApplyDividerBatch` двигают наше окно и всех соседей одной транзакцией; финальный тест пройден. Остаётся 4-оконный диагональный отрыв + косметический зазор (в конце) |
| 4 | Off-screen клон при ресайзе наполовину за экраном | ✅ РЕШЕНО | `EdgeClampMaxOverhang=32` — guard на work-area clamp в `ThemedFrameNcCalcSize` |
| 5 | Курсор joint-resize появляется только в полосе 2px | ✅ РЕШЕНО (сессия 2026-07-02) | прозрачные оверлеи `DivGrip` над разделителями ловят курсор/латч надёжно (заменили узкую полосу `WM_SETCURSOR`) — см. «СЕССИЯ 2026-07-02» §0 |

## БАГ 2 (РЕШЁН в сессии 2026-07-02)

> ИТОГ на 2026-07-02:
> — **BUG2** (задачи 1/2/5) РЕШЁН в этой сессии новым подходом (см. «СЕССИЯ 2026-07-02»).
>   Разбор BUG2 ниже — это ИСТОРИЯ старого (заброшенного) подхода SnapFollow-латча; сохранён для контекста.
> Контекст стенда ниже относится к BUG2 и к версии-откату, в которой он диагностировался.

### Контекст стенда (для воспроизведения)

- Один 4K-монитор: 3840×2160 физ., масштаб 150% → 2560×1440 логических.
- **Все координаты в troubleshoot-логах — ФИЗИЧЕСКИЕ пиксели.**
- Окно borderless (`WindowStyle=None`, без `WS_CAPTION`) + ControlzEx `WindowChromeBehavior`.
- Диаг-лог: `EnableTroubleshootLog=true` → `%LOCALAPPDATA%\ControlPanel\troubleshoot.log` (метод `TsLog`).
- Присланный «откат без деградаций» (3215 строк / 208711 байт) — это версия С диагностикой и Win32-свопами
  caption-unsnap, но БЕЗ блока `WH_MOUSE_LL` (тот дал деградацию). BUG2 в ней воспроизводится.

### БАГ 2 — joint-resize в 3-колоночной раскладке ✅ РЕШЁН в сессии 2026-07-02 (разбор ниже — ИСТОРИЯ)

> ⚠️ УСТАРЕЛО (подход): анализ и план патча ниже относятся к СТАРОМУ подходу `SnapFollow`-латча по полосе
> курсора, который НЕ был собран. В сессии 2026-07-02 задача решена ИНАЧЕ — через прозрачные оверлеи-грипы
> `DivGrip` над разделителями + атомарный `ApplyDividerBatch` (двигает все окна группы одной транзакцией,
> устраняя симптом B «уехало без соседа») + tear-off gate `HasFlushTileNeighborH` (симптом «отрыв»). См.
> «СЕССИЯ 2026-07-02» §0,§4,§5. Финальный тест пройден; остаточные дефекты (4-оконный диагональный отрыв,
> косметический зазор) описаны в конце файла. Ниже — исходный разбор симптомов и заброшенный план (история).

**Симптом (со слов пользователя):**
- (A) В 3 колонки (наше окно — широкое посередине) курсор ↔ / латч joint-resize не появляется ~25% случаев.
- (B) При перетягивании границы стягивается только один сосед, либо НАШЕ окно уезжает без соседа — раскладка
  рвётся. Правая граница — тянется только правый сосед; левая (на 2–3й раз) — наше окно уехало без соседа.

**Ключевые наблюдения (troubleshoot4.log, фаза 3-колонок ~00:52:xx):**
- (A) **НИ ОДНОГО `SfLatch`** во всей 3-колоночной фазе (все 5 SfLatch — из 2-колоночной фазы 00:51:52–55) →
  `SnapFollow` в 3-колонке вообще не зацепляется.
- `TsJrc fail=band` 194× при `vis=(954,2880)` + 50× при `vis=(1049,2880)`: края детектируются (`sl=True sr=True`),
  но курсор НИ РАЗУ не попал в полосу 12px. Ближайший замер WM_SETCURSOR `dl=13` — промах в 1px относительно
  `SnapFollowGrabBandPx=12`.
- `TsJrc fail=no-edges` 111×.
- (B) Поток `CacheSet` ~00:52:10 с `hasInternalDiv=False` — чистый MOVE окна (оба края смещаются одинаково ~5px)
  = симптом B «уехало без соседа». `CapDown` в этой фазе НЕТ → это НЕ caption-unsnap.

**Механизм (по чтению кода отката):**
- `TryGetSnapInternalEdges` для среднего столбца опирается на
  `spansWorkHeight = Near(top,workTop,tol) && Near(bottom,workBottom,tol)`, где `tol = Max(2, GetResizeGrip+2)`
  (~13–14px при 150%). При неточном совпадении высоты столбец не распознаётся как snapped → внутренние границы
  не отдаются → латч не происходит (симптом A).
- `ThemedHitTest` возвращает HTNOWHERE на внутренних границах, когда `DeferJointResizeToShell && snapLeft/Right`.
- `SnapFollow_Tick` латчится на `justPressed` в полосе 12px и ведёт соседа по HWND (`LatchNeighborGapPx=12`).
- Полоса 12px при 150% DPI слишком узкая (промах `dl=13`) → курсор/латч не появляются ~25% (симптом A).

**Запланированный, но НЕ собранный / НЕ протестированный патч (FS сбросился до сборки):**
- Отдельная, более широкая полоса латча `SnapFollowLatchBandPx ≈ 24–28` (отдельно от курсорной 12px).
- Расширить `spanTol` (`SnapSpanTolPx ≈ 28`) в `TryGetSnapInternalEdges`, чтобы средний столбец стабильно
  распознавался как snapped.
- Добавить диагностику `SfLatchMiss` и `HitOfferSide` (видеть промахи латча и предлагаемый край).
- Проверить, что нет ложного «snapped»-детекта на плавающих окнах.

**Рекомендации следующему агенту:**
- Симптом B: при латче вести ТОЛЬКО общий внутренний край с соседом по HWND; запретить чистый MOVE окна, когда
  внутренний делитель не найден (`hasInternalDiv=False`).
- Симптом A: расширить полосы (патч выше) и перепроверить tolerance `spansWorkHeight` при 150% DPI.

### Добавленный диагностический/тестовый код (УБРАТЬ перед релизом)

> ℹ️ Это диагностика ПРОШЛЫХ сессий. Сессия 2026-07-02 добавила ЕЩЁ теги (`PfWatch/PfFollow/PfCand/DivGrip/
> DivMove/TsJrc/NcCalc/NcRedrawSkip/FsnMiss`) — см. «СЕССИЯ 2026-07-02 → §8». Убирать нужно ВСЕ.

- `EnableTroubleshootLog` (bool, сейчас `true`) + метод `TsLog` → `%LOCALAPPDATA%\ControlPanel\troubleshoot.log`.
- `EnableSnapDiagLog=false` (const) + `SnapLog` / `LogHit` / `LogIncoming` (dormant).
- `_captionUnsnapWatchdog` — DispatcherTimer(15мс); оказался бесполезен.
- Win32-свопы caption-unsnap: P/Invoke `SetCapture` / `GetCapture`, обработка `WM_NCLBUTTONDOWN` / `WM_SYSCOMMAND`.
- Теги логов: `CapDown / UpdFirst / CapThreshold / CapRestoreMove / CapEnd / CacheSet / GetRestore / SfLatch /
  SfMove / WmCaptureChanged / WmNcLBtnDown SWALLOW / WmSysCmd SWALLOW / WmLBtnUp /
  TsJrc fail=(no-edges/no-vert/no-vis/Y-out/band)`.
- **Блок `WH_MOUSE_LL` в этом откате ОТСУТСТВУЕТ** — давал деградацию, в прежнем виде не восстанавливать.
- Перед релизом: `EnableTroubleshootLog → false`, убрать `TsLog` и все перечисленные диаг-теги/хуки.

### Полезные константы и коды

- `SnapFollowGrabBandPx=12`, `LatchNeighborGapPx=12`, `SnapFollowMinDimPx=200`, `SnapSettleTicks=20`,
  `CaptionUnsnapRestoreThresholdPx=30`, `EdgeClampMaxOverhang=32`, `NeighborTouchPx=12`, `FreeEdgeGripPx=7` (в откате; в текущей версии 12),
  `DeferJointResizeToShell=true`. Планировались (не собраны): `SnapFollowLatchBandPx=28`, `SnapSpanTolPx=28`.
- HT/WM: HTNOWHERE=0, HTCAPTION=2, HTLEFT=10, HTRIGHT=11; WM_SETCURSOR=0x0020, WM_NCHITTEST=0x0084,
  WM_NCLBUTTONDOWN=0x00A1, WM_LBUTTONUP=0x0202, WM_MOUSEMOVE=0x0200, WM_SYSCOMMAND=0x0112,
  WM_CAPTURECHANGED=0x0215; SC_MOVE=0xF010, SC_SIZE=0xF000; IDC_SIZEWE=32644.
- Ключевые места кода (откат, 3215 строк): `OnMouseLeftButtonDown`, `WindowProc`, `TrySetJointResizeCursor`,
  `TryGetSnapInternalEdges`, `SnapFollow_Tick`, регион caption-unsnap (`TryBeginCaptionUnsnapRestoreDrag` /
  `EndCaptionUnsnapRestoreDrag`).

### Текущее состояние / точка передачи

> ⚠️ УСТАРЕЛО: описывает состояние ДО сессии 2026-07-02. Актуальная авторитетная версия — `BorderlessWindow.cs`,
> **5022 строки**, с фиксами этой сессии (passive-follow, tear-off, repaint). Финальный тест
> пользователя пройден (кроме 4-оконного режима и косметического зазора).

- Пользователь откатился на версию без `WH_MOUSE_LL` (присланный cs, 3215 строк) — без деградации, но BUG2
  в ней воспроизводится.
- Патч БАГА 2 (широкая полоса латча + span tol + диагностики `SfLatchMiss` / `HitOfferSide`) НЕ собран и НЕ
  протестирован — песочница сбросилась до сборки.
- Задачи 1.1, 1.2, 4 — решены и подтверждены, не трогать.


## Архитектура (что важно знать)

- Окно `WindowStyle=None`, `UseThemedSystemFrame=true`, БЕЗ `WS_CAPTION` (это ЖЁСТКОЕ ограничение пользователя).
- Joint-resize ползунком Win11 невозможен через shell без `WS_CAPTION`, поэтому реализован вручную через
  «snap-follow»: таймер 15мс, HWND-lock соседей слева/справа на ЛКМ у внутренней границы, наш край догоняет
  край зафиксированного соседа.
- Видимые границы окон берутся через `DWMWA_EXTENDED_FRAME_BOUNDS` (без невидимого sizing-выступа DWM).
- Стенд пользователя: 3840×2160, высокий DPI.

## Ключевые механизмы (все ВКЛЮЧЕНЫ)

> ℹ️ Ниже — механизмы прошлых сессий. Механизмы этой сессии (PassiveFollow, DivGrip joint-resize, tear-off
> gate, атомарный batch разделителя, repaint-фикс) — в разделе «СЕССИЯ 2026-07-02 → §0–§6».

| Флаг | Назначение |
|---|---|
| `UseThemedSystemFrame=true` | Главный режим рендера (themed NCCALCSIZE: верх 1px-insert, бок/низ системные) |
| `ApplyDwmFrameAttributes=true` | DWM caption-color = фон, immersive dark, border-color |
| `EnableSnapFollow=true` | Snap-follow: ловим joint-resize и тянем наш край за соседом |
| `EnableJointResizeCursor=true` | Курсор ↔ на внутренних snap-границах в полосе `SnapFollowGrabBandPx`=12 px |
| `EnableAnchorUnsnapResize=true` | Якорь WM_SIZING: un-snap не сдвигает удерживаемый край |
| `EnableFreeEdgeGrip=true` | Внешний прозрачный grip-оверлей у свободного края (`FreeEdgeGripPx=12`) |
| `EnableCaptionUnsnapRestoreDrag=true` | Restore pre-snap rect по протяжке шапки (порог SM_CXDRAG/CYDRAG, свой move-loop) |
| `EnableSnapLayoutGapFix=true` | Гашение горизонтального зазора между snap-соседями (1px-thumb в NCCALCSIZE) |
| `DeferJointResizeToShell=true` | На внутренних snap-границах HitTest=HTNOWHERE → не un-снап-каемся |
| `EnableSuppressResizeBitBlt` (true) | WM_WINDOWPOSCHANGING: SWP_NOCOPYBITS при ресайзе — анти-призрак, кроме off-screen-ресайза |
| `AllowBitBltForOffscreenResize=true` | При ресайзе пересекающего экран окна разрешаем BitBlt — анти-клон-на-границе |
| `EnableStartupMask` (true) | Маска перехода при старте/восстановлении — без вспышек |
| `EnableGapMask` (true) | Маска зазора при snap → нормал |
| `EnableCursorDrive` (true) | Курсор-драйв при модальном ресайзе |

## Off-screen clone fix (задача 4)

Корень бага: `ThemedFrameNcCalcSize` зажимал client-rect к `MonitorFromWindow.rcWork`, и при перетягивании
окна частично ЗА экран (overhang в сотни px) клиент «оставался» зажатым к work-area → DWM выдавал клон-полосу
на краю экрана при последующем ресайзе.

Решение — `EdgeClampMaxOverhang=32` (px): клампим только small overhang (snap-выступ ~6–10px), большой
overhang (drag за экран) пропускаем без зажима. Каждая из 4 сторон в `ThemedFrameNcCalcSize`:

```csharp
int clampMax = EdgeClampMaxOverhang > 0 ? EdgeClampMaxOverhang : int.MaxValue;
if (wl <= wa.Left && wa.Left - wl <= clampMax) calc.rgrc0.Left = wa.Left;
// аналогично Top/Right/Bottom
```

Дополнительно: на `WM_WINDOWPOSCHANGED` для окна, реально пересекающего границу монитора
(`FloatingWindowCrossesMonitor`), вызывается `RedrawWindow(RDW_INVALIDATE|RDW_UPDATENOW|RDW_ERASE|RDW_FRAME|RDW_ALLCHILDREN)`.
И при `WM_WINDOWPOSCHANGING` в режиме user-resize off-screen-окна СНИМАЕТСЯ `SWP_NOCOPYBITS`
(`AllowBitBltForOffscreenResize=true`) — иначе DWM не восстановит освобождённую полосу.

## Joint-resize cursor (задача 5)

Проблема: внутренние snap-границы возвращают HitTest=HTNOWHERE (это нужно для DeferJointResizeToShell), и
курсор ↔ появлялся только при наведении на 2px видимую границу. У стандартных окон Windows полоса шире.

Решение — обработка `WM_SETCURSOR` в основном `WindowProc`:
```csharp
case WM_SETCURSOR:
    if (UseThemedSystemFrame && (lParam.ToInt64() & 0xFFFF) == HTCLIENT
        && TrySetJointResizeCursor(hwnd))
    { handled = true; return new IntPtr(1); }
    break;
```
`TrySetJointResizeCursor` проверяет: окно Normal, есть внутренняя snap-граница, курсор в видимой высоте окна,
расстояние до ближайшей внутренней границы ≤ `SnapFollowGrabBandPx`=12 px → `SetCursor(IDC_SIZEWE)`. Та же
полоса 12px используется для `SnapFollow` latch — поведение консистентно.

## Release-ready чистка (задача 6 — ПРОШЛАЯ сессия; ОТМЕНЕНА в сессии 2026-07-02)

> ⚠️ УСТАРЕЛО: эта чистка выполнялась в ПРОШЛОЙ сессии. В сессии 2026-07-02 диагностика ВОЗВРАЩЕНА
> (`EnableTroubleshootLog=true`, новые теги — см. §8 в конце), файл вырос ~3027 → 5022 строк. Раздел ниже
> сохранён как история; текущее release-состояние НЕ достигнуто. Перед релизом чистку нужно повторить.

Файл был приведён в финальный вид: 3216 → **3027 строк**, 210887 → **194247 байт** (≈8%). Все удаления —
строго confirmed-dead / abandoned scaffolding; load-bearing код не тронут. Скобки сбалансированы (381/381),
удалённые символы отсутствуют в коде (грэп подтвердил).

### Что удалено

**Failed/abandoned эксперименты (полностью):**
- `EnableOffscreenCloakReset` + метод `ResetDwmSurfaceForOffscreen` — попытка чистить DWM-поверхность через
  cloak/uncloak; давала регрессию (мигание призрака смещённого окна), откачена → теперь снесена.
- `EnableUnsnapFrameRecompute` + метод `ForceFrameRecompute` — попытка SWP_FRAMECHANGED на выходе из move-цикла;
  ломала 3-колоночную раскладку, серую зону не убирала, откачена → снесена. Задача 3 решена иначе
  (CaptionUnsnapRestoreDrag).
- Patch-1 кластер off-screen vacated-region: методы `RepaintOffscreenVacatedRegion`, `RepaintOffscreenCrossingWindow`,
  `InvalidateDesktopBand`, `IntersectRect`, `IsEmptyRect`, `SubtractTop/Bottom/Left/Right` (8 методов). Не
  устраняли off-screen-клон (неверный механизм); реальный фикс — `EdgeClampMaxOverhang`. → снесены.
- `ref RECT` overload P/Invoke `RedrawWindow` (использовался только `InvalidateDesktopBand`).

**Мёртвые тестовые флаги (decl + use-site):**
- `MinimalResizeStyles` (false) — тестовая упрощённая маска WS_*; ternary заменён на прямое присваивание.
- `ScopeNoCopyBitsToUserResize` (false) — условие в WM_WINDOWPOSCHANGING упрощено.
- `SnapJointResizeHitSlopPx` (=2) — нигде не использовалось в коде.

**Прочее:**
- BUILD MARKER (`SnapLog("=== BUILD MARKER ... ===")` + комментарий) — диагностика подхвата сборки, больше
  не нужна.
- EXITSIZEMOVE: убраны `bool recompute=...`, `if(recompute) ForceFrameRecompute(...)`, `bool offscreenResizeCrossing=...`,
  `if(offscreenResizeCrossing){...}` (вместе с привязанными комментариями).

### Главный выигрыш производительности

`EnableSnapDiagLog` переведён с `static readonly = true` на `const = false`. Это даёт **DCE компилятором** всех
~24 веток `if (EnableSnapDiagLog) SnapLog(...)`, включая `LogIncoming` на КАЖДОМ оконном сообщении (десятки
раз в секунду при ресайзе/перемещении). Заодно полностью устраняется файловый I/O в
`%LocalAppData%\ControlPanel\snap-debug.log` в release-сборке.

Диагностическая инфраструктура (`SnapLog`, `LogIncoming`, `RectStr`, `WorkStr`, `HitName`, `_snapDiagLock`,
`_lastLoggedHit`) сохранена в виде dormant-кода — для будущей диагностики достаточно переключить флаг в `true`
(потом обратно в `false`).

### Что НАМЕРЕННО оставлено

- Документированные disabled-флаги `IncludeCaptionForSnap=false` и `DisableDwmNcRendering=false` (вместе с
  связанной interop: `SetWindowThemeAttribute`, `WTA_OPTIONS`, `WTNCA_*`, `DWMWA_NCRENDERING_POLICY`,
  `DWMNCRP_DISABLED`). Это документирует ЗАКРЫТЫЙ путь «возврат WS_CAPTION ради snap-группы» и сохраняет
  готовый toolkit для гашения NC-рендера если когда-либо понадобится. Const-false → нулевой runtime-cost
  (DCE), значительная документная ценность.
- Весь активный функционал: SnapFollow / 1.1 / 1.2 / 3 / off-screen edge-clamp / joint-resize cursor /
  startup mask / gap mask / edge watcher / Patch-2 bitblt-skip — НЕ ТРОГАЛСЯ.

## Карта основных флагов и констант (ПРОШЛАЯ сессия — НЕ финал)

> ⚠️ УСТАРЕЛО/НЕПОЛНО: не содержит констант сессии 2026-07-02 (Passive*/tile-group/repaint). Актуальные
> значения и новые константы — в разделе «СЕССИЯ 2026-07-02 → §7». `EnableTroubleshootLog` сейчас `true`.

```
SnapFollowGrabBandPx       = 12   // полоса близости курсора (latch + cursor)
SnapFollowMinDimPx         = 200
SnapSettleTicks            = 20
SnapInternalDividerGuardPx = 1
EdgeClampMaxOverhang       = 32   // off-screen clone fix
ThemedFrameInset           = 1
ResizeGripPx               = 6
ResizeGripThin             = 2
CaptionUnsnapRestoreThresholdPx = 30
FreeEdgeGripPx             = 12   // ⚠️ было 7; поднято в сессии 2026-07-02
NeighborTouchPx            = 12   // FreeEdgeGrip: «сосед вплотную»
ArmBand                    = 3
ArmTimeoutTicks            = 25
GapMaskHeight              = 64
MaskColorRef               = 0x001C1A19
ThemeBorderColorRef        = 0x00403432
EnableSnapDiagLog          = false (const, DCE)
```

## Окружение / сборка

- Сборка: `dotnet build`. Запуск: `dotnet run -c Debug`. Перед пересборкой убить процесс:
  `taskkill /F /IM ControlPanel.exe`.
- Лог диагностики (если включить флаг): `%LOCALAPPDATA%\ControlPanel\snap-debug.log`.

## Что проверить после применения этой чистки

Сборка должна пройти без ошибок. Поведенческая регрессия не ожидается (все удаления — const-false-флаги и
их use-site, либо never-called методы). Рекомендуется быстрая проверка по всем сценариям:
1. Snap половиной экрана + joint-resize ползунком — двигаются оба окна, без зазора.
2. Snap + свободный край — внешний хват ~12px работает, курсор ↔.
3. Un-snap протяжкой шапки — pre-snap размер восстановлен, серой зоны нет.
4. Off-screen ресайз — клон-полоса на краю экрана не остаётся.
5. Курсор joint-resize появляется в полосе ~12px у внутренней границы (а не только 2px).
6. 3-колоночная раскладка — joint-resize центра двигает оба соседа корректно.


---

## СЕССИЯ 2026-07-02 — passive-follow, tear-off, tile-group, repaint (НОВОЕ)

> Эта сессия добавила крупный новый слой поверх версии из прошлого HANDOFF. Файл вырос до **5022 строк**
> (`{}`=1035/1035, `()`=3044/3044). Ниже — назначение и механика КАЖДОГО нового костыля/фикса, чтобы
> агент-рефакторинг понимал, что load-bearing, а что можно упрощать. Все новые механизмы ВКЛЮЧЕНЫ и
> подтверждены пользователем на финальном тесте (кроме дефекта 4-оконного режима и косметического зазора — см. ниже).
> Номера строк указаны на 5022-строчную версию (ищите по именам методов, если сместятся).

### 0. Два РАЗНЫХ механизма «follow» (не путать!)

Окно `WindowStyle=None` (без `WS_CAPTION`) НЕ входит в нативную snap-группу Windows. Отсюда два независимых пути:

1. **Active (SnapFollow / DivGrip)** — пользователь хватает НАШ край / НАШ разделитель. Наши прозрачные
   layered-оверлеи над внутренними разделителями (`DivGrip`, методы `EnsureDivGrips`/`RefreshDividerGrips`/
   `DivGripWndProc`, ~1202..1930) ловят курсор и ведут joint-resize: двигаем свой край + соседей.
2. **Passive (PassiveFollow) — НОВОЕ** — пользователь тащит СИСТЕМНЫЙ разделитель Windows между ЧУЖИМИ
   snapped-окнами. В группе ОС нас нет, поэтому мы пассивно ОТСЛЕЖИВАЕМ движение шва соседа и сдвигаем свой
   прилегающий край, чтобы плитка не расходилась. Ядро — `PassiveFollowNeighbors` (~2691), гоняется из того же
   snap-follow таймера (~15мс) с `allowMove` (кнопка зажата ИЛИ `PassiveSettleTicks`=45 тиков после отпускания).

### 1. PassiveFollow — ядро (`PassiveFollowNeighbors`, ~2691)

Для каждой стороны, где мы snapped (`sl/sr/st/sb`), ищем пассивного соседа (`TryFindPassiveNeighborH/V`).
Держим ЗАМОРОЖЕННЫЙ на ПОКОЕ базлайн на каждый край: near-край соседа (общий с нами), far-край соседа
(противоположный), перпендикулярный размах соседа и НАШ край. Пока `allowMove`:

- **FAR стабилен** (`|dFar| <= PassiveFarStableTol`=6) **И NEAR сдвинулся** (`PassiveMinFollowPx`=8 ≤ `|dNear|` ≤
  `PassiveMaxTravelPx`=1600) **И базлайн был вплотную** (`|_pfLe - _pfOurL| <= seamFlushTol`, где
  `seamFlushTol = Max(40, GetResizeGrip*3)`) **И перп-размах не изменился** (`PassivePerpStableTol`=32)
  → это ресайз общего шва → сдвигаем свой край на `dNear`: `L = _pfOurL + dNear`. Относительный сдвиг сохраняет
  зазор покоя, поэтому мы НИКОГДА не наезжаем на соседа.
- **FAR сдвинулся ИЛИ перп изменился** → соседа перетащили/переснапили → RE-BASELINE, НЕ следуем.
- Новый HWND соседа → re-baseline. Нет соседа → сбрасываем латч (`_pfL=IntPtr.Zero`).

**Зачем каждый гейт:** FAR-stable отличает ресайз от переноса; flush-базлайн гарантирует, что следуем только за
ИСТИННЫМ общим швом (а не за случайным окном рядом); perp-stable отбрасывает переснап; deadband игнорит дрожь
un-snap; travel-cap игнорит абсурдные прыжки. Поля состояния: `_pfL/_pfR/_pfT/_pfB` (латч HWND),
`_pfLe/_pfRe/_pfTe/_pfBe` (near-базлайн), `_pfLf/.../_pfBf` (far-базлайн), `_pfLp0/_pfLp1` … (перп-размах),
`_pfOurL/.../_pfOurB` (наш край).

### 2. Поиск пассивного соседа (`TryFindPassiveNeighborH/V`, ~2515/2575) + ФИКС «прыжок шва»

EnumWindows по тому же монитору: видимые, не cloaked, не toolwindow, не мельче 50px. Кандидат принимается, если
ОН ЛИБО классический приколотый snap-сосед (`farAnchored && perpTouch`: его дальний край на границе рабочей
области И он касается перпендикулярной границы), ЛИБО точный tile-group (`SideTilesCoverBigEdge*`, см. §3).
Плавающие окна отбрасываются. Далее `overlap >= minOverlap`, `sideOk`, и лимит зазора.

**Костыль «прыжок шва» (эта сессия, решающий фикс follow-влево):** лимит зазора теперь
`gapLimit = bigSnap ? Max(500, PassiveMaxTravelPx) : 500`. Причина: при перетаскивании СИСТЕМНОГО разделителя
Windows фиксирует границу соседа ОДНИМ рывком (в логе — прыжок правого края соседа `2360 → 1759`, т.е. 601px за
коммит). Прежний жёсткий лимит 500px терял соседа посреди слежения (`haveL=False`), и код движения до сдвига не
доходил. Для приколотого/плиточного соседа (`bigSnap`) лимит поднят до 1600; для плавающих остаётся 500.
**Безопасно:** сам сдвиг всё равно защищён flush-базлайном + стабильным far-краем, поэтому далёкое окно, к
которому мы никогда не были вплотную, телепортировать нас не может (при не-flush базлайне `moved` не выставится).

### 3. Критерий tile-group (`SideTilesCoverBigEdgeH/V`, ~2433/2475) — ПРАВИЛО ПОЛЬЗОВАТЕЛЯ

Дословное правило пользователя: наше окно следует за швом БОЛЬШОГО соседа только если наша сторона шва (наше окно
+ вплотную прилегающие соседи-сотайлы) ТОЧНО замощает его сторону — тот же старт, тот же конец, без зазоров и без
свеса. Реализация (H-вариант, шов вертикальный, сотайлы сравниваются по Y):

1. Большой сосед flush на шве (`|bigNear - seamX| <= SnapNeighborEdgeAlignPx`=8).
2. Большой СТРОГО выше нас (иначе сотайл не нужен) — иначе `return false`.
3. Собираем в группу наше окно + все окна, чей near-край на шве (сотайлы).
4. Нужно ≥2 участника (мы + минимум один сотайл).
5. Группа стартует где большой (`groupTop≈bigTop`) и кончается где большой (`groupBot≈bigBot`) — равная длина.
6. Покрытие непрерывно (нет вертикальных дыр в группе).

Это распознаёт L-образные / stacked раскладки, где большой сосед сам НЕ приколот к краю рабочей области, и при
этом отбрасывает одиночный плавающий (замощает ничего) и частичную группу (зазор/свес). V-вариант — зеркально по X.

### 4. Tear-off свободного края (Phase 2 + Phase 4): `HasFlushTileNeighborH` (~2398) как GATE

**Первопричина отрыва:** внешний free-edge грип (прозрачный layered-оверлей, дающий СЫРОЙ OS-resize на «свободном»
крае) показывался на крае, который на самом деле прилегает к соседу, потому что строгий `FindSnapNeighbors` не
видел full-height внутренние колонки и full-height тайлы рядом с нашим суб-тайлом → край помечался свободным →
внешний грип → сырой OS-resize растил наше окно ПОВЕРХ соседа = отрыв.

**Фикс:** гейт free-edge грипа переведён со строгого `FindSnapNeighbors` на устойчивый `HasFlushTileNeighborH`
(~2398, гейт вызывается в ~2014-2017). Он НЕ требует, чтобы сосед доставал до дальнего края экрана, и НЕ требует
совпадения размаха; плавающие отбрасываются по перпендикулярному касанию границы рабочей области; flush-допуск —
`EdgeGripFlushGapPx`=16. Итог: грип появляется ТОЛЬКО на крае, смотрящем в пустоту.

### 5. Атомарный batch разделителя (Issue #3): `ApplyDividerBatch` (~1355-1385)

При перетаскивании НАШЕГО внутреннего разделителя (оверлеи `DivGrip`) соседи + наше окно + захваченные
сотайлы того же столбца (строго выше/ниже нашего размаха, near-край на разделителе) коммитятся в ОДНОЙ транзакции
`DeferWindowPos`, чтобы менеджер окон и DWM применили все перемещения вместе → устраняет разрыв плитки при
перетаскивании разделителя. Полноэкранное окно не имеет соседей сверху/снизу → там это no-op (ноль влияния на
ранее работавшие сценарии).

### 6. Белая полоса снизу при выносе за нижний край: `ncLargeOverhang` в `ThemedFrameNcCalcSize` (~2112)

`ThemedFrameNcCalcSize` форсирует `WVR_REDRAW` (полная перерисовка клиента), когда наш client-rect отличается от
`DefWindowProc` — это гасит self-overlay смаз при ресайзе. НО на большом одно-кадровом скачке размера
(`NcRedrawMaxDeltaPx`=250) перерисовку ПРОПУСКАЕТ (`NcRedrawSkip`), чтобы не было «пустого» мигания на
snap↔unsnap переходах. При старте сохранённая позиция восстановила окно, свисающее за нижний край на 79px, с
коллапсом высоты `889→457` (Δ=432) → это ложно приняли за snap-переход → перерисовку пропустили → снизу осталась
неперерисованная белая полоса.

**Фикс:** флаг `ncLargeOverhang` — если окно свисает за границу рабочей области больше чем на
`EdgeClampMaxOverhang`=32px, это заэкранная парковка, а НЕ snap-переход, и перерисовку НЕ пропускаем (форсируем;
пустого мигания нет, т.к. недостающая часть всё равно за экраном). Настоящие snap-переходы (свес ~в рамку < 32px)
остаются под защитой от мигания. Вычисляется внутри блока `TryGetWorkArea` и учитывается в `ncBigJump`.

### 7. Новые константы (эта сессия)

```
EnablePassiveFollow        = true   // §1 пассивное слежение за швом соседа
PassiveMaxTravelPx         = 1600   // потолок сдвига + (НОВОЕ) лимит зазора для приколотого/плиточного соседа
PassiveFarStableTol        = 6      // far-край соседа должен стоять => это ресайз, не перенос
PassiveMinFollowPx         = 8      // deadband: игнор дрожи un-snap
PassivePerpStableTol       = 32     // перп-размах соседа должен стоять => не переснап
PassiveSettleTicks         = 45     // доводка ~300мс после отпускания
SnapNeighborEdgeAlignPx    = 8      // допуск «тот же старт/конец стороны» (tile-group, flush)
SnapNeighborMaxGapPx       = 6      // классический snap-сосед вплотную
EdgeGripFlushGapPx         = 16     // §4 порог «тайл прилегает» для подавления free-edge грипа
NcRedrawMaxDeltaPx         = 250    // §6 порог «большого скачка» размера клиента
WVR_REDRAW                 = 0x0300 // §6 форс полной перерисовки клиента на NCCALCSIZE
FreeEdgeGripPx             = 12     // (было 7) толщина внешнего grip-оверлея
```

### 8. Диагностика, добавленная В ЭТОЙ СЕССИИ (УБРАТЬ перед релизом)

`EnableTroubleshootLog=true` → `%LOCALAPPDATA%\ControlPanel\troubleshoot.log` (`TsLog`). Новые теги:

- `PfWatch` — состояние пассивного латча на тик (throttle `_lastPfLogTick`, 250/120мс).
- `PfFollow` — фактический сдвиг нашего края (может глушиться троттлингом — не пугаться отсутствия строки при реальном сдвиге).
- `PfCand` — метод `LogPassiveCandidates` (~2647): дамп ВСЕХ кандидатов, когда мы snapped на стороне, но соседа не нашли (`sl&&!haveL`), throttle `_lastPfCandLogTick`=700мс.
- `DivGrip` — состояние оверлеев разделителя (`RefreshDividerGrips`).
- `DivMove` — движение при drag разделителя (throttle `_lastDivMoveLogTick`=120мс).
- `TsJrc` — joint-resize cursor трасса (`fail=band/no-edges/...`).
- `NcCalc` / `NcRedrawSkip` — трасса `ThemedFrameNcCalcSize` (§6).
- `FsnMiss` — промах поиска snap-соседа для follow.

**Перед релизом:** `EnableTroubleshootLog → false`; удалить `TsLog`-вызовы, метод `LogPassiveCandidates`, поля
`_lastPfCandLogTick / _lastPfLogTick / _lastDivMoveLogTick / _ncLastW / _ncLastH`. Сама follow-логика от
диагностики НЕ зависит.

---

## НЕЗАВЕРШЁННАЯ ПРОБЛЕМА — косметический зазор при протяжке границы

**Симптом:** при смещении границы окна временно виден зазор — «большой и некрасивый». По решению пользователя в
этой сессии НЕ исправляется (отложено), но требует внимания при рефакторинге.

**НЕУДАЧНЫЕ ПРОШЛЫЕ ПОПЫТКИ (НЕ ПОВТОРЯТЬ):** любые подходы через заливку / маску / оверлей / подложку
(GDI fill / mask / overlay / backing поверх зазора). Все они несовместимы с WPF (airspace/DWM), давали лаги и
артефакты, перекрывали собственные правые кнопки окна, а «влево зазора и так не было». Пользователь ЯВНО и
ОКОНЧАТЕЛЬНО отказался от всех fill/mask/overlay/backing-решений («Представляю, сколько там будет лагов»,
«Откатывай!»). Также НЕ ограничивать шаг/скорость движения разделителя. Направление для нового решения — работать
с геометрией/таймингом коммита (атомарные `DeferWindowPos`, как в §5), а не рисовать поверх зазора.

## ИЗВЕСТНЫЙ ДЕФЕКТ — 4-оконный режим: диагональный отрыв

На финальном тесте в раскладке из 4 окон окно, стоящее ПО ДИАГОНАЛИ от нашего, оторвалось (не последовало за
общей группой). Пользователь этим режимом не пользуется → НЕ чинилось в этой сессии, низкий приоритет.
Вероятная причина в терминах §1-§3: диагональное окно не делит с нами общий шов напрямую (нет прямого
`sl/sr/st/sb`-контакта), поэтому под текущий критерий tile-group / passive-neighbor не попадает. При желании
чинить — рассматривать связность группы транзитивно (сосед соседа), но осторожно, чтобы не тянуть несвязанные окна.
