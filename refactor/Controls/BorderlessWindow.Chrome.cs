using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace ControlPanel
{
    // ============================================================================
    //  BorderlessWindow — CHROME partial (T3)
    //  Themed system frame (ПУТЬ A): стили рамки, DWM-атрибуты, метрики WPF-бордера,
    //  перехват WM_NCCALCSIZE (ThemedFrameNcCalcSize), hit-test (ThemedHitTest,
    //  IsInDraggableCaption, IsOverTitleInteractive), snap-layout gap-fix,
    //  DWM border color на внутренних Snap-разделителях, границы maximized.
    //
    //  Удалено при переносе (PLAN Часть 2 / METHOD_MAP §3):
    //   - TryBuildAlignedValidRects + ветка EnableFullRedrawOnOriginMove=false
    //     (WVR_REDRAW стал безусловным на изменённом client — baseline-поведение);
    //   - DisableDwmNcRendering-ветка (мёртвый эксперимент toolkit NC-rendering);
    //   - вся диагностика TsLog/SnapLog и троттлинг-поле _lastNoEdgeDiagTick.
    //
    //  ФИКС 2 (T3, скорректирован аудитом): EnableLeftEdgeGhostGuard (флаг в ядре,
    //  default true) — 1px NC-inset на ЛЕВОМ крае в ThemedFrameNcCalcSize
    //  (симметрично верхнему ghost-guard ThemedFrameInset). WPF-бордер слева
    //  НЕ обнуляется (точная симметрия с верхом: там inset + top-бордер 1px
    //  сосуществуют). Итог: floating — 1 видимый px (WPF-бордер; NC-полоса при
    //  DWMWA_COLOR_NONE не красится), snapped с внутренним разделителем — 2 px
    //  (WPF-бордер + DWM-полоса в ThemeBorderColorRef) — желаемое поведение,
    //  подтверждено пользователем. При false — поведение бит-в-бит как в исходнике.
    //
    //  Флаги (UseThemedSystemFrame, ApplyDwmFrameAttributes, IncludeCaptionForSnap,
    //  ShrinkThemedFrame, EnableSnapLayoutGapFix, EnableFullRedrawOnOriginMove,
    //  DeferJointResizeToShell, EnableLeftEdgeGhostGuard) объявлены в ядре
    //  (BorderlessWindow.cs). Константы цвета MaskColorRef/ThemeBorderColorRef —
    //  в ANIM partial (T5). Здесь только chrome-локальные tuning-константы и поля.
    // ============================================================================
    public partial class BorderlessWindow
    {
        // ------------------------------------------------------------------
        //  Tuning-константы CHROME
        // ------------------------------------------------------------------
        // ПУТЬ A: ширина зоны хвата ресайза (физ. px). Видимая рамка остаётся 1px — это лишь область, где
        // WM_NCHITTEST возвращает HT*-коды. Больше = легче попасть, но «съедает» верх кнопок шапки. ~6 px.
        // Fallback, если системные метрики недоступны (см. GetResizeGrip).
        private const int ResizeGripPx = 6;
        // ПУТЬ A: тонкая верхняя зона ресайза НАД кнопками шапки (физ. px) — чтобы почти не срезать кнопки.
        // На пустом месте шапки используется полная ширина GetResizeGrip. См. ThemedHitTest/IsOverTitleInteractive.
        private const int ResizeGripThin = 2;

        // Режим UseThemedSystemFrame: на сколько px системная рамка остаётся видимой (физ. px).
        // 1px — видимой «полки»/толстого канта нет (тонкая линия, как BorderThickness=1), но клиент НЕ
        // достаёт до края окна (там 1px системной рамки), поэтому призрак при ресайзе не возвращается.
        // Применяется к ВЕРХУ (исходный ghost-guard) и, при EnableLeftEdgeGhostGuard, к ЛЕВОМУ краю (фикс 2).
        private const int ThemedFrameInset = 1;

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
        // наезд. Важно: NCCALCSIZE работает в физических пикселях, поэтому здесь НЕ DIP и НЕ масштабируем.
        private const int SnapInternalDividerGuardPx = 1;

        // per-frame client-size delta above this = snap/unsnap transition: skip forced WVR_REDRAW to avoid
        // a full-window blank flash
        private const int NcRedrawMaxDeltaPx = 250;

        // ------------------------------------------------------------------
        //  Поля CHROME
        // ------------------------------------------------------------------
        private bool _snapDwmBorderColorApplied;

        private int _ncLastW, _ncLastH; // last NCCALCSIZE client size, used to detect big transition jumps

        // ------------------------------------------------------------------
        //  Стили рамки / DWM-атрибуты
        // ------------------------------------------------------------------
        private void EnsureResizeStyles(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            style |= WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
            // ЭКСПЕРИМЕНТ (БАГ 1): возвращаем WS_CAPTION. Без него shell не считает окно полноценным
            // участником Snap-группы → joint-resize ползунком не работает (окно либо un-снапится, либо вообще
            // не двигается — ДОКАЗАНО логами). Визуал caption гасит ThemedFrameNcCalcSize (client до верха) +
            // DWM-атрибуты (caption-цвет=фон, immersive dark). true = вернуть caption (проверяем snap). false =
            // прежнее borderless-поведение без caption.
            if (IncludeCaptionForSnap)
                style |= WS_CAPTION;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        /// <summary>
        /// ПУТЬ A: окно сохраняет СИСТЕМНУЮ рамку DWM (ради чистого ресайза без призрака), но её визуал
        /// убираем полностью: квадратные углы (DONOTROUND), тёмный режим (immersive dark), а главное —
        /// <b>DWM-рамку выключаем</b> (<see cref="DWMWA_COLOR_NONE"/>). Её толщину нельзя задать в 1 физ.
        /// пиксель (она масштабируется по DPI → у пользователя 2px), и она давала «тёмную рамку вокруг
        /// светлой». Единственную видимую рамку рисуем сами ровно в 1 физ. px WPF-бордером
        /// (<see cref="ApplyThemedBorderMetrics"/>); верх (и при фиксе 2 — лево) держит охранный
        /// NC-inset (<see cref="ThemedFrameNcCalcSize"/>), который держит призрак подальше от движущихся краёв.
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
            int captionColor = MaskColorRef; // #191A1C — caption-зона в цвет фона (на случай если мелькнёт)
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

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
        /// 1/DpiScale, чтобы на любом масштабе (125/150/200%) лечь ровно в 1 device-пиксель.
        /// Рамку 1 физ. px рисует WPF-бордер; клиент инсечен от движущихся краёв (ThemedFrameNcCalcSize),
        /// чтобы WPF-бордер не оказывался на движущемся крае (призрак).
        /// В развёрнутом окне рамки нет.
        /// <para>
        /// ФИКС 2 (EnableLeftEdgeGhostGuard) — ревизия аудита: WPF-бордер слева НЕ обнуляется.
        /// Точная симметрия с верхним ghost-guard: сверху NC-inset и top-бордер 1px сосуществуют.
        /// Floating: DWM-рамка = COLOR_NONE → NC-полоса не красится, видимый левый край = 1px WPF-бордер.
        /// Snapped с внутренним разделителем: DWM красит полосу в ThemeBorderColorRef → 2px суммарно —
        /// это и есть желаемое поведение исходника (боковые 1px в обычном состоянии, 2px при снапе).
        /// Метрики бордера при флаге on/off ИДЕНТИЧНЫ исходнику; отличается только client-rect (NCCALCSIZE).
        /// </para>
        /// </summary>
        private void ApplyThemedBorderMetrics()
        {
            if (!UseThemedSystemFrame) return;
            if (WindowState == WindowState.Maximized) { BorderThickness = new Thickness(0); return; }

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double tx = 1.0 / dpi.DpiScaleX;  // DIP, дающий ровно 1 физ. px по горизонтали
            double ty = 1.0 / dpi.DpiScaleY;  // ... по вертикали
            // Рамку 1 физ. px рисует WPF-бордер со всех сторон, как в исходнике (и при ghost-guard тоже:
            // левый NC-inset живёт ЗА бордером, как верхний — см. doc-comment выше).
            BorderThickness = new Thickness(tx, ty, tx, ty);
        }

        // ------------------------------------------------------------------
        //  Hit-test
        // ------------------------------------------------------------------
        private int ThemedHitTest(IntPtr hwnd, IntPtr lParam)
        {
            if (WindowState == WindowState.Maximized) return HTNOWHERE;
            if (!GetWindowRect(hwnd, out RECT r)) return HTNOWHERE;
            if (!TryGetClientRectScreen(hwnd, out RECT c)) return HTNOWHERE;

            int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
            int g = GetResizeGrip(hwnd);

            // Над кнопкой — тонкая полоса (почти не срезаем кнопку), на пустом месте шапки — полная.
            int topBand = IsOverTitleInteractive(x, y) ? ResizeGripThin : g;
            TryGetSnapInternalEdges(hwnd, out bool snapLeft, out bool snapRight, out bool snapTop, out bool snapBottom);

            // БАГ 1 (ДОКАЗАНО логами): на ВНУТРЕННИХ Snap-границах (за окном — соседнее окно Snap-группы, а не
            // край экрана) ВООБЩЕ НЕ заявляем ресайз. Иначе невидимый sizing-выступ нашего окна возвращает
            // HTLEFT/HTRIGHT/HTBOTTOM ровно под ползунком Snap-группы (он лежит на видимой границе), клик
            // попадает в НАШ край → Windows трактует это как РУЧНОЙ ресайз snapped-окна → UN-снапит его в
            // плавающую ширину и тащит индивидуально («окно уезжает целиком вправо»). Отдавая внутренние края
            // shell'у (HTNOWHERE), возвращаем штатный групповой joint-resize ползунком. Внешние края (край
            // экрана) и верхний хват над пустой шапкой не трогаем. См. DeferJointResizeToShell.
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

        private bool IsInDraggableCaption(IntPtr hwnd, IntPtr lParam)
        {
            if (WindowState == WindowState.Maximized) return false;
            if (!GetWindowRect(hwnd, out RECT r)) return false;

            int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
            var p = PointFromScreen(new Point(x, y));
            if (p.Y < 0 || p.Y > CaptionHeight) return false;

            // Не превращаем верхний resize-handle в caption, иначе потеряется HTTOP.
            int topBand = IsOverTitleInteractive(x, y) ? ResizeGripThin : GetResizeGrip(hwnd);
            if (y < r.Top + topBand) return false;

            return !IsOverTitleInteractive(x, y);
        }

        /// <summary>
        /// ПУТЬ A: ширина зоны хвата ресайза (физ. px) = ТОЛЩИНА СТАНДАРТНОЙ системной рамки ресайза для DPI
        /// окна: SM_CXSIZEFRAME + SM_CXPADDEDBORDER (то же, что у обычных окон Windows). DPI-зависима, поэтому
        /// на 150/200% полоса шире (px), и хват ощущается как у системных окон, а не «2px». Видимая рамка
        /// остаётся 1px — это лишь зона хит-теста. Раньше была фикс. 6 физ. px → на высоком DPI казалась тонкой.
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
        /// ПУТЬ A: true, если точка экрана (физ. px) над интерактивным элементом шапки (кнопки/меню/список).
        /// Используется в <see cref="ThemedHitTest"/>, чтобы над кнопками верхняя зона ресайза была тонкой
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

        // ------------------------------------------------------------------
        //  WM_NCCALCSIZE
        // ------------------------------------------------------------------
        /// <summary>
        /// Режим UseThemedSystemFrame (ПУТЬ A): держим системную рамку ради чистого ресайза без призрака, но
        /// делаем её невидимой. Развёрнутое — клиент на всё окно (рамки на maximized нет). Обычное:
        /// • БОКА/НИЗ — оставляем СТАНДАРТНЫЙ отступ DefWindowProc (sizing-граница ~7px): окно выступает за
        ///   видимый край, и хват ресайза там работает СНАРУЖИ видимого края, как у обычных окон Windows.
        /// • ВЕРХ — инсет <see cref="ThemedFrameInset"/> px (ghost-guard: на движущемся верхнем крае не
        ///   WPF-поверхность → призрак за верх не возвращается). Верхний хват даёт <see cref="ThemedHitTest"/>.
        /// • ЛЕВО (ФИКС 2, EnableLeftEdgeGhostGuard) — такой же инсет <see cref="ThemedFrameInset"/> px от
        ///   client-края DefWindowProc: клиент (WPF redirection-поверхность) больше не достаёт до видимого
        ///   левого края → растяг поверхности силами DWM не виден на движущемся крае — та же механика, что
        ///   уже спасла верх. Применяется ДО клампов к work-area, в том же порядке, что и верхний инсет.
        /// При snap Windows ставит ПРЯМОУГОЛЬНИК ОКНА на (граница рабочей области − ~6px sizing), а клиент внутри
        /// него на +6px → ВИДИМЫЙ край ложится ровно на границу (как у всех окон). Отступ НЕ обнуляем — обнуление
        /// вынесло бы видимый клиент на ~6px наружу (вылезал за монитор). Мёртвую зону на СОСЕДНЕМ мониторе от
        /// нашего выступа гасит WM_NCHITTEST (HTTRANSPARENT вне rcMonitor, см. IsOutsideCurrentMonitor).
        /// Видимую рамку 1px рисует WPF-бордер у внутренней кромки клиента.
        /// </summary>
        private IntPtr ThemedFrameNcCalcSize(IntPtr hwnd, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            handled = true;

            if (WindowState == WindowState.Maximized)
                return IntPtr.Zero; // client = всё окно (границы задаёт WM_GETMINMAXINFO); inset (top/left) = 0

            // Прямоугольник нового ОКНА читаем ДО DefWindowProc — он перезапишет rgrc0 рассчитанным клиентом.
            var ncp = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(lParam);
            int wl = ncp.rgrc0.Left, wt = ncp.rgrc0.Top, wr = ncp.rgrc0.Right, wb = ncp.rgrc0.Bottom;
            IntPtr res = DefWindowProcW(hwnd, WM_NCCALCSIZE, wParam, lParam); // valid-rects + стандартные отступы
            var calc = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(lParam);
            int dwL = calc.rgrc0.Left, dwT = calc.rgrc0.Top, dwR = calc.rgrc0.Right, dwB = calc.rgrc0.Bottom; // DefWindowProc client (pre gap-fix)
            // БОКА/НИЗ: оставляем СТАНДАРТНЫЙ неклиентский отступ DefWindowProc (sizing-граница ~7px). Окно
            // выступает за видимый край на эти px — там WM_NCHITTEST ловит ресайз СНАРУЖИ видимого края, как у
            // обычных окон Windows. DWM эту границу не показывает сквозной (WS_THICKFRAME), дыры у плавающего
            // окна нет; видимую рамку 1px рисует WPF-бордер у внутренней кромки клиента.
            // ВЕРХ: инсет 1px (ThemedFrameInset) — ghost-guard (не WPF на движущемся крае). Верхний хват даёт
            // ThemedHitTest над/между кнопками.
            // БАГ 1: в caption-режиме верх делаем FULL-BLEED (client.Top = wt), чтобы НЕ осталось неклиентской
            // зоны caption — иначе система рисует там полосу заголовка. Ghost-guard сверху теряем (цена snap).
            // Без caption — прежний 1px-инсет (анти-призрак сверху).
            calc.rgrc0.Top = wt + (IncludeCaptionForSnap ? 0 : ThemedFrameInset);
            // ФИКС 2 (T3): ЛЕВЫЙ ghost-guard. Отодвигаем client на ThemedFrameInset px ВНУТРЬ от левого
            // client-края DefWindowProc (= видимый левый край окна): левый видимый 1px становится DWM-owned
            // NC-полосой, WPF-поверхность не достаёт до движущегося края → призрак при left-drag не рисуется.
            // Порядок как у верхнего инсета: ДО клампов к work-area (кламп при snap к левому краю экрана
            // перекроет инсет, как перекрывает и верхний — там край не движется, guard не нужен).
            // WPF-бордер слева ОСТАЁТСЯ 1px (ревизия аудита, симметрия с верхом): floating — видна только
            // WPF-линия (NC-полоса при COLOR_NONE не красится), snapped — WPF + DWM-полоса = 2px (желаемое).
            if (EnableLeftEdgeGhostGuard)
                calc.rgrc0.Left += ThemedFrameInset;
            // SNAP: прижимаем ВИДИМЫЙ край клиента ТОЧНО к границе рабочей области. Windows при snap ставит
            // прямоугольник окна с поправкой на рамку, которую предполагает САМА (~4px), а наш отступ
            // DefWindowProc больше (~6px) — разница уходила внутрь зазором ~2px. Координаты NCCALCSIZE —
            // ЭКРАННЫЕ, поэтому приравниваем клиент к rcWork на том крае, где он выступал бы за неё. Это и
            // убирает зазор, и не выносит клиент наружу (вылезания за монитор нет). Выступ-хват за rcWork
            // обезврежен на соседнем мониторе через HTTRANSPARENT (IsOutsideCurrentMonitor).
            bool ncLargeOverhang = false;
            if (TryGetWorkArea(hwnd, out RECT wa))
            {
                // Прижимаем ВИДИМЫЙ край клиента к rcWork ТОЛЬКО при небольшом выступе рамки (SNAP). Большой
                // выступ = окно вынесено за край монитора как свободное — НЕ прижимаем (иначе off-screen клон).
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
            }
            // Our client rect intentionally differs from DefWindowProc's (top/left ghost-guard inset, work-area
            // clamps, inner-seam gap fix). The default resize blit is aligned to DefWindowProc's client, so
            // any difference smears the window over itself during resize (self-overlay stripes). Force a full
            // client repaint whenever we changed the client; this is flicker-free under DWM/WPF composition.
            // WVR_REDRAW forces a FULL client repaint (flicker-free + kills the self-overlay smear on incremental
            // resize, since our client differs from DefWindowProc by the inset/clamps). But on a LARGE single-
            // frame jump (snap<->unsnap state transition) the full erase blanks the whole window for one frame ->
            // it visibly disappears. Skip the FORCED redraw only on such big jumps (hand-resize stays small).
            //
            // BASELINE (EnableFullRedrawOnOriginMove=true, known-good): полный WVR_REDRAW на каждом изменённом
            // client-кадре — НЕТ origin-move ghost. Экспериментальный aligned copy-blit (WVR_VALIDRECTS,
            // TryBuildAlignedValidRects; flag=false) — источник left-edge ghost — удалён (PLAN Часть 2 п.3):
            // WVR_REDRAW стал безусловным для изменённого client.
            int ncNewW = calc.rgrc0.Right - calc.rgrc0.Left;
            int ncNewH = calc.rgrc0.Bottom - calc.rgrc0.Top;
            bool ncBigJump = _ncLastW > 0 && !ncLargeOverhang && (Math.Abs(ncNewW - _ncLastW) > NcRedrawMaxDeltaPx || Math.Abs(ncNewH - _ncLastH) > NcRedrawMaxDeltaPx);
            _ncLastW = ncNewW; _ncLastH = ncNewH;
            if (!ncBigJump && (calc.rgrc0.Left != dwL || calc.rgrc0.Top != dwT || calc.rgrc0.Right != dwR || calc.rgrc0.Bottom != dwB))
            {
                res = (IntPtr)WVR_REDRAW;
            }
            Marshal.StructureToPtr(calc, lParam, false);
            return res;
        }

        // ------------------------------------------------------------------
        //  Snap-layout gap-fix / DWM border color
        // ------------------------------------------------------------------
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

            // Вертикальные Snap-колонки: left/right/center layouts, включая Win+Left/Win+Right и Joint Resize.
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
            // когда верх является внешним краем рабочей области, но внутреннюю горизонтальную щель закрываем.
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
        /// При floating оставляем DWMWA_COLOR_NONE, как было раньше. На Snap-разделителе DWM штатно
        /// закрашивает оставленный 1 физ-px NC-guard, поэтому нет ни наезда client на соседа, ни пустого
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
            return left || right || top || bottom;
        }

        // ------------------------------------------------------------------
        //  Мониторы / рабочая область
        // ------------------------------------------------------------------
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
        /// ПУТЬ A: true, если точка WM_NCHITTEST (экран, физ. px) лежит ЗА пределами монитора, на котором сейчас
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
        /// Убирает «дребезг» противоположного края при ресайзе за ВЕРХ/ЛЕВО.
        /// <para>
        /// При таком ресайзе Windows делает BitBlt — копирует старый кадр, считая опорой
        /// верхне-левый угол. WPF красит содержимое на своём render-потоке асинхронно, и
        /// копия рассинхронизируется с новой рамкой → нижний/правый край колеблется. (При
        /// ресайзе за низ/право копирования нет — опора неподвижна, поэтому там гладко.)
        /// </para>
        /// <para>
        /// Флаг <c>SWP_NOCOPYBITS</c> на <c>WM_WINDOWPOSCHANGING</c> подавляет этот BitBlt:
        /// окно перерисовывается целиком вместо копирования старого кадра. Сообщение НЕ
        /// помечаем handled — даём ему дойти до DefWindowProc с уже изменёнными флагами.
        /// Это решение без glass frame (баги dotnet/wpf #1176/#3193): нет белой рамки/вспышки.
        /// </para>
        /// </summary>
        private static void SuppressResizeBitBlt(IntPtr lParam)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((wp.flags & SWP_NOSIZE) == 0) // только когда реально меняется размер
            {
                wp.flags |= SWP_NOCOPYBITS;
                Marshal.StructureToPtr(wp, lParam, false);
            }
        }

        // ------------------------------------------------------------------
        //  Maximized bounds
        // ------------------------------------------------------------------
        /// <summary>
        /// При обычной панели — рабочая область монитора. При нижней auto-hide панели —
        /// ВЕСЬ монитор. Если активно «сжатие» (_shrunk) — высота на 1px меньше, чтобы
        /// система не «прыгнула» окном на весь экран и панель не спряталась.
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
            // «Накрываем» весь монитор только на мониторе с главной панелью задач; на
            // дополнительных — обычная рабочая область, без сдвигов и спецлогики.
            bool autoHide = IsOnTaskbarMonitor(bounds) && HasBottomAutoHideTaskbar(bounds);
            RECT target = autoHide ? bounds : work;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMaxPosition.X = target.Left - bounds.Left;
            mmi.ptMaxPosition.Y = target.Top - bounds.Top;
            mmi.ptMaxSize.X = target.Right - target.Left;
            mmi.ptMaxSize.Y = (target.Bottom - target.Top) - (autoHide && _shrunk ? 1 : 0);
            Marshal.StructureToPtr(mmi, lParam, true);
        }
    }
}
