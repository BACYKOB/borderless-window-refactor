using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ControlPanel
{
    /// <summary>
    /// Базовое окно без системной рамки.
    /// <para>
    /// Мерцание при масштабировании/разворачивании в WPF возникает, когда
    /// безрамочное окно делают через <c>AllowsTransparency="True"</c>. Здесь
    /// прозрачность выключена, окно на аппаратном рендеринге, вид — через DWM-темизированную рамку.
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
    /// <para>
    /// ЯДРО (CORE partial, T2): единая карта флагов, конструктор, lifecycle-переопределения,
    /// диспетчер <see cref="WindowProc"/> и общие помощники. Реализация фич вынесена в partial-ы
    /// CHROME / SNAP / UNSNAP / TASKBAR / ANIM. Нативный слой — в BorderlessWindow.Interop.cs.
    /// </para>
    /// </summary>
    public partial class BorderlessWindow
    {
        // ====================================================================
        //  ЕДИНАЯ КАРТА ФЛАГОВ (T2)
        //  Все feature-toggle централизованы здесь. Комментарий = partial(ы),
        //  которые ПОТРЕБЛЯЮТ флаг. Значения перенесены 1:1 из исходника.
        //  (Числовые tuning-константы остаются в своих partial-ах по METHOD_MAP.)
        // --------------------------------------------------------------------
        //  Удалены как мёртвые (PLAN Часть 2): AttachControlzExChrome,
        //  DisableDwmNcRendering, GripDebugLog, EnableTroubleshootLog,
        //  EnableSnapDiagLog, EnableDivider* (×6).
        // ====================================================================

        // --- Рамка / хром (CHROME) ---
        private const bool UseThemedSystemFrame = true;         // CORE + CHROME: единственный режим (const=true, ветки не инлайним)
        private const bool ApplyDwmFrameAttributes = true;      // CHROME
        private const bool IncludeCaptionForSnap = false;       // CHROME: рабочий тюнинг hit-test, документирован
        private const bool ShrinkThemedFrame = true;            // CORE + CHROME (WM_NCCALCSIZE → ThemedFrameNcCalcSize)
        private const bool EnableSnapLayoutGapFix = true;       // CHROME
        private const bool EnableFullRedrawOnOriginMove = true; // CHROME: полный WVR_REDRAW на каждом изменённом NCCALCSIZE-кадре (known-good, без origin-move ghost)

        // --- Snap / joint-resize (SNAP) ---
        private const bool DeferJointResizeToShell = true;      // CORE + SNAP
        private const bool EnableFreeEdgeGrip = true;           // SNAP
        private const bool EnableSnapFollow = true;             // SNAP
        private const bool EnableJointResizeCursor = true;      // SNAP
        private const bool EnablePassiveFollow = true;          // SNAP

        // --- Un-snap restore (UNSNAP) ---
        private const bool EnableAnchorUnsnapResize = true;         // CORE + UNSNAP
        private const bool EnableCaptionUnsnapRestoreDrag = true;   // UNSNAP
        private const bool EnableUnsnapWinEventRestore = true;      // UNSNAP
        private const bool EnableUnsnapSuppressResnapFrame = true;  // UNSNAP (Вариант A: глушить кадр реверса в snap-rect)
        private const bool EnableUnsnapSteerGrowBack = true;       // UNSNAP (Variant A+: steer grow-back frames to floating)
        private const bool EnableUnsnapProactiveFloat = true;      // UNSNAP (Variant A++: OS-never-floats fallback)

        // --- Off-screen resize (CORE) ---
        // Для floating-окна, заехавшего ЗА край монитора, bitblt НЕ подавляем — иначе DWM
        // растягивает старую redirection-поверхность и закадровая часть «клонируется» у края.
        // EnableSuppressResizeBitBlt (=false) гейтит ручной SWP_NOCOPYBITS; PLAN Часть 2 «НЕ удалять».
        // Метод SuppressResizeBitBlt живёт в CHROME (T3) — держать, не удалять.
        private static readonly bool EnableSuppressResizeBitBlt = false; // CORE (source: static readonly)
        private const bool AllowBitBltForOffscreenResize = true;   // CORE

        // --- Taskbar / auto-hide (TASKBAR) ---
        private static readonly bool EnableCursorDrive = true;  // TASKBAR
        private static readonly bool EnableGapMask = true;      // TASKBAR

        // --- Startup / restore reveal (ANIM) ---
        private static readonly bool EnableStartupMask = true;  // ANIM

        // --- НОВЫЕ ФЛАГИ (T2, включены по умолчанию) ---
        private const bool EnableSeamGapFix = true;             // SNAP  (T4: устранение зазора шва при joint-resize)
        private const bool EnableLeftEdgeGhostGuard = true;     // CHROME (T3: охрана от «призрака» у левого края)

        // ====================================================================
        //  ПОЛЯ ЯДРА
        // ====================================================================
        // Высота зоны заголовка в DIP (первая строка Grid титула = 25). Пустая зона = HTCLIENT,
        // ОС сама не таскает; перекрыть в наследнике, если высота шапки иная.
        protected double CaptionHeight { get; set; } = 25;

        private bool _startupRevealStarted; // однократный старт наплыва маски (OnContentRendered/OnSourceInitialized)

        // БАГ off-screen-клон: floating-окно, заехавшее ЗА край монитор��, при уменьшении
        // ресайзом оставляет «клон» (устаревшую DWM/redirection-поверхность) в ОСВОБОЖДЁННОЙ
        // зоне. Запоминаем rect окна на старте цикла, чтобы на выходе вычислить и
        // инвалидировать именно освобождённую часть экрана.
        private RECT _offscreenPrevRect;
        private bool _offscreenPrevValid;

        // ====================================================================
        //  КОНСТРУКТОР
        // ====================================================================
        public BorderlessWindow()
        {
            // Инициализируем ПЕРВЫМ: старт в maximized синхронно вызывает OnStateChanged →
            // UpdateEdgeWatcher → StartEdgeWatcher, который обращается к _edgeWatcher.
            // Иначе — NRE при запуске в полноэкранном режиме.
            _edgeWatcher = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(10),
            };
            _edgeWatcher.Tick += EdgeWatcher_Tick;

            // WindowStyle=None — единственный режим без белой вспышки при старте: WPF НЕ рисует
            // стандартную рамку/заголовок. SingleBorderWindow даёт вспышку. Цена None: теряется
            // системная анимация сворачивания/восстановления. При None стили рамки (WS_THICKFRAME
            // и т.п.) возвращаются вручную в OnSourceInitialized (EnsureResizeStyles) — иначе
            // ресайз/перетаскивание не работают.
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false; // аппаратный рендеринг — нет мерцания окна
            ResizeMode = ResizeMode.CanResize;

            CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand,
                (_, e) => SystemCommands.CloseWindow((Window)e.Parameter)));
            // Свернуть по кнопке caption — через анимацию маски. Восстановление из трея ловится в OnStateChanged.
            CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand,
                (_, _) => AnimateMinimize()));
            // Развернуть/восстановить по кнопкам caption — через анимацию маски (как и двойной клик).
            CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand,
                (_, _) => AnimateToWindowState(WindowState.Maximized)));
            CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand,
                (_, _) => AnimateToWindowState(WindowState.Normal)));
        }

        // ====================================================================
        //  LIFECYCLE OVERRIDES
        // ====================================================================
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

            // При WindowStyle=None WPF снимает стили рамки. Возвращаем вручную (как делал встроенный
            // WindowChrome). Делаем ПОСЛЕ base.OnSourceInitialized и ДО RestorePlacement (он может
            // развернуть окно — стили нужны заранее).
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
            // наплыва маски.
            if (_startupHiding)
            {
                int recloak = 1;
                DwmSetWindowAttribute(handle, DWMWA_CLOAK, ref recloak, sizeof(int));
            }

            // Маску поднимаем не здесь, а в OnContentRendered — когда главное окно уже отрисовало
            // первый кадр (под cloak). ContextIdle — страховка, если ContentRendered не придёт.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(TryStartMaskReveal));
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            TryStartMaskReveal();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            // Восстановление из свёрнутого — материализуем окно «как при старте»: cloak + наплыв маски,
            // на пике uncloak, растворение. Сворачивание и прочие переходы здесь маской не трогаем.
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
        /// Пустая зона титула возвращает <c>HTCLIENT</c>, и система сама окно не двигает, поэтому
        /// делае�� это вручную. Клики по интерактивным элементам шапки (кнопки/меню) помечаются
        /// <c>Handled</c> и сюда не доходят. Верхние <c>ResizeBorderThickness</c> px — это
        /// <c>HTTOP</c> (нон-клиент, ресайз), не клиент, поэтому конфликта с ресайзом сверху нет.
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
            // как штатное окно Windows: НЕ на простой клик по шапке, а только после протяжки вниз.
            if (UseThemedSystemFrame && TryBeginCaptionUnsnapRestoreDrag(e))
            {
                e.Handled = true;
                return;
            }

            // В режиме UseThemedSystemFrame перетаскивание делает не WPF DragMove, а штатный shell через
            // WM_NCHITTEST -> HTCAPTION. Это важно для окон, которые до этого были snapped в колонку: Windows
            // должна сама выполнить unsnap/restore и пересчитать DWM/NC-геометрию.
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

        // ====================================================================
        //  WINDOWPROC — ДИСПЕТЧЕР Win32-СООБЩЕНИЙ
        //  Диагностика (LogIncoming/LogHit/SnapLog/TsLog) и мёртвые ветки
        //  (SuppressResizeBitBlt) удалены в T2. Ветки вызывают методы,
        //  живущие в CHROME / SNAP / UNSNAP partial-ах.
        // ====================================================================
        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // СТРАХОВКА caption-unsnap pending: перехват Win32-сообщений ДО WPF input.
            // Блокируем любые попытки shell-modal-drag и ведём порог напрямую из WM_MOUSEMOVE,
            // не завися от WPF capture/route.
            if (_captionUnsnapPending || _captionUnsnapDragging)
            {
                if (msg == WM_NCLBUTTONDOWN)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                if (msg == WM_SYSCOMMAND)
                {
                    int sc = (int)(wParam.ToInt64() & 0xFFF0);
                    if (sc == SC_MOVE || sc == SC_SIZE)
                    {
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
                    EndCaptionUnsnapRestoreDrag(source: "wm-lbuttonup");
                }
            }

            switch (msg)
            {
                case WM_ERASEBKGND:
                    // Мигание при анснапе: пока идёт armed OS-unsnap, ОС гоняет size restore↔regrow, и стандартное
                    // стирание клиента сплошной кистью фона мигает всем окном. Не гасим клиент — держим прежние пиксели.
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
                    // HTNOWHERE, и в 3-колоночной раскладке курсор обычно попадает именно в эту HTNOWHERE-полосу.
                    int htLo = (int)(lParam.ToInt64() & 0xFFFF);
                    bool acceptHt = (htLo == HTCLIENT || htLo == HTNOWHERE);
                    bool setOk = UseThemedSystemFrame && acceptHt && TrySetJointResizeCursor(hwnd);
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
                    // floating-окна (не snapped/maximized), которое заехало за край монитора.
                    _offscreenPrevValid = WindowState == WindowState.Normal &&
                                          GetWindowRect(hwnd, out _offscreenPrevRect) &&
                                          !HasInternalSnapDivider(hwnd) &&
                                          RectCrossesItsMonitor(hwnd, _offscreenPrevRect);
                    // ЧАСТЬ 1.1: запоминаем якорь анти-скачка ТОЛЬКО если окно стартует snapped.
                    _sizeAnchorValid = EnableAnchorUnsnapResize && UseThemedSystemFrame &&
                                       WindowState == WindowState.Normal &&
                                       GetWindowRect(hwnd, out _sizeAnchor) && HasInternalSnapDivider(hwnd);
                    // BUG2: ловим snapped-соседей ДО ресайза (окна ещё вплотную), чтобы двигать их вслед
                    // за краем, пока SnapFollow подавлен из-за _inSizeMove.
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
                    }
                    break;
                case WM_SIZING:
                    // Пришёл только при тяге РАМКИ → это настоящий пользовательский edge-resize.
                    _userEdgeResize = true;
                    _sizingEdge = (int)wParam; // BUG2: какой край рамки тянут (WMSZ_*)
                    // ЧАСТЬ 1.1 (анти-скачок): окно стартовало snapped → удерживаем НЕ-перетягиваемые края на
                    // якоре, чтобы un-snap-restore не сдвигал окно (см. EnableAnchorUnsnapResize).
                    if (_sizeAnchorValid && AnchorUnsnapResize(wParam, lParam))
                    {
                        handled = true;
                        return new IntPtr(1); // TRUE: мы изменили предлагаемый прямоугольник
                    }
                    break;
                case WM_EXITSIZEMOVE:
                {
                    ArmFrameReleaseRealign(); // BUG2: после рамочного ресайза тоже лечим shell-дрейф
                    _inSizeMove = false;
                    _userEdgeResize = false;
                    _sizeChangedInLoop = false;
                    _sizeAnchorValid = false; // ЧАСТЬ 1.1: цикл завершён — якорь анти-скачка больше не действует
                    _offscreenPrevValid = false;
                    _sizingEdge = 0;
                    _frameJointArmed = false;
                    _frameNbrsR.Clear();
                    _frameNbrsL.Clear();
                    RefreshEdgeGrip(); // 1.2: край settled — переставляем наружный grip
                    break;
                }
                case WM_WINDOWPOSCHANGING:
                    // Запоминаем, что в текущем цикле реально менялся размер (для форс-пересчёта на выходе).
                    // Вариант A: глушим кадр реверса в snap-rect во время armed OS-unsnap (упреждает телепорт).
                    MaybeSuppressUnsnapResnapFrame(lParam);
                    if (_inSizeMove && IsResizePosChange(lParam))
                        _sizeChangedInLoop = true;
                    // ФИКС 1 (T4, frame-resize, «grower-first»): если наш край при тяге рамки идёт
                    // ВНУТРЬ, растущий сосед коммитится ЗДЕСЬ — до применения нашего кадра, чтобы
                    // между кадрами не мелькал рабочий стол (см. FollowFrameResizeNeighborsPre).
                    if (EnableSeamGapFix && _inSizeMove && _userEdgeResize && _frameJointArmed)
                        FollowFrameResizeNeighborsPre(hwnd, lParam);
                    // ГЛАВНЫЙ ФИКС off-screen-клона: окно за краем монитора — не подавляем bitblt.
                    bool offscreenEdgeResize = AllowBitBltForOffscreenResize &&
                                               _inSizeMove && _userEdgeResize && _offscreenPrevValid;
                    if (EnableSuppressResizeBitBlt && !offscreenEdgeResize)
                        SuppressResizeBitBlt(lParam);
                    break;
                case WM_WINDOWPOSCHANGED:
                    // После Snap / Joint Resize геометрия уже применена. Если у окна есть внутренний Snap-
                    // разделитель, оставляем 1 физ-px NC-guard против DPI-наезда и просим DWM закрасить именно NC-рамку.
                    if (UseThemedSystemFrame && ApplyDwmFrameAttributes && EnableSnapLayoutGapFix)
                        UpdateSnapDwmBorderColor(hwnd);
                    if (UseThemedSystemFrame)
                    {
                        ApplyThemedBorderMetrics();
                        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN);
                        // Offscreen-resize: окно частью за краем монитора и тянется рамкой —
                        // синхронно перерисовываем его целиком, чтобы DWM не показывал клон закадровой области.
                        if (_inSizeMove && _userEdgeResize && FloatingWindowCrossesMonitor(hwnd))
                            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                                RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
                    }
                    // БАГ 1: окно могло снапнуться/раснапнуться — вкл/выкл слежение за ползунком joint-resize.
                    // BUG2: при ручной тяге РАМКИ двигаем захваченного снапнутого соседа вслед за краем.
                    if (_inSizeMove && _userEdgeResize && _frameJointArmed)
                        FollowFrameResizeNeighbors(hwnd);
                    UpdateSnapFollow();
                    UpdateCaptionUnsnapRestoreCache(hwnd);
                    // BUG2: в коротком окне после отпускания grip/рамки подтягиваем захваченного соседа
                    // вплотную к осевшему краю — гасим дрейф от shell-ре-снапа.
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
                    RefreshEdgeGrip(); // 1.2: окно сдвинулось/снапнулось — обновляем наружный grip
                    break;
                case WM_NCCALCSIZE:
                    if (UseThemedSystemFrame && ShrinkThemedFrame && wParam != IntPtr.Zero)
                        return ThemedFrameNcCalcSize(hwnd, wParam, lParam, ref handled);
                    break;
                case WM_NCACTIVATE:
                    // ПУТЬ A: при смене активности окна DefWindowProc перерисовывает NC-рамку и рисует белую
                    // линию сверху. lParam=-1 говорит DefWindowProc НЕ перерисовывать NC: тёмная DWM-граница
                    // остаётся, белой линии нет. wParam (active/inactive) пробрасываем штатно.
                    if (UseThemedSystemFrame)
                    {
                        handled = true;
                        return DefWindowProcW(hwnd, WM_NCACTIVATE, wParam, new IntPtr(-1));
                    }
                    break;
                case WM_NCPAINT:
                    // ПУТЬ A: подавляем NC-отрисовку DefWindowProc (его белую линию рамки). Видимую рамку 1px
                    // рисует WPF-бордер у внутренней кромки клиента.
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
                        // пределами ТЕКУЩЕГО монитора — отдаём HTTRANSPARENT, чтобы соседний монитор был кликабелен.
                        if (IsOutsideCurrentMonitor(hwnd, lParam))
                        {
                            handled = true;
                            return new IntPtr(HTTRANSPARENT);
                        }
                        // (2) Хват ресайза. Стороны отдаём только в реальной NC-полосе между HWND и client;
                        // верхний хват делаем шире вручную.
                        int ht = ThemedHitTest(hwnd, lParam);
                        if (ht != HTNOWHERE)
                        {
                            handled = true;
                            return new IntPtr(ht);
                        }

                        // (3) Пустая часть нашей WPF-шапки должна быть настоящим HTCAPTION (нативный detach из
                        // Snap, drag между мониторами, Aero Snap). Интерактивные элементы шапки сюда не попадают.
                        // BUG2 top joint-resize: на внутреннем ВЕРХНЕМ snap-шве НЕ возвращаем HTCAPTION —
                        // иначе стартует наш move-loop и SnapFollow подавляется. Возвращаем HTCLIENT на тонкой
                        // полосе вокруг видимого верхнего края, чтобы move-loop не стартовал и SnapFollow
                        // мог зацепить верхний край (edge=3).
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
                                    return new IntPtr(HTCLIENT);
                                }
                            }
                        }
                        if (IsInDraggableCaption(hwnd, lParam))
                        {
                            handled = true;

                            // Для full-height snapped окна не отдаём первый down в native HTCAPTION: иначе shell
                            // стартует move-loop сразу, а нам нужно дождаться протяжки вниз. Делаем это только
                            // если уже можем найти restore rect. Иначе оставляем штатный HTCAPTION.
                            if (IsCaptionUnsnapRestoreCandidate(hwnd) &&
                                GetWindowRect(hwnd, out RECT captionWin) &&
                                TryGetCaptionRestoreRect(hwnd, captionWin, out _))
                            {
                                return new IntPtr(HTCLIENT);
                            }

                            return new IntPtr(HTCAPTION);
                        }
                    }
                    break;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Является ли WINDOWPOS-изменение изменением РАЗМЕРА (а не только позиции/z-order).
        /// SWP_NOSIZE снят → размер меняется.
        /// </summary>
        private static bool IsResizePosChange(IntPtr lParam)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            return (wp.flags & SWP_NOSIZE) == 0;
        }
    }
}
