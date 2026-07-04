# AGENT_LOG — журнал задач и заметок для продолжения работы

> Для агента с пустым контекстом. Порядок чтения: этот файл → `PLAN.md` (утверждённый план — источник истины) → `TASKS.md` (поэтапные задачи T1–T6 со статусами, границами и критериями приёмки). Возьми первую задачу в статусе `TODO` и работай по её разделу. Работай автономно, не задавая вопросов пользователю без крайней необходимости.

## Что это за проект

Рефакторинг WPF-класса `BorderlessWindow` (кастомное безрамочное окно с themed system frame, snap joint-resize, unsnap-restore, анимациями) + два новых фикса. Веб-часть проекта (Next.js) — НЕ относится к задаче, это просто среда v0; вся работа идёт в каталоге `refactor/`.

## Структура каталога `refactor/`

| Путь | Что это |
|---|---|
| `original/BorderlessWindow.cs` | Исходник 5546 строк — ЕДИНСТВЕННЫЙ источник кода для переноса. Не редактировать. |
| `handoffs/*.md` | 5 handoff-документов прошлых агентов (Opus): un-snap-restore, resize-jitter, animation, snap-joint-resize, resize-ghost. Содержат карту флагов, доказанные тупики и жёсткие ограничения. |
| `xaml/MainWindow.xaml`, `xaml/Controls.xaml` | XAML окна (`MainWindow.xaml` из корня проекта пользователя) и `Themes/Controls.xaml` (подключается из App.xaml; merged: Brushes.xaml, Converters.xaml — их в repo нет). Отдельного ControlTemplate для типа BorderlessWindow НЕТ: стиль (BorderThickness `1,0,1,1`, триггер Maximized→0) задан inline в MainWindow.xaml; chrome целиком в коде. XAML НЕ трогаем; только сверка точек соприкосновения (имена элементов, BorderBrush/BorderThickness, `WindowChrome.IsHitTestVisibleInChrome`). |
| `TASKS.md` | Поэтапные задачи T1–T6: статусы, входы, границы, критерии приёмки, процесс исполнитель/ревьюер. РАБОЧИЙ файл — обновляй статусы. |
| `METHOD_MAP.md` | ГОТОВ (T1 done): полная разметка всех членов класса по диапазонам строк → partial-адресат или DELETE с причиной. Точки внедрения обоих фиксов помечены. Директива для T2–T5 — исходник целиком больше НЕ читать. |
| `logs/snap-joint-resize-gap.log` | Длинный лог (5543 строки) финального тестирования снапов — виден баг «зазор при движении границы» (поток CacheSet с дельтами до ~60px/кадр). |
| `logs/un-snap-restore-ghost.log` | Короткий лог — виден баг «призрак в правом верхнем углу / при движении левой границы». |
| `PLAN.md` | Утверждённый пользователем план (7 partial-файлов, удаления, 2 фикса). Источник истины. |
| `Controls/` | ЦЕЛЬ: сюда пишутся 7 partial-файлов. Ещё не создано. |
| `IDEAS.md` | ЦЕЛЬ: нереализованные предложения. Ещё не создано. |

## Решения пользователя (подтверждены, не переспрашивать)

1. Partial classes — 7 файлов (карта в PLAN.md Часть 1).
2. Удалить ВСЕ мёртвые эксперименты + весь путь ControlzEx.
3. Удалить диагностику TsLog/SnapLog ПОЛНОСТЬЮ (не оставлять dormant).
4. Оба новых фикса — за const-флагами, по умолчанию ВКЛЮЧЕНЫ; старые пути сохранить как fallback при false.
5. Пользователь хочет автономную работу до финального результата; нереализованные идеи — в отдельный `IDEAS.md`.
6. Стенд пользователя: Win11, 4K @150%, `net10.0-windows`. В песочнице НЕТ компилятора C# — сборку проверяет пользователь. Экономить кредиты: минимизировать повторные чтения, писать файлы крупными блоками.

## Статус задач

Актуальные статусы — в `TASKS.md`. T1 (METHOD_MAP) — `DONE`: исходник прочитан ПОЛНОСТЬЮ (все 5546 строк), разметка в `METHOD_MAP.md`. Следующая задача: T2 (модель Opus). Назначение моделей по задачам — таблица в начале `TASKS.md`.
Важно: вложения чата из `user_read_only_context` НЕ доступны в других чатах — всё нужное уже скопировано в этот каталог.

## Рабочие заметки по исходнику (что уже выяснено чтением)

- Один класс `BorderlessWindow : Window`, namespace смотри в шапке `original/BorderlessWindow.cs` (строки 1–40: usings, объявление, начало констант-флагов).
- Все флаги — `private const bool/int/double` в начале класса (~строки 34–260). При переносе собрать ЕДИНУЮ карту флагов в ядре с комментарием-указателем, какой partial их использует.
- `WindowProc` — центральный switch по WM_*; при разбиении оставить его в ядре, а обработчики-методы разнести по partial'ам (вызовы вида `HandleXxx(...)` уже в основном выделены в методы).
- Диагностика размазана по всему файлу: `TsLog(`, `SnapLog(` — сотни call-site'ов; удалять вызовы целиком вместе с окружающими `if (Enable...Log)` блоками. Осторожно: некоторые лог-блоки содержат side-effect-free вычисления только для лога — удалять вместе; но проверять, что переменная не используется дальше.
- Двухпроходный `ApplyDividerBatch`: предиктивный вызов с `moveOur=false` идёт из `UpdateDividerJointResize(V)`; финальный — оттуда же ниже. Для фикса 1 менять именно эту связку (см. PLAN.md Часть 3 п.1).
- `FollowFrameResizeNeighbors` вызывается из обработчика `WM_WINDOWPOSCHANGED`; для фикса 1 п.2 добавить вызов «grower-first» ветки в `WM_WINDOWPOSCHANGING` (там уже есть обработка pending-rect для других нужд — встроиться рядом).
- Верхний ghost-guard: `ThemedFrameInset` применяется в `ThemedFrameNcCalcSize` только к top. Фикс 2 — симметрично к left + `BorderThickness.Left=0` в `ApplyThemedBorderMetrics` (XAML `BorderThickness="1,0,1,1"` — проверить точное значение в `xaml/Controls.xaml.md`; триггер Maximized ставит 0).
- `ThemeBorderColorRef = 0x00403432` — DWM border color, совпадает с XAML BorderBrush (#323440 в BGR) — визуальная неизменность левого 1px подтверждена сверкой.
- Unsnap-restore: Variant A/A+/A++ (manual move + WinEvent hook + handoff to shell) — НЕ ТРОГАТЬ логику, только перенос 1:1 и вычистка логов. `CaptionUnsnapRestoreThresholdDip = 20.0` — не менять.
- Подтверждённые мёртвые ветки перечислены в PLAN.md Часть 2 — но перед удалением КАЖДОГО символа перепроверить грэпом по исходнику, что он не вызывается живым кодом (handoff'ы местами устарели).

## Следующи�� шаги

Пошаговое разбиение перенесено в `TASKS.md` (T1 METHOD_MAP → T2 Interop+ядро → T3 Chrome+ghost-fix → T4 SnapResize+gap-fix → T5 Unsnap/Animation/Taskbar → T6 верификация+IDEAS). Возьми первую `TODO`-задачу оттуда. Практика: пиши файлы крупными Write-блоками; после каждого файла — баланс скобок; не перечитывай написанное без нужды.

## Журнал сессий

> Каждый агент при старте и при сдаче задачи добавляет запись: дата, задача, что сделано, что выяснено нового, самостоятельные решения и их причины. Ревьюер добавляет вердикт.

- **2026-07-03/04, агент №1 (планирование + handoff)**: прочитаны все handoff'ы/логи/XAML, план утверждён пользователем, исходник прочитан детально до строки 4876 (конспект — «Рабочие заметки» выше), собран handoff-пакет (этот каталог), XAML распакован из .md в настоящие `.xaml`, создан `TASKS.md`. Кода не писалось. Роль дальше — ревьюер задач T2–T6.
- **2026-07-04, агент №1 (T1 закрыта)**: дочитан хвост исходника 4876–5546 (edge-watcher/ShrinkBottom, gap-mask, placement, весь WinAPI-регион). Создан `METHOD_MAP.md` — полная разметка. Новое из хвоста: (1) флаги мёртвых divider-экспериментов живут НЕ в шапке, а в interop-регионе, строки 5364–5370 — комментарии в них содержат итоговые диагнозы ghost'а, ценное перенесено в PLAN/METHOD_MAP; (2) `EnableFullRedrawOnOriginMove=true` — это baseline-поведение NCCALCSIZE, НЕ эксперимент, переносить как есть; (3) `GetWndClass` (5267) и `SetWindowThemeAttribute`-блок — кандидаты DEL с обязательной грэп-проверкой (помечено в METHOD_MAP). В `TASKS.md` добавлена таблица назначения моделей (T2/T6 → Opus, T3/T4 → Fable, T5 → GPT) по требованию пользователя.
- **2026-07-04, агент-исполнитель T2 (Opus) — T2 сдана в `REVIEW`**

  **Задача**: создать `Controls/BorderlessWindow.Interop.cs` и `Controls/BorderlessWindow.cs` — перенос 1:1 по METHOD_MAP (§1 поля/флаги, §2 ядро, §9 interop) минус удаления PLAN Часть 2, + единая карта флагов в ядре + 2 новых флага.

  **Результат — 2 файла (namespace `ControlPanel`, `partial class BorderlessWindow : Window`, переносы строк CRLF)**:
  - `Controls/BorderlessWindow.Interop.cs` — 400 строк. Весь нативный слой: 55 `[DllImport]`, 12 структур (`RECT/POINT/SIZE/MINMAXINFO/MONITORINFO/NCCALCSIZE_PARAMS/WINDOWPOS/WINDOWPLACEMENT/APPBARDATA/WNDCLASS/WTA_OPTIONS/BLENDFUNCTION`), 4 делегата (`WndProcDelegate/EnumWindowsProc/MonitorEnumProc/WinEventDelegate`), все Win32-константы (WM/SC/WMSZ/HT/IDC/SW/DWMWA/SWP/GWL/WS/RDW/WVR/SM/VK/LWA/ULW/GDI/AppBar/WinEvent). Баланс `{}` 14/14, `()` 149/149.
  - `Controls/BorderlessWindow.cs` — 576 строк. Ядро: единая карта флагов, `CaptionHeight`, поля `_startupRevealStarted/_offscreenPrevRect/_offscreenPrevValid`, конструктор, `OnSourceInitialized/OnContentRendered/OnStateChanged/OnDpiChanged`, 4 mouse-override, `OnClosing`, диспетчер `WindowProc`, `IsResizePosChange`. Баланс `{}` 50/50, `()` 258/258.

  **Самопроверка**: P/Invoke 55/55, структуры 12/12, делегаты 4/4, баланс скобок сходится. Запрещённые токены `TsLog|SnapLog|WindowChromeBehavior|ControlzEx` в коде — 0.

  **Грэп-проверки KEEP/DEL**: `SetWindowThemeAttribute`/`WTA_OPTIONS` → KEEP (живой вызов в `ApplyThemedSystemFrame`). `CreateRectRgn`/`SetWindowRgn` → KEEP (живой вызов в `StartScreenshotCrossfade`, стр. 4648). `DwmFlush` → KEEP в INTEROP (потребитель T5 удалит, чистка T6). `GetWndClass`/`GetClassName` → DEL (только диагностика).

  **Ключевые отклонения от METHOD_MAP**:
  1. Единая карта флагов в ядре (CORE), не в partial'ах — по требованию PLAN. ⚠️ T3/T4/T5: НЕ переобъявлять флаги, только ссылаться.
  2. `EnableSuppressResizeBitBlt` восстановлен (конфликт METHOD_MAP DEL vs PLAN KEEP — решено в пользу PLAN). ⚠️ T3: `SuppressResizeBitBlt` (стр. 3536) ОБЯЗАН перенести, не удалять.
  3. `CaptionHeight` (protected prop = 25) вынесен в CORE — критично для компиляции.
  4. Числовые tuning-константы оставлены partial'ам; `DWMWA_NCRENDERING_POLICY`/`DWMNCRP_DISABLED` оставлены в INTEROP до T6-чистки.

  **Каскады для следующих задач**: T3 — точка внедрения `EnableLeftEdgeGhostGuard` в `ThemedFrameNcCalcSize` (стр. 2254) + `ApplyThemedBorderMetrics`. T4 — точка внедрения `EnableSeamGapFix` в `UpdateDividerJointResize(V)` (стр. 1434) + `ApplyDividerBatch` (стр. 1508) + grower-first в `WM_WINDOWPOSCHANGING`.

  ✅ **РЕВЬЮ (v0/Opus 4)**: Сверка против оригинала 5546 строк. Все числовые показатели подтверждены независимым грэпом. Отклонения обоснованы. **Вердикт: T2 APPROVED.**

## Чего НЕ делать (уроки прошлых агентов, из handoff'ов)

- Не пытаться закрывать зазор рисованием: fill-окна, оверлеи, маски — доказанный тупик (артефакты/мерцание).
- Не бороться с призраком через `WVR_VALIDRECTS`, `SWP_NOCOPYBITS`, DWM frame-sync, deferred resize, программный рендер — всё доказанно не работает.
- Не включать старый `EnableDividerSingleBatch` как есть — он ломался на min-size clamp (наложение окон); фикс 1 решает это предвычислением клампа через `WM_GETMINMAXINFO`.
- Не менять поведение maximized/fullscreen путей и анимаций — они стабильны.
- Не редактировать XAML — среда не поддерживает; все компенсации только кодом (локальные значения DependencyProperty перекрывают стиль).
