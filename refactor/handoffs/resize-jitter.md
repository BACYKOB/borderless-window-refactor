# BorderlessWindow — архитектура и состояние

Файл: `Controls/BorderlessWindow.cs`. Платформа: Windows 11, PerMonitorV2 (`app.manifest`), `net10.0-windows`.
Сборка: `dotnet build`. Запуск: `dotnet run`.

## Что используется (итог)

- **Chrome через ControlzEx 7.0.4** (`WindowChromeBehavior`, прикрепляется в конструкторе): убирает
  «дребезг» основного слоя при ресайзе за верх/лево (его `WM_NCCALCSIZE` → `WVR_VALIDRECTS|WVR_REDRAW`),
  hit-test кнопок чтит `WindowChrome.IsHitTestVisibleInChrome`. Тумблер `AttachControlzExChrome`.
  `GlassFrameThickness=0` обязателен (glass запрещён — баги/«призрак»).
- **`WindowStyle=None`, `AllowsTransparency=false`** (аппаратный рендер; None — нет вспышки рамки при
  старте). При None WPF снимает стили рамки → в `OnSourceInitialized` вручную возвращаем
  `WS_THICKFRAME|WS_MIN/MAXIMIZEBOX|WS_SYSMENU` (`EnsureResizeStyles`), иначе нет ресайза.
- **Перетаскивание / двойной клик** — `OnMouseLeftButtonDown` (DragMove + переключение maximize), т.к.
  у ControlzEx нет `CaptionHeight`. Узкая верхняя зона ресайза: `ResizeBorderThickness=Thickness(6,2,6,6)`.
- **Стартовая маска** (гасит вспышку при запуске): главное окно прячется DWM-cloak до первого кадра,
  сверху Win32 layered-окно цвета темы крестфейдит 0→100% → uncloak главного окна → 100→0%. Альфа —
  `SetLayeredWindowAttributes(LWA_ALPHA)`, крестфейд на отдельном потоке с `DwmFlush()` после каждого
  шага (синхронизация с тактом композиции DWM). Тайминги `StartupMaskFadeInMs/OutMs` = 150/250 мс.
- **Анимация перехода обычное⇄развёрнутое** (`AnimateToWindowState` → `StartMaskCrossfade`): по кнопке
  caption и двойному клику по шапке — та же маска на весь монитор: наплыв 0→100% → под непрозрачной
  маской меняется `WindowState` (ресайз скрыт) → растворение 100→0%. Прочие способы (Win+стрелки,
  перетаскивание шапки, снап) идут штатным путём Windows БЕЗ маски — намеренно, чтобы не плодить баги.
- **Auto-hide панель задач** — edge-watcher + 1px-сжатие + gap-mask (без изменений).

## Ключевые факты

- **WindowChrome обязателен** (без него белая вспышка из WPF-пайплайна). **`WindowStyle=None` обязателен**.
- **Glass запрещён** (`GlassFrameThickness=0` всегда).
- **Мерцание тёмных фейдов** оказалось артефактом **8-битного выхода дисплея** (LG TV), а не кодом —
  фикс на стороне дисплея: вывод 10 бит. Подробности — в memory-заметке `display-8bit-dither-flicker`.

## Возможные доработки (не в этой сессии)

- **(б) «Призрачный» полупрозрачный дубль-слой при ресайзе за верх/лево.** Транзитный композиторский
  блит DWM поверх корректной redirection-поверхности (на скриншоте/записи не виден). Основной слой
  ControlzEx починил; остаток — вероятно платформенный лимит WPF+DWM.
  - **СТАТУС (сессия 2026-06-25):** глубоко исследовано, призрак НЕ убран. Полный разбор шагов, выводов
    (с пометкой предположений), карта экспериментальных флагов в коде и скриншоты этапов —
    в `docs/resize-ghost/resize-ghost.md`. ЧИТАТЬ ЕГО ПЕРВЫМ. Кратко: призрак = растяг redirection-bitmap у
    движущегося края full-bleed клиента; ghost-free только с видимой рамкой (отвергнуто из-за полосы
    сверху) или DirectComposition (WPF не умеет). Следующий шаг (выбран пользователем) — свой chrome (см. «в»).
- **(в) Убрать зависимость ControlzEx**, написав свой chrome: `WM_NCCALCSIZE` → `WVR_VALIDRECTS|WVR_REDRAW`
  (анти-jitter), `WM_NCHITTEST` (ресайз). НЕ переносить баг ControlzEx с обрезкой нижней 1px в maximized.
