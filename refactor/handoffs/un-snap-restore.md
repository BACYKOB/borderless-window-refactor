# BorderlessWindow — HANDOFF: BUG1 (un-snap-restore протяжкой шапки)

> Вынесено в отдельный файл 2026-07-02 по просьбе пользователя: работа над BUG1 продолжается в отдельном чате с другим агентом.
> Этот файл самодостаточен (контекст стенда включён). Общая архитектура, BUG2 и остальные механизмы — в основном `docs/snap-joint-resize/snap-joint-resize.md`.
>
> ⚠️ ВАЖНО: этот разбор сделан на базе старого отката (~3215 строк). За последнюю сессию код вырос до
> **5022 строк** (passive-follow / tear-off / tile-group / repaint-фиксы). BUG1 В ЭТОЙ СЕССИИ НЕ ТРОГАЛСЯ;
> имена методов ниже валидны концептуально, но номера строк сместились — ищите по именам в `BorderlessWindow.cs`.

### Контекст стенда (для воспроизведения)

- Один 4K-монитор: 3840×2160 физ., масштаб 150% → 2560×1440 логических.
- **Все координаты в troubleshoot-логах — ФИЗИЧЕСКИЕ пиксели.**
- Окно borderless (`WindowStyle=None`, без `WS_CAPTION`) + ControlzEx `WindowChromeBehavior`.
- Диаг-лог: `EnableTroubleshootLog=true` → `%LOCALAPPDATA%\ControlPanel\troubleshoot.log` (метод `TsLog`).
- Присланный «откат без деградаций» (3215 строк / 208711 байт) — это версия С диагностикой и Win32-свопами
  caption-unsnap, но БЕЗ блока `WH_MOUSE_LL` (тот дал деградацию — см. БАГ 1, попытка 5). Оба бага в ней воспроизводятся.

### БАГ 1 — мигание/возврат в полноэкранный snap при un-snap-restore протяжкой шапки

**Симптом (со слов пользователя):** после ресайза ползунком в half + протяжка за шапку вниз окно мигает и
возвращается в snap во всю высоту, вместо открепления и восстановления pre-snap размера.

**Доказанная первопричина (по troubleshoot4.log, Win32-hook сборка):** это OS-level жест Win11 «unsnap-drag».
При захвате шапки система сама запускает собственный модальный move/size-loop:
- через ~1мс после нашего `CapDown` (arm) приходит `WmCaptureChanged newCap=0xC509E4` — capture перехватывает
  служебное окно ОС `0xC509E4`;
- ОС восстанавливает float-SIZE `(584,0,1759,580)` = 1175×580, затем тут же заново растит во весь full-height
  snap `(75,42,1821,2210)` → визуально это и есть «мигание + возврат»;
- этот цикл ОС идёт В ОБХОД нашего `WndProc`, поэтому свопы сообщений в нём бесполезны.

**Все попытки и результаты:**
1. `EnableCaptionUnsnapRestoreDrag` + собственный move-loop по WM_MOUSEMOVE с порогом SM_CXDRAG/CYDRAG — базовый
   механизм; в чистом 2-окне работал, но в snap-группе перебивается жестом ОС. ❌
2. Свопы `WM_NCLBUTTONDOWN` / `WM_SYSCOMMAND (SC_MOVE/SC_SIZE)` в WndProc (теги `WmNcLBtnDown SWALLOW`,
   `WmSysCmd SWALLOW`) — не помогло: жест стартует не через эти сообщения нашего окна. ❌
3. Watchdog `_captionUnsnapWatchdog` (DispatcherTimer 15мс) для отмены — НЕ тикает во время модального цикла ОС
   (UI-поток занят), бесполезен. ❌
4. Анализ через Win32-hook доказал перехват capture окном `0xC509E4` (см. выше). ✅ (диагноз, не фикс)
5. **`WH_MOUSE_LL` (low-level mouse hook), «съедавший» WM_MOUSEMOVE пока pending/dragging** — СИЛЬНАЯ ДЕГРАДАЦИЯ:
   окно вообще перестало таскаться за шапку, в half ползунок почти сразу исчезал и одно окно уезжало. Вероятно
   `_captionUnsnapPending` залипал в true, а глобальное съедание движения ломало и обычный caption-drag, и
   SnapFollow. **Откачено; подход в таком виде нежизнеспособен. В присланном откате этого блока НЕТ.** ❌

**Рекомендации следующему агенту:**
- Подтвердить идентичность окна `0xC509E4` (служебное окно snap-жеста Win11).
- Вместо перехвата движения — **WinEvent-hook** `EVENT_SYSTEM_MOVESIZESTART/END` и форсировать восстановление
  pre-snap rect ПОСЛЕ завершения жеста ОС (re-apply restore-rect), а не бороться с жестом в реальном времени.
- Если всё же `WH_MOUSE_LL` — строго ограничить по времени (только на наш drag) и ВСЕГДА гарантированно
  сбрасывать pending/hook на WM_LBUTTONUP и по таймауту.

### Коды / константы / места кода, относящиеся к BUG1

- Флаги/константы: `EnableCaptionUnsnapRestoreDrag=true`, `CaptionUnsnapRestoreThresholdPx=30` (порог SM_CXDRAG/CYDRAG).
- Методы (искать по имени): `TryBeginCaptionUnsnapRestoreDrag`, `EndCaptionUnsnapRestoreDrag`, регион caption-unsnap в `WindowProc` / `OnMouseLeftButtonDown`.
- WM/HT/SC коды: WM_NCLBUTTONDOWN=0x00A1, WM_SYSCOMMAND=0x0112 (SC_MOVE=0xF010, SC_SIZE=0xF000),
  WM_CAPTURECHANGED=0x0215, WM_MOUSEMOVE=0x0200, WM_LBUTTONUP=0x0202; HTCAPTION=2.
- Служебное окно snap-жеста в логах: `0xC509E4` (перехватывало capture через `WmCaptureChanged`).
- Теги логов BUG1: `CapDown / UpdFirst / CapThreshold / CapRestoreMove / CapEnd / GetRestore / WmCaptureChanged / WmNcLBtnDown SWALLOW / WmSysCmd SWALLOW / WmLBtnUp`.
- **`WH_MOUSE_LL` в текущем коде НЕТ** — давал сильную деградацию, в прежнем виде не восстанавливать (попытка 5).

---

# ДОПОЛНЕНИЕ 2026-07-03 — сессия: корректный возврат размера при unsnap + остаточный призрак делителя

> Дописано по итогам сессии 2026-07-03. Читать ВМЕСТЕ с разделом BUG1 выше и с `docs/resize-ghost/resize-ghost.md` (разбор ПЕРВОНАЧАЛЬНОГО призрака прошлыми агентами — та же первопричина, те же выводы).
> Состояние файла на момент записи: `BorderlessWindow.cs` = 5346 строк, скобки 1118/1118 (сбалансированы), круглые скобки сбалансированы. Все координаты в `troubleshoot.log` — ФИЗИЧЕСКИЕ пиксели (4K 3840x2160 @150%).
> Компилятора C# в песочнице НЕТ — проверка только по балансу скобок и чтению. Аудитору: собрать `dotnet build` и прогнать сценарии ниже вручную.

## 0. TL;DR статус
- **Возврат размера при unsnap** (ползунок в half -> протяжка за шапку вниз -> открепление и восстановление pre-snap размера) — **РАБОТАЕТ**, пользователь подтвердил дословно «Все работает отлично!». НЕ ЛОМАТЬ.
- **Совместный ресайз внутреннего делителя (BUG2)** — **РАБОТАЕТ**. НЕ ломать двухпроходную структуру батча (см. §1.2).
- **Остаточный «призрак левого края» при ЖИВОМ ресайзе делителя** — **НЕ устранён**, оставлен как известный платформенный лимит WPF+DWM (см. §3). Все флаги-эксперименты этой сессии ВЫКЛЮЧЕНЫ; поведение = обычный живой ресайз (то, что было в исходнике; призрак был и там).

## 1. Рабочий функционал — НЕ ЛОМАТЬ

### 1.1 Возврат размера при unsnap (Variant A / A+ / A++)
Зачем: OS-жест Win11 «unsnap-drag» идёт в обход нашего WndProc (см. BUG1), поэтому боремся не с жестом, а с его кадрами и финалом.
- `MaybeSuppressUnsnapResnapFrame(lParam)` — на WM_WINDOWPOSCHANGING: пока armed, глушит кадр «реверса» обратно в snap-rect и ведёт живое следование за курсором.
  - Фаза 1: ждём, пока ОС ужмёт окно до floating(restore)-размера (`_unsnapHasFloated`).
  - **Variant A++ (`EnableUnsnapProactiveFloat`)**: если ОС нас НЕ всплывает (мы — большое окно в группе из 3 плиток: ОС двигает нас в полном snapped-размере и сворачивает только на отпускании), то при протяжке ВНИЗ дальше порога сами уводим в floating.
  - Фаза 2 (`EnableUnsnapSteerGrowBack`, Variant A+): если ОС снова раздувает до snapped-размера — навязываем floating-размер+позицию так, чтобы курсор остался над той же относительной точкой шапки (живое следование).
- `OnUnsnapWinEvent` / WinEvent-hook + `FinishUnsnapWinEventRestore` — финальный re-apply restore-rect ПОСЛЕ завершения жеста ОС (`EnableUnsnapWinEventRestore`).
- Порог: `CaptionUnsnapRestoreThresholdDip = 20.0` DIP (= 30 физ. px @150%; методы `CaptionUnsnapRestoreThresholdPxX/Y()`). Значение выверено пользователем — не менять произвольно.
- Поля состояния: `_unsnapArmValid`, `_unsnapArmSnapRect`, `_unsnapArmRestoreRect`, `_unsnapArmDownPt`, `_unsnapHasFloated`.

### 1.2 Совместный ресайз делителя (BUG2)
- Путь драга: `DivGripWndProc` (WM_LBUTTONDOWN arm -> WM_MOUSEMOVE -> WM_LBUTTONUP) -> `UpdateDividerJointResize(X)` / `UpdateDividerJointResizeV(Y)` -> `ApplyDividerBatch(...)`.
- Оверлеи-грипы делителя: `EnsureDivGrips` / `RefreshDividerGrips` / `HideDivGrips` / `DestroyDivGrips`; соседи — `FindSnapNeighbors(V)`; co-tiles одной колонки — `FindDividerCoTiles`.
- **ВАЖНО (не «упрощать»): на каждый кадр делается ДВА `ApplyDividerBatch` НАМЕРЕННО.**
  1. предиктивный (`moveOur=false`) — двигает ТОЛЬКО соседей, чтобы прочитать ФАКТИЧЕСКИЙ край после клампа ОС по мин. ширине;
  2. финальный (`moveOur=true`) — атомарно (один `BeginDeferWindowPos/EndDeferWindowPos`) двигает соседей + наше окно + co-tiles.
  Это устраняет per-frame double-move НАШЕГО окна (raw dividerX -> corrected effDiv), который иначе даёт полупрозрачный ghost-трейл за курсором при упоре соседа в мин. ширину. Схлопывание в один батч пробовали (`EnableDividerSingleBatch`) — призрак НЕ ушёл, зато появляется редкий риск наложения на мин. ширине. Оставить двухпроходную схему.

### 1.3 NCCALCSIZE (`ThemedFrameNcCalcSize`) — сейчас на базовом WVR_REDRAW
- Возвращает **WVR_REDRAW на каждом кадре с изменённым client** (`EnableFullRedrawOnOriginMove=true`) = известное рабочее до-сессионное поведение, БЕЗ origin-move смаза/расслоения.
- Сопутствующее (не ломать): верхний инсет `ThemedFrameInset` (ghost-guard сверху для НЕ-caption режима), клампы client к рабочей области (SNAP без зазора), `ApplySnapLayoutGapFix` (щели на внутренних Snap-стыках), `ThemedHitTest` (внешний хват ресайза), `NcRedrawSkip` на БОЛЬШОМ одно-кадровом скачке (иначе бланк всего окна при snap<->unsnap переходе), `ncLargeOverhang`-гард (иначе белая полоса у окна, припаркованного за краем монитора).

## 2. Карта флагов (текущее состояние)
| Флаг | Значение | Смысл |
|---|---|---|
| `EnableFullRedrawOnOriginMove` | **true** | NCCALCSIZE = WVR_REDRAW всегда (рабочий базлайн). false -> включает мёртвый copy-blit (вернёт призрак). ДЕРЖАТЬ true. |
| `EnableDividerNoCopyBits` | false | Эксперимент: SWP_NOCOPYBITS на нашем окне. Эффекта на призрак НЕТ. |
| `EnableDividerDeferredResize` | false | Коммит ресайза ОДИН раз на отпускании. УБИРАЕТ призрак (подтверждено), НО даёт «телепорт» окна. Отвергнут пользователем. |
| `EnableDividerGuideLine` | false | Видимая направляющая линия во время драга (маскировала телепорт). Отвергнута пользователем (видимый элемент). |
| `EnableDividerSingleBatch` | false | Схлопывание двух батчей в один. Призрак не убрал, риск наложения на мин. ширине. |
| `EnableDividerFrameSync` / `...DwmFlush` | false / false | Frame-sync геометрии через CompositionTarget.Rendering. Эффекта нет. |
| `EnableUnsnapWinEventRestore` | true | Финальный restore через WinEvent. НУЖЕН. |
| `EnableUnsnapSuppressResnapFrame` | true | Глушение кадра реверса (Variant A). НУЖЕН. |
| `EnableUnsnapSteerGrowBack` | true | Живое следование (Variant A+). НУЖЕН. |
| `EnableUnsnapProactiveFloat` | true | Проактивный float для большого окна в группе (Variant A++). НУЖЕН. |
| `EnableSuppressResizeBitBlt` | true | SWP_NOCOPYBITS (из прошлых сессий) — НУЖЕН (без него на быстрой тяге светит контент под окном). НЕ призрак. |

## 3. Остаточный призрак — ПОЛНЫЙ разбор неудачных попыток (чтобы не повторять)
**Симптом:** при ЖИВОМ ресайзе левого края (делитель с соседом слева; а в исходном виде — любой ресайз full-bleed окна за лево/верх) на ~1 кадр виден полупрозрачный дубль-край. На скриншот/запись НЕ попадает (живёт в слое DWM), только глазом. Чем быстрее тянешь — тем сильнее смещение.

**Первопричина (подтверждена и совпадает с выводом прошлых агентов):** DWM на 1-2 кадра РАСТЯГИВАЕТ старую redirection-поверхность окна на новый размер, пока WPF не отрисует новую область. Призрак есть тогда и только тогда, когда движущийся край — это сама full-bleed WPF-поверхность. Обработка сообщений тут ни при чём: мы уже возвращаем то же, что ОС.

**Логи опровергли теорию «большого скачка» (ncBigJump/NcRedrawSkip):** per-frame дельта client при драге < 250px (макс ~139), `NcRedrawSkip` во время драга НЕ срабатывает ни разу, WVR_REDRAW форсится каждый кадр драга — а призрак всё равно есть. Значит форсить WVR_REDRAW сильнее бессмысленно.

| # | Попытка | Результат | Вывод |
|---|---|---|---|
| 1 | WVR_REDRAW на каждом кадре (текущий базлайн) | призрак есть | это исходное поведение; призрак был и в оригинале |
| 2 | WVR_VALIDRECTS, выровненный copy-blit по TOP-LEFT (`TryBuildAlignedValidRects`) | origin-move ghost | при движении левого/верхнего края origin клиента смещается -> старые пиксели под новым origin на 1 кадр |
| 3 | WVR_VALIDRECTS с якорем по ПРАВОМУ/НИЖНЕМУ краю (dragLeft/dragTop) | ХУЖЕ: «ПОЛНОЕ расслоение» | массив пикселей остаётся на старом экранном месте, WPF рисует со сдвигом. НЕ ВКЛЮЧАТЬ |
| 4 | SWP_NOCOPYBITS на нашем окне (`EnableDividerNoCopyBits`) | без эффекта | подтверждает: это DWM async-present, не USER32 copy-blit. Осторожно: может растягивать устаревшую DWM-поверхность |
| 5 | Frame-sync через CompositionTarget.Rendering (+DwmFlush) (`EnableDividerFrameSync`) | без эффекта | презентация WPF всё равно асинхронна |
| 6 | Один атомарный батч вместо двух (`EnableDividerSingleBatch`) | призрак не убран; риск наложения на мин. ширине | двухпроходная схема лучше; откачено |
| 7 | Коммит на отпускании (`EnableDividerDeferredResize`) | призрак УБРАН, но «телепорт» окна | пользователь отверг телепорт |
| 8 | Направляющая линия поверх драга (`EnableDividerGuideLine`) | пользователь резко отверг видимый элемент | откачено |

**Единственные ghost-free пути (оба отвергнуты/неподъёмны):**
- Видимая рамка/граница на движущемся крае (DWM рисует её мгновенно, отставание клиента прячется за ней). Требует видимого элемента -> противоречит borderless-дизайну и явному нежеланию пользователя видеть добавки.
- DirectComposition/flip-swapchain рендер контента. WPF так не умеет без крупной переархитектуры (`WS_EX_NOREDIRECTIONBITMAP` -> чёрный экран у HwndTarget). Прошлые агенты отметили как неподъёмное.

## 4. Мёртвый / экспериментальный код (оставлен намеренно, НЕ баг, НЕ удалять бездумно)
Оставлен, чтобы аудитор/следующий агент мог мгновенно воспроизвести любое состояние флагом, а не писать заново. Всё выключено флагами (§2), на рабочее поведение НЕ влияет:
- `TryBuildAlignedValidRects` + ветка WVR_VALIDRECTS в `ThemedFrameNcCalcSize` (достижима только при `EnableFullRedrawOnOriginMove=false`).
- Guide-line: поля `_divGuideHwnd`, `_divGuideClassRegistered`, `_divGuideWndProc`, консты `DivGuideClassName/DivGuideColorRef/DivGuideAlpha/DivGuideThicknessPx`, методы `EnsureDivGuideClass/EnsureDivGuide/ShowDivGuideAt/HideDivGuide` (вызовы под `if (EnableDividerGuideLine)` / в deferred-ветке).
- Deferred-resize: поля `_divFsPendingCoord/_divFsPendingValid`, ветка в `DivGripWndProc` WM_MOUSEMOVE (под `EnableDividerDeferredResize`), флаш на WM_LBUTTONUP.
- Single-batch: `else`-ветка в `UpdateDividerJointResize(V)` (двухпроходный путь) — активна сейчас, т.к. флаг false включает именно её.
- Frame-sync: `DivFsEnsureHook/DivFsUnhook/DivFsOnRender`, поля `_divFs*`.

## 5. Чего НЕ делать (проверенные тупики)
- НЕ форсить WVR_REDRAW «сильнее» и не крутить NCCALCSIZE ради призрака — уже форсится каждый кадр, не помогает.
- НЕ включать WVR_VALIDRECTS copy-blit (любой якорь) — вернёт призрак/расслоение.
- НЕ трогать `SWP_NOCOPYBITS`/`EnableSuppressResizeBitBlt` ради призрака (не влияет; нужен для другого).
- НЕ включать софт-рендер (прошлые агенты: делает призрак ХУЖЕ).
- НЕ схлопывать двухпроходный батч делителя (§1.2).
- НЕ менять порог unsnap `CaptionUnsnapRestoreThresholdDip=20.0` без запроса пользователя.
- НЕ ждать, что «своя обработка сообщений» уберёт призрак у full-bleed — уже делаем то же, что ОС.
