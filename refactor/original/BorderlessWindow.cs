using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using ControlzEx.Behaviors;
using Microsoft.Xaml.Behaviors;

namespace ControlPanel
{
    /// <summary>
    /// Базовое окно без системной рамки.
    /// <para>
    /// Мерцание при масштабировании/разворачивании в WPF возникает, когда
    /// безрамочное окно делают через <c>AllowsTransparency="True"</c>. Здесь
    /// прозрачность выключена, окно на аппаратном рендеринге, вид — через WindowChrome.
    /// </para>
    /// <para>
    /// При нижней auto-hide панели окно занимает ВЕСЬ монитор (без «дыры»). Когда курсор
    /// подходит к краю (зона <see cref="ArmBand"/> px), окно заранее укорачивается на 1px —
    /// край свободен к моменту касания, и панель выезжает НАТИВНО без лага. Окно остаётся
    /// сжатым, пока панель видима, и расширяется обратно
    /// ТОЛЬКО после того, как панель была показана и затем спряталась. Флаг
    /// _taskbarWasVisible не даёт окну дёргаться (сжиматься/разжиматься) в промежутке,
    /// пока панель ещё не успела выехать — именно это вызывало мерцание в зазоре.
    /// Глобальные настройки системы не меняются.
    /// </para>
    /// </summary>
    public class BorderlessWindow : Window
    {
        private readonly DispatcherTimer _edgeWatcher;
        private RECT _currentMonitorRect;   // монитор окна в текущем maximized-сеансе (физ. px)
        private int _taskbarHeight = 40;    // высота нижней панели (px)
        private bool _watchActive;
        private bool _shrunk;               // true, если окно сейчас на 1px короче
        private bool _taskbarWasVisible;    // панель хотя бы раз показалась после сжатия
        private int _waitTicks;             // счётчик ожидания выезда (страховка от залипания)
        private int _prevCursorY;           // прошлый Y курсора (физ. px) — для направления
        private bool _hasPrevCursorY;       // _prevCursorY ещё не инициализирован
        private bool _armSuppressed;        // не пере-сжимать, пока курсор «припаркован» в зоне
        private bool _cursorDriven;         // курсор уже «доведён» до края в текущем цикле сжатия

        // Зона предв. освобождения края (физ. px): сжимаем заранее, не дожидаясь
        // самого последнего ряда, чтобы графа была свободна к моменту касания.
        private const int ArmBand = 3;
        // Короткий safety-timeout ожидания выезда панели (тиков по ~10 мс).
        private const int ArmTimeoutTicks = 25;
        // Доводка курсора: когда он замер в зоне, но не достал последний ряд пикселей,
        // мягко подвести его к краю (≤ ArmBand px, один раз), чтобы ОС открыла панель
        // без задержки. Выключить — откатить к чистому pre-arm без касания курсора.
        private static readonly bool EnableCursorDrive = true;

        // Маскировка зазора: пока окно сжато и панель ещё не выехала, в освобождённой
        // 1px-полосе виден рабочий стол. Топмост click-through «заглушка» тёмного цвета
        // прячет этот просвет; выехавшая панель перекрывает её. Выключить — откатить.
        private static readonly bool EnableGapMask = true;
        private Window? _gapMask;

        // Высота зоны заголовка в DIP (первая строка Grid титула = 25). По нажатию ЛКМ в этой
        // полосе окно перетаскивается (DragMove), двойной клик — разворачивает/восстанавливает.
        // Нужно, потому что ControlzEx не имеет CaptionHeight: пустая зона титула = HTCLIENT, и ОС
        // сама окно не таскает. Перекрыть в наследнике, если высота шапки иная.
        protected double CaptionHeight { get; set; } = 25;

        // Маска-крестфейд (исполь����уется и при старте, и при переходе обычное⇄развёрнутое). На старте:
        // главное окно прячется DWM-cloak до первого кадра (вспышка невидима), сверху окно-МАСКА цвета
        // темы наливается 0→100% → под непрозрачной маской uncloak главного окна → 100→0% → маска
        // уничтожается. При переходе тем же крестфейдом скрывается ресайз окна (см. AnimateToWindowState).
        // Выключить — откатить к мгновенным переходам и стартовой вспышке.
        private static readonly bool EnableStartupMask = true;
        // Видимые фазы короткие (большая площадь на >~300мс «укачивает», на <40мс мелькает); выход чуть
        // быстрее входа. Скрытие подложки даёт не время, а ожидание кадра с контентом (см. _gateOnRender).
        private const int StartupMaskFadeInMs = 80;     // длительность наплыва маски
        private const int StartupMaskFadeOutMs = 55;    // длительность растворения маски
        // Раскрытие из cloak растворяется не по таймеру, а по факту представленного кадра с контентом
        // (CompositionTarget.Rendering после uncloak) — подложка скрыта ровно до готовности, адаптивно к
        // железу. Таймаут — страховка, если кадр не пришёл (напр. окно свёрнуто).
        private const int PeakGateTimeoutMs = 250;
        private readonly ManualResetEventSlim _peakGate = new(false);

        // Вариант B (скриншот-мас��а): длительность растворения снимка и GDI-ресурсы активной маски.
        private const int ScreenshotAppearMs = 120;   // появление (старт, восстановление из трея, вход в fullscreen). Мс
        private const int ScreenshotDisappearMs = 90; // исчезновение (сворачивание, выход из fullscreen) — короче появления. Мс
        private int _shotDurationMs;                  // длительность текущего растворения скриншот-маски
        private IntPtr _shotScreenDc;
        private IntPtr _shotMemDc;
        private IntPtr _shotBmp;
        private IntPtr _shotOldBmp;
        private RECT _shotRect;
        private volatile bool _gateOnRender;    // ждать ли на пике кадр с контентом перед растворением
        // Вес easing-кривой альфы: 0 = линейно, 1 = чистая квадратика; 0.5 — мягкое ease-out без
        // «замороженных» концов. См. Ease.
        private const double MaskEaseWeight = 0.5;
        private bool _startupHiding;            // главное окно временно скрыто (cloak) до раскрытия
        private WindowState _prevWindowState = WindowState.Normal; // для детекта восстановления из трея
        // Маска — «голое» Win32 layered-окно (НЕ WPF), залитое сплошным цветом темы; альфа задаётся
        // SetLayeredWindowAttributes(LWA_ALPHA), крестфейд гонит отдельный поток с DwmFlush() после
        // каждого шага (привязка к такту композиции DWM — иначе шаги «склеиваются»). См. StartMaskReveal.
        private IntPtr _maskHwnd;               // HWND окна-маски
        private Thread? _maskThread;            // поток крестфейда (альфа + DwmFlush, вне UI-потока)
        private volatile bool _maskAbort;       // прервать крестфейд (закрытие/перезапуск)
        private Action? _maskAtPeak;            // действие на пике непрозрачности (uncloak старта / смена WindowState)

        // Подключать ли ControlzEx (chrome: anti-jitter ресайза + hit-test). Диагностика подтвердила,
        // что НЕ ControlzEx мешает оконному фейду. Оставляем true. Уда��ени�� ControlzEx — отдельная
        // задача (свой borderless-chrome), см. RESIZE-JITTER-TASK.md.
        // ЭКСПЕРИМЕНТ (задача "б", направление B): ���ме��то borderless-клиента на всё окно — ОСТАВИТЬ системную
        // рамку DWM (её дв����ущийся край при ресайзе рисует сам DWM мгновенно → нет растяга redirection-
        // поверхности → нет призрака), но УБРАТЬ её визуал через DWM-атрибуты: квадратные углы (DONOTROUND),
        // тёмная рамка (BORDER_COLOR), тёмный caption (CAPTION_COLOR). Тогда ControlzEx НЕ подключаем (он
        // как раз срезает рамку через NCCALCSIZE — нам она нужна). Титул рисуем своим XAML поверх клиента.
        // true = этот режим; false = прежний borderless+ControlzEx. См. resize-ghost-layer-progress.
        // ВАЖНО: const (не static readonly) — инициализаторы static-полей бегут в порядке объявления, и
        // AttachControlzExChrome ниже читал бы ещё-неинициализированный флаг (default false). const
        // вычисляется на компиляции, порядок не важен.
        // ПУТЬ A (сессия 2026-06-25 #2): ВКЛЮЧЕНО (true). Системная рамка = НЕ-WPF поверхность на движущемся
        // крае → нет растяга redirection-bitmap → нет призрака; но делаем рамку НЕВИДИМОЙ: 1px-кант в цвет
        // фона (#191A1C через DWMWA_BORDER_COLOR) + тонкий ВЕРХ через top-only NCCALCSIZE + гашение белой
        // вспышки при move через WM_ERASEBKGND. Это НЕ «системная шапка» (полки/заголовка нет) — волосок в
        // цвет фона. false = откат к borderless+ControlzEx (с призраком).
        private const bool UseThemedSystemFrame = true;
        // Отдельно: красить ли системную рамку DWM-атрибутами (углы/цвет/тёмный режим). Разнесено с
        // UseThemedSystemFrame, чтобы изолировать: сначала подтверждаем «рамка без покраски = нет призрака»
        // (репро Теста 3), затем включаем покраску и смотрим, не она ли возвращает призрак.
        private const bool ApplyDwmFrameAttributes = true;

        // Подключать ли ControlzEx (chrome: anti-jitter ресайза + hit-test). В режиме UseThemedSystemFrame
        // рамку оставляем системной → ControlzEx (срезающий её через NCCALCSIZE) НЕ нужен.
        private const bool AttachControlzExChrome = !UseThemedSystemFrame;

        // Наш ручной SWP_NOCOPYBITS на WM_WINDOWPOSCHANGING (см. SuppressResizeBitBlt). ГИПОТЕЗА (задача
        // "б", остаточный «призрак» при ресайзе за верх/лево): он КОНФЛИКТУЕТ с ControlzEx, который на
        // WM_NCCALCSIZE возвращает WVR_VALIDRECTS и делает точный выровненный копи-блит ради flicker-free.
        // NOCOPYBITS запрещает копирование вообще → новая область пустеет → DWM на 1-2 кадра растягивает
        // СТАРУЮ redirection-поверхность → это и есть полупрозрачный дубль. false = отдать копи-блит
        // ControlzEx (тест 1). true = прежнее поведение (наш NOCOPYBITS). См. resize-ghost-layer-progress.
        // ЗАДАЧА "б" (flicker-free без призрака и без бланка): теперь наш ThemedFrameNcCalcSize сам
        // возвращает WVR_VALIDRECTS с выровненным по НЕПОДВИЖНОМУ краю копи-блитом (роль, которую в
        // caption-режиме не выполняет неприкреплённый ControlzEx). NOCOPYBITS с этим КОНФЛИКТУЕТ:
        // запрещает копирование → новая область пуста → окно на кадр исчезает целиком (бланк). Выключаем.
        private static readonly bool EnableSuppressResizeBitBlt = false;


        public BorderlessWindow()
        {
            // Инициализируем ПЕРВЫМ: добавление поведения ControlzEx ниже при старте в maximized
            // синхронно вызывает OnStateChanged → UpdateEdgeWatcher → StartEdgeWatcher, который
            // обращается к _edgeWatcher. Иначе — NRE при запуске в полноэкранном режиме.
            _edgeWatcher = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(10),
            };
            _edgeWatcher.Tick += EdgeWatcher_Tick;

            // WindowStyle=None — единственный режим без белой вспышки при старте: WPF НЕ рисует
            // стандартную рамку/заголовок. SingleBorderWindow даёт вспышку (рамка мелькает до того,
            // как ControlzEx срежет её через NCCALCSIZE). Цена None: теряется системная анимация
            // сворачивания/восстановления (подтверждено мейнтейнером ControlzEx).
            // ВАЖН��: п��и None ControlzEx НЕ д��бавляет WS_THICKFRAME сам → ресайз/перетаскивание не
            // работают. Поэтому стили возвращаем вручную в OnSourceInitialized (EnsureResizeStyles) —
            // ровно так под капотом делал встроенный WindowChrome.
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false; // аппаратный рендеринг — нет мерцания ��кна
            ResizeMode = ResizeMode.CanResize;

            // Безрамочный chrome через ControlzEx вместо встроенного System.Windows.Shell.WindowChrome.
            // Причина: встроенный на WM_NCCALCSIZE возвращает 0 («сохранить старый client») → DWM
            // (Win8+) блитит старый кадр при ресайзе за верх/лево → раздвоение/дрожание контента.
            // ControlzEx возвращает WVR_VALIDRECTS|WVR_REDRAW (+ управление регионом) → flicker-free.
            // Hit-test кнопок шапки он чтит через тот же WindowChrome.IsHitTestVisibleInChrome (XAML
            // не меняем). Перетаскивание/ресайз — через его NCHITTEST по ResizeBorderThickness.
            // В ControlzEx 7 flicker-free (NCCALCSIZE → WVR_VALIDRECTS|WVR_REDRAW) включён по
            // умолчанию — отдельного флага нет.
            var chrome = new WindowChromeBehavior
            {
                // Верх тоньше (2px): верхние ~6px раньше были зоной ресайза (HTTOP) и «съедали» клики
                // по верхушке кнопок/шапки — широкая нерабочая полоса. Бока/низ оставляем 6px для
                // удобного захвата. Перетаскивание окна за шапку обеспечивает OnMouseLeftButtonDown.
                ResizeBorderThickness = new Thickness(6, 2, 6, 6),
                GlassFrameThickness = new Thickness(0), // НЕТ glass-рамки: это DWM-полупрозрачный
                                                        // слой («призрак», двоится при ресайзе) +
                                                        // мелькание линий при старте.
                KeepBorderOnMaximize = false,        // мы безрамочные; рамку на maximize не рисуем
                IgnoreTaskbarOnMaximize = false,     // maximized-границы держим сами (auto-hide ��огика)
                UseNativeCaptionButtons = false,     // кнопки Close/Max/Min у нас свои в XAML
            };
            if (AttachControlzExChrome)
                Interaction.GetBehaviors(this).Add(chrome);

            CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand,
                (_, e) => SystemCommands.CloseWindow((Window)e.Parameter)));
            // Свернуть по кнопке caption — через анимацию маски (как выход из fullscreen, но на пике
            // окно сворач��вается, а не меняет размер). Восстановление из трея ловится в OnStateChanged.
            CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand,
                (_, _) => AnimateMinimize()));
            // Развернуть/восстановить по кнопкам caption — через анимацию маски (как и дво��ной клик).
            CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand,
                (_, _) => AnimateToWindowState(WindowState.Maximized)));
            CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand,
                (_, _) => AnimateToWindowState(WindowState.Normal)));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
            InstallUnsnapWinEventHook(); // БАГ 1: реактивный restore после жеста ОС unsnap-drag
            this.Closed += (_, _) => DestroyEdgeGrip(); // 1.2: убрать оверлей при закрытии окна

            // Прячем главное окно (DWM cloak) ДО любого позиционирования и первого кадра — вспышка
            // пройдёт невидимо. Раскроем (uncloak) после того, как маска нальётся до 100%.
            HideMainForStartup(handle);

            // При WindowStyle=None WPF снимает стили рамки. ControlzEx их сам не возвращает →
            // ресайз/перетаскивание/snap не работают. Возвращаем вручную (как делал встроенный
            // WindowChrome). Делаем ПОСЛЕ base.OnSourceInitialized (ControlzEx уже прицепился) и
            // ДО RestorePlacement (он может развернуть окно — стили нужны заранее).
            EnsureResizeStyles(handle);

            // ПУТЬ A: оставляем системную рамку, но гасим её визуал (DWM-рамка off, тёмный режим, квадрат).
            if (UseThemedSystemFrame && ApplyDwmFrameAttributes)
            {
                ApplyThemedSystemFrame(handle);
                ApplyThemedBorderMetrics(); // 1-физ-px WPF-бордер право/низ (верх/лево — охранный NC-страйп)
            }

            RestorePlacement(handle);

            // RestorePlacement → SetWindowPlacement ПОКАЗЫВАЕТ окно, и показ может визуально «снять»
            // cloak. Пере-утверждаем его ПОСЛЕ показа, чтобы главное окно было точно невидимо до пика
            // наплыва маски (иначе сквозь полупрозрачную маску просвечивал бы его старт).
            if (_startupHiding)
            {
                int recloak = 1;
                DwmSetWindowAttribute(handle, DWMWA_CLOAK, ref recloak, sizeof(int));
            }

            // Маску поднимаем не здесь, а в OnContentRendered — когда главное окно уже отрисовало
            // первый кадр (под cloak). ContextIdle — страховка, если ContentRendered не придёт
            // (чтобы окно не осталось cloaked навсегда).
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(TryStartMaskReveal));
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            TryStartMaskReveal();
        }

        private bool _startupRevealStarted;

        /// <summary>Однократно поднять маску и запустить крестфейд (защита от повторных вызовов).</summary>
        private void TryStartMaskReveal()
        {
            if (_startupRevealStarted) return;
            _startupRevealStarted = true;
            StartMaskReveal(new WindowInteropHelper(this).Handle);
        }

        /// <summary>
        /// Возвращае���� окну ��тил��, снят��е WindowStyle=None: WS_THICKFRAME (ресайз), WS_MIN/MAXIMIZEBOX,
        /// WS_SYSMENU. БЕЗ WS_CAPTION НАМЕРЕННО: caption заставляет DWM рисовать отдельный слой рамки/
        /// заголовка — это кандидат на «призрачный» полупрозрачный слой, что двоится при ресайзе, и
        /// причина обрезки сверху в maximized. Ресайз работает и без caption (нужен WS_THICKFRAME).
        /// </summary>
        private void EnsureResizeStyles(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            style |= WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
            // ЭКСПЕРИМЕНТ (БАГ 1): возвращаем WS_CAPTION. Без него shell не считает окно полно��енным
            // участ��иком Snap-группы → joint-resize пол��унком не работает (окно либо un-снапится, либо вообще
            // не двигается — ДОКАЗАНО логами). Визуал caption гасит ThemedFrameNcCalcSize (client до верха) +
            // DWM-атрибуты (caption-цвет=фон, immersive dark). true = вернуть caption (проверяем snap). false =
            // прежнее borderless-поведение без caption.
            if (IncludeCaptionForSnap)
                style |= WS_CAPTION;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
            if (EnableSnapDiagLog) SnapLog($"EnsureResizeStyles style=0x{style:X} caption={(((style & WS_CAPTION) == WS_CAPTION) ? 1 : 0)}");

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        // БАГ 1: WS_CAPTION обязателен, чтобы shell считал окно полноценным участником Snap-г��уппы (joint-
        // resize ползунком). ПОДТВЕРЖДЕНО логами. Визуал системного заголовка/кнопок гасим НЕ full-bleed'ом
        // (он вернул бы «призрак»), а отключением неклиентского рендеринга DWM — см. DisableDwmNcRendering.
        private const bool IncludeCaptionForSnap = false;
        // БАГ 1: при WS_CAPTION система/DWM по умолчанию рисует полосу заголовка с кнопками. Отключаем
        // неклиентский рендеринг DWM (DWMWA_NCRENDERING_POLICY = DWMNCRP_DISABLED): окно о����таётся «нормальным»
        // для shell (snap работает), но заголовок/кнопки/рамку DWM НЕ рисует. Анти-призрачную рамку (themed
        // NCCALCSIZE, бок/низ системные) сохраняем — full-bleed НЕ нужен. true = гасить NC-рендер DWM.
        private const bool DisableDwmNcRendering = false;

        /// <summary>
        /// ПУТЬ A: окно сохраняет СИСТЕМНУЮ рамку DWM (ради чистого ресайза без призрака), но её визуал
        /// убираем полностью: квадратные углы (DONOTROUND), тёмный режим (immersive dark), а главное —
        /// <b>DWM-рамку выключаем</b> (<see cref="DWMWA_COLOR_NONE"/>). Её толщину нельзя задать в 1 физ.
        /// пиксель (она масштабируется по DPI → у пользователя 2px), и она давала «тёмную рамку вокруг
        /// светлой». Единственную видимую рамку рисуем сами ровно в 1 физ. px: право/низ — WPF-бордером
        /// (<see cref="ApplyThemedBorderMetrics"/>), верх/лево — охранным 1px-страйпом в NC
        /// (<see cref="PaintThemedGuards"/>), который заодно держит призрак подальше от движущихся краёв.
        /// </summary>
        private void ApplyThemedSystemFrame(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            int corner = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            int borderColor = DWMWA_COLOR_NONE; // выключаем DWM-рамку (DPI-масштабируемую, 2px) — рисуем сами 1 физ. px
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            _snapDwmBorderColorApplied = false;
            int captionColor = MaskColorRef; // #191A1C — caption-зона в цвет фона (на сл��чай если мелькнёт)
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

            // БАГ 1: при ��озвращённом WS_CAPTION (ради snap-группы) DWM по умолчанию рисует полосу заголовка с
            // системными кнопками. Гасим неклиентский рендеринг DWM — окно остаётся «нормальным» для shell
            // (snap/joint-resize работают), но caption/кнопки/рамку DWM не рисует. Анти-призрачный themed-frame
            // (NCCALCSIZE) сохраняем. Если у DWM это не уберёт кнопки — дополним SetWindowThemeAttribute.
            if (DisableDwmNcRendering)
            {
                int ncPolicy = DWMNCRP_DISABLED;
                DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(int));
            }

            // БАГ 1: запрещаем теме рисовать текст заголовка и иконку в NC (caption присутствует ради snap).
            // Кнопки этим не убираются — их гасит full-bleed верх (нет NC-зоны под caption). Если останутся —
            // следующий шаг: перехват WM_NCUAHDRAWCAPTION/WM_NCUAHDRAWFRAME.
            if (IncludeCaptionForSnap)
            {
                var opts = new WTA_OPTIONS { dwFlags = WTNCA_NODRAWCAPTION | WTNCA_NODRAWICON,
                                             dwMask = WTNCA_NODRAWCAPTION | WTNCA_NODRAWICON };
                SetWindowThemeAttribute(hwnd, WTA_NONCLIENT, ref opts, (uint)Marshal.SizeOf<WTA_OPTIONS>());
            }
        }

        /// <summary>
        /// ПУТЬ A: видимая рамка ровно в 1 ФИЗИЧЕСКИЙ пиксель монитора. Толщину в DIP считаем как
        /// 1/DpiScale, чтобы на любом масштабе (125/150/200%) лечь ровно в 1 device-пиксель. Рисуе�� только
        /// ПРАВО/НИЗ (там нет призрака — это неподвижны�� анкер ресайза): <c>Thickness(0,0,tx,ty)</c>. ВЕРХ/ЛЕВО
        /// (движущиеся края) рисует охранный NC-страйп в <see cref="PaintThemedGuards"/> — иначе WPF-бордер на
        /// движущемся крае вернул бы призрак. В развёрнутом окне рамки нет.
        /// </summary>
        private void ApplyThemedBorderMetrics()
        {
            if (!UseThemedSystemFrame) return;
            if (WindowState == WindowState.Maximized) { BorderThickness = new Thickness(0); return; }

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double tx = 1.0 / dpi.DpiScaleX;  // DIP, дающий ровно 1 физ. px по горизонтали
            double ty = 1.0 / dpi.DpiScaleY;  // ... по вертика����и
            // Рам��у 1 физ. px р��сует WPF-бордер со ВСЕХ сторон. Клиент инсечен на 1px со всех краёв
            // (ThemedFrameNcCalcSize) ������ WPF-бордер ����Е на ��вижущемся крае → призрак за верх/лево не возвращается.
            BorderThickness = new Thickness(tx, ty, tx, ty);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            // Восстановление из свёрнутого — материализуем окно «как при ст������������р������е»: cloak + ��а����л����������������в маски,
            // на ����ике uncloak, растворение. Сворачивание и прочие переходы здесь маской не трогаем
            // (сворачивание анимирует AnimateMinimize, развороты — AnimateToWindowState).
            var prev = _prevWindowState;
            _prevWindowState = WindowState;
            if (prev == WindowState.Minimized && WindowState != WindowState.Minimized)
                StartRestoreReveal();

            if (WindowState != WindowState.Maximized)
            {
                _shrunk = false;
                StopEdgeWatcher();
            }
            UpdateEdgeWatcher();
            RefreshEdgeGrip(); // 1.2: состояние окна изменилось — обновляем/прячем наружный grip

            // ПУТЬ A: в развёрнутом окне рамки нет, в обычном — 1-физ-px право/низ. Пересчитываем при смене.
            ApplyThemedBorderMetrics();

            // БАГ 1: при выходе из snapped-состояния (maximize/minimize/restore) выключаем слежение.
            UpdateSnapFollow();
        }

        /// <summary>ПУТЬ A: при смене DPI пересчитываем толщину рамки, чтобы она осталась ровно 1 физ. px.</summary>
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            ApplyThemedBorderMetrics();
        }

        /// <summary>
        /// Перетаскивание окна и разворот по двойному клику за зону заголовка.
        /// <para>
        /// ControlzEx (в отличие от встроенного WindowChrome) не имеет <c>CaptionHeight</c>:
        /// пустая зона титула возвращает <c>HTCLIENT</c>, и система сама окно не двигает.
        /// Поэтому делаем это вручную. Клики по интерактивным элементам шапки (кнопки/меню)
        /// помечаются <c>Handled</c> и сюда не доходят (override не handledEventsToo). Верхние
        /// <c>ResizeBorderThickness</c> px — это <c>HTTOP</c> (нон-клиент, ресайз), не клиент,
        /// поэтому конфликта с ресайзом сверху нет.
        /// </para>
        /// </summary>
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            if (e.Handled || e.GetPosition(this).Y > CaptionHeight)
                return;

            if (e.ClickCount == 2)
            {
                AnimateToWindowState(WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized);
                e.Handled = true;
                return;
            }
            // БАГ 2 / задача 3: snapped-окно высотой во весь текущий монитор должно выходить из snap
            // как штатное окно Windows: НЕ на простой ��лик по шапке, а только после протяжки вниз.
            if (UseThemedSystemFrame && TryBeginCaptionUnsnapRestoreDrag(e))
            {
                e.Handled = true;
                return;
            }

            // В режиме UseThemedSystemFrame перетаскивание делает не WPF DragMove, а штатный shell через
            // WM_NCHITTEST -> HTCAPTION. Это важно ��ля окон, которые до этого были snapped в колонку: Windows
            // должна сама выполнить unsnap/restore и пересчитать DWM/NC-геометрию. DragMove из client-зоны после
            // Snap обходил этот путь и мог оставить старую redirection/NC-поверхность: визуально рамка уже
            // уменьшилась, а серая область старого snapped-прямоугольника оставалась на экране.
            if (!UseThemedSystemFrame && WindowState == WindowState.Normal)
            {
                DragMove();
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (UpdateCaptionUnsnapRestoreDrag())
                e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            if (_captionUnsnapPending || _captionUnsnapDragging)
            {
                EndCaptionUnsnapRestoreDrag(source: "mouse-up");
                e.Handled = true;
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);

            if (_captionUnsnapPending || _captionUnsnapDragging)
            {
                if (EnableTroubleshootLog)
                    TsLog($"LostCapture pending={_captionUnsnapPending} dragging={_captionUnsnapDragging} ticks={_captionUnsnapMoveTickCount}");
                EndCaptionUnsnapRestoreDrag(false, "lost-capture");
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            StopSnapFollow();
            StopEdgeWatcher();
            RemoveUnsnapWinEventHook(); // БАГ 1: снять WinEvent-хук
            // На случай закрытия посреди крестфейда: прерываем маску и раскрываем окно.
            EndMask();
            UncloakMain(new WindowInteropHelper(this).Handle);
            _gapMask?.Close();
            _gapMask = null;
            SavePlacement(new WindowInteropHelper(this).Handle);
            base.OnClosing(e);
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_WINDOWPOSCHANGED = 0x0047;
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCPAINT = 0x0085;
        private const int WM_NCACTIVATE = 0x0086;
        // Модальный цикл move/resize. Различаем ручную тягу рамки (приходит WM_SIZING) от переноса окна и
        // от программных snap-операций (их вне цикла делает shell). Нужно, чтобы SWP_NOCOPYBITS и форс-
        // пересчёт рамки применялись только к настоящему пользовательскому ресайзу / un-snap. ��м. WindowProc.
        private const int WM_SIZING = 0x0214;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;
        // wParam у WM_SIZING — какой край/угол тянут (winuser.h). Нужно для анти-скачка (����асть 1.1):
        // фиксируем НЕ-перетягиваемые края на якоре, чтобы окно расширялось на месте, а не сдвигалось.
        private const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4,
            WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;

        // ПУТЬ A: ширина зоны хвата ресайза (физ. px). Видимая рамка остаётся 1px — это лишь область, где
        // WM_NCHITTEST возвращает HT*-коды. Больше = легче попасть, но «съедает» верх кнопок шапки. ~6 px.
        private const int ResizeGripPx = 6;
        // ПУТЬ A: тонкая верхняя зона ресайза НАД кнопками шапки (физ. px) — чтобы почти не срезать кнопки.
        // На пустом месте шапки используется полная ResizeGripPx. См. ThemedHitTest/IsOverTitleInteractive.
        private const int ResizeGripThin = 2;
        // HT-коды для WM_NCHITTEST (winuser.h).
        private const int HTTRANSPARENT = -1;
        private const int HTCLIENT = 1;
        private const int HTNOWHERE = 0, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
            HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        // DWMWA_BORDER_COLOR = это значение → DWM НЕ рисует рамку (рисуем сами 1 физ. px).
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        // Режим UseThemedSystemFrame: на сколько px системная рамка остаётся видимой по ВСЕМ краям (физ. px).
        // 1px — видимой «полки»/толстого канта нет (тонкая линия, как BorderThickness=1), но клиент НЕ
        // достаёт до краёв окна (там 1px системной рамки), поэтому призрак при ресайзе не возвращается.
        // Равномерно со всех сторон, иначе DWM красит толстый боковой/нижний отсту�� серым.
        private const int ThemedFrameInset = 1;
        // ПУТЬ A: ВКЛ (true). Перехват WM_NCCALCSIZE (ThemedFrameNcCalcSize) теперь ужимает ТОЛЬКО ВЕРХ
        // (top-only), оставляя бока/низ как посчитал DefWindowProc. Прошлый провал (толстая светлая рамка +
        // белый flicker) был от РАВНОМЕРНОГО инсе��а по всем 4 краям: на Win11 окно простирается на ~7px ЗА
        // видимый край по бокам/низу (невидимая sizing-граница), и «+1px от края окна» вытаскивал 6px NC
        // наружу → толстая светлая рамка. У верха такой добавки нет, top-only её не плодит. Белый flicker
        // при move гасим заливкой клиента цветом фона на WM_ERASEBKGND.
        private const bool ShrinkThemedFrame = true;
        // SNAP LAYOUTS: Windows кладёт HWND snapped-окна с внешним невидимым sizing-выступом, а видимую
        // границу стандартного окна дорисовывает DWM/NC. У нас NC-рамка намеренно невидима, поэтому на
        // внутренних границах Snap Layout / Joint Resize этот выступ превращался в тёмную щель. Старый фикс
        // прижимал client только к rcWork по внешним краям монитора; для 2/3/4-оконных раскладок нужны ещё
        // внутренние разделители. Флаг оставлен отдельным, чтобы можно было быстро откатить, не трогая
        // текущее поведение maximize, auto-hide taskbar и внешний resize-хват.
        private const bool EnableSnapLayoutGapFix = true;
        // ГЛАВНЫЙ ФИКС off-screen-клона. Клиент к rcWork в ThemedFrameNcCalcSize прижимаем ТОЛЬКО когда окно
        // выступает за рабочую область на величину НЕКЛИЕНТСКОЙ РАМКИ (это SNAP, выступ ~рамки). Если окно
        // утащили за край монитора КАК СВОБОДНОЕ (выступ в сотни px), прижим НЕЛЬЗЯ: клиент пинится у края,
        // а HWND уходит дальше — и DWM композитит закадровую NC-полосу как «клон» у края экрана. Свободное
        // окно должно вытекать за край штатно (как любое окно Windows) — тогда клона нет. Порог в физ. px
        // (координаты NCCALCSIZE экранные); с запасом на DPI. 0 = старое поведение (прижим всегда).
        private const int EdgeClampMaxOverhang = 32;
        // На внутренних разделителях Snap нельзя доводить client ровно до HWND-края: при 150% DPI Windows/DWM
        // и соседнее стандартное окно дают визуальное наложение на 1 физический пиксель. Оставляем ровно 1
        // физический px NC-guard на внутренней стороне. Это не возвращает старую щель в несколько px, но убирает
        // наезд. В��жно: NCCALCSIZE работает в физических пикселях, поэтому здесь НЕ DIP и НЕ масштабируем.
        private const int SnapInternalDividerGuardPx = 1;
        private const int EdgeGripFlushGapPx = 16; // free-edge grip is suppressed when a tile abuts this edge within this gap (px)
        // БАГ 1 (ползунок Snap-группы). П��лзунок Win11 между окн�����ми — это overlay САМОГО shell, ле����щий ровно
        // на внутренней Snap-границе. Если наше окно заявляет HT-ресайз на SnapJointResizeHitSlopPx px ВНУТРЬ
        // своего client у этой границы, клик по ползунку п��падает в нашу resize-зо��у → Windows з������пуск���ет
        // ИНДИВИДУАЛЬНЫЙ ресайз ��ашего окна (отрывая дальний край от границы экрана) ВМЕСТО группового joint-
        // resize силами shell. true = на ВНУТРЕННИХ Snap-границах окно ВООБЩЕ не заявляет ресайз (HTNOWHERE):
        // ползунок Snap-группы (overlay shell на видимой гра��ице) больше ����е поп��д����ет в на������ HTRIGHT/HTLEFT, окно
        // ��е UN-снапится и не «уезжае��». Внешние края (край экрана) и верх над шапкой не затронуты. false =
        // прежнее поведение (з��являем ресайз и в выступе у внутренней границы → окно un-снапится при клике по
        // ползунку). ДОК��З����О логами (SIZING edge=2 + прыжок л��вого края + сж��ти�� до пла���������ающ�����й ширины).
        private const bool DeferJointResizeToShell = true;
        // ЧАСТЬ 1.1 (анти-скачок свободного края). При ручном edge-resize snapped-окна Windows на старте
        // un-снапит его �� ВОССТАНАВЛИВАЕТ п��ед-снап floating-������ирин��: НЕ-пере��я��ивае��ый край пр��га��т (��ог:
        // ENTERSIZEMOVE left=-8 → первый SIZING left=228), и окн�� СДВИГАЕТСЯ ��мес��о расширения на месте.
        // Фикс: на WM_ENTERSIZEMOVE запоминаем якорь (GetWindowRect), и пока идёт WM_SIZING, держим
        // НЕ-пер��тя����и��аемые края (по wParam=WMSZ_*) на якоре — окно расширяется на месте. Применяем ТОЛЬКО
        // если на старте цикла окно было snapped (HasInternalSnapDivider): плавающий ресайз не трогаем
        // (там анти-скачок и так no-op — Windows сам держит противоположный край). Изолированно от
        // hit-test/gap-fix/детекта соседа (их правки в сессии #3 и дали регрессии HALF/3-кол). true =
        // включить анти-скачок. false = прежнее поведение (окно сдвигается при un-snap-resize).
        private const bool EnableAnchorUnsnapResize = true;
        // ЗАДАЧА 3: clean un-snap-restore при протяжке full-height snapped окна за шапку вниз.
        // Порог именно физический px, потому что GetCursorPos/GetWindowRect/SetWindowPos работают в px.
        private const bool EnableCaptionUnsnapRestoreDrag = true;
        // Порог un-snap-restore задаётся в ЛОГИЧЕСКИХ пикселях (DIP) и переводится в физ. px под текущий
        // DPI окна в момент сравнения: dx/dy берём из GetCursorPos (физ. px). 20 DIP = 30 физ. px при 150% —
        // сохраняет подобранное вживую поведение (30 физ. px), но теперь одинаково ощущается на любом масштабе.
        private const double CaptionUnsnapRestoreThresholdDip = 20.0;

        private bool _captionUnsnapPending;       // ЛКМ зажата в шапке full-height snapped окна, ждём жест вниз
        private bool _captionUnsnapDragging;      // restore уже применён, ведём окно вручную до отпускания ЛКМ
        private POINT _captionUnsnapDownPt;       // экранная точка нажатия, физ. px
        private RECT _captionUnsnapDownRect;      // snapped rect на момент нажатия
        private RECT _captionUnsnapRestoreRect;   // pre-snap floating rect, к нему возвращаем размер
        private int _captionUnsnapDragOffsetX;    // смещени�� курсора внутр�� восстановленного окна
        private int _captionUnsnapDragOffsetY;
        private RECT _lastFloatingRestoreRect;    // fallback, если WINDOWPLACEMENT.normalPosition невалиден
        private bool _lastFloatingRestoreRectValid;
        private bool _captionUnsnapHandoffToShell;
        private DispatcherTimer? _captionUnsnapWatchdog; // поллинг курсора на случай, если WPF mouse capture слетится
        private int _captionUnsnapMoveTickCount; // для диагностики

        private bool _snapDwmBorderColorApplied;

        // Состояние модального цикла move/resize (см. WM_ENTER/SIZING/EXITSIZEMOVE):
        private bool _inSizeMove;          // активен цикл (тяга шапки ИЛИ рамки)
        // BUG2 (детач при тяге РАМКИ): во время OS-модального ресайза SnapFollow подавлен (_inSizeMove),
        // поэтому снапнутый сосед не едет за нашим краем. Захватываем соседей НА СТАРТЕ цикла.
        private int _sizingEdge; // WMSZ_* активного edge-resize рамкой (0 = нет)
        private readonly System.Collections.Generic.List<IntPtr> _frameNbrsR = new System.Collections.Generic.List<IntPtr>();
        private readonly System.Collections.Generic.List<IntPtr> _frameNbrsL = new System.Collections.Generic.List<IntPtr>();
        private bool _frameJointArmed; // есть снапнутый сосед для совместного ресайза рамкой
        private bool _userEdgeResize;      // в этом цикле пришёл WM_SIZING (именно тяга РАМКИ, не перенос)
        private bool _sizeChangedInLoop;   // в этом цикле менялся размер (в т.ч. un-snap при переносе) →
                                           // по завершении форсируем пересчёт рамки (баг 2)
        private RECT _sizeAnchor;          // ЧАСТЬ 1.1: прямоугольник окна на старте цикла (якорь анти-скачка)
        private bool _sizeAnchorValid;     // ЧАСТЬ 1.1: окно было snapped на старте → держать края по якорю
        private int _lastLoggedHit = int.MinValue; // дедуп шумных WM_NCHITTEST в диаг-логе
        // БАГ off-screen-клон: floating-окно, заехавшее ЗА край монитора, при уменьшении
        // ресайзом оставляет «клон» (устаревшую DWM/redirection-поверхность) в ОСВОБОЖДЁННОЙ
        // зоне. Эта зона ВНЕ нового (меньшего) окна, поэтому RedrawWindow нашего hwnd её не
        // чистит — надо перерисовать десктоп и окна ПОД ней. Запоминаем rect окна на старте
        // цикла, чтобы на выходе вычислит�� и инвалидировать именно освобождённую часть экрана.
        private RECT _offscreenPrevRect;
        private bool _offscreenPrevValid;

        // ГЛАВНЫЙ ФИКС off-screen-клона: для floating-окна, заехавшего ЗА край монитора,
        // наш SWP_NOCOPYBITS заставляе�� DWM растянуть СТАРУЮ redirection-пов��рхность на
        // новый размер — и закадровая часть «клонируется» у края экрана (стойко: закадровую
        // полосу никто не перерисовывает). Поэтому ИМЕННО для этого случая bitblt НЕ подавляем
        // — даём Windows штатно скопировать пиксели, и DWM не растягивает устаревшую поверхность.
        // false = вернуть прежнее поведение (NOCOPYBITS и для off-screen).
        private const bool AllowBitBltForOffscreenResize = true;


        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (EnableSnapDiagLog) LogIncoming(hwnd, msg, wParam, lParam);

            // СТРАХОВКА caption-unsnap pending: перехват Win32-сообщений ДО WPF input.
            // Блокируем любые попытки shell-modal-drag и ведём порог 30 px напрямую из WM_MOUSEMOVE,
            // не завися от WPF capture/route.
            if (_captionUnsnapPending || _captionUnsnapDragging)
            {
                if (msg == WM_NCLBUTTONDOWN)
                {
                    if (EnableTroubleshootLog) TsLog($"WmNcLBtnDown SWALLOW ht={(int)wParam.ToInt64()} pending={_captionUnsnapPending} dragging={_captionUnsnapDragging}");
                    handled = true;
                    return IntPtr.Zero;
                }
                if (msg == WM_SYSCOMMAND)
                {
                    int sc = (int)(wParam.ToInt64() & 0xFFF0);
                    if (sc == SC_MOVE || sc == SC_SIZE)
                    {
                        if (EnableTroubleshootLog) TsLog($"WmSysCmd SWALLOW sc=0x{sc:X} pending={_captionUnsnapPending} dragging={_captionUnsnapDragging}");
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
                if (msg == WM_MOUSEMOVE)
                {
                    UpdateCaptionUnsnapRestoreDrag();
                }
                else if (msg == WM_LBUTTONUP)
                {
                    if (EnableTroubleshootLog) TsLog($"WmLBtnUp pending={_captionUnsnapPending} dragging={_captionUnsnapDragging}");
                    EndCaptionUnsnapRestoreDrag(source: "wm-lbuttonup");
                }
                else if (msg == WM_CAPTURECHANGED)
                {
                    if (EnableTroubleshootLog) TsLog($"WmCaptureChanged newCap=0x{lParam.ToInt64():X} pending={_captionUnsnapPending} dragging={_captionUnsnapDragging}");
                }
            }

            switch (msg)
            {
                case WM_ERASEBKGND:
                    // Мигание при анснапе: пока идёт armed OS-unsnap, ОС гоняет size restore↔regrow, и стандартное
                    // стирание клиента сплошной кистью фона мигает всем окном. Не гасим клиент — держим прежние
                    // пиксели (лучше краткий застой контента, чем мигание в сплошной цвет).
                    if (_unsnapArmValid)
                    {
                        handled = true;
                        return new IntPtr(1); // TRUE: считаем фон стёртым, реального стирания нет
                    }
                    break;
                case WM_SETCURSOR:
                {
                    // Курсор joint-resize (SizeWE) над зоной захвата внутреннего верт. Snap-разделителя.
                    // Принимаем HTCLIENT И HTNOWHERE: ThemedHitTest на самой полосе разделителя возвращает
                    // HTNOWHERE (чтобы клик не запускал индивидуальный ресайз/un-snap — БАГ 1), и в 3-колоночной
                    // раскладке курсор обычно попадает именно в эту HTNOWHERE-полосу. Если ограничиваться только
                    // HTCLIENT, ↔ не появлялся ~25% случаев (БАГ 2a). WM_NCHITTEST не трогаем — ресайз делает
                    // SnapFollow, un-snap не возникает. См. TrySetJointResizeCursor.
                    int htLo = (int)(lParam.ToInt64() & 0xFFFF);
                    bool acceptHt = (htLo == HTCLIENT || htLo == HTNOWHERE);
                    bool setOk = UseThemedSystemFrame && acceptHt && TrySetJointResizeCursor(hwnd);
                    if (EnableTroubleshootLog && acceptHt)
                        TsLog($"WmSetCursor htLo={htLo} setOk={setOk}");
                    if (setOk)
                    {
                        handled = true;
                        return new IntPtr(1); // TRUE: курсор установлен, дальнейшая обработка не нужна
                    }
                    break;
                }
                case WM_GETMINMAXINFO:
                    AdjustMaximizedBounds(hwnd, lParam);
                    handled = true;
                    break;
                case WM_ENTERSIZEMOVE:
                    // Старт цикла (тяга шапки или рамки). До первого WM_SIZING считаем это переносом.
                    _inSizeMove = true;
                    HideEdgeGrip(); // 1.2: прячем наружный grip на время drag главного окна
                    _userEdgeResize = false;
                    _sizeChangedInLoop = false;
                    // БАГ off-screen-клон: фиксируем rect окна на старте цикла ТОЛЬКО для обычного
                    // floating-окна (не snapped/maximized), которое заехало за край монитора. На выходе
                    // по нему перерисуем освобождённую зону, где иначе остаётся клон.
                    _offscreenPrevValid = WindowState == WindowState.Normal &&
                                          GetWindowRect(hwnd, out _offscreenPrevRect) &&
                                          !HasInternalSnapDivider(hwnd) &&
                                          RectCrossesItsMonitor(hwnd, _offscreenPrevRect);
                    if (EnableSnapDiagLog)
                        SnapLog($"OffscreenArm valid={_offscreenPrevValid} state={WindowState} rect={RectStr(hwnd)}");
                    // Ч����СТЬ 1.1: запоминаем якорь анти-скачка ТОЛЬКО если окно стартует snapped. Для
                    // плавающего окна якорь не нужен (Windows и так держит противоположный край).
                    _sizeAnchorValid = EnableAnchorUnsnapResize && UseThemedSystemFrame &&
                                       WindowState == WindowState.Normal &&
                                       GetWindowRect(hwnd, out _sizeAnchor) && HasInternalSnapDivider(hwnd);
                    if (EnableSnapDiagLog && _sizeAnchorValid)
                        SnapLog($"AnchorUnsnap ARM anchor=({_sizeAnchor.Left},{_sizeAnchor.Top},{_sizeAnchor.Right},{_sizeAnchor.Bottom})");
                    // BUG2: ловим snapped-соседей ДО ресайза (окна ещё вплотную), ��тобы двигать их вслед
                    // за краем, пок�� SnapFollow подавлен из-за _inSizeMove.
                    _sizingEdge = 0;
                    _frameNbrsR.Clear();
                    _frameNbrsL.Clear();
                    _frameJointArmed = false;
                    if (EnableFreeEdgeGrip && WindowState == WindowState.Normal &&
                        TryGetSnapInternalEdges(hwnd, out bool fsl, out bool fsr, out _, out _) && (fsl || fsr) &&
                        TryGetVisibleBounds(hwnd, out RECT fvis))
                    {
                        if (fsr) FindSnapNeighbors(hwnd, fvis, true, _frameNbrsR, out _);
                        if (fsl) FindSnapNeighbors(hwnd, fvis, false, _frameNbrsL, out _);
                        _frameJointArmed = _frameNbrsR.Count > 0 || _frameNbrsL.Count > 0;
                        if (EnableTroubleshootLog && _frameJointArmed)
                            TsLog($"FrmArm rN={_frameNbrsR.Count} lN={_frameNbrsL.Count} vis=({fvis.Left},{fvis.Right})");
                    }
                    break;
                case WM_SIZING:
                    // Пришёл только при тяге РАМКИ → это настоящий пользовательский edge-resize.
                    _userEdgeResize = true;
                    _sizingEdge = (int)wParam; // BUG2: какой край рамки тянут (WMSZ_*)
                    // ЧАСТЬ 1.1 (а��ти-скачок): окно ста��т����в��л�� snapped → удер��иваем НЕ-��еретягиваем��е края на
                    // якоре, чтобы un-snap-restore не сдвигал о��н�� (см. EnableAnchorUnsnapResize).
                    if (_sizeAnchorValid && AnchorUnsnapResize(wParam, lParam))
                    {
                        handled = true;
                        return new IntPtr(1); // TRUE: мы ��зменили предлагаемый прямоугольни��
                    }
                    break;
                case WM_EXITSIZEMOVE:
                {
                    ArmFrameReleaseRealign(); // BUG2: после рамочного ресайза тоже лечим shell-дрейф
                    _inSizeMove = false;
                    _userEdgeResize = false;
                    _sizeChangedInLoop = false;
                    _sizeAnchorValid = false; // ЧАСТЬ 1.1: цик�� завершё�� — ��корь анти-скачка больше не действует
                    _offscreenPrevValid = false;
                    _sizingEdge = 0;
                    _frameJointArmed = false;
                    _frameNbrsR.Clear();
                    _frameNbrsL.Clear();
                    RefreshEdgeGrip(); // 1.2: край settled — переставляем нар��жный grip
                    break;
                }
                case WM_WINDOWPOSCHANGING:
                    // За��оминаем, ч��о в текущем цикле реально менялся размер (для форс-пересчёта на выходе).
                    // Вариант A: глушим кадр реверса в snap-rect во время armed OS-unsnap (упреждает телепорт).
                    MaybeSuppressUnsnapResnapFrame(lParam);
                    if (_inSizeMove && IsResizePosChange(lParam))
                        _sizeChangedInLoop = true;
                    // SWP_NOCOPYBITS — только при настоящей ручной тяге рамки. Программные snap/joint-resize/
                    // un-snap (вне цикла или перенос шапки) его НЕ получают: иначе остаётся серая зона и рвётся
                    // перерисовка ползунком Snap-группы. См. ScopeNoCopyBitsToUserResize.
                    // ГЛАВНЫЙ ФИКС off-screen-клона: если окно старт��вало заехавшим за край монитора,
                    // наш SWP_NOCOPYBITS растягивает старую DWM-поверхность → закадровая часть клонируется
                    // у края. Для этого случая bitblt НЕ подавляем — Windows штатно копирует пикс��ли.
                    bool offscreenEdgeResize = AllowBitBltForOffscreenResize &&
                                               _inSizeMove && _userEdgeResize && _offscreenPrevValid;
                    if (EnableSuppressResizeBitBlt && !offscreenEdgeResize)
                        SuppressResizeBitBlt(lParam);
                    break;
                case WM_WINDOWPOSCHANGED:
                    // После Snap / Joint Resize геометрия уже применена. Если у окна ��сть внутренний Snap-
                    // разделитель, оста��ляем 1 физ-px NC-guard против DPI-наезда и просим DWM закрас��ть именно
                    // NC-рамку. Ручная GDI-закраска GetWindowDC здесь не р��ботает над��жно под DWM и может мешат��
                    // shell-resize; DWM-цвет рамки участвует в композиции штатно.
                    if (UseThemedSystemFrame && ApplyDwmFrameAttributes && EnableSnapLayoutGapFix)
                        UpdateSnapDwmBorderColor(hwnd);
                    if (UseThemedSystemFrame)
                    {
                        ApplyThemedBorderMetrics();
                        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN);
                        // Offscreen-resize: окно частью за краем монитора и тянется рамкой —
                        // синхронно ��ерери��о��ыва��м его целиком, чтобы DWM не показывал клон
                        // закадровой области у края экрана.
                        if (_inSizeMove && _userEdgeResize && FloatingWindowCrossesMonitor(hwnd))
                            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                                RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
                    }
                    // БАГ 1: окно могло снапнуться/раснапнуться — вкл/выкл слежение за ползунком joint-resize.
                    // BUG2: при ручной тяге РАМКИ двигаем захваченного снапнутого соседа вслед за краем
                    // (SnapFollow здесь подавлен из-за _inSizeMove).
                    if (_inSizeMove && _userEdgeResize && _frameJointArmed)
                        FollowFrameResizeNeighbors(hwnd);
                    UpdateSnapFollow();
                    UpdateCaptionUnsnapRestoreCache(hwnd);
                    // BUG2: в коротком окне после отпускания grip/рамки подтягиваем захваченного соседа
                    // вплотную к осевшему краю — гасим дрейф от shell-ре-снапа (хэндл соседа известен).
                    if (!_inSizeMove && _divReleaseNbrs.Count > 0)
                    {
                        if (Environment.TickCount64 <= _divReleaseRealignUntil)
                        {
                            if (_divReleaseSide == 3 || _divReleaseSide == 4)
                                MoveNeighborsFlushV(hwnd, _divReleaseSide == 4, _divReleaseNbrs);
                            else
                                MoveNeighborsFlush(hwnd, _divReleaseSide == 2, _divReleaseNbrs);
                        }
                        else _divReleaseNbrs.Clear();
                    }
                    RefreshEdgeGrip(); // 1.2: окно сдвинулось/снапнулось — обн��вляем наружный grip
                    break;
                case WM_NCCALCSIZE:
                    if (UseThemedSystemFrame && ShrinkThemedFrame && wParam != IntPtr.Zero)
                        return ThemedFrameNcCalcSize(hwnd, wParam, lParam, ref handled);
                    break;
                case WM_NCACTIVATE:
                    // П��ТЬ A: п��и смене активн��сти окна DefWindowProc перерисовывает NC-рамку и рисует белую
                    // линию сверху (статична на деактивации, мелькает при быстром move). lParam=-1 говорит
                    // DefWindowProc НЕ перерисовывать NC: тёмная DWM-граница (BORDER_COLOR) остаётся, белой
                    // линии нет. Это НЕ закраска, а подавление лишней перерисовки рамки. wParam (active/
                    // inactive) пробрасываем, чтобы активация/деактивация работала штатно.
                    if (UseThemedSystemFrame)
                    {
                        handled = true;
                        return DefWindowProcW(hwnd, WM_NCACTIVATE, wParam, new IntPtr(-1));
                    }
                    break;
                case WM_NCPAINT:
                    // ПУТЬ A: подавляем NC-отрисовку DefWindowProc (его белую линию рамки, что мелькала при
                    // move). Видимую рамку 1px рисует WPF-бордер у внутренней кромки к��иента. ��то НЕ закраска
                    // артефакта, а отказ от лишней отрисовки рамки системой.
                    if (UseThemedSystemFrame)
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                    break;
                case WM_NCHITTEST:
                    if (UseThemedSystemFrame)
                    {
                        // (1) Обрезка по монитору: если точка попала в НАШ внешний выступ окна, но лежит ЗА
                        // пределами ТЕКУЩЕГО монитора (т.е. на соседнем) — отдаём HTTRANSPARENT. Тогда хит
                        // проваливается на окно под нами, и все пиксели соседнего монитора кликабельны. Так
                        // делает и системная рамка: невидимый sizing-выступ не создаёт мёртвую зону на соседе.
                        if (IsOutsideCurrentMonitor(hwnd, lParam))
                        {
                            handled = true;
                            if (EnableSnapDiagLog) LogHit(lParam, HTTRANSPARENT, "outside-monitor");
                            return new IntPtr(HTTRANSPARENT);
                        }
                        // (2) Хват ресайза. Важно: НЕ возвращаем HTLEFT/HTRIGHT на всю ширину GetResizeGrip
                        // внутри client. Shell Snap тогда начинает трактовать drag разделителя как перемещение
                        // края окна и отрывает противоположный край от границы экрана. Стороны отдаём только в
                        // реальной NC-полосе между HWND и client; верхний хват по-прежнему делаем шире вручную.
                        int ht = ThemedHitTest(hwnd, lParam);
                        if (ht != HTNOWHERE)
                        {
                            handled = true;
                            if (EnableSnapDiagLog) LogHit(lParam, ht, "themed-resize");
                            return new IntPtr(ht);
                        }

                        // (3) Пустая част�� нашей WPF-шапки должна быть на��тоящим HTCAPTION, а не client+DragMove.
                        // Это возвращает Windows нативное поведение snapped-окон: корректный detach из Snap,
                        // drag между мониторами, Aero Snap и обновление shell-состояния окна. Интерактивные
                        // элементы шапки сюда не попадают, чтобы кнопки/меню продолжили получать WPF input.
                        // BUG2 top joint-resize: on the internal TOP snap seam do NOT return HTCAPTION.
                        // Grabbing HTCAPTION starts our own window move-loop (WM_ENTERSIZEMOVE -> _inSizeMove),
                        // which SUPPRESSES SnapFollow_Tick; then the shell Snap-group slider resizes the neighbor
                        // alone and our top edge stays put ("slider appeared, border moved without our window").
                        // The working left/right/bottom seams never become caption (fall through to HTCLIENT), so
                        // SnapFollow stays alive and follows the shell. Mirror that for the top seam: return
                        // HTCLIENT on a thin band around the visible top edge so no move-loop starts and
                        // SnapFollow can latch the top edge (edge=3) and follow the neighbor's bottom edge.
                        if (DeferJointResizeToShell)
                        {
                            TryGetSnapInternalEdges(hwnd, out _, out _, out bool seamTop, out _);
                            if (seamTop && TryGetVisibleBounds(hwnd, out RECT seamVis))
                            {
                                int seamY = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                                int seamBand = Math.Max(SnapFollowGrabBandPx, GetResizeGrip(hwnd));
                                if (seamY >= seamVis.Top - seamBand && seamY <= seamVis.Top + seamBand)
                                {
                                    handled = true;
                                    if (EnableSnapDiagLog) LogHit(lParam, HTCLIENT, "top-seam-snapfollow");
                                    return new IntPtr(HTCLIENT);
                                }
                            }
                        }
                        if (IsInDraggableCaption(hwnd, lParam))
                        {
                            handled = true;

                            // Для full-height snapped окна не отдаём первый down в native HTCAPTION: иначе shell
                            // стартует move-loop сразу, а нам нужно дождаться именно протяжки вниз >=15 px.
                            // Но делаем это только если уже можем найти restore rect. Иначе оставляем штатный
                            // HTCAPTION, чтобы drag по шапке не превратился в no-op.
                            if (IsCaptionUnsnapRestoreCandidate(hwnd) &&
                                GetWindowRect(hwnd, out RECT captionWin) &&
                                TryGetCaptionRestoreRect(hwnd, captionWin, out _))
                            {
                                if (EnableSnapDiagLog) LogHit(lParam, HTCLIENT, WindowState == WindowState.Maximized
                                    ? "caption-maximized-unsnap-pending-client"
                                    : "caption-snapped-unsnap-pending-client");
                                return new IntPtr(HTCLIENT);
                            }

                            if (EnableSnapDiagLog) LogHit(lParam, HTCAPTION, "caption");
                            return new IntPtr(HTCAPTION);
                        }
                        if (EnableSnapDiagLog) LogHit(lParam, HTCLIENT, "fallthrough->DefWindowProc");
                    }
                    break;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// ПУТЬ A: хит-тест resize-хвата. Верх считаем широкой полосой вручную. Бока/низ возвращаем только в
        /// фактической NC-полосе между HWND и client. Это критично для Snap Joint Resize: если вернуть HTRIGHT
        /// на несколько пикселей внутри client, shell может сдвигать всё окно вместо корректного изменения ширины.
        /// HTNOWHERE = «не моё» → дальше DefWindowProc/клиент.
        /// </summary>
        private int ThemedHitTest(IntPtr hwnd, IntPtr lParam)
        {
            if (WindowState == WindowState.Maximized) return HTNOWHERE;
            if (!GetWindowRect(hwnd, out RECT r)) return HTNOWHERE;
            if (!TryGetClientRectScreen(hwnd, out RECT c)) return HTNOWHERE;

            int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
            int g = GetResizeGrip(hwnd);

            // Над кнопкой — тонкая полоса (почти не срезаем кнопку), на пустом месте шапки — ��олная.
            int topBand = IsOverTitleInteractive(x, y) ? ResizeGripThin : g;
            TryGetSnapInternalEdges(hwnd, out bool snapLeft, out bool snapRight, out bool snapTop, out bool snapBottom);

            // БАГ 1 (Д��КАЗАНО логами): на ВНУТРЕННИХ Snap-границах (за окном — сос��дн��е окно Snap-групп��, а не
            // край экрана) ВООБЩЕ НЕ заявляем ресайз. Иначе невидимый sizing-выступ нашего окна возвращает
            // HTLEFT/HTRIGHT/HTBOTTOM ровно под ползунком Snap-группы (он л��жит на видимой границе), клик
            // попадает в НАШ край → Windows трактует это как ��УЧНОЙ ресайз snapped-окна → UN-снапит его в
            // плавающую ширину и тащит индивидуально («окно уезжает целиком впра�����»). Отдавая внутренние края
            // shell'у (HTNOWHERE), возвращаем штатный групповой joint-resize ползунком. Внешние края (край
            // экрана) и верхний хв��т над пустой шапкой не трогаем. См. DeferJointResizeToShell.
            bool blockLeft = DeferJointResizeToShell && snapLeft;
            bool blockRight = DeferJointResizeToShell && snapRight;
            bool blockTop = DeferJointResizeToShell && snapTop;
            bool blockBottom = DeferJointResizeToShell && snapBottom;

            // Resize grip width: mirror the wide top band on the sides/bottom so a floating (non-snapped)
            // window gets a full-width grip like normal windows, instead of only the ~2px NC strip (x < c.Left).
            // Snapped internal divider edges are already blocked above (block*), so this wide grip only applies
            // to free/external edges and does NOT reintroduce the shell joint-resize bug (BUG2).
            bool left = !blockLeft && x < r.Left + g;
            bool right = !blockRight && x >= r.Right - g;
            bool top = !blockTop && y < r.Top + topBand;
            bool bottom = !blockBottom && y >= r.Bottom - g;

            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
            return HTNOWHERE;
        }

        /// <summary>
        /// Показывает стандартный курсор SizeWE над зоной захвата внутреннего вертикального Snap-
        /// разделителя (полоса SnapFollowGrabBandPx у видимой границы — там же, где реально срабатывает
        /// SnapFollow). Расширяет аффорданс joint-resize до ширины как у обычных о��о��. WM_NCHITTEST НЕ трогаем
        /// (остаётся HTNOWHERE) → ресайз делае�� SnapFollow, индивидуального ресайза/un-snap (БАГ 1) нет.
        /// </summary>
        private bool TrySetJointResizeCursor(IntPtr hwnd)
        {
            if (!EnableJointResizeCursor || !EnableSnapFollow || !UseThemedSystemFrame) return false;
            if (WindowState != WindowState.Normal) return false;
            bool gotEdges = TryGetSnapInternalEdges(hwnd, out bool sl, out bool sr, out _, out _);
            if (!gotEdges) { if (EnableTroubleshootLog) TsLog("TsJrc fail=no-edges"); return false; }
            if (!sl && !sr) { if (EnableTroubleshootLog) TsLog($"TsJrc fail=no-vert sl={sl} sr={sr}"); return false; }
            if (!TryGetVisibleBounds(hwnd, out RECT vis)) { if (EnableTroubleshootLog) TsLog("TsJrc fail=no-vis"); return false; }
            if (!GetCursorPos(out POINT cur)) return false;
            if (cur.Y < vis.Top || cur.Y > vis.Bottom) { if (EnableTroubleshootLog) TsLog($"TsJrc fail=Y-out cur=({cur.X},{cur.Y}) vis=({vis.Left},{vis.Top},{vis.Right},{vis.Bottom})"); return false; }
            int dl = sl ? Math.Abs(cur.X - vis.Left) : int.MaxValue;
            int dr = sr ? Math.Abs(cur.X - vis.Right) : int.MaxValue;
            if (Math.Min(dl, dr) > SnapFollowGrabBandPx) { if (EnableTroubleshootLog) TsLog($"TsJrc fail=band sl={sl} sr={sr} dl={dl} dr={dr} cur=({cur.X},{cur.Y}) vis=({vis.Left},{vis.Right})"); return false; }
            // BUG2a: курсор ресайза во ВНУТРЕННЕЙ области показываем только если на хватаемой стороне есть
            // РЕАЛЬНЫЙ сосед (joint-resize через разделитель). Для СВОБОДНОГО края р��са��з делае�� наруж��ая
            // полоса хвата, поэтому внутри окна стрелку НЕ показываем - иначе она висит, но не работает.
            bool grabRight = dr <= dl;
            var jrcNbrs = new System.Collections.Generic.List<IntPtr>();
            bool sideHasNeighbor = grabRight
                ? FindSnapNeighbors(hwnd, vis, true, jrcNbrs, out _)
                : FindSnapNeighbors(hwnd, vis, false, jrcNbrs, out _);
            if (!sideHasNeighbor) { if (EnableTroubleshootLog) TsLog($"TsJrc fail=free-edge grabRight={grabRight} dl={dl} dr={dr} cur=({cur.X},{cur.Y}) vis=({vis.Left},{vis.Right})"); return false; }
            SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)IDC_SIZEWE));
            return true;
        }

        private static bool TryGetClientRectScreen(IntPtr hwnd, out RECT rect)
        {
            rect = default;
            if (!GetClientRect(hwnd, out RECT client)) return false;
            var tl = new POINT { X = client.Left, Y = client.Top };
            var br = new POINT { X = client.Right, Y = client.Bottom };
            if (!ClientToScreen(hwnd, ref tl)) return false;
            if (!ClientToScreen(hwnd, ref br)) return false;
            rect = new RECT { Left = tl.X, Top = tl.Y, Right = br.X, Bottom = br.Y };
            return true;
        }

        // ===================== Задача 1.2: внешний «хват» свободного края snapped-окна =====================
        // Реализация — «голое» Win32 layered-окно (как mask-окно), НЕ WPF: полный контроль позицией/видимостью
        // через SetWindowPos/ShowWindow без конфликтов с WPF-лейаутом. Тонкая (FreeEdgeGripPx) прозрачная
        // полоса ставится СНАРУЖИ видимого края в свободном экране. Класс за��аёт курсор SizeWE (стр��лка при
        // наведении), по нажатию полоса запускает Ш��АТНЫЙ ресайз Г��АВНОГО окна (WM_NCLBUTTONDOWN+HTLEFT/HTRIGHT)
        // — переиспользуя рабочий путь (в т.ч. анти-скачок 1.1). Главное окно НЕ трогаем (геометрия/hit-test/
        // gap-fix/стиль без изменений) → HALF/3-колонки не затрагиваются. Полоса активна ТОЛЬКО для свободного
        // ��рая (внутренний snap-край БЕЗ соседа + место в work-area), ширина обрезается по rcWork → не уходит
        // на соседний монитор. Сосед ищется ТОЛЬКО в покое; во время _inSizeMove полоса спрятана → не по��торяет
        // провал попытки #1 (пересчёт соседа во время drag).
        private const bool EnableFreeEdgeGrip = true;
        private const bool GripDebugLog = false; // диагностика в %TEMP%\edgegrip.log (вклю��ить при отладке)
        private const int FreeEdgeGripPx = 12;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_SETCURSOR = 0x0020;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int WM_CAPTURECHANGED = 0x0215;
        private const int SC_MOVE = 0xF010;
        private const int SC_SIZE = 0xF000;
        private const int IDC_SIZEWE = 32644;
        private const int IDC_SIZENS = 32645;
        private const int SW_HIDE = 0;
        private const string GripClassName = "ControlPanelFreeEdgeGrip";
        private static WndProcDelegate? _gripWndProc; // держи�� ссылку — иначе GC соберёт делегат
        private static bool _gripClassRegistered;
        private static readonly System.Collections.Generic.Dictionary<IntPtr, BorderlessWindow> _gripOwners = new();
        // BUG2: оверлеи ВНУТРЕННИХ разделителей (joint-resize обоих окон, не зависит от OS snap-группы)
        private const int SnapDividerGripBandPx = 7; // полуширина полосы со стороны СОСЕДА (внешняя, узкая)
        private const int SnapDividerGripInnerMarginPx = 0; // запас за пределы resize-р��мки окна: внутренняя сторона полосы = GetResizeGrip + этот запас (DPI-зависим��), что��ы разделитель ловился, но кр��я окна резалось минимум
        private const int SnapNeighborMaxGapPx = 6;     // классический snap-сосед прилегает вплотную (без зазо��а)
        private const int SnapNeighborEdgeAlignPx = 8;  // и имеет ту ж�� длину общей стороны: верх и низ совпадают
        private static readonly System.Collections.Generic.Dictionary<IntPtr, (BorderlessWindow self, int side)> _divGripOwners = new();
        private IntPtr _divGripHwndL, _divGripHwndR;
        // BUG2 (horizontal): top/bottom divider grips + neighbor lists (Y-axis mirror of L/R).
        private IntPtr _divGripHwndT, _divGripHwndB;
        private readonly System.Collections.Generic.List<IntPtr> _divNbrsL = new();
        private readonly System.Collections.Generic.List<IntPtr> _divNbrsR = new();
        private readonly System.Collections.Generic.List<IntPtr> _divNbrsT = new();
        private readonly System.Collections.Generic.List<IntPtr> _divNbrsB = new();
        private bool _divDragging;
        private int _divDragSide;     // 1=сосед слева, 2=сосед справа
        private System.Collections.Generic.List<IntPtr> _divDragNbrs = new();
        // BUG2: same-column co-tiles (a window stacked above/below us) sharing the dragged divider. Their near edge
        // must follow the divider too, else the column goes ragged (gap/overlap beside the other tile). Populated
        // only when our window is a sub-tile (a full-height window has nothing stacked above/below it).
        private readonly System.Collections.Generic.List<IntPtr> _divDragCoTiles = new();
        private long _lastDivMoveLogTick;
        // Frame-synced divider resize (experiment): coalesce the target divider coordinate from WM_MOUSEMOVE and
        // apply it inside CompositionTarget.Rendering (right before WPF composes its next frame) so the HWND resize
        // and WPF's matching frame land together, shrinking the async gap that shows the stale surface (left-edge ghost).
        private int _divFsPendingCoord;
        private bool _divFsPendingValid;
        private bool _divFsRenderHooked;
        // BUG2: лечение дрейфа после ОТПУСКАНИЯ grip/рамки. Shell д��-снапывает НАШ край (напр. 2849->2957),
        // сосед остаётся на месте -> перекрытие -> FindSnapNeighbors теряет соседа. В коротком окне после
        // релиза подтягиваем захваченного соседа вплотную к осевшему краю (хэндл соседа известен).
        private long _divReleaseRealignUntil;
        private int _divReleaseSide;
        private readonly System.Collections.Generic.List<IntPtr> _divReleaseNbrs = new System.Collections.Generic.List<IntPtr>();
        private long _lastNbrDiagTick; // троттлинг диага FsnMiss
        private long _lastFrmFollowLogTick; // BUG2: троттлинг лога FrmFollow
        private IntPtr _gripHwnd;
        private int _edgeGripHt;
        private bool _edgeGripResizing;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageW(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SetCapture(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        private static void GripLog(string m)
        {
            if (!GripDebugLog) return;
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "edgegrip.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + m + "\r\n"); }
            catch { }
        }

        private static void EnsureGripClass()
        {
            if (_gripClassRegistered) return;
            _gripWndProc = GripWndProcStatic;
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_gripWndProc),
                hInstance = GetModuleHandleW(null),
                hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_SIZEWE),
                lpszClassName = GripClassName,
            };
            RegisterClassW(ref wc);
            _gripClassRegistered = true;
        }

        private static IntPtr GripWndProcStatic(IntPtr h, int msg, IntPtr w, IntPtr l)
        {
            if (_gripOwners.TryGetValue(h, out var self))
                return self.GripWndProc(h, msg, w, l);
            if (_divGripOwners.TryGetValue(h, out var dctx))
                return dctx.self.DivGripWndProc(h, msg, w, l, dctx.side);
            return DefWindowProcW(h, msg, w, l);
        }

        private IntPtr GripWndProc(IntPtr h, int msg, IntPtr w, IntPtr l)
        {
            switch (msg)
            {
                case WM_SETCURSOR:
                    SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)IDC_SIZEWE));
                    return (IntPtr)1;
                case WM_LBUTTONDOWN:
                {
                    var main = new WindowInteropHelper(this).Handle;
                    GripLog("WM_LBUTTONDOWN main=" + main + " ht=" + _edgeGripHt);
                    if (main != IntPtr.Zero && _edgeGripHt != 0)
                    {
                        _edgeGripResizing = true;
                        ShowWindow(_gripHwnd, SW_HIDE);
                        ReleaseCapture();
                        SendMessageW(main, WM_NCLBUTTONDOWN, (IntPtr)_edgeGripHt, IntPtr.Zero);
                        _edgeGripResizing = false;
                        ScheduleEdgeGripRefresh(); // отложенно: окно оседает после модального ресайза, одиночное чтение может быть ��ожным
                    }
                    return IntPtr.Zero;
                }
            }
            return DefWindowProcW(h, msg, w, l);
        }

        private System.Windows.Threading.DispatcherTimer? _gripRefreshTimer;
        private int _gripRefreshTicks;

        /// <summary>
        /// Отложе��ный перерасчёт grip после grip-и��ициированного ресайза. EXITSIZEMOVE для модального цикла
        /// приходит ВНУТРИ SendMessageW (когда _edgeGripResizing ещё true и refresh заблокирован), а окно
        /// оседает не мгновенно — одиночное немедленное чтение даёт ложный rightFree=False и н��всегда прячет
        /// полосу. Поэтому опрашиваем несколько раз (150/300/450мс) до стабилизации.
        /// </summary>
        private void ScheduleEdgeGripRefresh()
        {
            _gripRefreshTicks = 0;
            if (_gripRefreshTimer == null)
            {
                _gripRefreshTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(150),
                };
                _gripRefreshTimer.Tick += (s, e) =>
                {
                    _gripRefreshTicks++;
                    RefreshEdgeGrip();
                    if (_gripRefreshTicks >= 3) _gripRefreshTimer!.Stop();
                };
            }
            _gripRefreshTimer.Stop();
            _gripRefreshTimer.Start();
            RefreshEdgeGrip(); // первая попытка сразу
        }

        private void EnsureGrip()
        {
            if (_gripHwnd != IntPtr.Zero) return;
            EnsureGripClass();
            _gripHwnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                GripClassName, null, WS_POPUP,
                -32000, -32000, 1, 1,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            GripLog("EnsureGrip created hwnd=" + _gripHwnd);
            if (_gripHwnd != IntPtr.Zero)
            {
                _gripOwners[_gripHwnd] = this;
                SetLayeredWindowAttributes(_gripHwnd, 0, 1, LWA_ALPHA); // alpha=1: невидимо, но ловит мышь
            }
        }

        private void HideEdgeGrip()
        {
            if (_gripHwnd != IntPtr.Zero) ShowWindow(_gripHwnd, SW_HIDE);
        }

        private void DestroyEdgeGrip()
        {
            DestroyDivGrips();
            if (_gripHwnd == IntPtr.Zero) return;
            _gripOwners.Remove(_gripHwnd);
            DestroyWindow(_gripHwnd);
            _gripHwnd = IntPtr.Zero;
        }

        // ===================== BUG2: оверлей-ползунок на ВНУТРЕННЕМ разделителе =====================
        // Два прозрачных топовых layered-окна (как FreeEdgeGrip) поверх вертикальных разделителей с соседом
        // вплотную. Лежат ВЫШЕ нативной рамки соседа, поэтому надёжно ловят и наведение (класс-курсор SizeWE
        // по обе стороны границы, как в half), и нажатие. По нажатию ведём СВОЙ joint-resize: синхронно двигаем наш
        // край и край соседа (SetWindowPos о��оих окон по X курсора), держим вплотную. На время drag SnapFollow подавлен.
        private void EnsureDivGrips()
        {
            EnsureGripClass();
            if (_divGripHwndL == IntPtr.Zero)
            {
                _divGripHwndL = CreateWindowExW(
                    WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                    GripClassName, null, WS_POPUP, -32000, -32000, 1, 1,
                    IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
                if (_divGripHwndL != IntPtr.Zero) { _divGripOwners[_divGripHwndL] = (this, 1); SetLayeredWindowAttributes(_divGripHwndL, 0, 1, LWA_ALPHA); }
            }
            if (_divGripHwndR == IntPtr.Zero)
            {
                _divGripHwndR = CreateWindowExW(
                    WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                    GripClassName, null, WS_POPUP, -32000, -32000, 1, 1,
                    IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
                if (_divGripHwndR != IntPtr.Zero) { _divGripOwners[_divGripHwndR] = (this, 2); SetLayeredWindowAttributes(_divGripHwndR, 0, 1, LWA_ALPHA); }
            }
            if (_divGripHwndT == IntPtr.Zero)
            {
                _divGripHwndT = CreateWindowExW(
                    WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                    GripClassName, null, WS_POPUP, -32000, -32000, 1, 1,
                    IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
                if (_divGripHwndT != IntPtr.Zero) { _divGripOwners[_divGripHwndT] = (this, 3); SetLayeredWindowAttributes(_divGripHwndT, 0, 1, LWA_ALPHA); }
            }
            if (_divGripHwndB == IntPtr.Zero)
            {
                _divGripHwndB = CreateWindowExW(
                    WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                    GripClassName, null, WS_POPUP, -32000, -32000, 1, 1,
                    IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
                if (_divGripHwndB != IntPtr.Zero) { _divGripOwners[_divGripHwndB] = (this, 4); SetLayeredWindowAttributes(_divGripHwndB, 0, 1, LWA_ALPHA); }
            }
        }

        private void HideDivGrips()
        {
            if (_divGripHwndL != IntPtr.Zero) ShowWindow(_divGripHwndL, SW_HIDE);
            if (_divGripHwndR != IntPtr.Zero) ShowWindow(_divGripHwndR, SW_HIDE);
            if (_divGripHwndT != IntPtr.Zero) ShowWindow(_divGripHwndT, SW_HIDE);
            if (_divGripHwndB != IntPtr.Zero) ShowWindow(_divGripHwndB, SW_HIDE);
        }

        private void DestroyDivGrips()
        {
            if (_divGripHwndL != IntPtr.Zero) { _divGripOwners.Remove(_divGripHwndL); DestroyWindow(_divGripHwndL); _divGripHwndL = IntPtr.Zero; }
            if (_divGripHwndR != IntPtr.Zero) { _divGripOwners.Remove(_divGripHwndR); DestroyWindow(_divGripHwndR); _divGripHwndR = IntPtr.Zero; }
            if (_divGripHwndT != IntPtr.Zero) { _divGripOwners.Remove(_divGripHwndT); DestroyWindow(_divGripHwndT); _divGripHwndT = IntPtr.Zero; }
            if (_divGripHwndB != IntPtr.Zero) { _divGripOwners.Remove(_divGripHwndB); DestroyWindow(_divGripHwndB); _divGripHwndB = IntPtr.Zero; }
            if (_divGuideHwnd != IntPtr.Zero) { DestroyWindow(_divGuideHwnd); _divGuideHwnd = IntPtr.Zero; }
        }

        // ===== Divider guide line (deferred-resize UX) =====
        // A thin visible accent bar follows the cursor during the divider drag while the real window
        // resize commits ONCE on WM_LBUTTONUP. Restores the smooth moving-divider feel without any
        // per-frame live resize (which is what reintroduced the left-edge ghost).
        private IntPtr _divGuideHwnd;
        private static bool _divGuideClassRegistered;
        private static WndProcDelegate? _divGuideWndProc; // keep ref so the delegate is not GC'd
        private const string DivGuideClassName = "ControlPanelDivGuide";
        private const int DivGuideColorRef = 0x00E0A64B; // COLORREF 0x00BBGGRR -> accent blue #4BA6E0
        private const byte DivGuideAlpha = 150;
        private const int DivGuideThicknessPx = 4;

        private static void EnsureDivGuideClass()
        {
            if (_divGuideClassRegistered) return;
            _divGuideWndProc = (h, m, w, l) => DefWindowProcW(h, m, w, l);
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_divGuideWndProc),
                hInstance = GetModuleHandleW(null),
                hbrBackground = CreateSolidBrush(DivGuideColorRef),
                lpszClassName = DivGuideClassName,
            };
            RegisterClassW(ref wc);
            _divGuideClassRegistered = true;
        }

        private void EnsureDivGuide()
        {
            if (_divGuideHwnd != IntPtr.Zero) return;
            EnsureDivGuideClass();
            _divGuideHwnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                DivGuideClassName, null, WS_POPUP, -32000, -32000, 1, 1,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (_divGuideHwnd != IntPtr.Zero)
                SetLayeredWindowAttributes(_divGuideHwnd, 0, DivGuideAlpha, LWA_ALPHA);
        }

        // Position the guide bar at the cursor coord, spanning our window on the cross axis.
        private void ShowDivGuideAt(int coord)
        {
            if (!EnableDividerGuideLine) return;
            var main = new WindowInteropHelper(this).Handle;
            if (main == IntPtr.Zero || !GetWindowRect(main, out RECT r)) return;
            EnsureDivGuide();
            if (_divGuideHwnd == IntPtr.Zero) return;
            int t = DivGuideThicknessPx;
            int x, y, ww, hh;
            if (_divDragSide == 3 || _divDragSide == 4) { x = r.Left; ww = r.Right - r.Left; y = coord - t / 2; hh = t; }
            else { x = coord - t / 2; ww = t; y = r.Top; hh = r.Bottom - r.Top; }
            SetWindowPos(_divGuideHwnd, HWND_TOPMOST, x, y, ww, hh, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        private void HideDivGuide()
        {
            if (_divGuideHwnd != IntPtr.Zero) ShowWindow(_divGuideHwnd, SW_HIDE);
        }

        // Перепозиционирование оверлеев разделителей. Только в покое; показываем для краёв, где сосед вплотную.
        private void RefreshDividerGrips()
        {
            if (!EnableFreeEdgeGrip || !UseThemedSystemFrame) return;
            if (_divDragging) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || _inSizeMove || WindowState != WindowState.Normal || !IsVisible) { HideDivGrips(); return; }
            if (!TryGetSnapInternalEdges(hwnd, out bool sl, out bool sr, out bool st, out bool sb) || (!sl && !sr && !st && !sb)) { HideDivGrips(); return; }
            if (!TryGetVisibleBounds(hwnd, out RECT vis)) { HideDivGrips(); return; }

            int top = vis.Top, h = Math.Max(1, vis.Bottom - vis.Top);
            // Внутрення�� сторона полосы должна выходить за resize-рамку окна (иначе рамка перехватывает клик первой),
            // но не больше: не съедаем лишний край окна. Внешняя сторона (к соседу) остаётся узкой (SnapDividerGripBandPx).
            int innerBand = Math.Max(SnapDividerGripBandPx, GetResizeGrip(hwnd) + SnapDividerGripInnerMarginPx);
            // BUG2: показываем оверлей для ЛЮБОГО snap-соседа в зоне разделителя. TryFindSnapNeighbor сам
            // ограничивает зазор <=400px и проверяет вертикальное перекрытие/монитор. НЕ требуем нулевого
            // зазора: иначе после OS-перетаскивания, создавшего зазор, оверлей исчезал бы навсегда и не мог
            // стянуть окна обратно вплотную.
            int rNear = vis.Right, lNear = vis.Left;
            bool rOk = sr && FindSnapNeighbors(hwnd, vis, true, _divNbrsR, out rNear);
            bool lOk = sl && FindSnapNeighbors(hwnd, vis, false, _divNbrsL, out lNear);
            if (rOk || lOk) EnsureDivGrips();

            if (rOk && _divGripHwndR != IntPtr.Zero)
            {
                // Полоса перекрывает ВСЮ зону разделителя: вглубь нашего окна (Inner) .. ближний край соседе�� (+ запас).
                // Сосед справа => наше окно слева от разделителя => внутренняя (широкая) сторона — ЛЕВАЯ.
                int ra = Math.Min(vis.Right, rNear) - innerBand;
                int rb = Math.Max(vis.Right, rNear) + SnapDividerGripBandPx;
                SetWindowPos(_divGripHwndR, HWND_TOPMOST, ra, top, Math.Max(innerBand + SnapDividerGripBandPx, rb - ra), h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            else { _divNbrsR.Clear(); if (_divGripHwndR != IntPtr.Zero) ShowWindow(_divGripHwndR, SW_HIDE); }

            if (lOk && _divGripHwndL != IntPtr.Zero)
            {
                // Сосед слева => наше окно справа от разделителя => внутренняя (широкая) сторона — ПРАВАЯ.
                int la = Math.Min(vis.Left, lNear) - SnapDividerGripBandPx;
                int lb = Math.Max(vis.Left, lNear) + innerBand;
                SetWindowPos(_divGripHwndL, HWND_TOPMOST, la, top, Math.Max(innerBand + SnapDividerGripBandPx, lb - la), h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            else { _divNbrsL.Clear(); if (_divGripHwndL != IntPtr.Zero) ShowWindow(_divGripHwndL, SW_HIDE); }

            // BUG2 (horizontal): same logic on the Y axis. Neighbor below => our BOTTOM edge is the divider;
            // neighbor above => our TOP edge is the divider (which coincides with our caption drag zone).
            int leftX = vis.Left, w = Math.Max(1, vis.Right - vis.Left);
            int bNear = vis.Bottom, tNear = vis.Top;
            bool bNbr = sb && FindSnapNeighborsV(hwnd, vis, true, _divNbrsB, out bNear);
            bool tNbr = st && FindSnapNeighborsV(hwnd, vis, false, _divNbrsT, out tNear);
            if (!bNbr) _divNbrsB.Clear();
            if (!tNbr) _divNbrsT.Clear();
            // BUG2 (decisive): drive vertical grip visibility off the reliable geometric divider flags st/sb,
            // NOT off finding a flush neighbor. Real snap partners can be non-flush (gap > SnapNeighborMaxGapPx)
            // or absent (L-shaped groups), yet the seam must stay grabbable. When a neighbor is captured we
            // co-resize it; otherwise we resize only our own edge and let the shell move the other side.
            bool bOk = sb, tOk = st;
            if (bOk || tOk) EnsureDivGrips();

            if (bOk && _divGripHwndB != IntPtr.Zero)
            {
                // Neighbor below => our window ABOVE the divider => inner (wide) band goes UP into our own bottom
                // area (no caption there); a thin band reaches DOWN toward the neighbor.
                int ba = Math.Min(vis.Bottom, bNear) - innerBand;
                int bb = Math.Max(vis.Bottom, bNear) + SnapDividerGripBandPx;
                SetWindowPos(_divGripHwndB, HWND_TOPMOST, leftX, ba, w, Math.Max(innerBand + SnapDividerGripBandPx, bb - ba), SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            else { _divNbrsB.Clear(); if (_divGripHwndB != IntPtr.Zero) ShowWindow(_divGripHwndB, SW_HIDE); }

            if (tOk && _divGripHwndT != IntPtr.Zero)
            {
                // Neighbor above => our TOP edge = our caption drag zone. Caption-conflict handling: put the WIDE
                // band UP into the neighbor's bottom (no caption there) and leave only a thin SnapDividerGripBandPx
                // sliver DOWN over our caption, so dragging the window by its title bar still works (mirrors the OS
                // thin top resize strip that coexists with the caption).
                int ta = Math.Min(vis.Top, tNear) - innerBand;
                int tb = Math.Max(vis.Top, tNear) + SnapDividerGripBandPx;
                SetWindowPos(_divGripHwndT, HWND_TOPMOST, leftX, ta, w, Math.Max(innerBand + SnapDividerGripBandPx, tb - ta), SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            else { _divNbrsT.Clear(); if (_divGripHwndT != IntPtr.Zero) ShowWindow(_divGripHwndT, SW_HIDE); }

            if (EnableTroubleshootLog && (lOk || rOk || tOk || bOk))
                TsLog($"DivGrip lOk={lOk} rOk={rOk} tOk={tOk} bOk={bOk} tNbr={tNbr} bNbr={bNbr} vis=({vis.Left},{vis.Top},{vis.Right},{vis.Bottom}) lN={_divNbrsL.Count} rN={_divNbrsR.Count} tN={_divNbrsT.Count} bN={_divNbrsB.Count} tNear={tNear} bNear={bNear}");
        }

        // joint-resize обоих о��он по X разделителя (экранные координаты). side: 1=сосед слева, 2=справа.
        private void UpdateDividerJointResize(int dividerX)
        {
            var main = new WindowInteropHelper(this).Handle;
            if (main == IntPtr.Zero || _divDragNbrs.Count == 0) return;
            if (!GetWindowRect(main, out RECT oR) || !TryGetVisibleBounds(main, out RECT oV)) return;
            int oOvL = oR.Left - oV.Left, oOvR = oR.Right - oV.Right;

            // СНАЧАЛА двигаем соседей к dividerX, затем ЧИТАЕМ их ФАКТИЧЕСКИЙ край (система могла
            // ограничить сжатие мин. шириной соседа) и ��одгоняем НАШ край к этому факту. Иначе наше окно
            // уезжает по курсору дальше, чем сосед может сжаться → наложение/разрыв (БАГ 2: 3 колонки на мин. ширине).
            int effDiv = dividerX;
            if (_divDragSide == 2)
            {
                int hiN = int.MaxValue;
                foreach (var nb in _divDragNbrs)
                    if (TryGetVisibleBounds(nb, out RECT nv)) hiN = Math.Min(hiN, nv.Right);
                if (hiN == int.MaxValue) return;
                int lo = oV.Left + SnapFollowMinDimPx, hi = hiN - SnapFollowMinDimPx;
                if (lo > hi) return;
                if (dividerX < lo) dividerX = lo; else if (dividerX > hi) dividerX = hi;
                effDiv = dividerX;
                // Issue #3: commit neighbor(s) + our window + co-tiles in ONE DeferWindowPos batch (atomic co-present) to remove divider-drag tearing.
                if (EnableDividerSingleBatch) { ApplyDividerBatch(main, oR, oOvR, effDiv, true, true); }
                else {
                ApplyDividerBatch(main, oR, oOvR, effDiv, true, false);
                int actualDiv = effDiv;
                foreach (var nb in _divDragNbrs)
                    if (TryGetVisibleBounds(nb, out RECT nV2)) actualDiv = Math.Min(actualDiv, nV2.Left);
                if (actualDiv < effDiv - 1) effDiv = actualDiv;
                ApplyDividerBatch(main, oR, oOvR, effDiv, true, true);
                }
            }
            else
            {
                int loN = int.MinValue;
                foreach (var nb in _divDragNbrs)
                    if (TryGetVisibleBounds(nb, out RECT nv)) loN = Math.Max(loN, nv.Left);
                if (loN == int.MinValue) return;
                int hi = oV.Right - SnapFollowMinDimPx, lo = loN + SnapFollowMinDimPx;
                if (lo > hi) return;
                if (dividerX < lo) dividerX = lo; else if (dividerX > hi) dividerX = hi;
                effDiv = dividerX;
                // Issue #3: commit neighbor(s) + our window + co-tiles in ONE DeferWindowPos batch (atomic co-present) to remove divider-drag tearing.
                if (EnableDividerSingleBatch) { ApplyDividerBatch(main, oR, oOvL, effDiv, false, true); }
                else {
                ApplyDividerBatch(main, oR, oOvL, effDiv, false, false);
                int actualDiv = effDiv;
                foreach (var nb in _divDragNbrs)
                    if (TryGetVisibleBounds(nb, out RECT nV2)) actualDiv = Math.Max(actualDiv, nV2.Right);
                if (actualDiv > effDiv + 1) effDiv = actualDiv;
                ApplyDividerBatch(main, oR, oOvL, effDiv, false, true);
                }
            }

            if (EnableTroubleshootLog)
            {
                long now = Environment.TickCount64;
                if (now - _lastDivMoveLogTick > 120)
                {
                    _lastDivMoveLogTick = now;
                    TsLog($"DivMove side={_divDragSide} dividerX={dividerX} effDiv={effDiv} our=({oV.Left},{oV.Right}) nbrs={_divDragNbrs.Count}");
                }
            }
        }

        // BUG2: capture windows stacked in the SAME column as us (strictly above/below our vertical span) whose
        // near edge lies on the divider we drag, so they follow it and the column stays flush. A full-height window
        // has nothing above/below it, so this is a no-op there (zero impact on previously working scenarios).
        // Issue #3 fix: reposition our window, the resized snap neighbor(s) and captured same-column co-tiles
        // in a SINGLE DeferWindowPos transaction so the window manager applies every move together and DWM
        // composites them in one frame. Separate SetWindowPos calls let the cross-process neighbor and our own
        // window present on different frames during a fast drag, briefly exposing the desktop between them
        // ("neighbor runs ahead / lags behind"). effDiv is the shared border (visible coords); rightEdge=true
        // when OUR right edge is the divider (neighbor on the right, side 2).
        private void ApplyDividerBatch(IntPtr main, RECT oR, int oOv, int effDiv, bool rightEdge, bool moveOur)
        {
            IntPtr hdwp = BeginDeferWindowPos(_divDragNbrs.Count + _divDragCoTiles.Count + 1);
            if (hdwp == IntPtr.Zero) return;
            // SWP_NOCOPYBITS experiment: applied ONLY to our window's move (origin/left edge moves here) to
            // discard stale bits instead of a USER32 copy-blit; neighbors/co-tiles keep the plain flags.
            uint oswp = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
            if (EnableDividerNoCopyBits) oswp |= SWP_NOCOPYBITS;
            foreach (var nb in _divDragNbrs)
            {
                if (!GetWindowRect(nb, out RECT nR) || !TryGetVisibleBounds(nb, out RECT nV)) continue;
                if (rightEdge)
                {
                    int newNL = effDiv + (nR.Left - nV.Left);
                    hdwp = DeferWindowPos(hdwp, nb, IntPtr.Zero, newNL, nR.Top, nR.Right - newNL, nR.Bottom - nR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
                else
                {
                    int newNR = effDiv + (nR.Right - nV.Right);
                    hdwp = DeferWindowPos(hdwp, nb, IntPtr.Zero, nR.Left, nR.Top, newNR - nR.Left, nR.Bottom - nR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
                if (hdwp == IntPtr.Zero) return;
            }
            // Ghost fix: predictive pass (moveOur=false) commits ONLY neighbors so we can read their
            // OS-clamped edge; our window (+co-tiles) is moved exactly once in the final pass. Prevents
            // the per-frame double-move (raw dividerX then corrected effDiv) that DWM composited as a
            // semi-transparent ghost trailing the cursor when a neighbor hit its min width.
            if (!moveOur) { EndDeferWindowPos(hdwp); return; }
            if (rightEdge)
            {
                int newOR = effDiv + oOv;
                hdwp = DeferWindowPos(hdwp, main, IntPtr.Zero, oR.Left, oR.Top, newOR - oR.Left, oR.Bottom - oR.Top, oswp);
            }
            else
            {
                int newOL = effDiv + oOv;
                hdwp = DeferWindowPos(hdwp, main, IntPtr.Zero, newOL, oR.Top, oR.Right - newOL, oR.Bottom - oR.Top, oswp);
            }
            if (hdwp == IntPtr.Zero) return;
            foreach (var co in _divDragCoTiles)
            {
                if (!GetWindowRect(co, out RECT cR) || !TryGetVisibleBounds(co, out RECT cV)) continue;
                if (rightEdge)
                {
                    int newCR = effDiv + (cR.Right - cV.Right);
                    if (newCR - cR.Left < SnapFollowMinDimPx) continue;
                    hdwp = DeferWindowPos(hdwp, co, IntPtr.Zero, cR.Left, cR.Top, newCR - cR.Left, cR.Bottom - cR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
                else
                {
                    int newCL = effDiv + (cR.Left - cV.Left);
                    if (cR.Right - newCL < SnapFollowMinDimPx) continue;
                    hdwp = DeferWindowPos(hdwp, co, IntPtr.Zero, newCL, cR.Top, cR.Right - newCL, cR.Bottom - cR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
                if (hdwp == IntPtr.Zero) return;
            }
            EndDeferWindowPos(hdwp);
        }

        private void FindDividerCoTiles(int side)
        {
            _divDragCoTiles.Clear();
            var self = new WindowInteropHelper(this).Handle;
            if (self == IntPtr.Zero) return;
            if (!TryGetVisibleBounds(self, out RECT ourVis)) return;
            if (!TryGetWorkArea(self, out RECT wa)) return;
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            bool leftEdge = side == 1;                       // side 1 => divider is OUR left edge; co-tiles share it on our side
            int edgeX = leftEdge ? ourVis.Left : ourVis.Right;
            int ourTop = ourVis.Top, ourBot = ourVis.Bottom;
            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT v)) return true;
                if (v.Right - v.Left < 50 || v.Bottom - v.Top < 50) return true;
                int nearX = leftEdge ? v.Left : v.Right;     // co-tile is on OUR side: its near edge aligns with ours
                if (Math.Abs(nearX - edgeX) > SnapNeighborEdgeAlignPx) return true;
                bool reachesFar = leftEdge ? (v.Right >= wa.Right - SnapNeighborEdgeAlignPx)
                                           : (v.Left <= wa.Left + SnapNeighborEdgeAlignPx);
                if (!reachesFar) return true;                // must span to the far screen edge (a real column tile)
                bool below = v.Top >= ourBot - SnapNeighborEdgeAlignPx;
                bool above = v.Bottom <= ourTop + SnapNeighborEdgeAlignPx;
                if (!below && !above) return true;           // overlaps our span vertically => not a stacked co-tile
                _divDragCoTiles.Add(h);
                return true;
            }, IntPtr.Zero);
            if (EnableTroubleshootLog && _divDragCoTiles.Count > 0)
                TsLog($"DivCoTiles side={side} count={_divDragCoTiles.Count} edgeX={edgeX}");
        }

        // BUG2: move captured same-column co-tiles so their near edge tracks the divider (effDiv, visible coords).
        private void MoveDividerCoTiles(int effDiv)
        {
            if (_divDragCoTiles.Count == 0) return;
            bool leftEdge = _divDragSide == 1;
            foreach (var co in _divDragCoTiles)
            {
                if (!GetWindowRect(co, out RECT cR) || !TryGetVisibleBounds(co, out RECT cV)) continue;
                if (leftEdge)
                {
                    int ovL = cR.Left - cV.Left;
                    int newCL = effDiv + ovL;
                    if (cR.Right - newCL < SnapFollowMinDimPx) continue;
                    SetWindowPos(co, IntPtr.Zero, newCL, cR.Top, cR.Right - newCL, cR.Bottom - cR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
                else
                {
                    int ovR = cR.Right - cV.Right;
                    int newCR = effDiv + ovR;
                    if (newCR - cR.Left < SnapFollowMinDimPx) continue;
                    SetWindowPos(co, IntPtr.Zero, cR.Left, cR.Top, newCR - cR.Left, cR.Bottom - cR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
            }
        }

        // ===================== BUG2 (horizontal axis mirror): Y-axis joint-resize =====================
        // Full mirror of the vertical divider system onto the Y axis so a horizontal internal divider (our window
        // stacked above/below another) can be dragged too. side codes: 3 = neighbor ABOVE (our top edge is the
        // divider), 4 = neighbor BELOW (our bottom edge is the divider). X<->Y, Left<->Top, Right<->Bottom.
        private bool FindSnapNeighborsV(IntPtr self, RECT ourVis, bool bottomEdge, System.Collections.Generic.List<IntPtr> outHwnds, out int nearEdgeY)
        {
            outHwnds.Clear();
            nearEdgeY = bottomEdge ? ourVis.Bottom : ourVis.Top;
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            int ourLeft = ourVis.Left, ourRight = ourVis.Right;
            var cands = new System.Collections.Generic.List<(IntPtr h, RECT v, int gap)>();
            bool haveWork = TryGetWorkArea(self, out RECT waSelf);

            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT v)) return true;
                if (v.Right - v.Left < 50 || v.Bottom - v.Top < 50) return true;

                // shared side must have the same WIDTH (a real snap pair), OR the neighbor CONTAINS our width
                // (a full-width window when WE are the sub-tile of the row).
                bool withinSpan = v.Left >= ourLeft - SnapNeighborEdgeAlignPx && v.Right <= ourRight + SnapNeighborEdgeAlignPx;
                bool containsSpan = v.Left <= ourLeft + SnapNeighborEdgeAlignPx && v.Right >= ourRight - SnapNeighborEdgeAlignPx;
                if (!withinSpan && !containsSpan) return true;

                int gap;
                if (bottomEdge)
                {
                    if (v.Top <= ourVis.Top) return true;       // partner below us
                    gap = v.Top - ourVis.Bottom;
                }
                else
                {
                    if (v.Bottom >= ourVis.Bottom) return true; // partner above us
                    gap = ourVis.Top - v.Bottom;
                }
                if (gap > SnapNeighborMaxGapPx) return true;
                if (haveWork)
                {
                    bool reachesEdge = bottomEdge
                        ? v.Bottom >= waSelf.Bottom - SnapNeighborEdgeAlignPx
                        : v.Top <= waSelf.Top + SnapNeighborEdgeAlignPx;
                    if (!reachesEdge) return true;
                }
                cands.Add((h, v, gap));
                return true;
            }, IntPtr.Zero);

            if (cands.Count == 0) return false;
            {
                int bestGapF = int.MaxValue; IntPtr bestHF = IntPtr.Zero; RECT bestVF = default;
                foreach (var c in cands)
                {
                    bool full = c.v.Left <= ourLeft + SnapNeighborEdgeAlignPx && c.v.Right >= ourRight - SnapNeighborEdgeAlignPx;
                    if (full && Math.Abs(c.gap) < Math.Abs(bestGapF)) { bestGapF = c.gap; bestHF = c.h; bestVF = c.v; }
                }
                if (bestHF != IntPtr.Zero)
                {
                    outHwnds.Add(bestHF);
                    nearEdgeY = bottomEdge ? bestVF.Top : bestVF.Bottom;
                    return true;
                }
            }
            cands.Sort((a, b) => a.v.Left.CompareTo(b.v.Left));
            const int tileTolPx = SnapNeighborEdgeAlignPx;
            int expectedLeft = ourLeft;
            bool tiled = true;
            foreach (var c in cands)
            {
                int lft = Math.Max(c.v.Left, ourLeft), rgt = Math.Min(c.v.Right, ourRight);
                if (rgt <= lft) { tiled = false; break; }
                if (Math.Abs(lft - expectedLeft) > tileTolPx) { tiled = false; break; }
                expectedLeft = rgt;
            }
            if (!tiled || Math.Abs(expectedLeft - ourRight) > tileTolPx) return false;
            int bestGap = int.MaxValue;
            foreach (var c in cands)
            {
                outHwnds.Add(c.h);
                if (c.gap < bestGap) { bestGap = c.gap; nearEdgeY = bottomEdge ? c.v.Top : c.v.Bottom; }
            }
            return true;
        }

        // Y-axis joint resize of both windows along the horizontal divider (screen coords). side: 3=above, 4=below.
        private void UpdateDividerJointResizeV(int dividerY)
        {
            var main = new WindowInteropHelper(this).Handle;
            if (main == IntPtr.Zero) return;
            if (!GetWindowRect(main, out RECT oR) || !TryGetVisibleBounds(main, out RECT oV)) return;
            int oOvT = oR.Top - oV.Top, oOvB = oR.Bottom - oV.Bottom;

            // BUG2 (decisive): no flush neighbor captured => resize ONLY our own edge to follow the divider
            // cursor (clamped to our min dimension). The shell resizes the window on the other side of the seam
            // independently; cursor Y == seam position keeps both in sync. Enables top/bottom seam joint-resize
            // in non-flush / L-shaped snap groups where FindSnapNeighborsV returns nothing.
            if (_divDragNbrs.Count == 0)
            {
                if (_divDragSide == 4)
                {
                    int newB = dividerY; if (newB < oV.Top + SnapFollowMinDimPx) newB = oV.Top + SnapFollowMinDimPx;
                    int newOB = newB + oOvB;
                    SetWindowPos(main, IntPtr.Zero, oR.Left, oR.Top, oR.Right - oR.Left, newOB - oR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                    MoveDividerCoTilesV(newB);
                }
                else
                {
                    int newT = dividerY; if (newT > oV.Bottom - SnapFollowMinDimPx) newT = oV.Bottom - SnapFollowMinDimPx;
                    int newOT = newT + oOvT;
                    SetWindowPos(main, IntPtr.Zero, oR.Left, newOT, oR.Right - oR.Left, oR.Bottom - newOT, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                    MoveDividerCoTilesV(newT);
                }
                if (EnableTroubleshootLog)
                {
                    long nowV = Environment.TickCount64;
                    if (nowV - _lastDivMoveLogTick > 120)
                    {
                        _lastDivMoveLogTick = nowV;
                        TsLog($"DivMoveV side={_divDragSide} dividerY={dividerY} our=({oV.Top},{oV.Bottom}) nbrs=0 selfOnly=1");
                    }
                }
                return;
            }

            int effDiv = dividerY;
            if (_divDragSide == 4) // neighbor below: move neighbor TOP to dividerY, then our BOTTOM to actual
            {
                int hiN = int.MaxValue;
                foreach (var nb in _divDragNbrs)
                    if (TryGetVisibleBounds(nb, out RECT nv)) hiN = Math.Min(hiN, nv.Bottom);
                if (hiN == int.MaxValue) return;
                int lo = oV.Top + SnapFollowMinDimPx, hi = hiN - SnapFollowMinDimPx;
                if (lo > hi) return;
                if (dividerY < lo) dividerY = lo; else if (dividerY > hi) dividerY = hi;
                effDiv = dividerY;
                foreach (var nb in _divDragNbrs)
                {
                    if (!GetWindowRect(nb, out RECT nR) || !TryGetVisibleBounds(nb, out RECT nV)) continue;
                    int ovT = nR.Top - nV.Top;
                    int newNT = dividerY + ovT;
                    SetWindowPos(nb, IntPtr.Zero, nR.Left, newNT, nR.Right - nR.Left, nR.Bottom - newNT, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                    if (GetWindowRect(nb, out RECT aft)) effDiv = Math.Min(effDiv, aft.Top - ovT);
                }
                int newOB = effDiv + oOvB;
                SetWindowPos(main, IntPtr.Zero, oR.Left, oR.Top, oR.Right - oR.Left, newOB - oR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                MoveDividerCoTilesV(effDiv);
            }
            else // side 3, neighbor above: move neighbor BOTTOM to dividerY, then our TOP to actual
            {
                int loN = int.MinValue;
                foreach (var nb in _divDragNbrs)
                    if (TryGetVisibleBounds(nb, out RECT nv)) loN = Math.Max(loN, nv.Top);
                if (loN == int.MinValue) return;
                int hi = oV.Bottom - SnapFollowMinDimPx, lo = loN + SnapFollowMinDimPx;
                if (lo > hi) return;
                if (dividerY < lo) dividerY = lo; else if (dividerY > hi) dividerY = hi;
                effDiv = dividerY;
                foreach (var nb in _divDragNbrs)
                {
                    if (!GetWindowRect(nb, out RECT nR) || !TryGetVisibleBounds(nb, out RECT nV)) continue;
                    int ovB = nR.Bottom - nV.Bottom;
                    int newNB = dividerY + ovB;
                    SetWindowPos(nb, IntPtr.Zero, nR.Left, nR.Top, nR.Right - nR.Left, newNB - nR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                    if (GetWindowRect(nb, out RECT aft)) effDiv = Math.Max(effDiv, aft.Bottom - ovB);
                }
                int newOT = effDiv + oOvT;
                SetWindowPos(main, IntPtr.Zero, oR.Left, newOT, oR.Right - oR.Left, oR.Bottom - newOT, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                MoveDividerCoTilesV(effDiv);
            }

            if (EnableTroubleshootLog)
            {
                long now = Environment.TickCount64;
                if (now - _lastDivMoveLogTick > 120)
                {
                    _lastDivMoveLogTick = now;
                    TsLog($"DivMoveV side={_divDragSide} dividerY={dividerY} effDiv={effDiv} our=({oV.Top},{oV.Bottom}) nbrs={_divDragNbrs.Count}");
                }
            }
        }

        // Mirror of FindDividerCoTiles on the Y axis: windows in the SAME ROW (strictly left/right of us) whose near
        // horizontal edge lies on the dragged divider, so the row stays flush. side: 3=our top edge, 4=our bottom edge.
        private void FindDividerCoTilesV(int side)
        {
            _divDragCoTiles.Clear();
            var self = new WindowInteropHelper(this).Handle;
            if (self == IntPtr.Zero) return;
            if (!TryGetVisibleBounds(self, out RECT ourVis)) return;
            if (!TryGetWorkArea(self, out RECT wa)) return;
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            bool topEdge = side == 3;
            int edgeY = topEdge ? ourVis.Top : ourVis.Bottom;
            int ourLeft = ourVis.Left, ourRight = ourVis.Right;
            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT v)) return true;
                if (v.Right - v.Left < 50 || v.Bottom - v.Top < 50) return true;
                int nearY = topEdge ? v.Top : v.Bottom;
                if (Math.Abs(nearY - edgeY) > SnapNeighborEdgeAlignPx) return true;
                bool reachesFar = topEdge ? (v.Bottom >= wa.Bottom - SnapNeighborEdgeAlignPx)
                                          : (v.Top <= wa.Top + SnapNeighborEdgeAlignPx);
                if (!reachesFar) return true;
                bool rightOf = v.Left >= ourRight - SnapNeighborEdgeAlignPx;
                bool leftOf = v.Right <= ourLeft + SnapNeighborEdgeAlignPx;
                if (!rightOf && !leftOf) return true;      // overlaps our width => not a side-by-side co-tile
                _divDragCoTiles.Add(h);
                return true;
            }, IntPtr.Zero);
            if (EnableTroubleshootLog && _divDragCoTiles.Count > 0)
                TsLog($"DivCoTilesV side={side} count={_divDragCoTiles.Count} edgeY={edgeY}");
        }

        // Mirror of MoveDividerCoTiles on the Y axis.
        private void MoveDividerCoTilesV(int effDiv)
        {
            if (_divDragCoTiles.Count == 0) return;
            bool topEdge = _divDragSide == 3;
            foreach (var co in _divDragCoTiles)
            {
                if (!GetWindowRect(co, out RECT cR) || !TryGetVisibleBounds(co, out RECT cV)) continue;
                if (topEdge)
                {
                    int ovT = cR.Top - cV.Top;
                    int newCT = effDiv + ovT;
                    if (cR.Bottom - newCT < SnapFollowMinDimPx) continue;
                    SetWindowPos(co, IntPtr.Zero, cR.Left, newCT, cR.Right - cR.Left, cR.Bottom - newCT, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
                else
                {
                    int ovB = cR.Bottom - cV.Bottom;
                    int newCB = effDiv + ovB;
                    if (newCB - cR.Top < SnapFollowMinDimPx) continue;
                    SetWindowPos(co, IntPtr.Zero, cR.Left, cR.Top, cR.Right - cR.Left, newCB - cR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
            }
        }

        // Mirror of MoveNeighborsFlush on the Y axis (post-release realign for horizontal dividers).
        private void MoveNeighborsFlushV(IntPtr hwnd, bool bottomSide, System.Collections.Generic.List<IntPtr> nbrs)
        {
            if (nbrs.Count == 0 || !TryGetVisibleBounds(hwnd, out RECT oV)) return;
            foreach (var nb in nbrs)
            {
                if (!GetWindowRect(nb, out RECT nR) || !TryGetVisibleBounds(nb, out RECT nV)) continue;
                if (bottomSide)
                {
                    int ovT = nR.Top - nV.Top;
                    int newNT = oV.Bottom + ovT;
                    if (nR.Bottom - newNT < SnapFollowMinDimPx) continue;
                    SetWindowPos(nb, IntPtr.Zero, nR.Left, newNT, nR.Right - nR.Left, nR.Bottom - newNT, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
                else
                {
                    int ovB = nR.Bottom - nV.Bottom;
                    int newNB = oV.Top + ovB;
                    if (newNB - nR.Top < SnapFollowMinDimPx) continue;
                    SetWindowPos(nb, IntPtr.Zero, nR.Left, nR.Top, nR.Right - nR.Left, newNB - nR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
            }
        }

        // BUG2: во время OS-модального ресайза РАМКОЙ SnapFollow выключен (_inSizeMove). Чтобы снапн��тый
        // сосед не «оторвался», двигаем его ближний край к нашему фактическому краю. Соседи захвачены на
        // старте цикла (_frameNbrsR/_frameNbrsL), пока окна были вплотную, — о��крывшийся зазор не мешает.
        private void FollowFrameResizeNeighbors(IntPtr hwnd)
        {
            bool dragRight = _sizingEdge == WMSZ_RIGHT || _sizingEdge == WMSZ_TOPRIGHT || _sizingEdge == WMSZ_BOTTOMRIGHT;
            bool dragLeft  = _sizingEdge == WMSZ_LEFT  || _sizingEdge == WMSZ_TOPLEFT  || _sizingEdge == WMSZ_BOTTOMLEFT;
            if (!dragRight && !dragLeft) return;
            if (!TryGetVisibleBounds(hwnd, out RECT oV)) return;

            if (dragRight && _frameNbrsR.Count > 0)
            {
                foreach (var nb in _frameNbrsR)
                {
                    if (!GetWindowRect(nb, out RECT nR) || !TryGetVisibleBounds(nb, out RECT nV)) continue;
                    int ovL = nR.Left - nV.Left;                 // оконные коорд vs DWM visible
                    int newNL = oV.Right + ovL;                  // видимый левый край соседа = наш видимый правый
                    if (nR.Right - newNL < SnapFollowMinDimPx) continue; // не сж��ма��м соседа уже минимума
                    SetWindowPos(nb, IntPtr.Zero, newNL, nR.Top, nR.Right - newNL, nR.Bottom - nR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
            }
            else if (dragLeft && _frameNbrsL.Count > 0)
            {
                foreach (var nb in _frameNbrsL)
                {
                    if (!GetWindowRect(nb, out RECT nR) || !TryGetVisibleBounds(nb, out RECT nV)) continue;
                    int ovR = nR.Right - nV.Right;
                    int newNR = oV.Left + ovR;                   // видимый правый край соседа = наш видимый левый
                    if (newNR - nR.Left < SnapFollowMinDimPx) continue;
                    SetWindowPos(nb, IntPtr.Zero, nR.Left, nR.Top, newNR - nR.Left, nR.Bottom - nR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
            }

            if (EnableTroubleshootLog)
            {
                long now = Environment.TickCount64;
                if (now - _lastFrmFollowLogTick > 120)
                {
                    _lastFrmFollowLogTick = now;
                    TsLog($"FrmFollow edge={_sizingEdge} our=({oV.Left},{oV.Right}) rN={_frameNbrsR.Count} lN={_frameNbrsL.Count}");
                }
            }
        }

        // BUG2: двигаем соседей так, чтобы их ближний край встал ВПЛОТНУЮ к нашему текущему краю.
        private void MoveNeighborsFlush(IntPtr hwnd, bool rightSide, System.Collections.Generic.List<IntPtr> nbrs)
        {
            if (nbrs.Count == 0 || !TryGetVisibleBounds(hwnd, out RECT oV)) return;
            foreach (var nb in nbrs)
            {
                if (!GetWindowRect(nb, out RECT nR) || !TryGetVisibleBounds(nb, out RECT nV)) continue;
                if (rightSide)
                {
                    int ovL = nR.Left - nV.Left;
                    int newNL = oV.Right + ovL;
                    if (nR.Right - newNL < SnapFollowMinDimPx) continue;
                    SetWindowPos(nb, IntPtr.Zero, newNL, nR.Top, nR.Right - newNL, nR.Bottom - nR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
                else
                {
                    int ovR = nR.Right - nV.Right;
                    int newNR = oV.Left + ovR;
                    if (newNR - nR.Left < SnapFollowMinDimPx) continue;
                    SetWindowPos(nb, IntPtr.Zero, nR.Left, nR.Top, newNR - nR.Left, nR.Bottom - nR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
            }
        }

        // BUG2: на отпускании grip-разделителя вооружаем короткое окно пост-релизного выравнивания.
        private void ArmDivReleaseRealign()
        {
            if (_divDragNbrs.Count == 0) return;
            _divReleaseNbrs.Clear();
            _divReleaseNbrs.AddRange(_divDragNbrs);
            _divReleaseSide = _divDragSide;
            _divReleaseRealignUntil = Environment.TickCount64 + 350;
            if (EnableTroubleshootLog) TsLog($"DivRelArm side={_divDragSide} nbrs={_divDragNbrs.Count}");
        }

        // BUG2: то же после ручного ресайза РАМ��О�� (shell может до-снапить наш край и после WeMoveSizeEnd).
        private void ArmFrameReleaseRealign()
        {
            bool right = _sizingEdge == WMSZ_RIGHT || _sizingEdge == WMSZ_TOPRIGHT || _sizingEdge == WMSZ_BOTTOMRIGHT;
            bool left  = _sizingEdge == WMSZ_LEFT  || _sizingEdge == WMSZ_TOPLEFT  || _sizingEdge == WMSZ_BOTTOMLEFT;
            var src = right ? _frameNbrsR : (left ? _frameNbrsL : null);
            if (src == null || src.Count == 0) return;
            _divReleaseNbrs.Clear();
            _divReleaseNbrs.AddRange(src);
            _divReleaseSide = right ? 2 : 1;
            _divReleaseRealignUntil = Environment.TickCount64 + 350;
            if (EnableTroubleshootLog) TsLog($"FrmRelArm side={_divReleaseSide} nbrs={src.Count}");
        }

        // BUG2 ДИА��НОСТИКА: ко��да FindSnapNeighbors никого не вернул, печатаем ближайшее окно на нужной стороне
        // с верт. перекрытием и причину отказа (gap/farDelta/span). Только ��ри провале и троттлингом — НЕ горячий путь.
        private void LogNearestRejectedNeighbor(IntPtr self, RECT ourVis, bool rightEdge)
        {
            if (!EnableTroubleshootLog) return;
            long now = Environment.TickCount64;
            if (now - _lastNbrDiagTick < 200) return;
            _lastNbrDiagTick = now;
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            bool haveWork = TryGetWorkArea(self, out RECT waSelf);
            int ourH = Math.Max(1, ourVis.Bottom - ourVis.Top);
            RECT bestV = default; int bestGap = int.MaxValue; bool found = false;
            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cl, sizeof(int)) == 0 && cl != 0) return true;
                if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT v)) return true;
                if (v.Right - v.Left < 50 || v.Bottom - v.Top < 50) return true;
                int vt = Math.Max(v.Top, ourVis.Top), vb = Math.Min(v.Bottom, ourVis.Bottom);
                if (vb - vt < ourH / 2) return true;
                if (rightEdge && v.Left <= ourVis.Left) return true;
                if (!rightEdge && v.Right >= ourVis.Right) return true;
                int g = rightEdge ? v.Left - ourVis.Right : ourVis.Left - v.Right;
                if (Math.Abs(g) < Math.Abs(bestGap)) { bestGap = g; bestV = v; found = true; }
                return true;
            }, IntPtr.Zero);
            if (found)
            {
                int farDelta = haveWork ? (rightEdge ? Math.Abs(bestV.Right - waSelf.Right) : Math.Abs(bestV.Left - waSelf.Left)) : -1;
                bool spanOk = bestV.Top >= ourVis.Top - SnapNeighborEdgeAlignPx && bestV.Bottom <= ourVis.Bottom + SnapNeighborEdgeAlignPx;
                TsLog($"FsnMiss side={(rightEdge ? 2 : 1)} nbr=({bestV.Left},{bestV.Top},{bestV.Right},{bestV.Bottom}) gap={bestGap} farDelta={farDelta} spanOk={spanOk} our=({ourVis.Left},{ourVis.Top},{ourVis.Right},{ourVis.Bottom}) waR={waSelf.Right} waL={waSelf.Left}");
            }
            else TsLog($"FsnMiss side={(rightEdge ? 2 : 1)} nbr=none our=({ourVis.Left},{ourVis.Right})");
        }

        // Frame-synced divider resize helpers: hook CompositionTarget.Rendering while dragging so the pending
        // divider coordinate is applied once per WPF frame (see field comment). Always unhook when the drag ends.
        private void DivFsEnsureHook()
        {
            if (_divFsRenderHooked) return;
            System.Windows.Media.CompositionTarget.Rendering += DivFsOnRender;
            _divFsRenderHooked = true;
        }

        private void DivFsUnhook()
        {
            if (!_divFsRenderHooked) return;
            System.Windows.Media.CompositionTarget.Rendering -= DivFsOnRender;
            _divFsRenderHooked = false;
            _divFsPendingValid = false;
        }

        private void DivFsOnRender(object? sender, EventArgs e)
        {
            if (!_divDragging) { DivFsUnhook(); return; }
            if (!_divFsPendingValid) return;
            int coord = _divFsPendingCoord;
            _divFsPendingValid = false;
            if (_divDragSide == 3 || _divDragSide == 4) UpdateDividerJointResizeV(coord); else UpdateDividerJointResize(coord);
            if (EnableDividerFrameSyncDwmFlush) DwmFlush();
        }

        private IntPtr DivGripWndProc(IntPtr h, int msg, IntPtr w, IntPtr l, int side)
        {
            switch (msg)
            {
                case WM_SETCURSOR:
                    SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)((side == 3 || side == 4) ? IDC_SIZENS : IDC_SIZEWE)));
                    return (IntPtr)1;
                case WM_LBUTTONDOWN:
                {
                    // BUG2: пока НАШЕ окно стоит на месте, RefreshDividerGrips не пересчитывается
                    // (его периодический таймер _edgeWatcher работает только в Maximized). Если сосед
                    // за это время отснапился/уехал, _divNbrsR хранит устаревший хэндл, а грип остаётся
                    // видимым. Принуди��ельно переоцениваем соседей В МОМЕ��Т захвата: исчезнувший сосед
                    // отсеивается (gap/дальний край/перекрытие), и клик по устаревшему грипу = no-op.
                    RefreshDividerGrips();
                    var nbrs = side == 1 ? _divNbrsL : side == 2 ? _divNbrsR : side == 3 ? _divNbrsT : _divNbrsB;
                    // BUG2 (decisive): vertical seams may have a non-flush/absent neighbor; still start the
                    // joint-resize drag when geometry confirms an internal divider. UpdateDividerJointResizeV
                    // then resizes only our own edge (the shell moves the other side of the seam).
                    bool geomOk = false;
                    if ((side == 3 || side == 4) && nbrs.Count == 0)
                    {
                        var gh = new WindowInteropHelper(this).Handle;
                        if (gh != IntPtr.Zero && TryGetSnapInternalEdges(gh, out _, out _, out bool gst, out bool gsb))
                            geomOk = side == 3 ? gst : gsb;
                    }
                    if (nbrs.Count > 0 || geomOk)
                    {
                        _divDragging = true;
                        _divDragSide = side;
                        _divDragNbrs = new System.Collections.Generic.List<IntPtr>(nbrs);
                        if (side == 3 || side == 4) FindDividerCoTilesV(side); else FindDividerCoTiles(side); // BUG2: also capture same-column/row co-tiles sharing this divider
                        SetCapture(h);
                        GripLog("DivDown side=" + side + " nbrs=" + nbrs.Count);
                        if (EnableTroubleshootLog) TsLog($"DivDown side={side} nbrs={nbrs.Count} geomOk={geomOk}");
                    }
                    return IntPtr.Zero;
                }
                case WM_MOUSEMOVE:
                    if (_divDragging) { if (GetCursorPos(out POINT p)) { if (EnableDividerDeferredResize) { int gc = (_divDragSide == 3 || _divDragSide == 4) ? p.Y : p.X; _divFsPendingCoord = gc; _divFsPendingValid = true; ShowDivGuideAt(gc); } else if (EnableDividerFrameSync) { _divFsPendingCoord = (_divDragSide == 3 || _divDragSide == 4) ? p.Y : p.X; _divFsPendingValid = true; DivFsEnsureHook(); } else if (_divDragSide == 3 || _divDragSide == 4) UpdateDividerJointResizeV(p.Y); else UpdateDividerJointResize(p.X); } return IntPtr.Zero; }
                    break;
                case WM_LBUTTONUP:
                    if (_divDragging)
                    {
                        HideDivGuide();
                        if (_divFsPendingValid) { int fc = _divFsPendingCoord; _divFsPendingValid = false; if (_divDragSide == 3 || _divDragSide == 4) UpdateDividerJointResizeV(fc); else UpdateDividerJointResize(fc); }
                        DivFsUnhook();
                        ArmDivReleaseRealign(); // BUG2: лечим пост-релизный дрейф shell-ре-снапа
                        _divDragging = false; _divDragNbrs.Clear(); _divDragCoTiles.Clear();
                        ReleaseCapture();
                        if (EnableTroubleshootLog) TsLog("DivUp");
                        ScheduleEdgeGripRefresh();
                        return IntPtr.Zero;
                    }
                    break;
                case WM_CAPTURECHANGED:
                    if (_divDragging) { HideDivGuide(); DivFsUnhook(); ArmDivReleaseRealign(); _divDragging = false; _divDragNbrs.Clear(); _divDragCoTiles.Clear(); ScheduleEdgeGripRefresh(); }
                    break;
            }
            return DefWindowProcW(h, msg, w, l);
        }

        /// <summary>
        /// Пересчёт позиции/видимости наружной полосы хвата. Только в покое (settle); во время _inSizeMove
        /// спрятана. Показывается только для свободного кра��; ширина обрезается по rcWork текущего монитора.
        /// </summary>
        private void RefreshEdgeGrip()
        {
            if (!EnableFreeEdgeGrip || !UseThemedSystemFrame) return;
            if (_edgeGripResizing) return;
            RefreshDividerGrips(); // 1.2/BUG2: оверлеи внутренних разделителей (независимо от free-edge грипа)
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || _inSizeMove || WindowState != WindowState.Normal || !IsVisible)
            {
                GripLog("hide: hwnd=" + hwnd + " inSize=" + _inSizeMove + " state=" + WindowState + " vis=" + IsVisible);
                HideEdgeGrip();
                return;
            }
            if (!TryGetSnapInternalEdges(hwnd, out bool sl, out bool sr, out _, out _) || (!sl && !sr))
            {
                GripLog("hide: not snapped sl=" + sl + " sr=" + sr);
                HideEdgeGrip();
                return;
            }
            if (!TryGetVisibleBounds(hwnd, out RECT vis) || !TryGetWorkArea(hwnd, out RECT wa))
            {
                GripLog("hide: no vis/wa");
                HideEdgeGrip();
                return;
            }

            int x, w, ht;
            int hgt = Math.Max(1, vis.Bottom - vis.Top);
            // Реальный snap-сосед стоит ВПЛОТНУЮ к разделителю (зазор ~0-2px). TryFindSnapNeighbor ловит
            // окн�� с заз��ром до 400px (нужно для отслеживания при ресай��е, 1.1), п��этому здесь дополнительно
            // проверяем фактический зазор: далёкое окно соседом НЕ считаем → край свободен.
            // BUG2: любой snap-сосед в зоне разделителя обслуживается оверлеем разделителя (joint-resize),
            // поэтому край НЕ считаем свободным — наружный free-edge грип уступает оверлею.
            // BUG (tear-off): gate the free-edge grip on a robust flush-tile test, not the strict FindSnapNeighbors
            // (which misses full-height inner columns and full-height tiles beside our sub-tile, then mislabels the
            // abutting edge as free -> external grip -> raw OS resize grows us over the neighbor = tear-off).
            bool rightNbr = sr && HasFlushTileNeighborH(hwnd, vis, true);
            bool leftNbr  = sl && HasFlushTileNeighborH(hwnd, vis, false);
            bool rightFree = sr && (wa.Right - vis.Right) > SnapInternalDividerGuardPx && !rightNbr;
            bool leftFree  = sl && (vis.Left - wa.Left) > SnapInternalDividerGuardPx && !leftNbr;
            GripLog("sl=" + sl + " sr=" + sr + " vis=[" + vis.Left + "," + vis.Top + "," + vis.Right + "," + vis.Bottom + "] waLR=[" + wa.Left + "," + wa.Right + "] rightFree=" + rightFree + " leftFree=" + leftFree);
            if (rightFree)
            {
                w = Math.Min(FreeEdgeGripPx, wa.Right - vis.Right);
                x = vis.Right;
                ht = HTRIGHT;
            }
            else if (leftFree)
            {
                w = Math.Min(FreeEdgeGripPx, vis.Left - wa.Left);
                x = vis.Left - w;
                ht = HTLEFT;
            }
            else
            {
                HideEdgeGrip();
                return;
            }

            EnsureGrip();
            _edgeGripHt = ht;
            if (_gripHwnd == IntPtr.Zero) return;
            SetWindowPos(_gripHwnd, HWND_TOPMOST, x, vis.Top, w, hgt, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            GripLog("show grip x=" + x + " y=" + vis.Top + " w=" + w + " h=" + hgt + " ht=" + ht);
        }

        private bool IsInDraggableCaption(IntPtr hwnd, IntPtr lParam)
        {
            if (WindowState == WindowState.Maximized) return false;
            if (!GetWindowRect(hwnd, out RECT r)) return false;

            int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
            var p = PointFromScreen(new Point(x, y));
            if (p.Y < 0 || p.Y > CaptionHeight) return false;

            // Не превращаем верхний resize-handle в caption, инач�� потер��ется HTTOP.
            int topBand = IsOverTitleInteractive(x, y) ? ResizeGripThin : GetResizeGrip(hwnd);
            if (y < r.Top + topBand) return false;

            return !IsOverTitleInteractive(x, y);
        }

        /// <summary>
        /// П��ТЬ A: ширина зоны хвата ресайза (физ. px) = ТОЛЩИНА СТАНДАРТНОЙ системной рамки ресайза для DPI
        /// окна: SM_CXSIZEFRAME + SM_CXPADDEDBORDER (����о же, что у обычных окон Windows). DPI-зависима, поэтому
        /// на 150/200% полоса шире (px), и хват ощущается как у системных окон, а не «2px». Видимая рамка
        /// остаётся 1px �� это лишь зона хит-теста. Раньше была фикс. 6 физ. px → на высо��ом DPI казалась тонкой.
        /// </summary>
        private int GetResizeGrip(IntPtr hwnd)
        {
            int dpi = (int)GetDpiForWindow(hwnd);
            if (dpi <= 0) dpi = 96;
            int frame = GetSystemMetricsForDpi(SM_CXSIZEFRAME, (uint)dpi)
                      + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, (uint)dpi);
            return frame > 0 ? frame : ResizeGripPx;
        }

        /// <summary>
        /// ПУТЬ A: true, если то��ка экрана (физ. px) над и��терактивным элементом шапки (кнопк��/меню/список).
        /// Используется в <see cref="ThemedHitTest"/>, чтобы над кнопками верхняя зона р��сайза была тонкой
        /// (не срезала кнопку), а на пустом месте — полной.
        /// </summary>
        private bool IsOverTitleInteractive(int screenX, int screenY)
        {
            try
            {
                var dip = PointFromScreen(new Point(screenX, screenY));
                if (InputHitTest(dip) is not DependencyObject hit) return false;
                for (var d = hit; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                {
                    if (d is System.Windows.Controls.Primitives.ButtonBase
                        || d is System.Windows.Controls.Primitives.Selector
                        || d is System.Windows.Controls.MenuItem)
                        return true;
                }
            }
            catch { /* хит-тест вне дерева — считаем «не над кнопкой» */ }
            return false;
        }

        /// <summary>
        /// Режим UseThemedSystemFrame (ПУТЬ A): держим системную рамку ради чистого ресайза без призрака, но
        /// делаем её невидимой. Развёрнутое — клиент на всё окно (рамки на maximized нет). Обычное:
        /// • БОКА/НИЗ — оставляем СТАНДАРТНЫЙ отступ DefWindowProc (sizing-граница ~7px): окно выступает за
        ///   видимый край, и хват ресайза там работает СНАРУЖИ видимого края, как у обы��ных окон Windows.
        /// • ВЕРХ — инсет <see cref="ThemedFrameInset"/> px (ghost-guard: на движущемся верхнем крае не
        ///   WPF-поверхность → призрак за верх не возвращается). Верхний хват д��ёт <see cref="ThemedHitTest"/>.
        /// При snap Windows ставит ПРЯМОУГОЛЬНИК ОКНА на (граница рабочей области − ~6px sizing), а клиент внутри
        /// него на +6px → ВИДИМЫЙ край ложится ровно на границу (как у всех окон). Отступ НЕ обнуляем — обнуление
        /// вынесло бы видимый клиент на ~6px наружу (вылезал за монитор). Мёртвую зону на СОСЕДНЕМ монито��е от
        /// нашего выступа га��ит WM_NCHITTEST (HTTRANSPARENT вне rcMonitor, см. IsOutsideCurrentMonitor).
        /// Видимую рамку 1px рис��ет WPF-бордер у внутр��нней кромки клиента.
        /// </summary>
        private IntPtr ThemedFrameNcCalcSize(IntPtr hwnd, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            handled = true;

            if (WindowState == WindowState.Maximized)
                return IntPtr.Zero; // client = всё окно (границы задаёт WM_GETMINMAXINFO)

            // Прямоугольник нового ОКНА читаем ДО DefWindowProc — он перезапишет rgrc0 рассчитанным клиентом.
            var ncp = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(lParam);
            int wl = ncp.rgrc0.Left, wt = ncp.rgrc0.Top, wr = ncp.rgrc0.Right, wb = ncp.rgrc0.Bottom;
            IntPtr res = DefWindowProcW(hwnd, WM_NCCALCSIZE, wParam, lParam); // valid-rects + стандартные отступы
            var calc = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(lParam);
            int dwL = calc.rgrc0.Left, dwT = calc.rgrc0.Top, dwR = calc.rgrc0.Right, dwB = calc.rgrc0.Bottom; // DefWindowProc client (pre gap-fix) for diag
            // БОКА/НИЗ: оставляем СТАНДАРТНЫЙ неклиентский отступ DefWindowProc (sizing-граница ~7px). Окно
            // высту��ает за видимы�� край на эти px — там WM_NCHITTEST ловит ресайз СНАРУЖИ видимого края, как у
            // обычных окон Windows. DWM эту границу не показывает скв��зной (WS_THICKFRAME), дыры у плавающего
            // окна нет; видимую рамку 1px рисует WPF-бордер у внутренней кромки к��иента.
            // ВЕРХ: инсет 1px (ThemedFrameInset) — ghost-guard (не WPF на движущемся крае). Верхний хват даёт
            // ThemedHitTest над/между кнопками. Бока/низ — стандартный отступ DefWindowProc (окно выступает за
            // видимый край → внешни�� хват ��есайза, как у обычных о��он).
            // БАГ 1: в caption-режиме верх делаем FULL-BLEED (client.Top = wt), чтобы НЕ о��та��ось неклиентской
            // зоны caption — иначе система рисует там полосу заголовка. Ghost-guard сверху теряем (цена snap).
            // Без caption — прежний 1px-инсет (анти-призрак сверху).
            calc.rgrc0.Top = wt + (IncludeCaptionForSnap ? 0 : ThemedFrameInset);
            // SNAP: прижимаем ВИДИМЫЙ край клиента ТОЧНО к границе рабочей области. Windows при snap ставит
            // прямоугольник окна с поправкой на рамку, которую предполагает САМА (~4px), а наш отступ
            // DefWindowProc больше (~6px) — разница уходила внутрь зазором ~2px. Координаты NCCALCSIZE —
            // ЭКРАННЫ��, поэтому приравниваем клиент к rcWork на том крае, где он выступал бы за неё. Это и
            // убирает з��зор, и не вынос��т клиент наружу (вылезания за монитор нет). Выступ-хва�� за rcWork
            // обезврежен на соседнем мониторе через HTTRANSPARENT (IsOutsideCurrentMonitor).
            bool ncLargeOverhang = false;
            if (TryGetWorkArea(hwnd, out RECT wa))
            {
                // Прижимаем ВИДИМЫЙ край клиента к rcWork ТОЛЬКО при небольшом выступе рамки (SNAP). Большой
                // выступ = окно вынесено за край монитор�� как свободное ��� НЕ прижимаем (иначе off-screen клон).
                int clampMax = EdgeClampMaxOverhang > 0 ? EdgeClampMaxOverhang : int.MaxValue;
                // A LARGE overhang past a work-area edge (more than the frame-sized clamp cap) means the
                // window is parked partly off-screen (e.g. below the bottom edge), NOT a snap/unsnap
                // transition. The NcRedrawSkip below must NOT fire for it, else the skipped repaint leaves a
                // white unpainted band at the overhanging edge. A snap transition overhangs only by ~frame.
                ncLargeOverhang = (wa.Left - wl) > EdgeClampMaxOverhang || (wa.Top - wt) > EdgeClampMaxOverhang || (wr - wa.Right) > EdgeClampMaxOverhang || (wb - wa.Bottom) > EdgeClampMaxOverhang;
                if (wl <= wa.Left && wa.Left - wl <= clampMax) calc.rgrc0.Left = wa.Left;     // примыкает слева → клиент ровно на границу
                if (wt <= wa.Top && wa.Top - wt <= clampMax) calc.rgrc0.Top = wa.Top;
                if (wr >= wa.Right && wr - wa.Right <= clampMax) calc.rgrc0.Right = wa.Right;
                if (wb >= wa.Bottom && wb - wa.Bottom <= clampMax) calc.rgrc0.Bottom = wa.Bottom;

                if (EnableSnapLayoutGapFix)
                    ApplySnapLayoutGapFix(hwnd, new RECT { Left = wl, Top = wt, Right = wr, Bottom = wb }, wa, ref calc.rgrc0);
                if (EnableTroubleshootLog)
                    TsLog($"NcCalc raw=({wl},{wt},{wr},{wb}) dwp=({dwL},{dwT},{dwR},{dwB}) client=({calc.rgrc0.Left},{calc.rgrc0.Top},{calc.rgrc0.Right},{calc.rgrc0.Bottom}) wa=({wa.Left},{wa.Top},{wa.Right},{wa.Bottom}) state={WindowState}");
            }
            if (EnableSnapDiagLog)
            {
                TryGetSnapInternalEdges(hwnd, out bool sl, out bool sr, out bool st, out bool sb);
                SnapLog($"NCCALCSIZE win=({wl},{wt},{wr},{wb}) -> client=({calc.rgrc0.Left},{calc.rgrc0.Top},{calc.rgrc0.Right},{calc.rgrc0.Bottom}) snapLRTB={(sl?1:0)}{(sr?1:0)}{(st?1:0)}{(sb?1:0)} state={WindowState}");
            }
            // Our client rect intentionally differs from DefWindowProc's (top ghost-guard inset, work-area
            // clamps, inner-seam gap fix). The default resize blit is aligned to DefWindowProc's client, so
            // any difference smears the window over itself during resize (self-overlay stripes). Force a full
            // client repaint whenever we changed the client; this is flicker-free under DWM/WPF composition.
            // WVR_REDRAW forces a FULL client repaint (flicker-free + kills the self-overlay smear on incremental
            // resize, since our client differs from DefWindowProc by the top inset/clamps). But on a LARGE single-
            // frame jump (snap<->unsnap state transition) the full erase blanks the whole window for one frame ->
            // it visibly disappears. Skip the FORCED redraw only on such big jumps (hand-resize stays small).
            int ncNewW = calc.rgrc0.Right - calc.rgrc0.Left;
            int ncNewH = calc.rgrc0.Bottom - calc.rgrc0.Top;
            bool ncBigJump = _ncLastW > 0 && !ncLargeOverhang && (Math.Abs(ncNewW - _ncLastW) > NcRedrawMaxDeltaPx || Math.Abs(ncNewH - _ncLastH) > NcRedrawMaxDeltaPx);
            if (EnableTroubleshootLog && ncBigJump)
                TsLog($"NcRedrawSkip newWH=({ncNewW},{ncNewH}) prevWH=({_ncLastW},{_ncLastH})");
            _ncLastW = ncNewW; _ncLastH = ncNewH;
            if (!ncBigJump && (calc.rgrc0.Left != dwL || calc.rgrc0.Top != dwT || calc.rgrc0.Right != dwR || calc.rgrc0.Bottom != dwB))
            {
                // FLICKER-FREE без призра��а и без бланка. Наш client отличается от DefWindowProc (верхний
                // инсет/клампы), поэтому ЕГО valid-rects дали бы смаз (self-overlay). Строим СВОИ выровненные
                // valid-rects: сохраняем перекрытие старого→нового client, приякоренное к НЕПОДВИЖНОМУ краю
                // (противоположен тянущемуся _sizingEdge). Система копирует source(rgrc2)→dest(rgrc1) и держит
                // их вали��ными (нет бланка), в��равнивание по нашему client (н��т призрака); дорисовывается
                // только новая полоса у тянущегося края. ncp.rgrc2 — старый client, снятый ДО DefWindowProc.
                // WPF раскладывает контент от ВЕРХНЕ-ЛЕВОГО origin клиента: визуал в лог.точке (x,y) всегда
                // на экране в (clientLeft+x, clientTop+y). Значит valid-rect ВСЕГДА выравниваем top-left — тогда
                // основной массив пикселей совпадает с тем, куда WPF перерисует (нет расслоения массива).
                // Правый/нижний якорь (по краю тяги) давал ПОЛНОЕ расслоение на left/top-drag: массив
                // оставался на старом экранном месте, а WPF рисовал его со сдвигом на (newOrigin-oldOrigin).
                // Остаточный дубль возможен только на ВНОВЬ ОТКРЫТОЙ полосе при РОСТЕ окна за движущийся
                // край (там старых пикселей нет — блит их взять неоткуда).
                bool vDragLeft = false; // top-left: гориз. якорь всег��а слева (origin WPF)
                bool vDragTop  = false; // top-left: верт. якорь всегда сверху (origin WPF)
                // Variant A (ghost fix): on a left/top-edge resize the client origin MOVES, so the
                // top-left-anchored WVR_VALIDRECTS copy-blit puts old pixels under the new origin one
                // frame before WPF repaints -> residual origin-shift ghost. For such frames force a
                // full WVR_REDRAW (flicker-free under DWM). Right/bottom-edge resize keeps the origin
                // fixed, so the copy-blit stays safe and flicker-free there.
                bool vOriginMoved = calc.rgrc0.Left != ncp.rgrc2.Left || calc.rgrc0.Top != ncp.rgrc2.Top;
                if (EnableFullRedrawOnOriginMove || vOriginMoved)
                {
                    res = (IntPtr)WVR_REDRAW;
                    if (EnableTroubleshootLog)
                        TsLog($"NcOriginRedraw moveLT=({ncp.rgrc2.Left - calc.rgrc0.Left},{ncp.rgrc2.Top - calc.rgrc0.Top}) old=({ncp.rgrc2.Left},{ncp.rgrc2.Top},{ncp.rgrc2.Right},{ncp.rgrc2.Bottom}) new=({calc.rgrc0.Left},{calc.rgrc0.Top},{calc.rgrc0.Right},{calc.rgrc0.Bottom})");
                }
                else if (TryBuildAlignedValidRects(ncp.rgrc2, calc.rgrc0, vDragLeft, vDragTop, out RECT vDst, out RECT vSrc))
                {
                    calc.rgrc1 = vDst; // valid DESTINATION (в новом client)
                    calc.rgrc2 = vSrc; // valid SOURCE (в старом client)
                    res = (IntPtr)WVR_VALIDRECTS;
                    if (EnableTroubleshootLog)
                        TsLog($"NcValidRects moveLT=({ncp.rgrc2.Left - calc.rgrc0.Left},{ncp.rgrc2.Top - calc.rgrc0.Top}) old=({ncp.rgrc2.Left},{ncp.rgrc2.Top},{ncp.rgrc2.Right},{ncp.rgrc2.Bottom}) new=({calc.rgrc0.Left},{calc.rgrc0.Top},{calc.rgrc0.Right},{calc.rgrc0.Bottom}) dst=({vDst.Left},{vDst.Top},{vDst.Right},{vDst.Bottom}) src=({vSrc.Left},{vSrc.Top},{vSrc.Right},{vSrc.Bottom})");
                }
                else
                {
                    res = (IntPtr)WVR_REDRAW; // вырожденное ��ерекрытие (схлопнулись в 0) — безопасный полный перерисов
                }
            }
            Marshal.StructureToPtr(calc, lParam, false);
            return res;
        }

        /// <summary>
        /// Строит выровненные valid-rects для WVR_VALIDRECTS: перекрытие старого и нового client,
        /// пр��якоренное к НЕПОДВИЖНОМУ краю (противоположному тянущемуся). dst — в но����ом client, src — в
        /// старом; координаты экранные. Возвращает false при вырожденном перекрытии (зовущий → WVR_REDRAW).
        /// </summary>
        private static bool TryBuildAlignedValidRects(RECT oldClient, RECT newClient, bool dragLeft, bool dragTop, out RECT dst, out RECT src)
        {
            dst = default; src = default;
            int oldW = oldClient.Right - oldClient.Left, oldH = oldClient.Bottom - oldClient.Top;
            int newW = newClient.Right - newClient.Left, newH = newClient.Bottom - newClient.Top;
            int ovW = Math.Min(oldW, newW), ovH = Math.Min(oldH, newH);
            if (ovW <= 0 || ovH <= 0) return false;
            // Горизонтал��: тянут левый край → правый неподвижен (якорь справа); иначе якорь слева.
            int dstL, srcL;
            if (dragLeft) { dstL = newClient.Right - ovW; srcL = oldClient.Right - ovW; }
            else          { dstL = newClient.Left;        srcL = oldClient.Left; }
            // Вертикаль: тянут верхний край → низ неподвижен (якорь снизу); иначе якорь сверху.
            int dstT, srcT;
            if (dragTop) { dstT = newClient.Bottom - ovH; srcT = oldClient.Bottom - ovH; }
            else         { dstT = newClient.Top;          srcT = oldClient.Top; }
            dst = new RECT { Left = dstL, Top = dstT, Right = dstL + ovW, Bottom = dstT + ovH };
            src = new RECT { Left = srcL, Top = srcT, Right = srcL + ovW, Bottom = srcT + ovH };
            return true;
        }

        /// <summary>
        /// Убирает щели на ВНУТРЕННИХ разделителях Snap Layout / Joint Resize.
        /// <para>
        /// При обы��ном плавающем окне б��ка/низ оставляем как посчитал DefWindowProc: там живёт внешний
        /// невидимый resize-хват. Но snapped-окно почти всегда приж��то к верх/низ рабочей области (вертикальные
        /// колонки) или к одному из внешних краёв (квадранты). В этом состоянии нев��димый NC-выст��п на
        /// в��ут��енних разделителях не должен быть пустой щелью, по��тому расширяем client до той же "видимой"
        /// границы, которую Windows ожидает для стандартной рамки.
        /// </para>
        /// <para>
        /// В первой попытке client сдвигался только до предпо��агаемой "видимой" границы стандартной рамки. Этого
        /// оказалось недостаточно: у нашего окна NC-рамка полностью невидима, поэтому на внутренних Snap-стыках
        /// нужно закрывать почти весь HWND-выступ. Ровно до HWND-края не доходим: на 150% DPI это даёт визуальный
        /// наезд на соседнее окно на 1 физический пиксель. Resize при этом не ломается, потому что
        /// <see cref="ThemedHitTest"/> тепе��ь явно возвращает HTLEFT/HTRIGHT/HTBOTTOM даже там, где client расширен
        /// почти до края HWND.
        /// </para>
        /// </summary>
        private void ApplySnapLayoutGapFix(IntPtr hwnd, RECT window, RECT work, ref RECT client)
        {
            if (WindowState == WindowState.Maximized) return;

            int tol = Math.Max(2, GetResizeGrip(hwnd) + 2);
            // "Touching" a work-area edge must mean SNAPPED to it: the edge sits on the boundary, overhanging
            // only by the invisible sizing border. A floating window dragged far past the work area (after an
            // unsnap it can hang 100+px off-screen) must NOT count as touching, else vInset = overhang becomes
            // huge and client.Left is shoved inward by that much -> the window smears over itself. Bound by
            // EdgeClampMaxOverhang (the same cap the edge clamp uses to tell snap from off-screen).
            bool touchesLeft = window.Left <= work.Left && (work.Left - window.Left) <= EdgeClampMaxOverhang;
            bool touchesRight = window.Right >= work.Right && (window.Right - work.Right) <= EdgeClampMaxOverhang;
            bool touchesTop = window.Top <= work.Top && (work.Top - window.Top) <= EdgeClampMaxOverhang;
            bool touchesBottom = window.Bottom >= work.Bottom && (window.Bottom - work.Bottom) <= EdgeClampMaxOverhang;

            bool spansWorkHeight = Near(client.Top, work.Top, tol) && Near(client.Bottom, work.Bottom, tol);
            bool spansWorkWidth = Near(client.Left, work.Left, tol) && Near(client.Right, work.Right, tol);

            // Верти��альные Snap-колонки: left/right/center layouts, включая Win+Left/Win+Right и Joint Resize.
            if (spansWorkHeight || ((touchesTop || touchesBottom) && (touchesLeft || touchesRight)))
            {
                // Measured overhang: how far the raw window extends past the OUTER work-area edge it touches.
                // The DWM sizing border is the same thickness on the inner seam, so inset the client by it.
                int vInset = touchesRight ? (window.Right - work.Right)
                    : touchesLeft ? (work.Left - window.Left)
                    : touchesBottom ? (window.Bottom - work.Bottom)
                    : touchesTop ? (work.Top - window.Top)
                    : GetResizeGrip(hwnd);
                vInset = Math.Max(SnapInternalDividerGuardPx, vInset);
                if (!touchesLeft)
                    client.Left = window.Left + vInset;
                if (!touchesRight)
                    client.Right = window.Right - vInset;
            }

            // Горизонтальные внутренние разделители в квадрантах / stacked layouts. Верхний 1px guard не трогаем,
            // когда верх является внешним краем ��абочей области, но внутрен��юю горизонтальную щель закрываем.
            if (spansWorkWidth || ((touchesLeft || touchesRight) && (touchesTop || touchesBottom)))
            {
                int hInset = touchesBottom ? (window.Bottom - work.Bottom)
                    : touchesTop ? (work.Top - window.Top)
                    : touchesRight ? (window.Right - work.Right)
                    : touchesLeft ? (work.Left - window.Left)
                    : GetResizeGrip(hwnd);
                hInset = Math.Max(SnapInternalDividerGuardPx, hInset);
                if (!touchesTop)
                    client.Top = window.Top + SnapInternalDividerGuardPx; // TOP has no invisible DWM border; measured inset would draw a gray strip above the caption
                if (!touchesBottom)
                    client.Bottom = window.Bottom - hInset;
            }
        }

        private static bool Near(int a, int b, int tolerance) => Math.Abs(a - b) <= tolerance;

        /// <summary>
        /// Включает DWM-цвет рамки только когда у snapped-окна есть внутренний разделитель.
        /// При floating оставляем DWMWA_COLOR_NONE, как было ра��ьше. На Snap-разделителе DWM шт��тно
        /// закрашивает оставленный 1 физ-px NC-guard, поэтом�� нет ни наезда client на соседа, ни пустого
        /// desktop-gap.
        /// </summary>
        private void UpdateSnapDwmBorderColor(IntPtr hwnd)
        {
            bool needBorder = HasInternalSnapDivider(hwnd);
            if (_snapDwmBorderColorApplied == needBorder) return;

            int borderColor = needBorder ? ThemeBorderColorRef : DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            _snapDwmBorderColorApplied = needBorder;
        }

        private bool HasInternalSnapDivider(IntPtr hwnd)
        {
            TryGetSnapInternalEdges(hwnd, out bool left, out bool right, out bool top, out bool bottom);
            return left || right || top || bottom;
        }

        private bool TryGetSnapInternalEdges(IntPtr hwnd, out bool left, out bool right, out bool top, out bool bottom)
        {
            left = right = top = bottom = false;
            if (!EnableSnapLayoutGapFix || WindowState == WindowState.Maximized) return false;
            if (!GetWindowRect(hwnd, out RECT raw)) return false;
            if (!TryGetWorkArea(hwnd, out RECT work)) return false;
            // BUG1.2 (первопричина): меряем по ВИДИМЫМ границам (DWMWA_EXTENDED_FRAME_BOUNDS), а НЕ по GetWindowRect.
            // Raw-rect включает невидимый sizing-выступ DWM (~8-16px при 150% DPI). Из-за этого СРЕДНЯЯ колонка
            // (не касается боковых краёв work-area) промахивалась по spanH (raw top=16 при tol=13) и НЕ считалась
            // snapped, тогда как half-окна распознавались через touch боковой границы. Видимые границы точно
            // ложатся на work-area (vis top=0, bottom~2162), поэтому колонка теперь распознаётся как half.
            RECT window = TryGetVisibleBounds(hwnd, out RECT visb) ? visb : raw;

            int tol = Math.Max(2, GetResizeGrip(hwnd) + 2);
            bool touchesLeft = window.Left <= work.Left;
            bool touchesRight = window.Right >= work.Right;
            bool touchesTop = window.Top <= work.Top;
            bool touchesBottom = window.Bottom >= work.Bottom;
            bool spansWorkHeight = Near(window.Top, work.Top, tol) && Near(window.Bottom, work.Bottom, tol);
            bool spansWorkWidth = Near(window.Left, work.Left, tol) && Near(window.Right, work.Right, tol);

            bool verticalSnap = spansWorkHeight || ((touchesTop || touchesBottom) && (touchesLeft || touchesRight));
            bool horizontalSnap = spansWorkWidth || ((touchesLeft || touchesRight) && (touchesTop || touchesBottom));

            if (verticalSnap)
            {
                left = !touchesLeft;
                right = !touchesRight;
            }
            if (horizontalSnap)
            {
                top = !touchesTop;
                bottom = !touchesBottom;
            }
            // BUG1.2: когда рёбер не нашлос�� (fail=no-edges) - логируем геометрию (троттлинг 250мс), чтобы
            // понять, почему при snap к уже расшире��ном�� с��седу разделитель не распознаётся.
            if (!(left || right || top || bottom) && EnableTroubleshootLog)
            {
                long now = Environment.TickCount64;
                if (now - _lastNoEdgeDiagTick > 250)
                {
                    _lastNoEdgeDiagTick = now;
                    TsLog($"JrcEdges fail vis=({window.Left},{window.Top},{window.Right},{window.Bottom}) raw=({raw.Left},{raw.Top},{raw.Right},{raw.Bottom}) work=({work.Left},{work.Top},{work.Right},{work.Bottom}) tol={tol} tL={touchesLeft} tR={touchesRight} tT={touchesTop} tB={touchesBottom} spanH={spansWorkHeight} spanW={spansWorkWidth} vSnap={verticalSnap} hSnap={horizontalSnap} state={WindowState}");
                }
            }
            return left || right || top || bottom;
        }

        #region Snap joint-resize follow (БАГ 1)
        // Наше окно борлесс (без WS_CAPTION) → shell НЕ включает его в Snap-группу и не двигает ползунком
        // joint-resize. П��этому ПОДСТРАИВАЕМ окно сами: пока окно снапнуто с внутренним ЛЕВО/ПРАВО-разделителем,
        // лёгкий таймер следит за ЛКМ; если пользователь нажал у видимой границы-разделителя и тянет — двигаем
        // СВОЙ край за курсором. Сосед (обычное окно) в это время двигается shell'ом за тем же курсором → оба
        // видимых края встречаются на ползунке. Видимые границы бе��ём через DWMWA_EXTENDED_FRAME_BOUNDS (без
        // невидимого выступа), чтобы кр���� встал точно на ползунок. Верх/низ-разделители пока не трогаем (там
        // конфликт с зоной перетаскивания шапки) — ��сновной кей�� пользователя это колонки (верт. разделитель).

        private const bool EnableSnapFollow = true;
        private const int SnapFollowGrabBandPx = 12; // допуск близости курсора к видимой границе при нажатии (px)
        // КУРСОР joint-resize. Окно бордерлесс на внутреннем Snap-разделителе возвращает HTNOWHERE (чтобы
        // клик не запускал индивидуальный ресайз/un-snap — БАГ 1), поэтому системный курсор ��↔» появлялся
        // только на узкой shell-полосе. Реальный ресайз делает SnapFollow в полосе SnapFollowGrabBandPx, поэтому
        // п��казыва��м SizeWE на ТОЙ ЖЕ полосе через WM_SETCURSOR — аффорданс как �� стандартных окон, без
        // изменения WM_NCHITTEST (риска un-snap нет). false = выкл (курсор только на узкой границе, как раньше).
        private const bool EnableJointResizeCursor = true;
        private const int SnapFollowMinDimPx = 200;  // не ужимать окно у́же этого (px)
        private const int SnapSettleTicks = 20;
        // Passive-follow: mirror a snap-divider dragged BETWEEN two OTHER windows onto our matching edge. To avoid
        // gluing to a freely-moved window, we require the neighbor to be RESIZED on the shared side (its FAR edge
        // stays put while its NEAR edge, shared with us, moves). A translated window moves BOTH edges together and
        // is therefore ignored. See PassiveFollowNeighbors.
        private const bool EnablePassiveFollow = true;
        private const int PassiveMaxTravelPx = 1600; // ignore absurd neighbor edge jumps (likely a mis-detected neighbor)
        private const int PassiveFarStableTol = 6;   // neighbor FAR edge must stay within this (px) to count as a resize, not a move
        private const int PassiveMinFollowPx = 8;   // deadband: ignore sub-threshold neighbor jitter (unsnap transient); follow only real resizes
        private const int PassivePerpStableTol = 32; // neighbor PERPENDICULAR extent (top/bottom for side neighbors) must stay put; a bigger change means it re-snapped/moved, not a joint-resize
        private const int PassiveSettleTicks = 45;   // keep passive-follow re-flushing this many ticks after release      // доводка после отпускания ползунка (~300мс при 15мс/тик)
        private DispatcherTimer? _snapFollowTimer;
        private bool _snapFollowActive;
        private bool _snapPrevLBtn;
        private IntPtr _topNbr;      // tracked top neighbor HWND during drag
        private IntPtr _botNbr;      // tracked bottom neighbor HWND during drag
        private int _snapDragEdge;    // 0=нет, 1=left, 2=right (залаченный край при активном перетягивании)
        private int _snapSettleEdge;  // край, который доводим после отпускания
        private int _snapSettleTicks; // >0 = ид��т доводка после отпускания (держим залаченный край по с��седу)
        private IntPtr _leftNbr;      // HWND зафиксированного левого соседа на время перетя��ивания
        private IntPtr _rightNbr;     // HWND зафиксированного правого соседа на время перетягивания

        // BUG2 passive-follow state: our borderless window is excluded from the OS Snap group, so when the
        // shell drags a shared snap-divider that lives on OTHER windows, we get no message. We mirror that
        // neighbor edge MOVEMENT (delta) onto our matching edge from the always-running SnapFollow timer.
        private IntPtr _pfL, _pfR, _pfT, _pfB;
        private int _pfLe, _pfRe, _pfTe, _pfBe;
        private bool _pfHave;
        private int _pfOurL, _pfOurR, _pfOurT, _pfOurB;
        private int _pfLf, _pfRf, _pfTf, _pfBf; // baselined FAR edge of each tracked neighbor (opposite the shared edge)
        private int _pfLp0, _pfLp1, _pfRp0, _pfRp1, _pfTp0, _pfTp1, _pfBp0, _pfBp1; // baselined PERPENDICULAR extent of each neighbor (Top/Bottom for L/R, Left/Right for T/B)
        private int _pfSettleTicks;
        private long _lastPfLogTick;

        private void ResetPassiveFollow()
        {
            _pfL = _pfR = _pfT = _pfB = IntPtr.Zero;
            _pfHave = false;
            _pfSettleTicks = 0;
        }

        // Passive-follow finder is intentionally more relaxed than TryFindSnapNeighbor. The OS snap-divider can be
        // dragged on OTHER windows whose span only partially overlaps ours (L-shaped/uneven snap layouts). Requiring
        // full-height/full-width was the reason PfFollow never fired in the real log: the closest left neighbor had
        // substantial overlap but failed the strict fullHeight/spanOk test.
        // BUG (tear-off): true if a real side TILE sits flush against our left/right edge. Unlike strict
        // FindSnapNeighbors it does NOT require the neighbor to reach the far screen edge (so a full-height inner
        // column is recognised) nor to match our span (so a full-height tile beside our sub-tile / L-shape is
        // recognised). Free-floating windows are rejected via the perpendicular work-edge touch test. Used only to
        // decide whether the external free-edge grip is shown, so the grip appears ONLY on edges facing empty space.
        private bool HasFlushTileNeighborH(IntPtr self, RECT ourVis, bool rightEdge)
        {
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            int ourH = Math.Max(1, ourVis.Bottom - ourVis.Top);
            int minOverlap = Math.Max(80, ourH / 5);
            bool haveWork = TryGetWorkArea(self, out RECT waSelf);
            bool found = false;
            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT v)) return true;
                if (v.Right - v.Left < 50 || v.Bottom - v.Top < 50) return true;
                if (haveWork)
                {
                    bool perpTouch = v.Top <= waSelf.Top + SnapNeighborEdgeAlignPx || v.Bottom >= waSelf.Bottom - SnapNeighborEdgeAlignPx;
                    if (!perpTouch) return true; // free-floating window: not a tile, ignore
                }
                int overlap = Math.Min(v.Bottom, ourVis.Bottom) - Math.Max(v.Top, ourVis.Top);
                if (overlap < minOverlap) return true;
                int gap = rightEdge ? (v.Left - ourVis.Right) : (ourVis.Left - v.Right);
                if (gap < -SnapNeighborEdgeAlignPx || gap > EdgeGripFlushGapPx) return true; // must sit flush at this edge
                found = true;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        // Tile-group criterion (user rule): our window follows a BIG neighbor's shared edge only when our side of
        // the seam (our window + flush sibling co-tiles) EXACTLY tiles that neighbor's edge - same start, same end,
        // no gaps, no overhang. This recognises L-shaped / stacked layouts whose big window is not itself anchored
        // to a work-area edge, while still rejecting a lone floating window (which tiles nothing) and a partial
        // group (which leaves a gap or overhangs). Horizontal variant: seam is vertical, co-tiles compared along Y.
        private bool SideTilesCoverBigEdgeH(IntPtr self, RECT ourVis, RECT big, bool rightEdge)
        {
            int seamX = rightEdge ? ourVis.Right : ourVis.Left;
            int bigNear = rightEdge ? big.Left : big.Right;
            if (Math.Abs(bigNear - seamX) > SnapNeighborEdgeAlignPx) return false; // big must sit flush on the seam
            int bigTop = big.Top, bigBot = big.Bottom;
            // The big window is the LARGE side: strictly taller than us, so the group genuinely needs a sibling.
            if ((bigBot - bigTop) <= (ourVis.Bottom - ourVis.Top) + SnapNeighborEdgeAlignPx) return false;
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            var tops = new System.Collections.Generic.List<int>();
            var bots = new System.Collections.Generic.List<int>();
            tops.Add(ourVis.Top); bots.Add(ourVis.Bottom); // our window is part of the group
            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT w)) return true;
                if (w.Right - w.Left < 50 || w.Bottom - w.Top < 50) return true;
                int coNear = rightEdge ? w.Right : w.Left; // a co-tile faces the big window with the same edge as us
                if (Math.Abs(coNear - seamX) > SnapNeighborEdgeAlignPx) return true;
                tops.Add(w.Top); bots.Add(w.Bottom);
                return true;
            }, IntPtr.Zero);
            if (tops.Count < 2) return false; // our window + at least one flush sibling required
            int groupTop = int.MaxValue, groupBot = int.MinValue;
            for (int i = 0; i < tops.Count; i++) { if (tops[i] < groupTop) groupTop = tops[i]; if (bots[i] > groupBot) groupBot = bots[i]; }
            if (Math.Abs(groupTop - bigTop) > SnapNeighborEdgeAlignPx) return false; // group must start where big does
            if (Math.Abs(groupBot - bigBot) > SnapNeighborEdgeAlignPx) return false; // and end where big does (equal length)
            int covered = bigTop; bool progress = true; // contiguous coverage: no vertical gaps in the group
            while (covered < bigBot - SnapNeighborEdgeAlignPx && progress)
            {
                progress = false; int bestHi = covered;
                for (int i = 0; i < tops.Count; i++)
                    if (tops[i] <= covered + SnapNeighborEdgeAlignPx && bots[i] > bestHi) bestHi = bots[i];
                if (bestHi > covered) { covered = bestHi; progress = true; }
            }
            return covered >= bigBot - SnapNeighborEdgeAlignPx;
        }

        // Vertical variant of the tile-group criterion: seam is horizontal, co-tiles compared along X.
        private bool SideTilesCoverBigEdgeV(IntPtr self, RECT ourVis, RECT big, bool bottomEdge)
        {
            int seamY = bottomEdge ? ourVis.Bottom : ourVis.Top;
            int bigNear = bottomEdge ? big.Top : big.Bottom;
            if (Math.Abs(bigNear - seamY) > SnapNeighborEdgeAlignPx) return false;
            int bigL = big.Left, bigR = big.Right;
            if ((bigR - bigL) <= (ourVis.Right - ourVis.Left) + SnapNeighborEdgeAlignPx) return false;
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            var lefts = new System.Collections.Generic.List<int>();
            var rights = new System.Collections.Generic.List<int>();
            lefts.Add(ourVis.Left); rights.Add(ourVis.Right);
            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT w)) return true;
                if (w.Right - w.Left < 50 || w.Bottom - w.Top < 50) return true;
                int coNear = bottomEdge ? w.Bottom : w.Top;
                if (Math.Abs(coNear - seamY) > SnapNeighborEdgeAlignPx) return true;
                lefts.Add(w.Left); rights.Add(w.Right);
                return true;
            }, IntPtr.Zero);
            if (lefts.Count < 2) return false;
            int groupL = int.MaxValue, groupR = int.MinValue;
            for (int i = 0; i < lefts.Count; i++) { if (lefts[i] < groupL) groupL = lefts[i]; if (rights[i] > groupR) groupR = rights[i]; }
            if (Math.Abs(groupL - bigL) > SnapNeighborEdgeAlignPx) return false;
            if (Math.Abs(groupR - bigR) > SnapNeighborEdgeAlignPx) return false;
            int covered = bigL; bool progress = true;
            while (covered < bigR - SnapNeighborEdgeAlignPx && progress)
            {
                progress = false; int bestHi = covered;
                for (int i = 0; i < lefts.Count; i++)
                    if (lefts[i] <= covered + SnapNeighborEdgeAlignPx && rights[i] > bestHi) bestHi = rights[i];
                if (bestHi > covered) { covered = bestHi; progress = true; }
            }
            return covered >= bigR - SnapNeighborEdgeAlignPx;
        }

        private bool TryFindPassiveNeighborH(IntPtr self, RECT ourVis, bool rightEdge, out IntPtr neighborHwnd, out RECT neighborVis)
        {
            neighborHwnd = IntPtr.Zero;
            neighborVis = default;
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            int ourH = Math.Max(1, ourVis.Bottom - ourVis.Top);
            int minOverlap = Math.Max(80, ourH / 5);
            bool haveWork = TryGetWorkArea(self, out RECT waSelf);
            int bestGap = int.MaxValue;
            IntPtr bestHwnd = IntPtr.Zero;
            RECT bestVis = default;
            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT v)) return true;
                if (v.Right - v.Left < 50 || v.Bottom - v.Top < 50) return true;
                bool bigSnap = false;
                if (haveWork)
                {
                    // A genuine snap neighbor fills from the shared seam to the FAR work-area edge (its far
                    // edge sits on the work boundary). A free-floating window never reaches that boundary,
                    // so requiring far-edge anchoring stops a floating window from hijacking the follow and
                    // resizing our edge. All real snap layouts keep working (snapped windows butt the edge).
                    bool farAnchored = rightEdge
                        ? v.Right >= waSelf.Right - SnapNeighborEdgeAlignPx
                        : v.Left <= waSelf.Left + SnapNeighborEdgeAlignPx;
                    // A genuinely snapped side-by-side neighbor also touches a PERPENDICULAR work-area edge.
                    bool perpTouch = v.Top <= waSelf.Top + SnapNeighborEdgeAlignPx || v.Bottom >= waSelf.Bottom - SnapNeighborEdgeAlignPx;
                    bool tileGroup = SideTilesCoverBigEdgeH(self, ourVis, v, rightEdge);
                    // Accept EITHER a classic work-edge-anchored snapped neighbor (farAnchored AND perpTouch),
                    // OR a BIG neighbor whose shared edge is EXACTLY tiled by our side (our window + flush sibling
                    // co-tiles together equal its edge, no gaps and no overhang) - the tile-group criterion for
                    // L-shaped / stacked layouts. A lone floating window tiles nothing, so it stays rejected.
                    if (!(farAnchored && perpTouch) && !tileGroup) return true;
                    bigSnap = (farAnchored && perpTouch) || tileGroup;
                }
                int overlap = Math.Min(v.Bottom, ourVis.Bottom) - Math.Max(v.Top, ourVis.Top);
                if (overlap < minOverlap) return true;
                bool sideOk = rightEdge ? (v.Left > ourVis.Left) : (v.Right < ourVis.Right);
                if (!sideOk) return true;
                int rawGap = rightEdge ? Math.Abs(v.Left - ourVis.Right) : Math.Abs(v.Right - ourVis.Left);
                // A real snapped/tiled partner can be committed by the OS in ONE big jump during a system
                // divider drag (the neighbor seam can leap ~600px on release). Keep tracking such a partner
                // across a jump up to the follow travel cap; a floating window keeps the tight 500px limit.
                // The follow MOVE itself stays gated in PassiveFollowNeighbors by a flush baseline + stable far
                // edge, so a distant window we were never flush against can never teleport us.
                int gapLimit = bigSnap ? Math.Max(500, PassiveMaxTravelPx) : 500;
                if (rawGap > gapLimit) return true;
                if (rawGap < bestGap) { bestGap = rawGap; bestHwnd = h; bestVis = v; }
                return true;
            }, IntPtr.Zero);
            neighborHwnd = bestHwnd;
            neighborVis = bestVis;
            return neighborHwnd != IntPtr.Zero;
        }

        private bool TryFindPassiveNeighborV(IntPtr self, RECT ourVis, bool bottomEdge, out IntPtr neighborHwnd, out RECT neighborVis)
        {
            neighborHwnd = IntPtr.Zero;
            neighborVis = default;
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            int ourW = Math.Max(1, ourVis.Right - ourVis.Left);
            int minOverlap = Math.Max(80, ourW / 5);
            bool haveWork = TryGetWorkArea(self, out RECT waSelf);
            int bestGap = int.MaxValue;
            IntPtr bestHwnd = IntPtr.Zero;
            RECT bestVis = default;
            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT v)) return true;
                if (v.Right - v.Left < 50 || v.Bottom - v.Top < 50) return true;
                bool bigSnap = false;
                if (haveWork)
                {
                    // A genuine snap neighbor fills from the shared seam to the FAR work-area edge (its far
                    // edge sits on the work boundary). A free-floating window never reaches that boundary,
                    // so requiring far-edge anchoring stops a floating window from hijacking the follow and
                    // resizing our edge. All real snap layouts keep working (snapped windows butt the edge).
                    bool farAnchored = bottomEdge
                        ? v.Bottom >= waSelf.Bottom - SnapNeighborEdgeAlignPx
                        : v.Top <= waSelf.Top + SnapNeighborEdgeAlignPx;
                    // A genuinely snapped stacked neighbor also touches a PERPENDICULAR work-area edge.
                    bool perpTouch = v.Left <= waSelf.Left + SnapNeighborEdgeAlignPx || v.Right >= waSelf.Right - SnapNeighborEdgeAlignPx;
                    bool tileGroup = SideTilesCoverBigEdgeV(self, ourVis, v, bottomEdge);
                    // Accept EITHER a classic work-edge-anchored snapped neighbor (farAnchored AND perpTouch),
                    // OR a BIG neighbor whose shared edge is EXACTLY tiled by our side (our window + flush sibling
                    // co-tiles together equal its edge, no gaps and no overhang) - the tile-group criterion for
                    // L-shaped / stacked layouts. A lone floating window tiles nothing, so it stays rejected.
                    if (!(farAnchored && perpTouch) && !tileGroup) return true;
                    bigSnap = (farAnchored && perpTouch) || tileGroup;
                }
                int overlap = Math.Min(v.Right, ourVis.Right) - Math.Max(v.Left, ourVis.Left);
                if (overlap < minOverlap) return true;
                bool sideOk = bottomEdge ? (v.Top > ourVis.Top) : (v.Bottom < ourVis.Bottom);
                if (!sideOk) return true;
                int rawGap = bottomEdge ? Math.Abs(v.Top - ourVis.Bottom) : Math.Abs(v.Bottom - ourVis.Top);
                // A real snapped/tiled partner can be committed by the OS in ONE big jump during a system
                // divider drag (the neighbor seam can leap ~600px on release). Keep tracking such a partner
                // across a jump up to the follow travel cap; a floating window keeps the tight 500px limit.
                // The follow MOVE itself stays gated in PassiveFollowNeighbors by a flush baseline + stable far
                // edge, so a distant window we were never flush against can never teleport us.
                int gapLimit = bigSnap ? Math.Max(500, PassiveMaxTravelPx) : 500;
                if (rawGap > gapLimit) return true;
                if (rawGap < bestGap) { bestGap = rawGap; bestHwnd = h; bestVis = v; }
                return true;
            }, IntPtr.Zero);
            neighborHwnd = bestHwnd;
            neighborVis = bestVis;
            return neighborHwnd != IntPtr.Zero;
        }

        // BUG2: while the mouse is held (or during the settle window right after release) and we are NOT running
        // our own edge-drag, follow a shared snap-divider that lies OUTSIDE our window. We anchor on the fact that
        // an edge WAS flush against a neighbor, then re-flush ABSOLUTELY to that neighbor current edge once a
        // gap/overlap appears within range. This survives the OS deferring the neighbor commit until release.
        // DIAGNOSTIC ONLY: throttle for the candidate dump below.
        private long _lastPfCandLogTick;

        // DIAGNOSTIC ONLY: when we are snapped on a side but found NO passive-follow neighbor there,
        // enumerate every top-level window near that side and log each one together with the exact
        // per-check verdicts the real detector uses (farAnchored / perpTouch / sideOk / tile-group).
        // This reveals the true window set and WHY a layout is not recognized. horizontal=true => L/R
        // side (rightEdge selects our Right vs Left); horizontal=false => T/B side (rightEdge=bottom).
        private void LogPassiveCandidates(IntPtr self, RECT ourVis, bool rightEdge, bool horizontal)
        {
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            bool haveWork = TryGetWorkArea(self, out RECT waSelf);
            int ourExtent = horizontal
                ? Math.Max(1, ourVis.Bottom - ourVis.Top)
                : Math.Max(1, ourVis.Right - ourVis.Left);
            int minOverlap = Math.Max(80, ourExtent / 5);
            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT v)) return true;
                if (v.Right - v.Left < 50 || v.Bottom - v.Top < 50) return true;
                int overlap, rawGap;
                bool farAnchored, perpTouch, sideOk, tile;
                if (horizontal)
                {
                    overlap = Math.Min(v.Bottom, ourVis.Bottom) - Math.Max(v.Top, ourVis.Top);
                    rawGap = rightEdge ? Math.Abs(v.Left - ourVis.Right) : Math.Abs(v.Right - ourVis.Left);
                    farAnchored = haveWork && (rightEdge ? v.Right >= waSelf.Right - SnapNeighborEdgeAlignPx : v.Left <= waSelf.Left + SnapNeighborEdgeAlignPx);
                    perpTouch = haveWork && (v.Top <= waSelf.Top + SnapNeighborEdgeAlignPx || v.Bottom >= waSelf.Bottom - SnapNeighborEdgeAlignPx);
                    sideOk = rightEdge ? (v.Left > ourVis.Left) : (v.Right < ourVis.Right);
                    tile = SideTilesCoverBigEdgeH(self, ourVis, v, rightEdge);
                }
                else
                {
                    overlap = Math.Min(v.Right, ourVis.Right) - Math.Max(v.Left, ourVis.Left);
                    rawGap = rightEdge ? Math.Abs(v.Top - ourVis.Bottom) : Math.Abs(v.Bottom - ourVis.Top);
                    farAnchored = haveWork && (rightEdge ? v.Bottom >= waSelf.Bottom - SnapNeighborEdgeAlignPx : v.Top <= waSelf.Top + SnapNeighborEdgeAlignPx);
                    perpTouch = haveWork && (v.Left <= waSelf.Left + SnapNeighborEdgeAlignPx || v.Right >= waSelf.Right - SnapNeighborEdgeAlignPx);
                    sideOk = rightEdge ? (v.Top > ourVis.Top) : (v.Bottom < ourVis.Bottom);
                    tile = SideTilesCoverBigEdgeV(self, ourVis, v, rightEdge);
                }
                if (rawGap > 600 && overlap < minOverlap) return true; // ignore clearly-unrelated windows to keep the log small
                string sideTag = horizontal ? (rightEdge ? "R" : "L") : (rightEdge ? "B" : "T");
                TsLog($"PfCand side={sideTag} our=({ourVis.Left},{ourVis.Top},{ourVis.Right},{ourVis.Bottom}) win=({v.Left},{v.Top},{v.Right},{v.Bottom}) gap={rawGap} ov={overlap}/{minOverlap} far={farAnchored} perp={perpTouch} sideOk={sideOk} tile={tile}");
                return true;
            }, IntPtr.Zero);
        }

        private void PassiveFollowNeighbors(IntPtr hwnd, RECT win, RECT vis, bool sl, bool sr, bool st, bool sb, bool allowMove)
        {
            int L = win.Left, R = win.Right, T = win.Top, B = win.Bottom;
            IntPtr nl = IntPtr.Zero, nr = IntPtr.Zero, nt = IntPtr.Zero, nb = IntPtr.Zero;
            int le = 0, re = 0, te = 0, be = 0;
            int lf = 0, rf = 0, tf = 0, bf = 0; // FAR edge of each candidate (opposite the edge shared with us)
            bool haveL = false, haveR = false, haveT = false, haveB = false;
            RECT rv = default, lv = default, tv = default, bv = default;
            if (sl && TryFindPassiveNeighborH(hwnd, vis, false, out IntPtr lhc, out lv)) { nl = lhc; le = lv.Right; lf = lv.Left; haveL = true; }
            if (sr && TryFindPassiveNeighborH(hwnd, vis, true, out IntPtr rhc, out rv)) { nr = rhc; re = rv.Left; rf = rv.Right; haveR = true; }
            if (st && TryFindPassiveNeighborV(hwnd, vis, false, out IntPtr thc, out tv)) { nt = thc; te = tv.Bottom; tf = tv.Top; haveT = true; }
            if (sb && TryFindPassiveNeighborV(hwnd, vis, true, out IntPtr bhc, out bv)) { nb = bhc; be = bv.Top; bf = bv.Bottom; haveB = true; }

            // DIAGNOSTIC ONLY: snapped on a side but no neighbor recognized there -> dump the candidate set.
            if (EnableTroubleshootLog && ((sl && !haveL) || (sr && !haveR) || (st && !haveT) || (sb && !haveB)))
            {
                long nowC = Environment.TickCount64;
                if (nowC - _lastPfCandLogTick > 700)
                {
                    _lastPfCandLogTick = nowC;
                    if (sl && !haveL) LogPassiveCandidates(hwnd, vis, false, true);
                    if (sr && !haveR) LogPassiveCandidates(hwnd, vis, true, true);
                    if (st && !haveT) LogPassiveCandidates(hwnd, vis, false, false);
                    if (sb && !haveB) LogPassiveCandidates(hwnd, vis, true, false);
                }
            }

            // BUG2 (decisive): a foreign snap-divider drag does NOT update the neighbor committed bounds until
            // the drag SETTLES (usually on release), so a per-tick delta reads 0 during the drag. The old code
            // was delta-based AND button-gated, so it stopped tracking on release, missing that late commit. We
            // now keep a frozen per-edge baseline (neighbor edge + our edge) captured at REST and, while allowMove
            // is true (button held OR settle ticks after release), shift our edge by the neighbor total travel from
            // that baseline. Relative displacement preserves the resting offset, so we never force overlap.
            bool moved = false;
            int seamFlushTol = Math.Max(40, GetResizeGrip(hwnd) * 3); // real shared-seam windows are flush; a larger baseline gap means an unrelated window
            // Each edge, for the SAME neighbor we baselined at rest: compare how far its NEAR edge (shared with us)
            // and its FAR edge (opposite) have travelled. FAR stable + NEAR moved => the shared seam was resized, so we
            // shift our matching edge by the NEAR delta (our.edge = ourBase + dNear), preserving the resting offset so
            // we never force overlap. FAR also moved => the neighbor was dragged/translated, so we re-baseline and do NOT
            // follow. The baseline is frozen while following and only refreshed at REST; a new neighbor HWND re-baselines.
            // Left neighbor: NEAR edge = its Right (shared with our Left), FAR edge = its Left.
            if (haveL)
            {
                if (nl == _pfL)
                {
                    if (allowMove)
                    {
                        int dNear = le - _pfLe, dFar = lf - _pfLf;
                        if (Math.Abs(dFar) <= PassiveFarStableTol && Math.Abs(dNear) >= PassiveMinFollowPx && Math.Abs(dNear) <= PassiveMaxTravelPx
                            && Math.Abs(_pfLe - _pfOurL) <= seamFlushTol
                            && Math.Abs(lv.Top - _pfLp0) <= PassivePerpStableTol && Math.Abs(lv.Bottom - _pfLp1) <= PassivePerpStableTol)
                        { L = _pfOurL + dNear; moved = true; } // FAR edge pinned + NEAR edge moved + perpendicular extent unchanged => shared seam resized: follow
                        else if (Math.Abs(dFar) > PassiveFarStableTol || Math.Abs(lv.Top - _pfLp0) > PassivePerpStableTol || Math.Abs(lv.Bottom - _pfLp1) > PassivePerpStableTol)
                        { _pfLe = le; _pfLf = lf; _pfLp0 = lv.Top; _pfLp1 = lv.Bottom; _pfOurL = win.Left; } // far edge OR perpendicular extent moved => neighbor translated/re-snapped: re-baseline, do NOT follow
                    }
                    else { _pfLe = le; _pfLf = lf; _pfLp0 = lv.Top; _pfLp1 = lv.Bottom; _pfOurL = win.Left; }
                }
                else { _pfL = nl; _pfLe = le; _pfLf = lf; _pfLp0 = lv.Top; _pfLp1 = lv.Bottom; _pfOurL = win.Left; }
            }
            else _pfL = IntPtr.Zero;
            // Right neighbor: NEAR edge = its Left (shared with our Right), FAR edge = its Right.
            if (haveR)
            {
                if (nr == _pfR)
                {
                    if (allowMove)
                    {
                        int dNear = re - _pfRe, dFar = rf - _pfRf;
                        if (Math.Abs(dFar) <= PassiveFarStableTol && Math.Abs(dNear) >= PassiveMinFollowPx && Math.Abs(dNear) <= PassiveMaxTravelPx
                            && Math.Abs(_pfRe - _pfOurR) <= seamFlushTol
                            && Math.Abs(rv.Top - _pfRp0) <= PassivePerpStableTol && Math.Abs(rv.Bottom - _pfRp1) <= PassivePerpStableTol)
                        { R = _pfOurR + dNear; moved = true; }
                        else if (Math.Abs(dFar) > PassiveFarStableTol || Math.Abs(rv.Top - _pfRp0) > PassivePerpStableTol || Math.Abs(rv.Bottom - _pfRp1) > PassivePerpStableTol)
                        { _pfRe = re; _pfRf = rf; _pfRp0 = rv.Top; _pfRp1 = rv.Bottom; _pfOurR = win.Right; }
                    }
                    else { _pfRe = re; _pfRf = rf; _pfRp0 = rv.Top; _pfRp1 = rv.Bottom; _pfOurR = win.Right; }
                }
                else { _pfR = nr; _pfRe = re; _pfRf = rf; _pfRp0 = rv.Top; _pfRp1 = rv.Bottom; _pfOurR = win.Right; }
            }
            else _pfR = IntPtr.Zero;
            // Top neighbor: NEAR edge = its Bottom (shared with our Top), FAR edge = its Top.
            if (haveT)
            {
                if (nt == _pfT)
                {
                    if (allowMove)
                    {
                        int dNear = te - _pfTe, dFar = tf - _pfTf;
                        if (Math.Abs(dFar) <= PassiveFarStableTol && Math.Abs(dNear) >= PassiveMinFollowPx && Math.Abs(dNear) <= PassiveMaxTravelPx
                            && Math.Abs(_pfTe - _pfOurT) <= seamFlushTol
                            && Math.Abs(tv.Left - _pfTp0) <= PassivePerpStableTol && Math.Abs(tv.Right - _pfTp1) <= PassivePerpStableTol)
                        { T = _pfOurT + dNear; moved = true; }
                        else if (Math.Abs(dFar) > PassiveFarStableTol || Math.Abs(tv.Left - _pfTp0) > PassivePerpStableTol || Math.Abs(tv.Right - _pfTp1) > PassivePerpStableTol)
                        { _pfTe = te; _pfTf = tf; _pfTp0 = tv.Left; _pfTp1 = tv.Right; _pfOurT = win.Top; }
                    }
                    else { _pfTe = te; _pfTf = tf; _pfTp0 = tv.Left; _pfTp1 = tv.Right; _pfOurT = win.Top; }
                }
                else { _pfT = nt; _pfTe = te; _pfTf = tf; _pfTp0 = tv.Left; _pfTp1 = tv.Right; _pfOurT = win.Top; }
            }
            else _pfT = IntPtr.Zero;
            // Bottom neighbor: NEAR edge = its Top (shared with our Bottom), FAR edge = its Bottom.
            if (haveB)
            {
                if (nb == _pfB)
                {
                    if (allowMove)
                    {
                        int dNear = be - _pfBe, dFar = bf - _pfBf;
                        if (Math.Abs(dFar) <= PassiveFarStableTol && Math.Abs(dNear) >= PassiveMinFollowPx && Math.Abs(dNear) <= PassiveMaxTravelPx
                            && Math.Abs(_pfBe - _pfOurB) <= seamFlushTol
                            && Math.Abs(bv.Left - _pfBp0) <= PassivePerpStableTol && Math.Abs(bv.Right - _pfBp1) <= PassivePerpStableTol)
                        { B = _pfOurB + dNear; moved = true; }
                        else if (Math.Abs(dFar) > PassiveFarStableTol || Math.Abs(bv.Left - _pfBp0) > PassivePerpStableTol || Math.Abs(bv.Right - _pfBp1) > PassivePerpStableTol)
                        { _pfBe = be; _pfBf = bf; _pfBp0 = bv.Left; _pfBp1 = bv.Right; _pfOurB = win.Bottom; }
                    }
                    else { _pfBe = be; _pfBf = bf; _pfBp0 = bv.Left; _pfBp1 = bv.Right; _pfOurB = win.Bottom; }
                }
                else { _pfB = nb; _pfBe = be; _pfBf = bf; _pfBp0 = bv.Left; _pfBp1 = bv.Right; _pfOurB = win.Bottom; }
            }
            else _pfB = IntPtr.Zero;

            _pfHave = true;

            if (!moved)
            {
                if (EnableTroubleshootLog)
                {
                    long now0 = Environment.TickCount64;
                    if (now0 - _lastPfLogTick > 250)
                    {
                        _lastPfLogTick = now0;
                        TsLog($"PfWatch am={allowMove} le={le} lf={lf} baseLe={_pfLe} baseLf={_pfLf} baseOurL={_pfOurL} re={re} nlSame={(nl==_pfL)} haveL={haveL} haveR={haveR} vis=({vis.Left},{vis.Top},{vis.Right},{vis.Bottom}) rf={rf} rv=({rv.Left},{rv.Top},{rv.Right},{rv.Bottom}) baseRe={_pfRe} baseRf={_pfRf} baseOurR={_pfOurR} baseRp0={_pfRp0} baseRp1={_pfRp1}");
                    }
                }
                return;
            }
            if (R - L < SnapFollowMinDimPx || B - T < SnapFollowMinDimPx) return;
            if (L == win.Left && R == win.Right && T == win.Top && B == win.Bottom) return;
            SetWindowPos(hwnd, IntPtr.Zero, L, T, R - L, B - T, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            if (EnableTroubleshootLog)
            {
                long now = Environment.TickCount64;
                if (now - _lastPfLogTick > 120)
                {
                    _lastPfLogTick = now;
                    TsLog($"PfFollow winX=({win.Left},{win.Right})->({L},{R}) winY=({win.Top},{win.Bottom})->({T},{B}) le={le} lf={lf} baseLe={_pfLe} baseLf={_pfLf} re={re} te={te} be={be} rf={rf} rv=({rv.Left},{rv.Top},{rv.Right},{rv.Bottom}) baseRe={_pfRe} baseRf={_pfRf} baseOurR={_pfOurR} baseRp0={_pfRp0} baseRp1={_pfRp1}");
                }
            }
        }

        private void UpdateSnapFollow()
        {
            if (!EnableSnapFollow || !UseThemedSystemFrame) { StopSnapFollow(); return; }
            var hwnd = new WindowInteropHelper(this).Handle;
            bool snapped = hwnd != IntPtr.Zero && WindowState == WindowState.Normal && HasInternalSnapDivider(hwnd);
            if (snapped) StartSnapFollow(); else StopSnapFollow();
        }

        private void StartSnapFollow()
        {
            if (_snapFollowActive) return;
            _snapFollowActive = true;
            if (_snapFollowTimer == null)
            {
                _snapFollowTimer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(15) };
                _snapFollowTimer.Tick += SnapFollow_Tick;
            }
            _snapPrevLBtn = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0; // уже зажатую кнопку не латчим
            _snapDragEdge = 0;
            _snapFollowTimer.Start();
        }

        private void StopSnapFollow()
        {
            if (!_snapFollowActive) return;
            _snapFollowActive = false;
            _snapDragEdge = 0;
            _snapSettleTicks = 0;
            _leftNbr = _rightNbr = _topNbr = _botNbr = IntPtr.Zero;
            _pfL = _pfR = _pfT = _pfB = IntPtr.Zero;
            _pfHave = false;
            _snapFollowTimer?.Stop();
        }

        private void SnapFollow_Tick(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Оверлей-разделитель ведёт собственный joint-drag - SnapFollow не вмешиваем.
            if (_divDragging) { _snapPrevLBtn = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0; _snapDragEdge = 0; return; }
            // Не мешаем собственному мо��альному ��ере��аскиванию/ресайзу (un-snap за шапк����, ручной ресайз).
            if (_inSizeMove || hwnd == IntPtr.Zero || WindowState != WindowState.Normal) { _snapDragEdge = 0; return; }
            if (!TryGetSnapInternalEdges(hwnd, out bool sl, out bool sr, out bool st, out bool sb)) { StopSnapFollow(); return; }
            if (!GetWindowRect(hwnd, out RECT win)) return;
            if (!TryGetVisibleBounds(hwnd, out RECT vis)) return;

            bool lbtn = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            bool justPressed = lbtn && !_snapPrevLBtn;
            bool justReleased = !lbtn && _snapPrevLBtn;
            _snapPrevLBtn = lbtn;
            if (!GetCursorPos(out POINT cur)) return;

            // Отпустили ползунок → ДОВОДКА: ещё SnapSettleTicks тиков держим ЗАЛАЧЕННЫЙ край по соседу, чтобы
            // поймать финально�� доразмещение группы силами shell (иначе появлялся зазор «после» перетягивания).
            if (justReleased && _snapDragEdge != 0) { _snapSettleEdge = _snapDragEdge; _snapSettleTicks = SnapSettleTicks; _snapDragEdge = 0; }

            // На нажатии латчим ��ЛИЖА��ШИЙ ��нутренний край и ФИКСИРУ��М HWND обоих соседей (по разделителям).
            // Дальше тянем каждый край за ��ВОИМ конкретным ��кном-соседом — стабильно, без шумного перепоиска.
            if (lbtn && justPressed && _snapDragEdge == 0)
            {
                bool inX = cur.X >= vis.Left && cur.X <= vis.Right;
                bool inY = cur.Y >= vis.Top && cur.Y <= vis.Bottom;
                int dl = (sl && inY) ? Math.Abs(cur.X - vis.Left) : int.MaxValue;
                int dr = (sr && inY) ? Math.Abs(cur.X - vis.Right) : int.MaxValue;
                int dt = (st && inX) ? Math.Abs(cur.Y - vis.Top) : int.MaxValue;
                int db = (sb && inX) ? Math.Abs(cur.Y - vis.Bottom) : int.MaxValue;
                int dmin = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
                if (dmin <= SnapFollowGrabBandPx)
                {
                    _snapDragEdge = dmin == dr ? 2 : dmin == dl ? 1 : dmin == dt ? 3 : 4;
                    _snapSettleTicks = 0;
                    // БАГ 2b: фильтр вплотную (NeighborTouchPx=12). TryFindSnapNeighbor допускает gap до 400 px —
                    // этого в latch НЕ хватает: после пер��ой joint-resize фоновое окно с близким ребром может победить
                    // настоящего сосе��а по bestGap. Snap-сосед ПО ОПРЕДЕЛЕНИЮ вплотную (0-2 px). Режем сильнее.
                    const int LatchNeighborGapPx = 12;
                    IntPtr lh = IntPtr.Zero, rh = IntPtr.Zero;
                    int lGap = 0, rGap = 0;
                    bool lFound=false, rFound=false;
                    RECT lnv = default, rnv = default;
                    if (sl)
                    {
                        lFound = TryFindSnapNeighbor(hwnd, vis, false, out IntPtr lhCand, out lnv);
                        if (lFound) { lGap = vis.Left - lnv.Right; if (Math.Abs(lGap) <= LatchNeighborGapPx) lh = lhCand; }
                    }
                    if (sr)
                    {
                        rFound = TryFindSnapNeighbor(hwnd, vis, true, out IntPtr rhCand, out rnv);
                        if (rFound) { rGap = rnv.Left - vis.Right; if (Math.Abs(rGap) <= LatchNeighborGapPx) rh = rhCand; }
                    }
                    _leftNbr = lh;
                    _rightNbr = rh;
                    IntPtr th = IntPtr.Zero, bh = IntPtr.Zero;
                    bool tFound = false, bFound = false; int tGap = 0, bGap = 0; RECT tnv = default, bnv = default;
                    // Vertical seam gap can exceed the 12px used for side columns (DWM/caption seam), so use a
                    // wider gate here; fullWidth in TryFindSnapNeighborV already guarantees a real row-neighbor.
                    const int VLatchNeighborGapPx = 64;
                    if (st)
                    {
                        tFound = TryFindSnapNeighborV(hwnd, vis, false, out IntPtr thCand, out tnv);
                        if (tFound) { tGap = vis.Top - tnv.Bottom; if (Math.Abs(tGap) <= VLatchNeighborGapPx) th = thCand; }
                    }
                    if (sb)
                    {
                        bFound = TryFindSnapNeighborV(hwnd, vis, true, out IntPtr bhCand, out bnv);
                        if (bFound) { bGap = bnv.Top - vis.Bottom; if (Math.Abs(bGap) <= VLatchNeighborGapPx) bh = bhCand; }
                    }
                    _topNbr = th;
                    _botNbr = bh;
                    if (EnableTroubleshootLog)
                        TsLog($"SfLatch edge={_snapDragEdge} cur=({cur.X},{cur.Y}) sl={sl} sr={sr} dl={dl} dr={dr} vis=({vis.Left},{vis.Top},{vis.Right},{vis.Bottom}) win=({win.Left},{win.Top},{win.Right},{win.Bottom}) lFound={lFound} lnv=({lnv.Left},{lnv.Top},{lnv.Right},{lnv.Bottom}) lGap={lGap} lhSet={(lh!=IntPtr.Zero)} rFound={rFound} rnv=({rnv.Left},{rnv.Top},{rnv.Right},{rnv.Bottom}) rGap={rGap} rhSet={(rh!=IntPtr.Zero)} st={st} sb={sb} tFound={tFound} tnv=({tnv.Left},{tnv.Top},{tnv.Right},{tnv.Bottom}) tGap={tGap} thSet={(th!=IntPtr.Zero)} bFound={bFound} bnv=({bnv.Left},{bnv.Top},{bnv.Right},{bnv.Bottom}) bGap={bGap} bhSet={(bh!=IntPtr.Zero)}");
                    if (EnableSnapDiagLog)
                        SnapLog($"SnapFollow LATCH edge={_snapDragEdge} cur=({cur.X},{cur.Y}) vis=({vis.Left},{vis.Top},{vis.Right},{vis.Bottom}) Lnbr={_leftNbr:X} Rnbr={_rightNbr:X}");
                }
            }

            // Активны, пока залачен кр��й ИЛИ идёт доводка после отпускания.
            int edge = _snapDragEdge;
            if (edge == 0 && _snapSettleTicks > 0) { edge = _snapSettleEdge; _snapSettleTicks--; }
            if (edge == 0)
            {
                // BUG2 (decisive): no own-edge drag latched. The user may be dragging a shared snap-divider that
                // lies OUTSIDE our window (the seam between two other snapped windows). The OS commits those
                // neighbors bounds only when the drag settles, so we keep evaluating for a settle window AFTER
                // release too. We always run the finder to keep the flush anchor fresh, but only MOVE while the
                // button is held or during the post-release settle window.
                if (!EnablePassiveFollow) { ResetPassiveFollow(); return; }
                if (lbtn) _pfSettleTicks = PassiveSettleTicks;
                else if (_pfSettleTicks > 0) _pfSettleTicks--;
                PassiveFollowNeighbors(hwnd, win, vis, sl, sr, st, sb, lbtn || _pfSettleTicks > 0);
                return;
            }
            ResetPassiveFollow();

            // Каждый наш внутр��нний край тянем за ЕГО зафиксированным соседом (по HWND): статичный сосед → край
            // стоит; уехавший сосед (групповое перераспределение shell) → край следует за ним вплотную. Так нет
            // ни сдвига (мы не двигаем край без причины), ни перекрытия (догоняем уехавшего соседа). Курсор —
            // fallback только для ЗАЛАЧЕННОГО края, если ��го сосед потеря��.
            int ovL = win.Left - vis.Left, ovR = win.Right - vis.Right;
            int L = win.Left, R = win.Right;
            // БАГ 2b: cursor-fallback разрешён ТОЛЬКО если сосед БЫЛ найден на latch (_*Nbr != Zero), но сейчас
            // потерян (минимизирован/cloaked). Если на latch соседа не нашли вовсе — НЕ двигаем свой край за
            // курсором: иначе в 3-колонке наше окно ездит в одиночку, когда shell-полоса (~2 px) не подцепилась,
            // а наша 12-px latch — да. Рез��льтат — наложение/разрыв. Лучше не двинуться, чем разъехаться.
            if (sr)
            {
                if (TryGetTrackedNeighborEdge(_rightNbr, vis, true, out int re)) R = re + ovR;
            }
            if (sl)
            {
                if (TryGetTrackedNeighborEdge(_leftNbr, vis, false, out int le)) L = le + ovL;
            }
            int ovT = win.Top - vis.Top, ovB = win.Bottom - vis.Bottom;
            int T = win.Top, B = win.Bottom;
            if (sb)
            {
                if (TryGetTrackedNeighborEdgeV(_botNbr, vis, true, out int be)) B = be + ovB;
                else if (edge == 4 && lbtn) B = cur.Y + ovB; // fallback: follow the actively dragged bottom seam via cursor
            }
            if (st)
            {
                if (TryGetTrackedNeighborEdgeV(_topNbr, vis, false, out int te)) T = te + ovT;
                else if (edge == 3 && lbtn) T = cur.Y + ovT; // fallback: follow the actively dragged top seam via cursor
            }

            if (R - L < SnapFollowMinDimPx || B - T < SnapFollowMinDimPx) return;
            if (L != win.Left || R != win.Right || T != win.Top || B != win.Bottom)
            {
                if (EnableSnapDiagLog)
                    SnapLog($"SnapFollow MOVE edge={edge} cur.X={cur.X} win=({win.Left},{win.Right})->({L},{R})");
                bool trackedL = _leftNbr != IntPtr.Zero;
                bool trackedR = _rightNbr != IntPtr.Zero;
                if (EnableTroubleshootLog)
                    TsLog($"SfMove edge={edge} cur=({cur.X},{cur.Y}) winX=({win.Left},{win.Right})->({L},{R}) trackedL={trackedL} trackedR={trackedR} settleTicks={_snapSettleTicks} winY=({win.Top},{win.Bottom})->({T},{B}) trackedT={(_topNbr!=IntPtr.Zero)} trackedB={(_botNbr!=IntPtr.Zero)}");
                SetWindowPos(hwnd, IntPtr.Zero, L, T, R - L, B - T,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
        }

        /// <summary>
        /// БАГ 1: ищет соседнее snapped-окно — партнёра ��о внутреннему разделителю (для rightEdge — справа,
        /// иначе слева). Партнёр: в��димое не-cloaked не-tool окно на том же монитор��, с существенным
        /// вертикальным перекрытием, по нужную сторону от нас и наиболее примыкающее (минимальный зазор по X
        /// между нашим краем и его краем). Возвращает его ВИДИМЫЕ границ�� (DWMWA_EXTENDED_FRAME_BOUNDS).
        /// </summary>
        private bool FindSnapNeighbors(IntPtr self, RECT ourVis, bool rightEdge, System.Collections.Generic.List<IntPtr> outHwnds, out int nearEdgeX)
        {
            outHwnds.Clear();
            nearEdgeX = rightEdge ? ourVis.Right : ourVis.Left;
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            int ourTop = ourVis.Top, ourBot = ourVis.Bottom;
            int ourH = Math.Max(1, ourBot - ourTop);
            var cands = new System.Collections.Generic.List<(IntPtr h, RECT v, int gap)>();
            // БАГ 2c: рабочая область нашего монитора — для проверки, что партнёр САМ приснаплен (дальний край у границ�� экрана).
            bool haveWork = TryGetWorkArea(self, out RECT waSelf);

            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT v)) return true;
                if (v.Right - v.Left < 50 || v.Bottom - v.Top < 50) return true;

                // классический snap-сосед: общая сторона той же длины — верх и низ совпадают (иначе это не snap-пара)
                bool withinSpan = v.Top >= ourTop - SnapNeighborEdgeAlignPx && v.Bottom <= ourBot + SnapNeighborEdgeAlignPx;
                bool containsSpan = v.Top <= ourTop + SnapNeighborEdgeAlignPx && v.Bottom >= ourBot - SnapNeighborEdgeAlignPx;
                // BUG2: keep a neighbor that is WITHIN our span (stacked tiles together cover our edge) OR one that
                // CONTAINS our span (a taller full-height neighbor when WE are the sub-tile: our top/bottom tile of
                // a column vs a full-height window on the other side). A window past only one side is still rejected.
                if (!withinSpan && !containsSpan) return true; // плитка цели��ом внутри нашег�� края (с допуском)

                int gap;
                if (rightEdge)
                {
                    if (v.Left <= ourVis.Left) return true;       // парт��ёр справа
                    gap = v.Left - ourVis.Right; // BUG2: знаковый зазор: <=0 перекрытие/вплотную (заведомо смежны), >0 насто��щий разрыв
                }
                else
                {
                    if (v.Right >= ourVis.Right) return true;     // партнёр слева
                    gap = ourVis.Left - v.Right; // BUG2: знаковый зазор: <=0 перекрытие/вплотную (заведомо смежны), >0 настоящий разрыв
                }
                if (gap > SnapNeighborMaxGapPx) return true;                    // слишком далеко — не партнёр по разделителю
                if (haveWork)
                {
                    // партнёр должен ДОСТАВАТЬ до дальней границы рабочей области (последняя плитка колонки).
                    // СВЕС за край экрана ��опустим (OS/наш дрейф мог вытолкнуть дальний край наружу -
                    // мы его потом прибиваем обратно). Отвергаем ТОЛЬКО если дальний край НЕ ДОТЯГИВАЕТ (окно посреди экрана).
                    bool reachesEdge = rightEdge
                        ? v.Right >= waSelf.Right - SnapNeighborEdgeAlignPx
                        : v.Left <= waSelf.Left + SnapNeighborEdgeAlignPx;
                    if (!reachesEdge) return true;
                }
                cands.Add((h, v, gap));
                return true;
            }, IntPtr.Zero);

            if (cands.Count == 0) { LogNearestRejectedNeighbor(self, ourVis, rightEdge); return false; }
            // BUG2: сначала берём ОДИНОЧНОГО соседа, закрывающего наш к��ай на ПОЛНУЮ высоту (обычный случай
            // 2 окон рядом / 3 колонок). Лишние окна, вертикально вложенные в наш край (оверлеи/диалоги/
            // фоновые окна у края экрана), НЕ должны ломать обнаружение настоящего полно-высотного соседа.
            {
                int bestGapF = int.MaxValue; IntPtr bestHF = IntPtr.Zero; RECT bestVF = default;
                foreach (var c in cands)
                {
                    bool full = c.v.Top <= ourTop + SnapNeighborEdgeAlignPx && c.v.Bottom >= ourBot - SnapNeighborEdgeAlignPx;
                    if (full && Math.Abs(c.gap) < Math.Abs(bestGapF)) { bestGapF = c.gap; bestHF = c.h; bestVF = c.v; }
                }
                if (bestHF != IntPtr.Zero)
                {
                    outHwnds.Add(bestHF);
                    nearEdgeX = rightEdge ? bestVF.Left : bestVF.Right;
                    return true;
                }
            }
            // кандидаты должны ВМЕСТЕ замостить наш край: объединение их вертикальных интервалов ~= наша высота.
            // одиночное окно, закрывающее лишь часть ��рая, набор не образует (исключаем случайные окна рядом).
            cands.Sort((a, b) => a.v.Top.CompareTo(b.v.Top));
            // БАГ 2c: сосед(и) тащатся вместе с нами ТОЛЬКО если их прилегающие стороны ТОЧНО мостят наш край
            // без перекрытий и зазоров: одно окно с равной и полностью прилегающей стороной, либо набор окон,
            // чьи стороны в сумме = нашей (stacked). Любое ли��нее/перекрывающее окно ломает мозаику -> НЕ партнёр.
            // Так случайное чужое окно у границы (в т.ч. половинной выс��ты) в группу НЕ попадает.
            const int tileTolPx = SnapNeighborEdgeAlignPx;
            int expectedTop = ourTop;
            bool tiled = true;
            foreach (var c in cands)
            {
                int t = Math.Max(c.v.Top, ourTop), b2 = Math.Min(c.v.Bottom, ourBot);
                if (b2 <= t) { tiled = false; break; }
                if (Math.Abs(t - expectedTop) > tileTolPx) { tiled = false; break; }
                expectedTop = b2;
            }
            // BUG2: НЕ объединяем несколько окон в мозаику по нашему краю. Это дав��ло ложные захваты: постороннее
            // маленькое окно снизу-сбоку попадало в группу и тащилось за нами (оставаясь не вплотную). Реальный
            // snap-сосед (2 окна рядом / 3 колонки) - это одиночное окно на полную высоту, его берёт ветка выше.
            // BUG2 (regression fix): a genuine stacked tiling - e.g. two half-height windows covering our full
            // edge (top + bottom) - IS a valid neighbor group and MUST follow us during joint-resize. Reject only
            // when the candidates do not CONTIGUOUSLY tile our full height (stray / partial / overlapping windows).
            // The single full-height neighbor (2 windows side-by-side / 3 columns) is taken by the branch above.
            if (!tiled || Math.Abs(expectedTop - ourBot) > tileTolPx)
            {
                LogNearestRejectedNeighbor(self, ourVis, rightEdge); return false;
            }
            int bestGap = int.MaxValue;
            foreach (var c in cands)
            {
                outHwnds.Add(c.h);
                if (c.gap < bestGap) { bestGap = c.gap; nearEdgeX = rightEdge ? c.v.Left : c.v.Right; }
            }
            return true;
        }

        // Ближайший ОДИНОЧНЫЙ snap-сосед (для SnapFollow-латча при OS-перетаскивании нашего окна): допускает gap до 400px,
        // латч сам сужает до ±12px. Отдельно от FindSnapNeighbors (joint-resize разделителя).
        private bool TryFindSnapNeighbor(IntPtr self, RECT ourVis, bool rightEdge, out IntPtr neighborHwnd, out RECT neighborVis)
        {
            neighborHwnd = IntPtr.Zero;
            neighborVis = default;
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            int ourH = Math.Max(1, ourVis.Bottom - ourVis.Top);
            RECT best = default;
            IntPtr bestHwnd = IntPtr.Zero;
            bool found = false;
            int bestGap = int.MaxValue;
            // ДИАГНОСТИКА (БАГ 2c): ближайший по зазору кандидат, прошедший базовые фильтры (видимый/не-cloaked/
            // не-tool/тот же монитор/размер>=50). Нужен, чтобы при промахе видеть РЕАЛЬНУЮ позицию соседа.
            RECT diagV = default; int diagGap = int.MaxValue, diagOverlap = 0; bool diagSideOk = false, diagSet = false;

            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT v)) return true;
                if (v.Right - v.Left < 50 || v.Bottom - v.Top < 50) return true;

                int overlap = Math.Min(v.Bottom, ourVis.Bottom) - Math.Max(v.Top, ourVis.Top);
                int rawGap = rightEdge ? Math.Abs(v.Left - ourVis.Right) : Math.Abs(v.Right - ourVis.Left);
                bool sideOk = rightEdge ? (v.Left > ourVis.Left) : (v.Right < ourVis.Right);
                if (rawGap < diagGap) { diagGap = rawGap; diagV = v; diagOverlap = overlap; diagSideOk = sideOk; diagSet = true; }

                // BUG2: a divider neighbor is ALWAYS a full-height column - top/bottom match ours within
                // SnapNeighborEdgeAlignPx. A partially overlapping window (e.g. the small bottom window whose
                // edge does not actually touch ours) is NOT a neighbor, otherwise it magnetizes to us.
                bool fullHeight = v.Top <= ourVis.Top + SnapNeighborEdgeAlignPx && v.Bottom >= ourVis.Bottom - SnapNeighborEdgeAlignPx;
                if (!fullHeight) return true;
                if (!sideOk) return true;
                if (rawGap > 400) return true;
                if (rawGap < bestGap) { bestGap = rawGap; best = v; bestHwnd = h; found = true; }
                return true;
            }, IntPtr.Zero);

            if (EnableTroubleshootLog && !found)
                TsLog($"TfsnMiss rightEdge={rightEdge} our=({ourVis.Left},{ourVis.Right}) ourHhalf={ourH / 2} diagSet={diagSet} diagGap={diagGap} diagV=({diagV.Left},{diagV.Top},{diagV.Right},{diagV.Bottom}) diagOverlap={diagOverlap} diagSideOk={diagSideOk}");

            neighborHwnd = bestHwnd;
            neighborVis = best;
            return found;
        }

        /// <summary>
        /// БАГ 1: текущая видимая граница ЗАФИКСИРОВАННОГО (по HWND) соседа, если о�� ещё валиден (виден, не
        /// cloaked, вертикально перекрывает нас). rightSide=true → его ЛЕВАЯ граница (мы слева от него),
        /// ин������че его ПРАВАЯ. Возвращает false, если сосед ��отерян/уехал — тогда соответствующий край держим.
        /// </summary>
        private bool TryGetTrackedNeighborEdge(IntPtr nbr, RECT ourVis, bool rightSide, out int edgeX)
        {
            edgeX = 0;
            if (nbr == IntPtr.Zero || !IsWindowVisible(nbr)) return false;
            if (DwmGetWindowAttribute(nbr, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            if (!TryGetVisibleBounds(nbr, out RECT v)) return false;
            int overlap = Math.Min(v.Bottom, ourVis.Bottom) - Math.Max(v.Top, ourVis.Top);
            if (overlap < Math.Max(1, ourVis.Bottom - ourVis.Top) / 2) return false;
            edgeX = rightSide ? v.Left : v.Right;
            return true;
        }

        // Vertical mirror of TryFindSnapNeighbor: finds a full-WIDTH row neighbor above/below.
        private bool TryFindSnapNeighborV(IntPtr self, RECT ourVis, bool bottomEdge, out IntPtr neighborHwnd, out RECT neighborVis)
        {
            neighborHwnd = IntPtr.Zero;
            neighborVis = default;
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            RECT best = default;
            IntPtr bestHwnd = IntPtr.Zero;
            bool found = false;
            int bestGap = int.MaxValue;
            EnumWindows((h, _) =>
            {
                if (h == self || !IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != selfMon) return true;
                if (!TryGetVisibleBounds(h, out RECT v)) return true;
                if (v.Right - v.Left < 50 || v.Bottom - v.Top < 50) return true;
                int rawGap = bottomEdge ? Math.Abs(v.Top - ourVis.Bottom) : Math.Abs(v.Bottom - ourVis.Top);
                bool sideOk = bottomEdge ? (v.Top > ourVis.Top) : (v.Bottom < ourVis.Bottom);
                bool fullWidth = v.Left <= ourVis.Left + SnapNeighborEdgeAlignPx && v.Right >= ourVis.Right - SnapNeighborEdgeAlignPx;
                if (!fullWidth) return true;
                if (!sideOk) return true;
                if (rawGap > 400) return true;
                if (rawGap < bestGap) { bestGap = rawGap; best = v; bestHwnd = h; found = true; }
                return true;
            }, IntPtr.Zero);
            neighborHwnd = bestHwnd;
            neighborVis = best;
            return found;
        }

        // Vertical mirror of TryGetTrackedNeighborEdge: current shared Y edge of the tracked neighbor.
        private bool TryGetTrackedNeighborEdgeV(IntPtr nbr, RECT ourVis, bool bottomSide, out int edgeY)
        {
            edgeY = 0;
            if (nbr == IntPtr.Zero || !IsWindowVisible(nbr)) return false;
            if (DwmGetWindowAttribute(nbr, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            if (!TryGetVisibleBounds(nbr, out RECT v)) return false;
            int overlap = Math.Min(v.Right, ourVis.Right) - Math.Max(v.Left, ourVis.Left);
            if (overlap < Math.Max(1, ourVis.Right - ourVis.Left) / 2) return false;
            edgeY = bottomSide ? v.Top : v.Bottom;
            return true;
        }

        private static bool TryGetVisibleBounds(IntPtr hwnd, out RECT rect)
        {
            rect = default;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0) return false;
            rect = r;
            return true;
        }

        // Запас вокруг видимого окна, чтобы маска ПОЛНОСТЬЮ накрывала DWM-тень и анимировала её
        // вместе с окном (иначе тень мелькала бы мгновенно на 1-м кадре, вне анимации). Должен
        // превышать реальный вылет тени, иначе маска режет тень -> видимая рамка. Физ. px.
        // Запас маски под тень — НЕСИММЕТРИЧНый (замеры по скринам: бока ~30px, низ ~80px;
        // слабый контур на краю маски виден только снизу, поэтому там запас больше). Физ. px.
        private const int MaskShadowMarginLeft = 48;
        private const int MaskShadowMarginTop = 40;
        private const int MaskShadowMarginRight = 48;
        private const int MaskShadowMarginBottom = 140;

        /// <summary>
        /// Прямоугольник скриншот-маски: ВИДИМЫЕ границы (DWMWA_EXTENDED_FRAME_BOUNDS), РАСШИРЕННЫЕ
        /// на запас тени (см. MaskShadowMargin* ��о сторонам), чтобы DWM-тень окна анимировалась вместе с ним. Откат
        /// на GetWindowRect. За краем тени снимок совпадает с десктопом -> шва нет.
        /// </summary>
        private static bool TryGetMaskRect(IntPtr hwnd, out RECT rect)
        {
            if (!(TryGetVisibleBounds(hwnd, out rect) && rect.Right > rect.Left && rect.Bottom > rect.Top) &&
                !(GetWindowRect(hwnd, out rect) && rect.Right > rect.Left && rect.Bottom > rect.Top))
                return false;
            rect.Left -= MaskShadowMarginLeft;
            rect.Top -= MaskShadowMarginTop;
            rect.Right += MaskShadowMarginRight;
            rect.Bottom += MaskShadowMarginBottom;
            return true;
        }
        #endregion

        /// <summary>Рабочая область монитора окна (физ. px) — для ���рижатия клиента к границе при snap.</summary>
        private static bool TryGetWorkArea(IntPtr hwnd, out RECT work)
        {
            work = default;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            work = mi.rcWork;
            return true;
        }

        /// <summary>
        /// ��УТЬ A: true, если точка WM_NCHITTEST (экран, физ. px) лежит ЗА пр��делами монитора, на котором сейчас
        /// окно (т.е. на соседнем мониторе). Используется, чтобы наш внешний sizing-выступ не перехватывал хиты
        /// на соседнем мониторе (HTTRANSPARENT). Берём rcMonitor ИМЕННО монитора окна (MonitorFromWindow), а не
        /// монитора под курсором.
        /// </summary>
        private bool IsOutsideCurrentMonitor(IntPtr hwnd, IntPtr lParam)
        {
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;

            int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
            RECT m = mi.rcMonitor;
            return x < m.Left || x >= m.Right || y < m.Top || y >= m.Bottom;
        }

        /// <summary>
        /// Убирает «дребезг» ����р��тивоположного края при ресайзе з�� ВЕРХ/ЛЕВ��.
        /// <para>
        /// При таком ресайзе Windows делает BitBlt — копирует старый ��а��р, считая опорой
        /// верхне-левый уго����. WPF красит содержимое на своём render-потоке асинхронно, и
        /// копия расси��хронизируется с новой рамкой → нижний/пра��ый край коле��лется. (При
        /// ресайзе за низ/право копи��ования нет — опора неподвижна, поэтому там гладко.)
        /// </para>
        /// <para>
        /// Флаг <c>SWP_NOCOPYBITS</c> на <c>WM_WINDOWPOSCHANGING</c> пода��ляет этот BitBlt:
        /// окно перерисовывается целиком вместо копирования старого кадра. Сообщение НЕ
        /// помечаем handled — ��аём ему дойти до DefWindowProc с уже изменёнными флагами.
        /// Это реше����ие без glass frame (баги dotnet/wpf #1176/#3193): нет белой рамки/вспышки.
        /// </para>
        /// </summary>
        private static void SuppressResizeBitBlt(IntPtr lParam)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((wp.flags & SWP_NOSIZE) == 0) // только когда реально меняе��ся размер
            {
                wp.flags |= SWP_NOCOPYBITS;
                Marshal.StructureToPtr(wp, lParam, false);
            }
        }

        /// <summary>true, если дан��ое WM_WINDOWPOSCHANGING меняет размер окна (а не только позицию).</summary>
        private static bool IsResizePosChange(IntPtr lParam)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            return (wp.flags & SWP_NOSIZE) == 0;
        }

        /// <summary>
        /// ЧАСТЬ 1.1 (анти-скачок свободного края). На WM_SIZING правит предлагаемый прямоугольник (lParam):
        /// держит НЕ-перетягиваемые края на якоре <see cref="_sizeAnchor"/> (снят на WM_ENTERSIZEMOVE).
        /// <para>
        /// Зачем: при ручном ресайзе snapped-окна Windows на старте un-снапит его и ВОССТАНАВ��ИВАЕТ п��ед-снап
        /// floating-ширину — НЕ-перетягиваемый край прыгает (лог: ENTERSIZEMOVE left=−8 → первый SIZING
        /// left=228), и окно СДВИГАЕТСЯ, а не расширяется на месте. Фиксируя нетянущиеся кр��я на якоре,
        /// окно растёт из неподвижного угла. Для обычного (НЕ snapped) окна метод не вы��ывается
        /// (<see cref="_sizeAnchorValid"/>=false), поэтому штатный ресайз не затронут.
        /// </para>
        /// <para>wParam=WMSZ_* — какой край/угол тянут; lParam → RECT (экранные px, in/out). Возвращает true,
        /// если прямоугольник изменён (тогда WindowProc вернёт TRUE).</para>
        /// </summary>
        private bool AnchorUnsnapResize(IntPtr wParam, IntPtr lParam)
        {
            int edge = wParam.ToInt32();
            bool dragLeft = edge == WMSZ_LEFT || edge == WMSZ_TOPLEFT || edge == WMSZ_BOTTOMLEFT;
            bool dragRight = edge == WMSZ_RIGHT || edge == WMSZ_TOPRIGHT || edge == WMSZ_BOTTOMRIGHT;
            bool dragTop = edge == WMSZ_TOP || edge == WMSZ_TOPLEFT || edge == WMSZ_TOPRIGHT;
            bool dragBottom = edge == WMSZ_BOTTOM || edge == WMSZ_BOTTOMLEFT || edge == WMSZ_BOTTOMRIGHT;

            var rc = Marshal.PtrToStructure<RECT>(lParam);
            var before = rc;
            // Каждый край, который пользователь НЕ тянет, возвращаем на якорь — un-snap-restore его не сдвинет.
            if (!dragLeft) rc.Left = _sizeAnchor.Left;
            if (!dragRight) rc.Right = _sizeAnchor.Right;
            if (!dragTop) rc.Top = _sizeAnchor.Top;
            if (!dragBottom) rc.Bottom = _sizeAnchor.Bottom;

            if (rc.Left == before.Left && rc.Right == before.Right &&
                rc.Top == before.Top && rc.Bottom == before.Bottom)
                return false; // ничего не правили (плавающий ресайз и т.п.) — пусть идёт штатно

            Marshal.StructureToPtr(rc, lParam, false);
            if (EnableSnapDiagLog)
                SnapLog($"AnchorUnsnap FIX edge={edge} rc=({before.Left},{before.Top},{before.Right},{before.Bottom})->({rc.Left},{rc.Top},{rc.Right},{rc.Bottom})");
            return true;
        }

        #region Caption un-snap restore drag (ЗАДАЧА 3)

        private bool TryBeginCaptionUnsnapRestoreDrag(MouseButtonEventArgs e)
        {
            if (!EnableCaptionUnsnapRestoreDrag || e.ClickCount != 1)
                return false;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return false;
            if (!GetCursorPos(out POINT cur)) return false;
            if (!GetWindowRect(hwnd, out RECT win)) return false;

            // Максимально узкий вход: snapped full-height окно или maximized окно.
            // Для snapped ждём вниз >=15 px, но горизонтальный drag отдаём обратно shell,
            // чтобы сохранить стан��артное скольжение snapped-окна вправо/вле��о с прежней вы��отой.
            bool cand = IsCaptionUnsnapRestoreCandidate(hwnd, win);
            RECT restore = default;
            bool gotRestore = cand && TryGetCaptionRestoreRect(hwnd, win, out restore);
            if (EnableTroubleshootLog)
            {
                var wpDbg = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                bool gotWp = GetWindowPlacement(hwnd, ref wpDbg);
                var np = gotWp ? wpDbg.normalPosition : default;
                TsLog($"CapDown state={WindowState} cur=({cur.X},{cur.Y}) win=({win.Left},{win.Top},{win.Right},{win.Bottom}) cand={cand} cacheValid={_lastFloatingRestoreRectValid} cache=({_lastFloatingRestoreRect.Left},{_lastFloatingRestoreRect.Top},{_lastFloatingRestoreRect.Right},{_lastFloatingRestoreRect.Bottom}) wpOk={gotWp} np=({np.Left},{np.Top},{np.Right},{np.Bottom}) gotRestore={gotRestore} restore=({restore.Left},{restore.Top},{restore.Right},{restore.Bottom}) hasInternalDiv={HasInternalSnapDivider(hwnd)}");
            }
            if (!cand) return false;
            if (!gotRestore) return false;

            _captionUnsnapPending = true;
            _captionUnsnapDragging = false;
            _captionUnsnapHandoffToShell = false;
            _captionUnsnapDownPt = cur;
            _captionUnsnapDownRect = win;
            _captionUnsnapRestoreRect = restore;
            // БАГ 1: durable-arm для реакти��ного WinEvent-restore (на ��лучай, если жест ОС перехватит capture
            // и наш ручной move-loop не отработает). См. OnUnsnapWinEvent / FinishUnsnapWinEventRestore.
            ArmUnsnapWinEventRestore(cur, win, restore);
            _captionUnsnapMoveTickCount = 0;
            CaptureMouse();
            try { var src = (HwndSource)PresentationSource.FromVisual(this); if (src != null) SetCapture(src.Handle); } catch { }
            HideEdgeGrip();

            // СТРАХОВКА: если WPF теряет mouse capture, OnMouseMove не прих��дит и порог 30 px не проверяется.
            // Поднимаем фоновый таймер с тем же решателем, что и OnMouseMove — этот путь не ��ависит от WPF входа.
            if (_captionUnsnapWatchdog == null)
            {
                _captionUnsnapWatchdog = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(15) };
                _captionUnsnapWatchdog.Tick += CaptionUnsnapWatchdog_Tick;
            }
            _captionUnsnapWatchdog.Start();

            if (EnableSnapDiagLog)
                SnapLog($"CaptionUnsnap ARM cur=({cur.X},{cur.Y}) snap=({win.Left},{win.Top},{win.Right},{win.Bottom}) restore=({restore.Left},{restore.Top},{restore.Right},{restore.Bottom}) state={WindowState}");
            return true;
        }

        private void LogFirstUpdate(POINT cur)
        {
            if (!EnableTroubleshootLog) return;
            if (_captionUnsnapMoveTickCount > 1) return;
            TsLog($"UpdFirst cur=({cur.X},{cur.Y}) down=({_captionUnsnapDownPt.X},{_captionUnsnapDownPt.Y}) pending={_captionUnsnapPending} dragging={_captionUnsnapDragging}");
        }

        private bool UpdateCaptionUnsnapRestoreDrag()
        {
            if (!_captionUnsnapPending && !_captionUnsnapDragging)
                return false;

            if (_captionUnsnapHandoffToShell)
                return false;

            if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
            {
                EndCaptionUnsnapRestoreDrag(source: "lbutton-released");
                return true;
            }
            if (!GetCursorPos(out POINT cur))
                return true;
            _captionUnsnapMoveTickCount++;
            LogFirstUpdate(cur);

            if (_captionUnsnapPending)
            {
                int dx = cur.X - _captionUnsnapDownPt.X;
                int dy = cur.Y - _captionUnsnapDownPt.Y;
                int absDx = Math.Abs(dx);

                // ВАЖНО: snapped full-height окно должно по-прежн��му скользить вправо/влево стандартным
                // способом Windows, сохраняя высоту. Поэтому если жест явно горизонтальный и ещё не достиг
                // порога "стянуть вниз", прекращаем наш pending path и передаём drag обратно shell.
                if (WindowState == WindowState.Normal && dy < CaptionUnsnapRestoreThresholdPxY() &&
                    absDx >= CaptionUnsnapRestoreThresholdPxX() && absDx > Math.Max(0, dy))
                {
                    HandoffCaptionDragToShell(cur);
                    return true;
                }

                if (dy < CaptionUnsnapRestoreThresholdPxY())
                    return true;

                if (EnableTroubleshootLog)
                    TsLog($"CapThreshold dy={dy} dx={dx} cur=({cur.X},{cur.Y}) ticks={_captionUnsnapMoveTickCount}");
                BeginCaptionUnsnapManualMove(cur);
            }

            if (_captionUnsnapDragging)
                MoveCaptionUnsnapRestoredWindow(cur);

            return true;
        }

        private void BeginCaptionUnsnapManualMove(POINT cur)
        {
            _captionUnsnapPending = false;
            _captionUnsnapDragging = true;
            // БАГ 1: наш ручной откат выиграл гонку у жеста ОС, снимаем WinEvent-страховку, чтобы она потом
            // не сработала пов��орно поверх уже восстановленного окна.
            _unsnapArmValid = false;

            int snapW = Math.Max(1, _captionUnsnapDownRect.Right - _captionUnsnapDownRect.Left);
            int restoreW = Math.Max(1, _captionUnsnapRestoreRect.Right - _captionUnsnapRestoreRect.Left);
            int clickX = Math.Clamp(_captionUnsnapDownPt.X - _captionUnsnapDownRect.Left, 0, snapW);

            // По X берём пропорцию внутри snapped/maximized ширины, как делает Windows при restore:
            // курсор остаётся над той же относительной частью шапки. По Y сохраняем фактический caption-offset.
            _captionUnsnapDragOffsetX = Math.Clamp((int)Math.Round((double)clickX * restoreW / snapW), 0, restoreW - 1);
            _captionUnsnapDragOffsetY = Math.Clamp(_captionUnsnapDownPt.Y - _captionUnsnapDownRect.Top, 0,
                Math.Max(0, _captionUnsnapRestoreRect.Bottom - _captionUnsnapRestoreRect.Top - 1));

            // Для maximized сначала переводим WPF-состояни�� обратно в Normal, иначе WPF может пытаться
            // удерживать maximized placement поверх нашего SetWindowPos.
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;

            if (EnableSnapDiagLog)
                SnapLog($"CaptionUnsnap RESTORE threshold cur=({cur.X},{cur.Y}) offset=({_captionUnsnapDragOffsetX},{_captionUnsnapDragOffsetY})");
            if (EnableTroubleshootLog)
                TsLog($"CapRestoreMove cur=({cur.X},{cur.Y}) restore=({_captionUnsnapRestoreRect.Left},{_captionUnsnapRestoreRect.Top},{_captionUnsnapRestoreRect.Right},{_captionUnsnapRestoreRect.Bottom}) snap=({_captionUnsnapDownRect.Left},{_captionUnsnapDownRect.Top},{_captionUnsnapDownRect.Right},{_captionUnsnapDownRect.Bottom}) offset=({_captionUnsnapDragOffsetX},{_captionUnsnapDragOffsetY})");

            MoveCaptionUnsnapRestoredWindow(cur);
        }

        private void MoveCaptionUnsnapRestoredWindow(POINT cur)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int w = Math.Max(1, _captionUnsnapRestoreRect.Right - _captionUnsnapRestoreRect.Left);
            int h = Math.Max(1, _captionUnsnapRestoreRect.Bottom - _captionUnsnapRestoreRect.Top);
            int x = cur.X - _captionUnsnapDragOffsetX;
            int y = cur.Y - _captionUnsnapDragOffsetY;

            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        private void EndCaptionUnsnapRestoreDrag(bool releaseCapture = true, string source = "?")
        {
            bool wasActive = _captionUnsnapPending || _captionUnsnapDragging || _captionUnsnapHandoffToShell;
            bool wasPending = _captionUnsnapPending;
            bool wasDragging = _captionUnsnapDragging;
            _captionUnsnapPending = false;
            _captionUnsnapDragging = false;
            _captionUnsnapHandoffToShell = false;
            if (_captionUnsnapWatchdog != null)
            {
                _captionUnsnapWatchdog.Stop();
            }
            if (releaseCapture && IsMouseCaptured)
                ReleaseMouseCapture();
            if (releaseCapture && wasActive)
                ReleaseCapture();

            if (wasActive)
            {
                RefreshEdgeGrip();
                if (EnableSnapDiagLog)
                    SnapLog("CaptionUnsnap END");
                if (EnableTroubleshootLog)
                    TsLog($"CapEnd src={source} wasPending={wasPending} wasDragging={wasDragging} ticks={_captionUnsnapMoveTickCount} releaseCapture={releaseCapture}");
            }
            _captionUnsnapMoveTickCount = 0;
        }

        private void CaptionUnsnapWatchdog_Tick(object? sender, EventArgs e)
        {
            if (!_captionUnsnapPending && !_captionUnsnapDragging)
            {
                _captionUnsnapWatchdog?.Stop();
                return;
            }
            _captionUnsnapMoveTickCount++;
            UpdateCaptionUnsnapRestoreDrag();
        }

        private void HandoffCaptionDragToShell(POINT cur)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                EndCaptionUnsnapRestoreDrag(source: "handoff-no-hwnd");
                return;
            }

            _captionUnsnapPending = false;
            _captionUnsnapDragging = false;
            _captionUnsnapHandoffToShell = true;

            if (IsMouseCaptured)
                ReleaseMouseCapture();

            if (EnableSnapDiagLog)
                SnapLog($"CaptionUnsnap HANDOFF shell cur=({cur.X},{cur.Y})");

            ReleaseCapture();
            SendMessageW(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, MakeScreenLParam(cur.X, cur.Y));

            _captionUnsnapHandoffToShell = false;
            RefreshEdgeGrip();
        }

        private static IntPtr MakeScreenLParam(int x, int y)
        {
            unchecked
            {
                return new IntPtr((int)(((uint)(ushort)x) | ((uint)(ushort)y << 16)));
            }
        }

        private bool IsCaptionUnsnapRestoreCandidate(IntPtr hwnd)
        {
            return GetWindowRect(hwnd, out RECT win) && IsCaptionUnsnapRestoreCandidate(hwnd, win);
        }

        private bool IsCaptionUnsnapRestoreCandidate(IntPtr hwnd, RECT win)
        {
            if (!EnableCaptionUnsnapRestoreDrag || !UseThemedSystemFrame)
                return false;

            if (!IsWindowAtLeastCurrentMonitorHeight(hwnd, win))
                return false;

            // Maximized тоже должен уметь "стягиваться" за шапку вниз.
            if (WindowState == WindowState.Maximized)
                return true;

            // Full-height окно — кандидат и БЕЗ внутреннего snap-divider. После отрыва от snap-группы
            // (напр. когда предыдущая протяжка вниз не дотянула до порога) делитель исчезает, но окно
            // остаётся полноэкранной высоты. Height-gate выше уже пройден, поэтому здесь достаточно Normal.
            // Это устраняет рассинхрон с WinEvent-restore, который уже опирается на высоту, а не на делитель.
            // Ложные срабатывания отсекает TryGetCaptionRestoreRect (нужен валидный МЕНЬШИЙ pre-snap размер),
            // а не наличие делителя.
            return WindowState == WindowState.Normal;
        }

        private bool IsWindowAtLeastCurrentMonitorHeight(IntPtr hwnd, RECT win)
        {
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;

            // В обычном режиме Snap растягивает окно до rcWork, то есть без панели задач.
            // При auto-hide панель может дать высоту rcMonitor. Поэтому прин��маем оба full-height варианта.
            int monitorH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            int workH = mi.rcWork.Bottom - mi.rcWork.Top;
            int fullHeight = Math.Min(monitorH, workH);
            return (win.Bottom - win.Top) >= fullHeight;
        }

        // Порог un-snap-restore хранится в DIP; переводим в физ. px под текущий DPI окна, т.к. dx/dy
        // считаются из GetCursorPos (физ. px). Отдельно по осям — на случай анизотропного DPI.
        private int CaptionUnsnapRestoreThresholdPxX()
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            return Math.Max(1, (int)Math.Round(CaptionUnsnapRestoreThresholdDip * dpi.DpiScaleX));
        }

        private int CaptionUnsnapRestoreThresholdPxY()
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            return Math.Max(1, (int)Math.Round(CaptionUnsnapRestoreThresholdDip * dpi.DpiScaleY));
        }

        private bool TryGetCaptionRestoreRect(IntPtr hwnd, RECT current, out RECT restore)
        {
            restore = default;

            // БАГ 1 (исправление): ПРИОРИТЕТ — сначала наш кэш (_lastFloatingRestoreRect), потом WP.normalPosition.
            // Кэш обновляется ТОЛЬКО в floating-состоянии (!HasInternalSnapDivider) — это авторитетный pre-snap размер.
            // normalPosition Windows же обновляет при любом SetWindowPos — SnapFollow в snapped-состоянии «отравляет» его
            // текущим snapped-rect'ом. Используем его только как fallback, если кэша нет (первый запуск, ещё не было floating).
            if (_lastFloatingRestoreRectValid && IsUsableCaptionRestoreRect(hwnd, _lastFloatingRestoreRect, current))
            {
                restore = _lastFloatingRestoreRect;
                if (EnableTroubleshootLog) TsLog($"GetRestore PICK=cache rc=({restore.Left},{restore.Top},{restore.Right},{restore.Bottom})");
                return true;
            }

            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            bool wpOk = GetWindowPlacement(hwnd, ref wp);
            bool wpUsable = wpOk && IsUsableCaptionRestoreRect(hwnd, wp.normalPosition, current);
            if (wpUsable)
            {
                restore = wp.normalPosition;
                if (EnableTroubleshootLog) TsLog($"GetRestore PICK=wp rc=({restore.Left},{restore.Top},{restore.Right},{restore.Bottom})");
                return true;
            }

            if (EnableTroubleshootLog)
                TsLog($"GetRestore PICK=none cacheValid={_lastFloatingRestoreRectValid} cache=({_lastFloatingRestoreRect.Left},{_lastFloatingRestoreRect.Top},{_lastFloatingRestoreRect.Right},{_lastFloatingRestoreRect.Bottom}) wpOk={wpOk} np=({wp.normalPosition.Left},{wp.normalPosition.Top},{wp.normalPosition.Right},{wp.normalPosition.Bottom}) current=({current.Left},{current.Top},{current.Right},{current.Bottom})");
            return false;
        }

        private bool IsUsableCaptionRestoreRect(IntPtr hwnd, RECT rc, RECT current)
        {
            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (w < 100 || h < 100) return false;

            // Не восстанавливаем в тот же snapped прямоугольник: нужен именно прежний floating size.
            if (Math.Abs(w - (current.Right - current.Left)) <= 2 &&
                Math.Abs(h - (current.Bottom - current.Top)) <= 2)
                return false;

            // БАГ 1 (точечное исправление): Отвергаем кан��идата с высотой ≈ текущ��й (±4 px). Это РОВНО попадает
            // в polluted normalPosition: SnapFollow.SetWindowPos в snapped-состоянии сохраняет вы��оту (меняет
            // только ширину), и Windows ��б��овляет normalPosition c такой же высотой, как current. floating-кэш
            // почти всегда имеет другую высоту (pre-snap floating != full-work-height) и проходит этот фильтр.
            if (Math.Abs(h - (current.Bottom - current.Top)) <= 4)
                return false;
            return true;
        }

        private void UpdateCaptionUnsnapRestoreCache(IntPtr hwnd)
        {
            if (!EnableCaptionUnsnapRestoreDrag || hwnd == IntPtr.Zero || WindowState != WindowState.Normal)
                return;
            if (!GetWindowRect(hwnd, out RECT win)) return;
            if (HasInternalSnapDivider(hwnd))
                return;

            // НЕ кэшируем full-height rect как pre-snap floating размер. Если окно оторвано от snap-группы,
            // но осталось полноэкранной высоты, делителя уже нет — без этой проверки кэш затирался бы
            // full-height'ом, и восстановить прежний размер становилось невозможно (restore-rect ≈ current →
            // отбраковка в IsUsableCaptionRestoreRect). Держим последний НАСТОЯЩИЙ (суб-full-height) floating размер.
            if (IsWindowAtLeastCurrentMonitorHeight(hwnd, win))
                return;

            bool wasValid = _lastFloatingRestoreRectValid;
            RECT prev = _lastFloatingRestoreRect;
            _lastFloatingRestoreRect = win;
            _lastFloatingRestoreRectValid = true;
            if (EnableTroubleshootLog && (!wasValid || prev.Left!=win.Left || prev.Top!=win.Top || prev.Right!=win.Right || prev.Bottom!=win.Bottom))
                TsLog($"CacheSet win=({win.Left},{win.Top},{win.Right},{win.Bottom}) prevValid={wasValid} prev=({prev.Left},{prev.Top},{prev.Right},{prev.Bottom}) hasInternalDiv={HasInternalSnapDivider(hwnd)}");
        }

        // ===================== БАГ 1: реактивный WinEvent-restore =====================
        // OS-level жес�� Win11 «unsnap-drag» крадёт mouse-capture служебным окном (лог: WmCaptureChanged
        // newCap=0xC509E4 через ~1мс после нашего CapDown) и В ОБХОД нашего WndProc сам вос��танавливает
        // float-size, а затем снова растит окно во full-height snap → визуально «мигание + возврат». Бороться
        // с жестом в реальном времени бесполезно (доказано: свопы WndProc, watchdog, WH_MOUSE_LL — см. HANDOFF БАГ 1).
        // Поэтому НЕ мешаем жесту ОС, а ПОСЛЕ его завершения (EVENT_SYSTEM_MOVESIZEEND) принудительно применяем
        // pre-snap rect, если ОС оставила окно во full-height snap. Гарантирует корректный КОНЕЧНЫЙ разм��р.
        private const bool EnableUnsnapWinEventRestore = true;
        // БАГ 1: насколько окно должно остаться ВЫШЕ целевого restore, чтобы считать, что ОС оставила его
        // во full-height snap (restore ~1086px vs full ~2168px, порог 200 надёжно их разделяет).
        private const int UnsnapRestoreGrownMarginPx = 200;
        private IntPtr _unsnapWinEventHook;
        private long _lastNoEdgeDiagTick;              // BUG1.2: троттлинг диагностики JrcEdges fail
        private WinEventDelegate? _unsnapWinEventProc; // держим ссылку — иначе GC соберёт делегат
        private bool _unsnapArmValid;        // на mouse-down был full-height snapped кандидат с валидным restore-rect
        private RECT _unsnapArmRestoreRect;  // pre-snap floating rect, к нему возвращаем
        private POINT _unsnapArmDownPt;      // экра��ная точка нажатия (физ. px)

        private RECT _unsnapArmSnapRect;     // snap-rect на момент arm (для детекции reversion, Вариант A)
        private bool _unsnapHasFloated;      // окно уже отлипло от snap-rect в текущем armed-жесте
        private const bool EnableUnsnapSuppressResnapFrame = true; // Вариант A: глушить кадр реверса в snap-rect

        private const bool EnableUnsnapSteerGrowBack = true; // Variant A+: steer grow-back frames to floating (live follow)
        private const bool EnableUnsnapProactiveFloat = true; // Variant A++: OS-never-floats fallback (big window in multi-tile group) — proactively float on downward drag
        private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const int OBJID_WINDOW = 0;

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private void InstallUnsnapWinEventHook()
        {
            if (!EnableUnsnapWinEventRestore || _unsnapWinEventHook != IntPtr.Zero) return;
            _unsnapWinEventProc = OnUnsnapWinEvent;
            // Скоупим хук на НАШ UI-поток (idThread): модальный move/size-цикл ОС крутится на потоке, владеющем
            // окном, поэто��у событ���я приходят сюда. Дешевле, чем глобальный out-of-context хук всех процессов.
            _unsnapWinEventHook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND,
                IntPtr.Zero, _unsnapWinEventProc, 0, GetCurrentThreadId(), WINEVENT_OUTOFCONTEXT);
            if (EnableTroubleshootLog) TsLog($"WeHookInstall ok={_unsnapWinEventHook != IntPtr.Zero}");
        }

        private void RemoveUnsnapWinEventHook()
        {
            if (_unsnapWinEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_unsnapWinEventHook);
                _unsnapWinEventHook = IntPtr.Zero;
            }
            _unsnapWinEventProc = null;
        }

        // durable-arm: фиксируем намерение восстановиться ДО старта жеста ОС. В отличие от _captionUnsnapPending,
        // он НЕ сбрасывается при потере capture (именно её крадёт жест ОС). Вызывается из TryBeginCaptionUnsnapRestoreDrag.
        private void ArmUnsnapWinEventRestore(POINT downPt, RECT snapRect, RECT restore)
        {
            if (!EnableUnsnapWinEventRestore) return;
            _unsnapArmValid = true;
            _unsnapArmDownPt = downPt;
            _unsnapArmSnapRect = snapRect;
            _unsnapArmRestoreRect = restore;
            _unsnapHasFloated = false;
        }

        // Вариант A (глушение кадра реверса): во время armed OS-unsnap ОС может на один кадр
        // вернуть окно в исходный snap-rect (визуальный телепорт). Как только окно ОТЛИПЛО от snap-rect,
        // любой последующий кадр, который пытается вернуть его В snap-rect, отклоняем (SWP_NOMOVE|SWP_NOSIZE).
        // Само окно НЕ двигаем — только запрещаем реверс. Финальный restore делает FinishUnsnapWinEventRestore.
        private void MaybeSuppressUnsnapResnapFrame(IntPtr lParam)
        {
            if (!EnableUnsnapSuppressResnapFrame || !_unsnapArmValid) return;

            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            bool noMove = (wp.flags & SWP_NOMOVE) != 0;
            bool noSize = (wp.flags & SWP_NOSIZE) != 0;
            if (noMove && noSize) return;

            if (!GetWindowRect(new WindowInteropHelper(this).Handle, out RECT curW)) return;

            int width = noSize ? (curW.Right - curW.Left) : wp.cx;
            int height = noSize ? (curW.Bottom - curW.Top) : wp.cy;

            int snapW = Math.Max(1, _unsnapArmSnapRect.Right - _unsnapArmSnapRect.Left);
            int snapH = Math.Max(1, _unsnapArmSnapRect.Bottom - _unsnapArmSnapRect.Top);
            int restoreW = Math.Max(1, _unsnapArmRestoreRect.Right - _unsnapArmRestoreRect.Left);
            int restoreH = Math.Max(1, _unsnapArmRestoreRect.Bottom - _unsnapArmRestoreRect.Top);
            const int tol = 24;

            bool nearSnapSize = Math.Abs(width - snapW) <= tol && Math.Abs(height - snapH) <= tol;
            bool nearRestoreSize = Math.Abs(width - restoreW) <= tol && Math.Abs(height - restoreH) <= tol;

            // Фаза 1: ждём, пока ОС реально ужмёт окно до floating (restore) размера.
            if (!_unsnapHasFloated)
            {
                if (nearRestoreSize)
                {
                    _unsnapHasFloated = true;
                    if (EnableTroubleshootLog) TsLog($"WeSuppress FLOATED at=({wp.x},{wp.y},{width}x{height})");
                    return;
                }
                // Variant A++ fallback: ОС нас НЕ всплывает (мы — большое окно в группе из 3 плиток: она
                // ДВИГАЕТ нас �� полном snapped-размере и сворачивает только на отпускании). Как только тянут
                // ВНИЗ дальше того же порога, что и на релизе — сами уводим в floating, чтобы ужаться вживую.
                if (EnableUnsnapProactiveFloat && EnableUnsnapSteerGrowBack && nearSnapSize
                    && GetCursorPos(out POINT pp) && (pp.Y - _unsnapArmDownPt.Y) >= CaptionUnsnapRestoreThresholdPxY())
                {
                    int clickX = Math.Clamp(_unsnapArmDownPt.X - _unsnapArmSnapRect.Left, 0, snapW);
                    int offX = Math.Clamp((int)Math.Round((double)clickX * restoreW / snapW), 0, restoreW - 1);
                    int offY = Math.Clamp(_unsnapArmDownPt.Y - _unsnapArmSnapRect.Top, 0, restoreH - 1);
                    wp.x = pp.X - offX;
                    wp.y = pp.Y - offY;
                    wp.cx = restoreW;
                    wp.cy = restoreH;
                    wp.flags &= ~(SWP_NOSIZE | SWP_NOMOVE);
                    Marshal.StructureToPtr(wp, lParam, false);
                    _unsnapHasFloated = true;
                    if (EnableTroubleshootLog) TsLog($"WeSuppress PROACT to=({wp.x},{wp.y},{restoreW}x{restoreH})");
                }
                return;
            }

            // Фаза 2: окно уже плавало в floating-размере. Если ОС снова РАЗДУВАЕТ его до snapped-размера
            // (реверс либо перетаскивание в snapped-виде) — навязываем floating-размер и позицию так,
            // чтобы курсор остался над той же относительной частью шапки => живое следование за курсором.
            if (nearSnapSize)
            {
                if (EnableUnsnapSteerGrowBack && GetCursorPos(out POINT pt))
                {
                    int clickX = Math.Clamp(_unsnapArmDownPt.X - _unsnapArmSnapRect.Left, 0, snapW);
                    int offX = Math.Clamp((int)Math.Round((double)clickX * restoreW / snapW), 0, restoreW - 1);
                    int offY = Math.Clamp(_unsnapArmDownPt.Y - _unsnapArmSnapRect.Top, 0, restoreH - 1);
                    wp.x = pt.X - offX;
                    wp.y = pt.Y - offY;
                    wp.cx = restoreW;
                    wp.cy = restoreH;
                    wp.flags &= ~(SWP_NOSIZE | SWP_NOMOVE);
                    Marshal.StructureToPtr(wp, lParam, false);
                    if (EnableTroubleshootLog) TsLog($"WeSuppress STEER to=({wp.x},{wp.y},{restoreW}x{restoreH})");
                }
                else
                {
                    wp.flags |= SWP_NOMOVE | SWP_NOSIZE;
                    Marshal.StructureToPtr(wp, lParam, false);
                    if (EnableTroubleshootLog) TsLog($"WeSuppress BLOCK resnap to=({wp.x},{wp.y},{width}x{height})");
                }
            }
        }

        private void OnUnsnapWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject != OBJID_WINDOW) return; // только событие самого окна, не дочерних объектов
            if (hwnd != new WindowInteropHelper(this).Handle) return;

            if (eventType == EVENT_SYSTEM_MOVESIZESTART)
            {
                if (EnableTroubleshootLog) TsLog($"WeMoveSizeStart armValid={_unsnapArmValid}");
                return;
            }
            if (eventType == EVENT_SYSTEM_MOVESIZEEND)
            {
                bool wasArmed = _unsnapArmValid;
                if (EnableTroubleshootLog) TsLog($"WeMoveSizeEnd armValid={wasArmed}");
                if (wasArmed)
                    // Чиним конечное состояние асинхронно — после того, как ОС окончательно применит свою
                    // геометрию (иначе наш SetWindowPos может быть перезатёрт хвостом жеста).
                    Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(FinishUnsnapWinEventRestore));
            }
        }

        private void FinishUnsnapWinEventRestore()
        {
            if (!_unsnapArmValid) return;
            _unsnapArmValid = false;
            _unsnapHasFloated = false;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || WindowState != WindowState.Normal) return;
            if (!GetWindowRect(hwnd, out RECT cur) || !GetCursorPos(out POINT pt)) return;

            int dy = pt.Y - _unsnapArmDownPt.Y;
            RECT r = _unsnapArmRestoreRect;
            int restoreH = Math.Max(1, r.Bottom - r.Top);
            int curH = cur.Bottom - cur.Top;
            // БАГ 1 (исправление guard): чиним ТОЛЬКО реальный кейс - пользователь потянул шапку ВНИЗ (un-snap),
            // но ОС оставила окно заметно ВЫ��Е целевого restore (в логе было 2168 vs restore 1086). НЕ полагаемся
            // на HasInternalSnapDivider: после re-snap соседа уже нет, делитель не виден, ��отя окно полноэкранной
            // высоты - именно из-за этого был ложны�� SKIP (stillFullHeight=False) и баг воспроизводился.
            bool stillGrown = curH > restoreH + UnsnapRestoreGrownMarginPx;
            if (dy < CaptionUnsnapRestoreThresholdPxY() || !stillGrown)
            {
                if (EnableTroubleshootLog) TsLog($"WeRestore SKIP dy={dy} curH={curH} restoreH={restoreH} stillGrown={stillGrown} cur=({cur.Left},{cur.Top},{cur.Right},{cur.Bottom})");
                return;
            }

            int w = Math.Max(1, r.Right - r.Left);
            int h = restoreH;
            int snapW = Math.Max(1, _unsnapArmSnapRect.Right - _unsnapArmSnapRect.Left);
            // Курсор остаётся над той же относительной частью шапки, как при штатном restore Windows.
            int clickX = Math.Clamp(_unsnapArmDownPt.X - _unsnapArmSnapRect.Left, 0, snapW);
            int offX = Math.Clamp((int)Math.Round((double)clickX * w / snapW), 0, w - 1);
            int offY = Math.Clamp(_unsnapArmDownPt.Y - _unsnapArmSnapRect.Top, 0, Math.Max(0, h - 1));
            int x = pt.X - offX;
            int y = pt.Y - offY;

            if (EnableTroubleshootLog) TsLog($"WeRestore APPLY dy={dy} to=({x},{y},{w}x{h}) restore=({r.Left},{r.Top},{r.Right},{r.Bottom})");
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        #endregion


        /// <summary>
        /// true, если обычное (НЕ snapped и НЕ maximized) floating-окно выходит за любой край
        /// свое��о монитор��. Именно в это�� случае DWM держит ��старевшую redirection-поверхность
        /// ��ля закадровой части и при resize показывает её клон у самого края экрана.
        /// </summary>
        private bool FloatingWindowCrossesMonitor(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (WindowState != WindowState.Normal) return false;   // только обычный режим
            if (HasInternalSnapDivider(hwnd)) return false;        // не трогаем snapped-группы
            if (!GetWindowRect(hwnd, out RECT r)) return false;
            var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            var m = mi.rcMonitor;
            return r.Bottom > m.Bottom || r.Right > m.Right || r.Top < m.Top || r.Left < m.Left;
        }







        /// <summary>true, если rect выходит за любой край монитора, на котором сейчас hwnd.</summary>
        private bool RectCrossesItsMonitor(IntPtr hwnd, RECT r)
        {
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            RECT m = mi.rcMonitor;
            return r.Bottom > m.Bottom || r.Right > m.Right || r.Top < m.Top || r.Left < m.Left;
        }


        #region Диагностика Snap (ВРЕМЕННОЕ логирование — убрать после диаг��оза)

        // true = писать трассу ок��нных сообщений Snap/resize/move в файл (см. SnapDiagLogPath). Нужно, чтобы
        // понять, КАК наше окно отвечает на навязанную shell геометрию по��зунка Snap-группы и на un-snap.
        // ТОЧЕЧНЫЙ troubleshoot-лог для багов caption-restore + 3-колоночного joint-resize. Пишет ТОЛЬКО на
        // редкие события (click-caption, snap-follow latch). Для release снова вернуть в false.
        private const bool EnableTroubleshootLog = true;
        private static readonly string TsLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControlPanel", "troubleshoot.log");
        private static readonly object _tsLock = new();
        private static void TsLog(string line)
        {
            if (!EnableTroubleshootLog) return;
            try
            {
                lock (_tsLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(TsLogPath)!);
                    File.AppendAllText(TsLogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
                }
            }
            catch { }
        }

        // Выключить (false), когда диагностика завершена.
        private const bool EnableSnapDiagLog = false;
        private static readonly string SnapDiagLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControlPanel", "snap-debug.log");
        private static readonly object _snapDiagLock = new();

        private static void SnapLog(string line)
        {
            if (!EnableSnapDiagLog) return;
            try
            {
                lock (_snapDiagLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SnapDiagLogPath)!);
                    File.AppendAllText(SnapDiagLogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
                }
            }
            catch { /* лог не критичен */ }
        }

        private static string RectStr(IntPtr hwnd) =>
            GetWindowRect(hwnd, out RECT r) ? $"({r.Left},{r.Top},{r.Right},{r.Bottom})[{r.Right - r.Left}x{r.Bottom - r.Top}]" : "(?)";

        private static string WorkStr(IntPtr hwnd) =>
            TryGetWorkArea(hwnd, out RECT w) ? $"({w.Left},{w.Top},{w.Right},{w.Bottom})" : "(?)";

        private static string HitName(int code) => code switch
        {
            HTTRANSPARENT => "TRANSPARENT", HTNOWHERE => "NOWHERE", HTCLIENT => "CLIENT", HTCAPTION => "CAPTION",
            HTLEFT => "LEFT", HTRIGHT => "RIGHT", HTTOP => "TOP", HTTOPLEFT => "TOPLEFT", HTTOPRIGHT => "TOPRIGHT",
            HTBOTTOM => "BOTTOM", HTBOTTOMLEFT => "BOTTOMLEFT", HTBOTTOMRIGHT => "BOTTOMRIGHT", _ => code.ToString(),
        };

        /// <summary>Лог итогового решения WM_NCHITTEST (с дедупом одинаковых подряд, чтобы не залить hover-ом).</summary>
        private void LogHit(IntPtr lParam, int code, string why)
        {
            if (code == _lastLoggedHit) return;
            _lastLoggedHit = code;
            int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
            SnapLog($"NCHITTEST ({x},{y}) -> {HitName(code)} [{why}]");
        }

        /// <summary>Трасса входящих сообщений move/resize/snap (кроме NCHITTEST/NCCALCSIZE — те логируются ��тдельно).</summary>
        private void LogIncoming(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_ENTERSIZEMOVE:
                    SnapLog($"--- ENTERSIZEMOVE state={WindowState} rect={RectStr(hwnd)} work={WorkStr(hwnd)}");
                    break;
                case WM_EXITSIZEMOVE:
                    SnapLog($"--- EXITSIZEMOVE state={WindowState} rect={RectStr(hwnd)} userEdge={_userEdgeResize} sizeChanged={_sizeChangedInLoop}");
                    break;
                case WM_SIZING:
                {
                    var r = Marshal.PtrToStructure<RECT>(lParam);
                    SnapLog($"SIZING edge={wParam.ToInt64()} rc=({r.Left},{r.Top},{r.Right},{r.Bottom})");
                    break;
                }
                case WM_GETMINMAXINFO:
                {
                    var m = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    SnapLog($"GETMINMAXINFO maxPos=({m.ptMaxPosition.X},{m.ptMaxPosition.Y}) maxSize=({m.ptMaxSize.X},{m.ptMaxSize.Y}) minTrack=({m.ptMinTrackSize.X},{m.ptMinTrackSize.Y}) maxTrack=({m.ptMaxTrackSize.X},{m.ptMaxTrackSize.Y}) state={WindowState}");
                    break;
                }
                case WM_WINDOWPOSCHANGING:
                {
                    var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    SnapLog($"POSCHANGING flags=0x{wp.flags:X4} pos=({wp.x},{wp.y}) size=({wp.cx},{wp.cy}) inSizeMove={_inSizeMove} edgeResize={_userEdgeResize}");
                    break;
                }
                case WM_WINDOWPOSCHANGED:
                {
                    var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    SnapLog($"POSCHANGED  flags=0x{wp.flags:X4} pos=({wp.x},{wp.y}) size=({wp.cx},{wp.cy}) rect={RectStr(hwnd)}");
                    break;
                }
            }
        }

        #endregion

        /// <summary>
        /// При обычной панели — рабочая область монитора. При нижней auto-hide панели —
        /// ВЕСЬ монитор. Если активно «сжатие» (_shrunk) — выс��та на 1px меньше, чтобы
        /// система не «прыгнула» ок��ом ��а весь экран и панель не спряталась.
        /// </summary>
        private void AdjustMaximizedBounds(IntPtr hwnd, IntPtr lParam)
        {
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return;

            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
                return;

            RECT work = monitorInfo.rcWork;
            RECT bounds = monitorInfo.rcMonitor;
            // «Н��крываем» весь монитор только на мониторе с главной панелью задач; на
            // дополнительных — обычная рабоч��я область, без сдвигов и спецлогики.
            bool autoHide = IsOnTaskbarMonitor(bounds) && HasBottomAutoHideTaskbar(bounds);
            RECT target = autoHide ? bounds : work;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMaxPosition.X = target.Left - bounds.Left;
            mmi.ptMaxPosition.Y = target.Top - bounds.Top;
            mmi.ptMaxSize.X = target.Right - target.Left;
            mmi.ptMaxSize.Y = (target.Bottom - target.Top) - (autoHide && _shrunk ? 1 : 0);
            Marshal.StructureToPtr(mmi, lParam, true);
        }

        #region Стартовое появление (маска + гашение вспышки)

        /// <summary>
        /// Прячет главное окно на время старта через DWM-CLOAK: полностью убирает его из композитор��
        /// (бинарно, без гонки за кадр), поэтому «вспышка» (чёрная/бе��ая до перв��го present WPF) не
        /// видна. Раскрытие — <see cref="UncloakMain"/>. Это НЕ <c>AllowsTransparency</c> и не меняет
        /// рендер главного окна (cloak снимается сразу после старта) — лагов нет.
        /// </summary>
        private void HideMainForStartup(IntPtr hwnd)
        {
            if (!EnableStartupMask || hwnd == IntPtr.Zero) return;

            int cloak = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
            _startupHiding = true;
        }

        /// <summary>Снимает cloak — главное окно становится видимым (под уже непрозрачной маской).</summary>
        private void UncloakMain(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int cloak = 0;
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
        }

        /// <summary>Стартовое раскрытие (вариант B): снимок десктопа под cloaked-окном → показ непрозрачно → uncloak под ним → растворение, окно проявляется из десктопа.</summary>
        private void StartMaskReveal(IntPtr mainHwnd)
        {
            if (!_startupHiding) return;
            _startupHiding = false;

            if (mainHwnd == IntPtr.Zero || !TryGetMaskRect(mainHwnd, out RECT r))
            {
                UncloakMain(mainHwnd);
                return;
            }
            StartScreenshotCrossfade(r, () => UncloakMain(mainHwnd), gateOnContent: true, durationMs: ScreenshotAppearMs);
        }

        /// <summary>Восс��ановление из трея (вариант B): cloak → снимок десктопа → uncloak под снимком → растворение.</summary>
        private void StartRestoreReveal()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!EnableStartupMask || hwnd == IntPtr.Zero) return;

            int cloak = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!TryGetMaskRect(hwnd, out RECT r))
                {
                    UncloakMain(hwnd);
                    return;
                }
                StartScreenshotCrossfade(r, () => UncloakMain(hwnd), gateOnContent: true, durationMs: ScreenshotAppearMs);
            }));
        }

        /// <summary>
        /// Смен�� состояния окна (обычное⇄развёрнутое) С анимацией маски: на весь монитор наплывает
        /// маска, под её непрозрачностью окно меняет размер, затем маск�� растворяется. Вызывается
        /// ТОЛЬКО из ��вных действий пользователя (кнопки caption, двойной клик по шапке). Прочие способы
        /// (Win+стрелки, перетаскивание, снап) идут штатным путём Windows и маску НЕ вызывают.
        /// </summary>
        private void AnimateToWindowState(WindowState target)
        {
            if (WindowState == target) return;

            // Маска выключена или монитор не оп��еделить — обычная мгновенная смена.
            if (!EnableStartupMask || !TryGetWindowMonitor(out RECT mon))
            {
                WindowState = target;
                return;
            }
            StartScreenshotCrossfade(mon, () => WindowState = target, gateOnContent: true, durationMs: target == WindowState.Maximized ? ScreenshotAppearMs : ScreenshotDisappearMs);
        }

        /// <summary>Сворачивание (вариант B): снимок видимого окна → показ непрозрачно → сворачивание под ним → растворение, проступает десктоп. Восстановление ловит OnStateChanged.</summary>
        private void AnimateMinimize()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!EnableStartupMask || hwnd == IntPtr.Zero || !TryGetMaskRect(hwnd, out RECT r))
            {
                WindowState = WindowState.Minimized;
                return;
            }
            StartScreenshotCrossfade(r, () => WindowState = WindowState.Minimized, gateOnContent: false, durationMs: ScreenshotDisappearMs);
        }

        /// <summary>
        /// Универсальный кр��стфейд маски над прямоугольником <paramref name="rect"/> (физ. px):
        /// альфа 0→255 (наплыв) → <paramref name="atPeak"/> на UI-потоке под ��епрозрачной маской
        /// (uncloak старта либо смена WindowState — ресайз скрыт) → 255→0 (растворение) → уничтожить
        /// окно-маску. Ес��и крестфейд уже идёт — действие выполняется сразу, без новой маски.
        /// <para>
        /// Маска — НЕ WPF-окно, а «голое» Win32 layered-окно (см. <see cref="EnsureMaskClass"/>),
        /// ��алитое сплошным цветом темы; альфа задаётся <c>SetLayeredWindowAttributes(LWA_ALPHA)</c>.
        /// Крестфейд гоним на ОТДЕЛЬНОМ потоке, и после ка��дого шага альфы ждём <c>DwmFlush()</c> —
        /// он блокируется до следующего прохода КОМПОЗИЦИИ DWM. Так каждый шаг скомпонован ровно один
        /// ра�� перед следующим (привязка к такту композитора, а не к свободному таймеру/WPF-кадрам).
        /// </para>
        /// </summary>
        private void StartMaskCrossfade(RECT rect, Action atPeak, bool gateOnContent = false)
        {
            if (_maskHwnd != IntPtr.Zero) { atPeak(); return; } // крестфейд уже идёт — без повторной маски

            EnsureMaskClass();
            _maskHwnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                MaskClassName, null, WS_POPUP,
                rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (_maskHwnd == IntPtr.Zero) { atPeak(); return; } // не удалось создать маску — хотя бы ��ействие

            _maskAtPeak = atPeak;
            _gateOnRender = gateOnContent;
            _peakGate.Reset();
            SetLayeredWindowAttributes(_maskHwnd, 0, 0, LWA_ALPHA); // старт с полной прозрачности
            ShowWindow(_maskHwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(_maskHwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

            _maskAbort = false;
            _maskThread = new Thread(MaskFadeLoop) { IsBackground = true, Name = "MaskCrossfade" };
            _maskThread.Start();
        }

        /// <summary>
        /// Кривая альфы маски (ease-out): взвешенная смесь линейной и квадратичной, вес
        /// <see cref="MaskEaseWeight"/>. Скорость макси��альна у прозрачного края и плавно гаснет к
        /// непрозрачности. Наплыв зовёт <c>Ease(t)</c> (замедление к пику), растворение — <c>Ease(1−t)</c>
        /// (ускорение к прозрачности). Линейная доля убирает «замороженные» концы чистой квадратики.
        /// </summary>
        private static double Ease(double x)
        {
            const double w = MaskEaseWeight;
            double u = 1.0 - x;
            return 1.0 - ((1.0 - w) * u + w * u * u);
        }

        /// <summary>
        /// Плавность ОДИНОЧНОГО растворения скриншот-маски: ease-in-out (smoothstep) — мягкий
        /// старт и мягкая осадка (скорость 0 на концах). Фаза теперь одна, а «удержание» на
        /// непрозрачности уже даёт gate по кадру контента (прежняя двухфазная Ease-схема не нужна).
        /// </summary>
        private static double SmoothStep(double t)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>
        /// Поток крестфейда: фаза 0 альфа 0→255, на пике — действие <see cref="_maskAtPeak"/> на
        /// UI-потоке (uncloak / смена WindowState под непрозр��чной маской); при раскрытии из cloak ждём
        /// реальный кадр с контентом (<see cref="_gateOnRender"/> через <c>CompositionTarget.Rendering</c>,
        /// иначе подложка просвечивала бы), затем фаза 1 255→0. После КАЖДОГО шага — <c>DwmFlush()</c>
        /// (ждём проход композиции DWM), чтобы шаги не «склеивались». Окно создаётся/уничтожается на
        /// UI-потоке — финальный <c>DestroyWindow</c> маршалим обр��тно.
        /// </summary>
        private void MaskFadeLoop()
        {
            IntPtr hwnd = _maskHwnd;
            Action? atPeak = _maskAtPeak;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (!_maskAbort) // фаза 0: наплыв 0→255
            {
                double t = sw.Elapsed.TotalMilliseconds / StartupMaskFadeInMs;
                if (t >= 1.0) break;
                SetLayeredWindowAttributes(hwnd, 0, (byte)(Ease(t) * 255.0), LWA_ALPHA);
                DwmFlush();
            }
            if (_maskAbort) return;

            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            // Пик: маска непрозрачна. Выполняем действие (uncloak / смена WindowState) на UI-потоке и, если
            // это раскрытие из cloak, вешаем ��дноразовый хук на следующий кадр композиции — он сработает,
            // когда WPF реально представит кадр с контентом (белая подложка Windows уже перекрыта).
            if (atPeak != null || _gateOnRender)
            {
                Dispatcher.Invoke(() =>
                {
                    atPeak?.Invoke();
                    if (_gateOnRender)
                    {
                        EventHandler? onRender = null;
                        onRender = (_, _) =>
                        {
                            System.Windows.Media.CompositionTarget.Rendering -= onRender;
                            _peakGate.Set(); // контент представлен — можно раств��рять
                        };
                        System.Windows.Media.CompositionTarget.Rendering += onRender;
                    }
                });
            }
            DwmFlush();

            // Растворяем не по таймеру, а по факту представленного кадра с контентом (подложка скрыт��).
            // Таймаут — страховка, чтобы не зависнуть, если кадр почему-то не пришёл (окно св��рнуто и т.п.).
            if (_gateOnRender) _peakGate.Wait(PeakGateTimeoutMs);
            if (_maskAbort) return;

            sw.Restart();
            while (!_maskAbort) // фаза 1: растворе��ие 255→0
            {
                double t = sw.Elapsed.TotalMilliseconds / StartupMaskFadeOutMs;
                if (t >= 1.0) break;
                SetLayeredWindowAttributes(hwnd, 0, (byte)(Ease(1.0 - t) * 255.0), LWA_ALPHA);
                DwmFlush();
            }
            SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);

            // Уничтожаем окно на UI-потоке (создавалось там). Поля гасим, только если они ещё про ЭТО
            // окно — защита от гонки при быстром повторном крестфейде.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                DestroyWindow(hwnd);
                if (_maskHwnd == hwnd) { _maskHwnd = IntPtr.Zero; _maskThread = null; }
            }));
        }

        /// <summary>Прерывает крестфейд и уничтожает окно-маску + GDI-ресурсы (закрытие/перезапуск).</summary>
        private void EndMask()
        {
            _maskAbort = true;
            _peakGate.Set();
            if (_maskHwnd != IntPtr.Zero)
                CleanupScreenshotMask(_maskHwnd); // для серой маски _shot* нулевые → просто DestroyWindow
        }

        // Класс окна-маски регистрируется один раз на процесс. WndProc = DefWindowProc (всё поведение
        // по умолчанию), фон — сплошная кисть цвета темы (DefWindowProc сам зальёт по WM_ERASEBKGND).
        private const string MaskClassName = "ControlPanelStartupMask";
        private const int MaskColorRef = 0x001C1A19; // #191A1C как COLORREF (0x00BBGGRR)
        private const int ThemeBorderColorRef = 0x00403432; // #323440 как COLORREF (��ема BorderColor)
        private static bool _maskClassRegistered;
        private static WndProcDelegate? _maskWndProc; // держим ссылку — иначе делегат соберёт GC

        private static void EnsureMaskClass()
        {
            if (_maskClassRegistered) return;
            _maskWndProc = (h, m, w, l) => DefWindowProcW(h, m, w, l);
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_maskWndProc),
                hInstance = GetModuleHandleW(null),
                hbrBackground = CreateSolidBrush(MaskColorRef),
                lpszClassName = MaskClassName,
            };
            RegisterClassW(ref wc);
            _maskClassRegistered = true;
        }
        /// <summary>
        /// Вариант B: снимает область экрана по rect в HBITMAP, показывает его непрозрачным
        /// GDI layered-окном (UpdateLayeredWindow), на пике выполняет atPeak (uncloak / сворачивание)
        /// под непрозрачным снимком, затем растворяет снимок 255->0. Фазы наплыва НЕТ:
        /// снимок совпадает с тем, что уже на экране (бесшовно). GDI-окно анимируется (в отличие от WPF).
        /// </summary>
        private void StartScreenshotCrossfade(RECT rect, Action atPeak, bool gateOnContent, int durationMs)
        {
            if (_maskHwnd != IntPtr.Zero) { atPeak(); return; }

            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) { atPeak(); return; }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr bmp = CreateCompatibleBitmap(screenDc, w, h);
            if (screenDc == IntPtr.Zero || memDc == IntPtr.Zero || bmp == IntPtr.Zero)
            {
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
                atPeak(); return;
            }
            IntPtr oldBmp = SelectObject(memDc, bmp);
            // Снимок области экрана: при появлении окно cloaked (виден десктоп), при сворачивании — ещё видимо (пиксели окна).
            BitBlt(memDc, 0, 0, w, h, screenDc, rect.Left, rect.Top, SRCCOPY | CAPTUREBLT);

            EnsureMaskClass();
            IntPtr hwnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                MaskClassName, null, WS_POPUP,
                rect.Left, rect.Top, w, h,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                SelectObject(memDc, oldBmp);
                DeleteObject(bmp); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc);
                atPeak(); return;
            }

            // Маска — WS_POPUP: Win11 по умолчанию скругляет углы и может дать свою тень — при запасе это
            // читалось как смещённая «рамка как ещё одно окно». Гасим: квадратные углы + без NC-тени.
            int maskCorner = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref maskCorner, sizeof(int));
            int maskNc = DWMNCRP_DISABLED;
            DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref maskNc, sizeof(int));
            // Главное от двойной тени: окно с ОКОННЫМ РЕГИОНОМ DWM-тень НЕ отбрасывает.
            // Регион = весь прямоугольник маски (содержимое не обрезается). SetWindowRgn забирает владение рег��оном.
            SetWindowRgn(hwnd, CreateRectRgn(0, 0, w, h), false);

            _maskHwnd = hwnd;
            _shotScreenDc = screenDc; _shotMemDc = memDc; _shotBmp = bmp; _shotOldBmp = oldBmp;
            _shotRect = rect;
            _maskAtPeak = atPeak;
            _gateOnRender = gateOnContent;
            _shotDurationMs = durationMs;
            _peakGate.Reset();

            // Показываем снимок СРАЗУ на 100% (бесшовно), фазы наплыва нет.
            UpdateShotAlpha(hwnd, memDc, rect, 255);
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

            _maskAbort = false;
            _maskThread = new Thread(ScreenshotFadeLoop) { IsBackground = true, Name = "ScreenshotCrossfade" };
            _maskThread.Start();
        }

        /// <summary>Обновляет контент+альфу скриншот-маски через UpdateLayeredWindow (константная альфа).</summary>
        private static void UpdateShotAlpha(IntPtr hwnd, IntPtr memDc, RECT rect, byte alpha)
        {
            var ptSrc = new POINT { X = 0, Y = 0 };
            var ptDst = new POINT { X = rect.Left, Y = rect.Top };
            var size = new SIZE { cx = rect.Right - rect.Left, cy = rect.Bottom - rect.Top };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 0 };
            UpdateLayeredWindow(hwnd, IntPtr.Zero, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, ULW_ALPHA);
        }

        /// <summary>Поток скриншот-крестфейда: пик (atPeak+gate) -> растворение 255->0 -> очистка.</summary>
        private void ScreenshotFadeLoop()
        {
            IntPtr hwnd = _maskHwnd;
            IntPtr memDc = _shotMemDc;
            RECT rect = _shotRect;
            Action? atPeak = _maskAtPeak;

            if (atPeak != null || _gateOnRender)
            {
                Dispatcher.Invoke(() =>
                {
                    atPeak?.Invoke();
                    if (_gateOnRender)
                    {
                        EventHandler? onRender = null;
                        onRender = (_, _) =>
                        {
                            System.Windows.Media.CompositionTarget.Rendering -= onRender;
                            _peakGate.Set();
                        };
                        System.Windows.Media.CompositionTarget.Rendering += onRender;
                    }
                });
            }
            DwmFlush();

            if (_gateOnRender) _peakGate.Wait(PeakGateTimeoutMs);
            if (_maskAbort) { Dispatcher.BeginInvoke(new Action(() => CleanupScreenshotMask(hwnd))); return; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!_maskAbort)
            {
                double t = sw.Elapsed.TotalMilliseconds / _shotDurationMs;
                if (t >= 1.0) break;
                UpdateShotAlpha(hwnd, memDc, rect, (byte)((1.0 - SmoothStep(t)) * 255.0));
                DwmFlush();
            }
            if (!_maskAbort) UpdateShotAlpha(hwnd, memDc, rect, 0);

            Dispatcher.BeginInvoke(new Action(() => CleanupScreenshotMask(hwnd)));
        }

        /// <summary>Уничтожает окно-маску и освобождает GDI-ресурсы снимка (безопасно и для серой маски).</summary>
        private void CleanupScreenshotMask(IntPtr hwnd)
        {
            if (_shotMemDc != IntPtr.Zero && _shotOldBmp != IntPtr.Zero) SelectObject(_shotMemDc, _shotOldBmp);
            if (_shotBmp != IntPtr.Zero) DeleteObject(_shotBmp);
            if (_shotMemDc != IntPtr.Zero) DeleteDC(_shotMemDc);
            if (_shotScreenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _shotScreenDc);
            _shotBmp = IntPtr.Zero; _shotOldBmp = IntPtr.Zero; _shotMemDc = IntPtr.Zero; _shotScreenDc = IntPtr.Zero;

            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            if (_maskHwnd == hwnd) { _maskHwnd = IntPtr.Zero; _maskThread = null; }
        }

        #endregion

        #region Динамическое освобождение нижнего края

        private void UpdateEdgeWatcher()
        {
            if (WindowState == WindowState.Maximized && TryGetWindowMonitor(out var rect)
                && IsOnTaskbarMonitor(rect) && HasBottomAutoHideTaskbar(rect))
            {
                _currentMonitorRect = rect;
                _taskbarHeight = GetTaskbarThickness(rect);
                StartEdgeWatcher();
            }
            else
            {
                StopEdgeWatcher();
            }
        }

        private void StartEdgeWatcher()
        {
            if (_watchActive) return;
            _watchActive = true;
            // «Прогреваем» о��но-маску заранее (создание + первый present), чтобы её
            // показ в ��омент сжатия не запаздывал на кадр �� не мелькал зазор.
            if (EnableGapMask)
                EnsureGapMaskHandle();
            _edgeWatcher.Start();
        }

        /// <param name="restoreSize">
        /// true — вернуть выс��ту окна (обычная остановка на том же мониторе). false — окно уже
        /// «переехало» на другой монитор: не ресайзим по устаре��шему прямоугольнику, размер
        /// пересчитает система через WM_GETMINMAXINFO; просто чистим состояние и прячем маску.
        /// </param>
        private void StopEdgeWatcher(bool restoreSize = true)
        {
            if (!_watchActive) return;
            _watchActive = false;
            _edgeWatcher.Stop();
            if (_shrunk)
            {
                if (restoreSize)
                {
                    ShrinkBottom(false);
                }
                else
                {
                    _shrunk = false;
                    HideGapMask();
                }
            }
            _taskbarWasVisible = false;
            _waitTicks = 0;
            _armSuppressed = false;
            _cursorDriven = false;
            _hasPrevCursorY = false;
        }

        private void EdgeWatcher_Tick(object? sender, EventArgs e)
        {
            if (WindowState != WindowState.Maximized)
            {
                StopEdgeWatcher();
                return;
            }

            // Окно «переехало» на другой монитор в развёрнутом вид�� (напр��мер, Win+Shift+стрелка) —
            // выключаемся без ресайза по устаревшему прямоугольнику (его пересчитает система).
            if (!TryGetWindowMonitor(out var curMon) || !RectEquals(curMon, _currentMonitorRect))
            {
                StopEdgeWatcher(restoreSize: false);
                return;
            }

            if (!GetCursorPos(out POINT p))
                return;

            int bottom = _currentMonitorRect.Bottom;
            bool onMonitor =
                p.X >= _currentMonitorRect.Left && p.X < _currentMonitorRect.Right &&
                p.Y >= _currentMonitorRect.Top && p.Y < _currentMonitorRect.Bottom;

            // Направление движения курсора по вертикали (физ. px). >0 �� вниз, к краю.
            int dy = _hasPrevCursorY ? p.Y - _prevCursorY : 0;
            _prevCursorY = p.Y;
            _hasPrevCursorY = true;
            bool movingUp = dy < 0;

            bool nearEdge = onMonitor && p.Y >= bottom - ArmBand; // в зоне у края (вкл. сам край)
            bool atEdge = onMonitor && p.Y >= bottom - 1;         // на последне�� ряду пикселей
            bool visible = IsTaskbarCurrentlyVisible();

            if (!_shrunk)
            {
                // Курсор ушёл из зоны — снимаем запрет на пере-сжатие.
                if (!nearEdge)
                    _armSuppressed = false;

                // Pre-arm: освобождаем по��осу заран��е, пока курсор приближается к краю.
                // Не сжимаем, если это уже уходящее вверх касание (грязный мазок).
                if (nearEdge && !movingUp && !_armSuppressed)
                {
                    _taskbarWasVisible = false;
                    _waitTicks = 0;
                    _cursorDriven = false;
                    ShrinkBottom(true);
                }
                return;
            }

            // _shrunk == true:
            if (visible)
            {
                // Панель показала��ь (и сама перекрыла полосу) — прячем заглушку,
                // зап��минаем и сбрасываем таймаут ожидания.
                _taskbarWasVisible = true;
                _waitTicks = 0;
                HideGapMask();
                return;
            }

            if (_taskbarWasVisible)
            {
                // Панель была видима и т��перь спряталась — расширяем окно обратно.
                ShrinkBottom(false);
                _taskbarWasVisible = false;
                _waitTicks = 0;
                return;
            }

            // Панель ещё НЕ выехала после сжатия.
            // Быстрая отмена: курсор ушёл вверх из зоны — это ��лучайно�� касание края.
            if (!nearEdge)
            {
                ShrinkBottom(false);
                _waitTicks = 0;
                return;
            }

            // Доводка курсора: он в зоне, но не достал последний ряд и не уходит вверх —
            // один раз мягко подвести к краю (нудж ≤ ArmBand px), чтобы ОС открыла панель
            // без ожидания. Затем «отпускаем» — больше курсор не трогаем.
            if (EnableCursorDrive && !_cursorDriven && !atEdge && !movingUp)
            {
                _cursorDriven = true;
                SetCursorPos(p.X, bottom - 1);
                _prevCursorY = bottom - 1; // наш собственный сдвиг — не считать движением вниз
                return;
            }

            // Курсор завис в зоне (например, у края, но не на последнем ряду) — панель не
            // выезжает. Чере�� короткий таймаут возвращаем вы��оту и подавляем пере-сжатие,
            // ��ока курсор не покинет зону: иначе была бы осцилляция сжатие/разжатие.
            if (++_waitTicks > ArmTimeoutTicks) // ~250 мс при интервале 10 мс
            {
                ShrinkBottom(false);
                _waitTicks = 0;
                _armSuppressed = true;
            }
        }

        /// <summary>Уменьшить/вернуть высоту окна на 1px снизу через SetWindowPos (физ. пиксели).</summary>
        private void ShrinkBottom(bool shrink)
        {
            if (_shrunk == shrink) return;
            _shrunk = shrink;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            RECT m = _currentMonitorRect;
            int width = m.Right - m.Left;
            int height = (m.Bottom - m.Top) - (shrink ? 1 : 0);

            // Порядок важен, чтобы в полосе не мелькнул рабочий стол:
            //  • при сжатии — сперва закрываем полосу маской, пото�� сжимаем окно;
            //  • при возврате — сперва окно само закрывает полосу, потом прячем маску.
            if (shrink)
                ShowGapMask();

            SetWindowPos(hwnd, IntPtr.Zero, m.Left, m.Top, width, height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOCOPYBITS | SWP_DEFERERASE);

            if (!shrink)
                HideGapMask();
        }

        #region ��аскировка ��азора (тонкая topmost-заглу��ка)

        // «Свес» окна-маски ниже экрана: на экране видна только 1px-полоса у самого края,
        // но сама поверхность достаточно высокая, чтобы WPF гарантированно её красил
        // (окно высотой в 1 физ. px кадр не отрисовывает — отсюда «мас��а не видна»).
        private const int GapMaskHeight = 64;

        // Точка «парковки» ��аски за пределами всех мониторов (не прячем через WS_HIDE,
        // иначе WPF-поверхность не перерисовывается при повторном показе — зазор
        // возвращался со 2-го раза). Окно всегда видимо, мы лишь двигаем его.
        private const int GapMaskParked = -32000;

        /// <summary>Показать заглушку на освобождённой нижней полосе монитора (физ. px).</summary>
        private void ShowGapMask()
        {
            if (!EnableGapMask) return;

            IntPtr h = EnsureGapMaskHandle();
            if (h == IntPtr.Zero) return;

            RECT m = _currentMonitorRect;
            // Верх — на последнем видимом ряду; высота уходит за нижнюю кро��ку экрана.
            // SWP без SHOWWINDOW: окно уже видимо, только переносим и поднимаем поверх.
            SetWindowPos(h, HWND_TOPMOST, m.Left, m.Bottom - 1, m.Right - m.Left, GapMaskHeight,
                SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        /// <summary>Увести заглушку за ��кран (панель выехала или окно расширено обратно).</summary>
        private void HideGapMask()
        {
            if (_gapMask is null) return;

            IntPtr h = new WindowInteropHelper(_gapMask).Handle;
            if (h != IntPtr.Zero)
                SetWindowPos(h, HWND_TOPMOST, GapMaskParked, GapMaskParked, 0, 0,
                    SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        private IntPtr EnsureGapMaskHandle()
        {
            if (_gapMask is null)
            {
                _gapMask = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    AllowsTransparency = false,
                    Background = System.Windows.Media.Brushes.Black,
                    Topmost = true,
                    Focusable = false,
                    IsHitTestVisible = false,
                    // Не вырожденный размер — чтобы WPF сразу строил красимую поверхность.
                    // Реальные размер/позиция задаются через SetWindowPos в физ. px.
                    Width = 400,
                    Height = GapMaskHeight,
                    Left = GapMaskParked, // создаём за пределами экрана, чтобы не мелькнуло
                    Top = GapMaskParked,
                };
                _gapMask.SourceInitialized += (_, _) =>
                {
                    IntPtr h = new WindowInteropHelper(_gapMask!).Handle;
                    // click-through, без активации, не в Alt-Tab
                    IntPtr ex = GetWindowLongPtr(h, GWL_EXSTYLE);
                    SetWindowLongPtr(h, GWL_EXSTYLE,
                        new IntPtr(ex.ToInt64() | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
                };
                // ��оказываем один раз (за экраном) и больше НЕ прячем через WS_HIDE —
                // окно остаётся видимым и постоянно отрис��ванным, мы лишь двигаем его.
                _gapMask.Show();
            }
            return new WindowInteropHelper(_gapMask).Handle;
        }

        #endregion

        /// <summary>
        /// true, если auto-hide панель сейчас выехала (видима) на основном мониторе.
        /// Спрятанная панель прижата к краю (её top �� низ монитора); если её верх заметно
        /// выше низа экрана — панель ��оказана.
        /// </summary>
        private bool IsTaskbarCurrentlyVisible()
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;
            if (!GetWindowRect(tray, out RECT tb)) return false;

            int bottom = _currentMonitorRect.Bottom;
            return tb.Top <= bottom - (_taskbarHeight / 2);
        }

        private bool TryGetWindowMonitor(out RECT rect)
        {
            rect = default;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return false;

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi)) return false;

            rect = mi.rcMonitor;
            return true;
        }

        #endregion

        private const int ABE_BOTTOM = 3;
        private const int ABM_GETAUTOHIDEBAREX = 0x0000000B;

        /// <summary>
        /// true, если указанный монитор — тот, на котором закреплена ГЛАВНА�� панель задач
        /// (<c>Shell_TrayWnd</c>), т.е. «основной» монитор. Нужно, чтобы вся логика
        /// освобождения края/маски работала только там и не трогала окно на дополнительных
        /// мониторах (где панели снизу нет — иначе лишний сдвиг и артефакты).
        /// </summary>
        private static bool IsOnTaskbarMonitor(RECT monitorRect)
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;

            IntPtr trayMonitor = MonitorFromWindow(tray, MONITOR_DEFAULTTONEAREST);
            if (trayMonitor == IntPtr.Zero) return false;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(trayMonitor, ref mi)) return false;

            return RectEquals(mi.rcMonitor, monitorRect);
        }

        private static bool RectEquals(RECT a, RECT b) =>
            a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        /// <summary>true, если на нижнем крае указанного монитора закреплена auto-hide панель.</summary>
        private static bool HasBottomAutoHideTaskbar(RECT monitorRect)
        {
            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                uEdge = ABE_BOTTOM,
                rc = monitorRect,
            };
            return SHAppBarMessage(ABM_GETAUTOHIDEBAREX, ref data) != IntPtr.Zero;
        }

        /// <summary>Высота нижней auto-hide панели (px). 40 по умолчанию, если не удалось определить.</summary>
        private static int GetTaskbarThickness(RECT monitorRect)
        {
            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                uEdge = ABE_BOTTOM,
                rc = monitorRect,
            };
            IntPtr bar = SHAppBarMessage(ABM_GETAUTOHIDEBAREX, ref data);
            if (bar != IntPtr.Zero)
            {
                int h = data.rc.Bottom - data.rc.Top;
                if (h > 0 && h < (monitorRect.Bottom - monitorRect.Top))
                    return h;
            }
            return 40;
        }

        #region Сохранение положения окна

        private static readonly string PlacementFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControlPanel", "window-placement.json");

        private const int SW_SHOWNORMAL = 1;
        private const int SW_SHOWMINIMIZED = 2;

        private static void SavePlacement(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            try
            {
                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (!GetWindowPlacement(hwnd, ref wp))
                    return;

                var dto = new PlacementDto
                {
                    ShowCmd = wp.showCmd,
                    Left = wp.normalPosition.Left,
                    Top = wp.normalPosition.Top,
                    Right = wp.normalPosition.Right,
                    Bottom = wp.normalPosition.Bottom,
                    Monitors = GetMonitorSignature(),
                };

                Directory.CreateDirectory(Path.GetDirectoryName(PlacementFilePath)!);
                File.WriteAllText(PlacementFilePath, JsonSerializer.Serialize(dto));
            }
            catch
            {
                // не кр��тично
            }
        }

        private void RestorePlacement(IntPtr hwnd)
        {
            PlacementDto? dto = null;
            try
            {
                if (File.Exists(PlacementFilePath))
                    dto = JsonSerializer.Deserialize<PlacementDto>(File.ReadAllText(PlacementFilePath));
            }
            catch
            {
                dto = null;
            }

            if (dto is null || dto.Monitors != GetMonitorSignature())
            {
                CenterOnPrimaryScreen();
                return;
            }

            try
            {
                var wp = new WINDOWPLACEMENT
                {
                    length = Marshal.SizeOf<WINDOWPLACEMENT>(),
                    flags = 0,
                    showCmd = dto.ShowCmd == SW_SHOWMINIMIZED ? SW_SHOWNORMAL : dto.ShowCmd,
                    minPosition = new POINT { X = -1, Y = -1 },
                    maxPosition = new POINT { X = -1, Y = -1 },
                    normalPosition = new RECT
                    {
                        Left = dto.Left,
                        Top = dto.Top,
                        Right = dto.Right,
                        Bottom = dto.Bottom,
                    },
                };
                SetWindowPlacement(hwnd, ref wp);
            }
            catch
            {
                CenterOnPrimaryScreen();
            }
        }

        private void CenterOnPrimaryScreen()
        {
            WindowState = WindowState.Normal;
            Rect work = SystemParameters.WorkArea;
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
        }

        private static string GetMonitorSignature()
        {
            var rects = new List<string>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data) =>
                {
                    rects.Add($"{r.Left},{r.Top},{r.Right},{r.Bottom}");
                    return true;
                }, IntPtr.Zero);
            rects.Sort(StringComparer.Ordinal);
            return string.Join(";", rects);
        }

        private sealed class PlacementDto
        {
            public int ShowCmd { get; set; }
            public int Left { get; set; }
            public int Top { get; set; }
            public int Right { get; set; }
            public int Bottom { get; set; }
            public string? Monitors { get; set; }
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT minPosition;
            public POINT maxPosition;
            public RECT normalPosition;
        }

        #endregion

        #region WinAPI interop

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private const int SM_CXSIZEFRAME = 32;
        private const int SM_CXPADDEDBORDER = 92;

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        // ДИАГНОСТИКА (БАГ 2c): класс окна-кандидата в соседи — чтобы отличить чужое окно от партнёра.
        private static string GetWndClass(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(96);
            int n = GetClassName(h, sb, sb.Capacity);
            return n > 0 ? sb.ToString() : "";
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr BeginDeferWindowPos(int nNumWindows);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);


        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        // БАГ 1 (snap-follow): видимые границы окна без невидимого sizing-выступа — чтобы наш край встал ровно
        // на ползунок, без зазора/нахлеста. И состояние ЛКМ/курсора для детекта перетягивания ползунка.
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // БАГ 1 (snap-follow): поиск соседнего snapped-окна (партнёра по разделителю), чтобы встать ровно по его
        // ��идимой границе. EnumWindows + фильтры (видимое, не cloaked, не tool, тот же монитор).
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;
        private const int VK_LBUTTON = 0x01;

        // БА�� 1: запрет отрисовки темой текста/��к��нки заголовка при сохранённом WS_CAPTION (ради snap).
        [DllImport("uxtheme.dll")]
        private static extern int SetWindowThemeAttribute(IntPtr hWnd, int eAttribute, ref WTA_OPTIONS pvAttribute, uint cbAttribute);

        [StructLayout(LayoutKind.Sequential)]
        private struct WTA_OPTIONS { public uint dwFlags; public uint dwMask; }

        private const int WTA_NONCLIENT = 1;
        private const uint WTNCA_NODRAWCAPTION = 0x00000001;
        private const uint WTNCA_NODRAWICON = 0x00000002;

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private const int DWMWA_NCRENDERING_POLICY = 2;
        private const int DWMNCRP_DISABLED = 1;
        private const int DWMWA_CLOAK = 13;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWCP_DONOTROUND = 1;

        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_HIDEWINDOW = 0x0080;
        private const uint SWP_NOCOPYBITS = 0x0100;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const uint SWP_DEFERERASE = 0x2000;
        private const uint SWP_FRAMECHANGED = 0x0020;

        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_FRAME = 0x0400;
        private const int WVR_REDRAW = 0x0300; // WVR_HREDRAW | WVR_VREDRAW: force full client repaint on NCCALCSIZE
        private const int WVR_VALIDRECTS = 0x0400; // NCCALCSIZE: rgrc1=valid DEST, rgrc2=valid SOURCE -> aligned copy-blit (no ghost, no blank)
        private const int NcRedrawMaxDeltaPx = 250; // per-frame client-size delta above this = snap/unsnap transition: skip forced WVR_REDRAW to avoid a full-window blank flash
        private const bool EnableFullRedrawOnOriginMove = true; // BASELINE REVERT (correct): return full WVR_REDRAW on every changed-client NCCALCSIZE frame = the known-good pre-session behavior -> NO origin-move ghost. The user's baseline used plain WVR_REDRAW here and had no ghost; the WVR_VALIDRECTS aligned copy-blit below (flag=false) is what introduced the left-edge ghost. Keep true. Set false ONLY to re-enable the experimental copy-blit (smoother but reintroduces the ghost).
        private const bool EnableDividerNoCopyBits = false; // Experiment (disabled): SWP_NOCOPYBITS on our window's divider move had NO effect on the left-edge ghost -> confirms the ghost is the DWM compositor async-present lag (stale redirection surface shown until WPF's next frame), not a USER32 copy-blit. Left off. Cheap window-message/flag levers are exhausted for this artifact.
        private const bool EnableDividerDeferredResize = false; // DEFER-ON-RELEASE (new approach): during divider drag do NOT resize live; coalesce the target coord and commit ONE resize on WM_LBUTTONUP. All per-frame low-level levers (WVR_REDRAW, both blit anchors, SWP_NOCOPYBITS, frame-sync) were confirmed by logs to NOT remove the left-edge divider ghost, because the seam artifact is the cross-process neighbor's one-frame-late repaint during live joint-resize. Committing once sidesteps live resize entirely. Set false to restore live resize.
        private const bool EnableDividerGuideLine = false; // DEFER-ON-RELEASE UX: thin accent guide bar follows the cursor during the divider drag (real resize still commits once on release). Set false to drag with no visual guide (window jumps on release).
        private const bool EnableDividerSingleBatch = false; // Real-fix probe: collapse the per-frame TWO ApplyDividerBatch calls (predictive neighbor-only present + final) into ONE atomic batch (neighbor + our window + co-tiles moved together, once). Removes the redundant per-frame neighbor double-move/double-invalidate that can expose a one-frame seam. Set false to restore the two-pass predictive path.
        private const bool EnableDividerFrameSync = false; // Experiment (disabled): frame-synced divider geometry via CompositionTarget.Rendering did not remove the ghost (the ghost was the NCCALCSIZE copy-blit, now reverted to WVR_REDRAW). Off = baseline synchronous divider path. Set true only to re-experiment.
        private const bool EnableDividerFrameSyncDwmFlush = false; // Optional add-on to EnableDividerFrameSync: call DwmFlush() after applying geometry each frame to wait for DWM compose. May reduce tearing but can stall the UI thread; enable only to experiment.
        private int _ncLastW, _ncLastH; // last NCCALCSIZE client size, used to detect big transition jumps
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_UPDATENOW = 0x0100;

        private static readonly IntPtr HWND_TOPMOST = new(-1);

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TRANSPARENT = 0x00000020;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const long WS_EX_NOACTIVATE = 0x08000000;
        private const long WS_EX_LAYERED = 0x00080000;
        private const long WS_EX_TOPMOST = 0x00000008;
        private const uint WS_POPUP = 0x80000000;
        private const int LWA_ALPHA = 0x2;
        private const int SW_SHOWNOACTIVATE = 4;

        // --- Win32 layered-маска (см. StartMaskReveal): рендер минует WPF, аль��а через DWM ---

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string? lpszClassName;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(long dwExStyle, string lpClassName, string? lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, int crKey, byte bAlpha, int dwFlags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int crColor);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        // --- Вариант B: захват экрана + UpdateLayeredWindow (скриншот-маска) ---
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);
        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

        private const uint SRCCOPY = 0x00CC0020;
        private const uint CAPTUREBLT = 0x40000000;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;

        private const int GWL_STYLE = -16;
        private const long WS_CAPTION = 0x00C00000;
        private const long WS_SYSMENU = 0x00080000;
        private const long WS_THICKFRAME = 0x00040000;
        private const long WS_MINIMIZEBOX = 0x00020000;
        private const long WS_MAXIMIZEBOX = 0x00010000;

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public int uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NCCALCSIZE_PARAMS
        {
            public RECT rgrc0; // [0] предлагаемый/новый client (вход: новое окно; выход: новый client)
            public RECT rgrc1; // [1] старое окно
            public RECT rgrc2; // [2] старый client
            public IntPtr lppos;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        #endregion
    }
}