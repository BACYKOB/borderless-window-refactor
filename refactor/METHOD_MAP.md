# METHOD_MAP — разметка исходника `original/BorderlessWindow.cs` (5546 строк)

> Чекпойнт задачи T1. Каждый член класса → целевой partial-файл или DELETE (с причиной).
> Номера строк — по `original/BorderlessWindow.cs`. Исполнитель следующей задачи НЕ должен
> перечитывать исходник целиком: бери свой диапазон строк отсюда.

## Целевые файлы

| Код | Файл |
|---|---|
| CORE | `Controls/BorderlessWindow.cs` |
| INTEROP | `Controls/BorderlessWindow.Interop.cs` |
| CHROME | `Controls/BorderlessWindow.Chrome.cs` |
| SNAP | `Controls/BorderlessWindow.SnapResize.cs` |
| UNSNAP | `Controls/BorderlessWindow.Unsnap.cs` |
| ANIM | `Controls/BorderlessWindow.Animation.cs` |
| TASKBAR | `Controls/BorderlessWindow.Taskbar.cs` |
| DEL | удалить (причина указана) |

## 1. Поля и флаги (строки 34–148, 539–631, 2546–2587, 3956–3972, 4213–4233, 5363–5374)

| Строки | Член | Куда |
|---|---|---|
| 36–46 | `_edgeWatcher`, `_currentMonitorRect`, `_taskbarHeight`, `_watchActive`, `_shrunk`, `_taskbarWasVisible`, `_waitTicks`, `_prevCursorY`, `_hasPrevCursorY`, `_armSuppressed`, `_cursorDriven` | TASKBAR |
| 56 | `EnableCursorDrive=true` | TASKBAR |
| 61–62 | `EnableGapMask=true`, `_gapMask` | TASKBAR |
| 68 | `CaptionHeight` (protected prop) | CORE |
| 75 | `EnableStartupMask=true` | ANIM |
| 84–107 | `_peakGate`, `_shotDurationMs`, `_shotScreenDc/_shotMemDc/_shotBmp/_shotOldBmp/_shotRect`, `_gateOnRender`, `_startupHiding`, `_prevWindowState`, `_maskHwnd`, `_maskThread`, `_maskAbort`, `_maskAtPeak` | ANIM |
| 126 | `UseThemedSystemFrame=true` | CHROME (оставить: документированный главный переключатель; ветку false см. DEL ниже) |
| 130 | `ApplyDwmFrameAttributes=true` | CHROME |
| 134 | `AttachControlzExChrome` | **DEL** — ControlzEx-путь мёртв (PLAN Часть 2) |
| 146 | `EnableSuppressResizeBitBlt=false` | **DEL** — выключенный эксперимент; вместе с `SuppressResizeBitBlt()` (3536) |
| 296 | `IncludeCaptionForSnap=false` | CHROME (оставить: рабочий тюнинг hit-test, документирован) |
| 301 | `DisableDwmNcRendering=false` | **DEL** — мёртвый эксперимент toolkit NC-rendering (PLAN Часть 2) |
| 539 | `ShrinkThemedFrame=true` | CHROME |
| 546 | `EnableSnapLayoutGapFix=true` | CHROME |
| 569 | `DeferJointResizeToShell=true` | SNAP |
| 579 | `EnableAnchorUnsnapResize=true` | UNSNAP |
| 582 | `EnableCaptionUnsnapRestoreDrag=true` | UNSNAP |
| 588–599 | `_captionUnsnap*` (9 полей), `_lastFloatingRestoreRect(+Valid)`, `_captionUnsnapHandoffToShell`, `_captionUnsnapWatchdog`, `_captionUnsnapMoveTickCount` | UNSNAP |
| 601 | `_snapDwmBorderColorApplied` | CHROME |
| 604–616 | `_inSizeMove`, `_sizingEdge`, `_frameNbrsR/L`, `_frameJointArmed`, `_userEdgeResize`, `_sizeChangedInLoop`, `_sizeAnchor(+Valid)` | SNAP; `_lastLoggedHit` (616) — **DEL** (только для лога) |
| 622–623 | `_offscreenPrevRect(+Valid)` | CORE (используется в WindowProc offscreen-ветке) |
| 631 | `AllowBitBltForOffscreenResize=true` | CORE |
| 1045–1046 | `EnableFreeEdgeGrip=true`; `GripDebugLog=false` | SNAP; GripDebugLog+`GripLog()` (1114) — **DEL** (диагностика) |
| 1061–1101 | грип-поля: `_gripWndProc`, `_gripClassRegistered`, `_divGripHwnd*`, `_divNbrs*`, `_divDragging`, `_divDragSide`, `_divDragNbrs`, `_divDragCoTiles`, `_gripHwnd`, `_edgeGripHt`, `_edgeGripResizing` | SNAP |
| 1084, 1097–1098 | `_lastDivMoveLogTick`, `_lastNbrDiagTick`, `_lastFrmFollowLogTick` | **DEL** — троттлинг логов |
| 1088–1090 | `_divFsPending*`, `_divFsRenderHooked` | **DEL** — frame-sync выключен (PLAN Часть 2) |
| 1094–1096 | `_divReleaseRealignUntil`, `_divReleaseSide`, `_divReleaseNbrs` | SNAP (release-realign РАБОТАЕТ) |
| 1171–1172 | `_gripRefreshTimer`, `_gripRefreshTicks` | SNAP |
| 1295–1296 | `_divGuideHwnd`, `_divGuideClassRegistered` | **DEL** — guide-line выключен (PLAN Часть 2) |
| 2546–2587 | `EnableSnapFollow`, `EnableJointResizeCursor`, `EnablePassiveFollow` (все true), `_snapFollow*`, `_topNbr/_botNbr/_leftNbr/_rightNbr`, `_snapDragEdge`, `_snapSettleEdge/Ticks`, все `_pf*` | SNAP; `_lastPfLogTick` (2587), `_lastPfCandLogTick` (2847) — **DEL** |
| 3956–3972 | `EnableUnsnapWinEventRestore/SuppressResnapFrame/SteerGrowBack/ProactiveFloat` (все true), `_unsnap*` поля | UNSNAP; `_lastNoEdgeDiagTick` (3961) — **DEL** |
| 4213, 4233 | `EnableTroubleshootLog`, `EnableSnapDiagLog` | **DEL** — вся диагностика удаляется |
| 5363–5364 | `NcRedrawMaxDeltaPx`, `EnableFullRedrawOnOriginMove=true` | CHROME (рабочая baseline-логика NCCALCSIZE) |
| 5365–5370 | `EnableDividerNoCopyBits`, `EnableDividerDeferredResize`, `EnableDividerGuideLine`, `EnableDividerSingleBatch`, `EnableDividerFrameSync(+DwmFlush)` (все false) | **DEL** — мёртвые эксперименты (PLAN Часть 2) |
| 5371 | `_ncLastW/_ncLastH` | CHROME |

## 2. Ядро (CORE): конструктор, lifecycle, WindowProc

| Строки | Член | Куда / примечание |
|---|---|---|
| 149–207 | ctor `BorderlessWindow()` | CORE. Вычистить ControlzEx-ветку и TsLog |
| 208–250 | `OnSourceInitialized` | CORE |
| 251–259 | `OnContentRendered`, `_startupRevealStarted` | CORE (вызов в ANIM) |
| 260–272 | `TryStartMaskReveal` | ANIM |
| 273–311 | `EnsureResizeStyles` | CHROME |
| 312–355 | `ApplyThemedSystemFrame` | CHROME |
| 356–368 | `ApplyThemedBorderMetrics` | CHROME |
| 369–396 | `OnStateChanged` | CORE |
| 397–413 | `OnDpiChanged` | CORE |
| 414–479 | `OnMouseLeftButtonDown/Move/LeftButtonUp/OnLostMouseCapture` | CORE (тонкие обёртки; тело unsnap-drag в UNSNAP) |
| 480–537 | `OnClosing` | CORE |
| 634–944 | `WindowProc` — центральный диспетчер | CORE. Ветки остаются здесь; тела вынесены по partial'ам. Вычистить: LogIncoming/LogHit, ветки мёртвых флагов (frame-sync, deferred-resize, single-batch), ControlzEx |

## 3. CHROME: hit-test, NCCALCSIZE, DWM

| Строки | Член | Куда |
|---|---|---|
| 945–996 | `ThemedHitTest` | CHROME |
| 1023–1060 | `TryGetClientRectScreen` | CHROME |
| 2186–2253 | `IsInDraggableCaption`, `GetResizeGrip`, `IsOverTitleInteractive` | CHROME |
| 2254–2374 | `ThemedFrameNcCalcSize` | CHROME. **Точка внедрения фикса 2 (левый призрак, `EnableLeftEdgeGhostGuard`)** — симметрично top ghost-guard; PLAN Часть 4 |
| 2375–2412 | `TryBuildAlignedValidRects` | **DEL** — aligned valid-rects эксперимент (flag=false, PLAN Часть 2) |
| 2413–2464 | `ApplySnapLayoutGapFix` | CHROME |
| 2465 | `Near()` | CHROME |
| 2473–2482 | `UpdateSnapDwmBorderColor` | CHROME |
| 2483–2536 | `HasInternalSnapDivider`, `TryGetSnapInternalEdges` | CHROME (используются и SNAP — partial, видимость общая) |
| 3491–3535 | `TryGetWorkArea`, `IsOutsideCurrentMonitor` | CHROME |
| 3536–3546 | `SuppressResizeBitBlt` | **DEL** — флаг false |
| 3547–3565 | `IsResizePosChange` | CORE (нужен WindowProc) |
| 4321–4345 | `AdjustMaximizedBounds` | CHROME |

## 4. SNAP: грипы, divider joint-resize, snap-follow, passive-follow

| Строки | Член | Куда |
|---|---|---|
| 997–1022 | `TrySetJointResizeCursor` | SNAP |
| 1114–1120 | `GripLog` | **DEL** |
| 1121–1170 | `EnsureGripClass`, `GripWndProcStatic`, `GripWndProc` | SNAP |
| 1180–1294 | `ScheduleEdgeGripRefresh`, `EnsureGrip`, `HideEdgeGrip`, `DestroyEdgeGrip`, `EnsureDivGrips`, `HideDivGrips`, `DestroyDivGrips` | SNAP |
| 1303–1350 | `EnsureDivGuideClass`, `EnsureDivGuide`, `ShowDivGuideAt`, `HideDivGuide` | **DEL** — guide-line (flag=false) |
| 1351–1433 | `RefreshDividerGrips` | SNAP |
| 1434–1507 | `UpdateDividerJointResize` | SNAP. **Точка внедрения фикса 1 (зазор, `EnableSeamGapFix`)** — PLAN Часть 3. Вычистить ветки deferred/frame-sync/guide |
| 1508–1566 | `ApplyDividerBatch` | SNAP. **Ядро фикса 1: один атомарный DeferWindowPos, порядок «растущий первым»** |
| 1567–1630 | `FindDividerCoTiles`, `MoveDividerCoTiles` | SNAP |
| 1631–1715 | `FindSnapNeighborsV` | SNAP |
| 1716–1874 | `UpdateDividerJointResizeV`, `FindDividerCoTilesV`, `MoveDividerCoTilesV` | SNAP (фикс 1 симметрично для V) |
| 1875–1966 | `MoveNeighborsFlushV`, `FollowFrameResizeNeighbors`, `MoveNeighborsFlush` | SNAP |
| 1967–1992 | `ArmDivReleaseRealign`, `ArmFrameReleaseRealign` | SNAP |
| 1993–2029 | `LogNearestRejectedNeighbor` | **DEL** — диагностика |
| 2030–2054 | `DivFsEnsureHook`, `DivFsUnhook`, `DivFsOnRender` | **DEL** — frame-sync (flag=false) |
| 2055–2120 | `DivGripWndProc` | SNAP. Вычистить ветки guide/deferred |
| 2121–2185 | `RefreshEdgeGrip` | SNAP |
| 2589–2604 | `ResetPassiveFollow` | SNAP |
| 2605–2846 | `HasFlushTileNeighborH`, `SideTilesCoverBigEdgeH/V`, `TryFindPassiveNeighborH/V` | SNAP |
| 2854–2897 | `LogPassiveCandidates` | **DEL** — диагностика |
| 2898–3047 | `PassiveFollowNeighbors` | SNAP |
| 3048–3230 | `UpdateSnapFollow`, `StartSnapFollow`, `StopSnapFollow`, `SnapFollow_Tick` | SNAP |
| 3231–3453 | `FindSnapNeighbors`, `TryFindSnapNeighbor(+V)`, `TryGetTrackedNeighborEdge(+V)` | SNAP |
| 3454–3487 | `TryGetVisibleBounds`, `TryGetMaskRect` | SNAP (общие хелперы; используются и UNSNAP) |

## 5. UNSNAP: caption un-snap restore, WinEvent restore, anchor

| Строки | Член | Куда |
|---|---|---|
| 3566–3591 | `AnchorUnsnapResize` | UNSNAP |
| 3594–3647 | `TryBeginCaptionUnsnapRestoreDrag` | UNSNAP |
| 3648–3654 | `LogFirstUpdate` | **DEL** — диагностика |
| 3655–3959 | `UpdateCaptionUnsnapRestoreDrag`, `BeginCaptionUnsnapManualMove`, `MoveCaptionUnsnapRestoredWindow`, `EndCaptionUnsnapRestoreDrag`, `CaptionUnsnapWatchdog_Tick`, `HandoffCaptionDragToShell`, `MakeScreenLParam`, `IsCaptionUnsnapRestoreCandidate` (x2), `IsWindowAtLeastCurrentMonitorHeight`, `CaptionUnsnapRestoreThresholdPxX/Y`, `TryGetCaptionRestoreRect`, `IsUsableCaptionRestoreRect`, `UpdateCaptionUnsnapRestoreCache` | UNSNAP |
| 3989–4166 | `InstallUnsnapWinEventHook`, `RemoveUnsnapWinEventHook`, `ArmUnsnapWinEventRestore`, `MaybeSuppressUnsnapResnapFrame`, `OnUnsnapWinEvent`, `FinishUnsnapWinEventRestore` | UNSNAP |
| 4175–4206 | `FloatingWindowCrossesMonitor`, `RectCrossesItsMonitor` | UNSNAP |

## 6. Диагностика — вся DEL (строки 4207–4314)

`TsLog`, `SnapLog`, `RectStr`, `WorkStr`, `HitName`, `LogHit`, `LogIncoming` + все вызовы по файлу (~200+ вызовов TsLog/SnapLog/GripLog). Удалять вызовы вместе с окружающими `if (EnableTroubleshootLog)`-обёртками.

## 7. ANIM: стартовая маска, крестфейды (4346–4735)

| Строки | Член | Куда |
|---|---|---|
| 4354–4410 | `HideMainForStartup`, `UncloakMain`, `StartMaskReveal`, `StartRestoreReveal` | ANIM |
| 4411–4448 | `AnimateToWindowState`, `AnimateMinimize` | ANIM |
| 4449–4605 | `StartMaskCrossfade`, `Ease`, `SmoothStep`, `MaskFadeLoop`, `EndMask`, `_maskClassRegistered`, `_maskWndProc`, `EnsureMaskClass` | ANIM |
| 4606–4734 | `StartScreenshotCrossfade`, `UpdateShotAlpha`, `ScreenshotFadeLoop`, `CleanupScreenshotMask` | ANIM |

## 8. TASKBAR: edge-watcher, gap-mask, placement (4737–5226)

| Строки | Член | Куда |
|---|---|---|
| 4739–4921 | `UpdateEdgeWatcher`, `StartEdgeWatcher`, `StopEdgeWatcher`, `EdgeWatcher_Tick`, `ShrinkBottom` | TASKBAR |
| 4928–4998 | `GapMaskHeight`, `GapMaskParked`, `ShowGapMask`, `HideGapMask`, `EnsureGapMaskHandle` | TASKBAR |
| 5005–5088 | `IsTaskbarCurrentlyVisible`, `TryGetWindowMonitor`, `IsOnTaskbarMonitor`, `RectEquals`, `HasBottomAutoHideTaskbar`, `GetTaskbarThickness`, `ABE_BOTTOM`, `ABM_GETAUTOHIDEBAREX` | TASKBAR |
| 5090–5226 | `SavePlacement`, `RestorePlacement`, `CenterOnPrimaryScreen`, `GetMonitorSignature`, `PlacementDto`, `EnumDisplayMonitors`, `GetWindowPlacement/Set`, `WINDOWPLACEMENT` | TASKBAR (регион «Сохранение положения окна» целиком) |

## 9. INTEROP: WinAPI (5228–5543 + разбросанные)

Всё из `#region WinAPI interop` → INTEROP: все `[DllImport]`, структуры (`RECT`, `POINT`, `SIZE`, `MONITORINFO`, `MINMAXINFO`, `WINDOWPOS`, `NCCALCSIZE_PARAMS`, `APPBARDATA`, `WNDCLASS`, `WTA_OPTIONS`, `BLENDFUNCTION`), все константы (SWP_*, WS_*, DWMWA_*, GWL_*, RDW_*, WVR_*, SM_*, VK_*, LWA_*, ULW_*, SRCCOPY и пр.), делегаты (`WndProcDelegate`, `EnumWindowsProc`, `MonitorEnumProc`, `WinEventDelegate`).

Исключения:
- `GetWndClass` (5267) — **DEL** (помечен «ДИАГНОСТИКА», используется только в логах — проверить грэпом перед удалением).
- `SetWindowThemeAttribute` + `WTA_OPTIONS` + `WTNCA_*` (5323–5331) — проверить: если используется только в `DisableDwmNcRendering`-ветке (мёртвой) → **DEL**, иначе INTEROP.
- `DwmFlush` (5333) — используется в MaskFadeLoop (ANIM) → INTEROP.
- GDI-блок (5427–5468: CreateCompatibleDC, BitBlt, UpdateLayeredWindow и пр.) — используется скриншот-маской (ANIM) → INTEROP.
- `CreateRectRgn`/`SetWindowRgn` (5453–5457) — проверить использование; если только мёртвые ветки → DEL.
- Флаги `EnableDivider*` (5365–5370) и `TryBuildAlignedValidRects` — DEL (см. выше).

## Правила для исполнителей

1. Разметка выше — директива. Отклонение (нашёл использование у члена, помеченного DEL) → НЕ удалять, оставить и записать в AGENT_LOG «Журнал сессий».
2. Перед удалением каждого DEL-члена — грэп по имени: 0 оставшихся использований после вычистки вызывающих веток.
3. Не менять сигнатуры переносимых методов. Не переименовывать поля.
4. `TryGetSnapInternalEdges`/`TryGetVisibleBounds` используются из нескольких partial'ов — это нормально (один класс).
