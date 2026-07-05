using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace ControlPanel
{
    // ============================================================================
    //  BorderlessWindow — SNAP partial (T4)
    //  Snap joint-resize: free-edge grip, divider-грипы (H+V), frame-resize follow,
    //  snap-follow таймер, passive-follow, release-realign, общие хелперы
    //  TryGetVisibleBounds/TryGetMaskRect (используются и UNSNAP).
    //
    //  Удалено при переносе (PLAN Часть 2 / METHOD_MAP §4):
    //   - вся диагностика (GripLog/TsLog/SnapLog/PfLog + троттлинг-поля);
    //   - мёртвые эксперименты EnableDivider* (deferred/guide/frame-sync/no-copybits/
    //     single-batch probe) — все были false;
    //   - LogNearestRejectedNeighbor / LogPassiveCandidates (диаг-дампы).
    //
    //  ФИКС 1 (T4, PLAN Часть 3): EnableSeamGapFix — устранение межкадрового зазора
    //  шва при joint-resize:
    //   1) DivGrip-путь: кламп effDiv по MINMAXINFO соседа ЗАРАНЕЕ (без реального
    //      предиктивного сдвига) + ОДИН атомарный BeginDefer/EndDeferWindowPos
    //      (мы + соседи + co-tiles) за кадр — нет двух раздельных коммитов, нет шва.
    //   2) Frame-resize путь: «grower-first» — растущий сосед коммитится в
    //      WM_WINDOWPOSCHANGING (до нашего кадра), сжимающийся — после
    //      WM_WINDOWPOSCHANGED (наше растущее окно само накрывает шов).
    //  При EnableSeamGapFix=false — прежняя двухпроходная схема бит-в-бит.
    // ============================================================================
    public partial class BorderlessWindow
    {
        // ====================================================================
        //  Поля и константы: frame-resize цикл (BUG2)
        // ====================================================================
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
        private const int EdgeGripFlushGapPx = 16; // free-edge grip is suppressed when a tile abuts this edge within this gap (px)

        // ====================================================================
        //  Поля и константы: free-edge grip / divider-грипы
        // ====================================================================
        private const int FreeEdgeGripPx = 12;
        // WM_*/SC_*/IDC_*/SW_* — в BorderlessWindow.Interop.cs (T2), здесь не дублируются.
        private const string GripClassName = "ControlPanelFreeEdgeGrip";
        private static WndProcDelegate? _gripWndProc; // держим ссылку — иначе GC соберёт делегат
        private static bool _gripClassRegistered;
        private static readonly System.Collections.Generic.Dictionary<IntPtr, BorderlessWindow> _gripOwners = new();
        // BUG2: оверлеи ВНУТРЕННИХ разделителей (joint-resize обоих окон, не зависит от OS snap-группы)
        private const int SnapDividerGripBandPx = 7; // полуширина полосы со стороны СОСЕДА (внешняя, узкая)
        private const int SnapDividerGripInnerMarginPx = 0; // запас за пределы resize-рамки окна: внутренняя сторона полосы = GetResizeGrip + этот запас (DPI-зависимо), чтобы разделитель ловился, но края окна резалось минимум
        private const int SnapNeighborMaxGapPx = 6;     // классический snap-сосед прилегает вплотную (без зазора)
        private const int SnapNeighborEdgeAlignPx = 8;  // и имеет ту же длину общей стороны: верх и низ совпадают
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
        // ФИКС 1 (ревизия аудита): кэш MINMAXINFO соседей на время одного драга разделителя.
        // Без кэша TryGetMinTrackSize шлёт синхронный кросс-процессный WM_GETMINMAXINFO (таймаут 50ms)
        // КАЖДОМУ соседу на КАЖДЫЙ mouse-move кадр: занятый (не hung) чужой процесс может съедать до
        // 50ms×N на кадр → рывки — тот самый симптом, который фикс лечит. Min-track-size окна в течение
        // драга практически не меняется; кэш очищается при захвате грипа (свежий на каждый драг).
        private readonly System.Collections.Generic.Dictionary<IntPtr, (int minW, int minH, bool ok)> _divMinTrackCache = new();
        // BUG2: same-column co-tiles (a window stacked above/below us) sharing the dragged divider. Their near edge
        // must follow the divider too, else the column goes ragged (gap/overlap beside the other tile). Populated
        // only when our window is a sub-tile (a full-height window has nothing stacked above/below it).
        private readonly System.Collections.Generic.List<IntPtr> _divDragCoTiles = new();
        // Frame-synced divider resize (experiment): coalesce the target divider coordinate from WM_MOUSEMOVE and
        // apply it inside CompositionTarget.Rendering (right before WPF composes its next frame) so the HWND resize
        // and WPF's matching frame land together, shrinking the async gap that shows the stale surface (left-edge ghost).
        // BUG2: лечение дрейфа после ОТПУСКАНИЯ grip/рамки. Shell до-снапывает ��АШ край (напр. 2849->2957),
        // сосед остаётся на месте -> перекрытие -> FindSnapNeighbors теряет соседа. В коротком окне после
        // релиза подтягиваем захваченного соседа вплотную к осевшему краю (хэндл соседа известен).
        private long _divReleaseRealignUntil;
        private int _divReleaseSide;
        private readonly System.Collections.Generic.List<IntPtr> _divReleaseNbrs = new System.Collections.Generic.List<IntPtr>();
        private IntPtr _gripHwnd;
        private int _edgeGripHt;
        private bool _edgeGripResizing;

        // ====================================================================
        //  Поля и константы: snap-follow / passive-follow
        // ====================================================================
        // Флаги EnableSnapFollow / EnableJointResizeCursor / EnablePassiveFollow — в ядре (T3).
        private const int SnapFollowGrabBandPx = 12; // допуск близости курсора к видимой границе при нажатии (px)
        // КУРСОР joint-resize (EnableJointResizeCursor). Окно бордерлесс на внутреннем Snap-разделителе
        // возвращает HTNOWHERE (чтобы клик не запускал индивидуальный ресайз/un-snap — БАГ 1), поэтому
        // системный курсор «↔» появлялся только на узкой shell-полосе. Реальный ресайз делает SnapFollow
        // в полосе SnapFollowGrabBandPx, поэтому показываем SizeWE на ТОЙ ЖЕ полосе через WM_SETCURSOR —
        // аффорданс как у стандартных окон, без изменения WM_NCHITTEST (риска un-snap нет).
        private const int SnapFollowMinDimPx = 200;  // не ужимать окно у́же этого (px)
        private const int SnapSettleTicks = 20;
        // Passive-follow: mirror a snap-divider dragged BETWEEN two OTHER windows onto our matching edge. To avoid
        // gluing to a freely-moved window, we require the neighbor to be RESIZED on the shared side (its FAR edge
        // stays put while its NEAR edge, shared with us, moves). A translated window moves BOTH edges together and
        // is therefore ignored. See PassiveFollowNeighbors.
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
        private int _snapSettleTicks; // >0 = идёт доводка после отпускания (держим залаченный край по соседу)
        private IntPtr _leftNbr;      // HWND зафиксированного левого соседа на время перетягивания
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

        // ====================================================================
        //  Курсор joint-resize (WM_SETCURSOR из ядра)
        // ====================================================================
        private bool TrySetJointResizeCursor(IntPtr hwnd)
        {
            if (!EnableJointResizeCursor || !EnableSnapFollow || !UseThemedSystemFrame) return false;
            if (WindowState != WindowState.Normal) return false;
            bool gotEdges = TryGetSnapInternalEdges(hwnd, out bool sl, out bool sr, out _, out _);
            if (!gotEdges) { return false; }
            if (!sl && !sr) { return false; }
            if (!TryGetVisibleBounds(hwnd, out RECT vis)) { return false; }
            if (!GetCursorPos(out POINT cur)) return false;
            if (cur.Y < vis.Top || cur.Y > vis.Bottom) { return false; }
            int dl = sl ? Math.Abs(cur.X - vis.Left) : int.MaxValue;
            int dr = sr ? Math.Abs(cur.X - vis.Right) : int.MaxValue;
            if (Math.Min(dl, dr) > SnapFollowGrabBandPx) { return false; }
            // BUG2a: курсор ресайза во ВНУТРЕННЕЙ области показываем только если на хватаемой стороне есть
            // РЕАЛЬНЫЙ сосед (joint-resize через разделитель). Для СВОБОДНОГО края ресайз делает наружная
            // полоса хвата, поэтому внутри окна стрелку НЕ показываем - иначе она висит, но не работает.
            bool grabRight = dr <= dl;
            var jrcNbrs = new System.Collections.Generic.List<IntPtr>();
            bool sideHasNeighbor = grabRight
                ? FindSnapNeighbors(hwnd, vis, true, jrcNbrs, out _)
                : FindSnapNeighbors(hwnd, vis, false, jrcNbrs, out _);
            if (!sideHasNeighbor) { return false; }
            SetCursor(LoadCursorW(IntPtr.Zero, (IntPtr)IDC_SIZEWE));
            return true;
        }


        // ====================================================================
        //  Грип-окна: класс, создание, скрытие, разрушение
        // ====================================================================
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
                    if (main != IntPtr.Zero && _edgeGripHt != 0)
                    {
                        _edgeGripResizing = true;
                        ShowWindow(_gripHwnd, SW_HIDE);
                        ReleaseCapture();
                        SendMessageW(main, WM_NCLBUTTONDOWN, (IntPtr)_edgeGripHt, IntPtr.Zero);
                        _edgeGripResizing = false;
                        ScheduleEdgeGripRefresh(); // отложенно: окно оседает после модального ресайза, одиночное чтение может быть ложным
                    }
                    return IntPtr.Zero;
                }
            }
            return DefWindowProcW(h, msg, w, l);
        }

        private System.Windows.Threading.DispatcherTimer? _gripRefreshTimer;
        private int _gripRefreshTicks;

        /// <summary>
        /// Отложенный перерасчёт grip после grip-инициированного ресайза. EXITSIZEMOVE для модального цикла
        /// приходит ВНУТРИ SendMessageW (когда _edgeGripResizing ещё true и refresh заблокирован), а окно
        /// оседает не мгновенно — одиночное немедленное чтение даёт ложный rightFree=False и навсегда прячет
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
        // край и край соседа (SetWindowPos обоих окон по X курсора), держим вплотную. На время drag SnapFollow подавлен.
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
        }

        // ===== Divider guide line (deferred-resize UX) =====
        // A thin visible accent bar follows the cursor during the divider drag while the real window
        // resize commits ONCE on WM_LBUTTONUP. Restores the smooth moving-divider feel without any
        // per-frame live resize (which is what reintroduced the left-edge ghost).

        // ====================================================================
        //  Divider joint-resize (H+V), co-tiles, frame-follow, release-realign
        // ====================================================================
        private void RefreshDividerGrips()
        {
            if (!EnableFreeEdgeGrip || !UseThemedSystemFrame) return;
            if (_divDragging) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || _inSizeMove || WindowState != WindowState.Normal || !IsVisible) { HideDivGrips(); return; }
            if (!TryGetSnapInternalEdges(hwnd, out bool sl, out bool sr, out bool st, out bool sb) || (!sl && !sr && !st && !sb)) { HideDivGrips(); return; }
            if (!TryGetVisibleBounds(hwnd, out RECT vis)) { HideDivGrips(); return; }

            int top = vis.Top, h = Math.Max(1, vis.Bottom - vis.Top);
            // Внутренняя сторона полосы должна выходить за resize-рамку окна (иначе рамка перехватывает клик первой),
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
                // Полоса перекрывает ВСЮ зону разделителя: вглубь нашего окна (Inner) .. ближний край соседей (+ запас).
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

        }

        // joint-resize обоих окон по X разделителя (экранные координаты). side: 1=сосед слева, 2=справа.
        private void UpdateDividerJointResize(int dividerX)
        {
            var main = new WindowInteropHelper(this).Handle;
            if (main == IntPtr.Zero || _divDragNbrs.Count == 0) return;
            if (!GetWindowRect(main, out RECT oR) || !TryGetVisibleBounds(main, out RECT oV)) return;
            int oOvL = oR.Left - oV.Left, oOvR = oR.Right - oV.Right;

            // СНАЧАЛА двигаем соседей к dividerX, затем ЧИТАЕМ их ФАКТИЧЕСКИЙ край (система могла
            // ограничить сжатие мин. шириной соседа) и подгоняем НАШ край к этому факту. Иначе наше окно
            // уезжает по курсору дальше, чем сос��д может сжаться → наложение/разрыв (БАГ 2: 3 колонки на мин. ширине).
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
                if (EnableSeamGapFix)
                {
                    // ФИКС 1 (DivGrip): кламп effDiv по MINMAXINFO соседа ЗАРАНЕЕ (без реального
                    // предиктивного сдвига) → ровно ОДИН атомарный batch за кадр (мы + соседи +
                    // co-tiles вместе). Нет двух раздельных коммитов → нет межкадрового шва.
                    // Если ОС всё же клампит сверх MINMAXINFO, следующий кадр читает фактический
                    // край (hi выше) и effDiv корректируется; ArmDivReleaseRealign докрывает релиз.
                    foreach (var nb in _divDragNbrs)
                        if (GetWindowRect(nb, out RECT nbR) && TryGetVisibleBounds(nb, out RECT nbV) &&
                            TryGetMinTrackSize(nb, out int minW, out _))
                        {
                            int nbMaxDiv = nbR.Right - (nbR.Left - nbV.Left) - minW; // ширина соседа >= его minW
                            if (effDiv > nbMaxDiv) effDiv = nbMaxDiv;
                        }
                    if (effDiv < lo) effDiv = lo;
                    ApplyDividerBatch(main, oR, oOvR, effDiv, true, true);
                }
                else
                {
                    // Двухпроходная схема (fallback): предиктивный сдвиг соседей → читаем их
                    // фактический край (ОС могла ограничить сжатие) → финальный batch по факту.
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
                if (EnableSeamGapFix)
                {
                    // ФИКС 1 (DivGrip, зеркало side 1): кламп по MINMAXINFO соседа СЛЕВА + один атомарный batch.
                    foreach (var nb in _divDragNbrs)
                        if (GetWindowRect(nb, out RECT nbR) && TryGetVisibleBounds(nb, out RECT nbV) &&
                            TryGetMinTrackSize(nb, out int minW, out _))
                        {
                            int nbMinDiv = nbR.Left + (nbV.Right - nbR.Right) + minW; // effDiv+(nR.Right-nV.Right)-nR.Left >= minW
                            if (effDiv < nbMinDiv) effDiv = nbMinDiv;
                        }
                    if (effDiv > hi) effDiv = hi;
                    ApplyDividerBatch(main, oR, oOvL, effDiv, false, true);
                }
                else
                {
                    // Двухпроходная схема (fallback), см. ветку side 2.
                    ApplyDividerBatch(main, oR, oOvL, effDiv, false, false);
                    int actualDiv = effDiv;
                    foreach (var nb in _divDragNbrs)
                        if (TryGetVisibleBounds(nb, out RECT nV2)) actualDiv = Math.Max(actualDiv, nV2.Right);
                    if (actualDiv > effDiv + 1) effDiv = actualDiv;
                    ApplyDividerBatch(main, oR, oOvL, effDiv, false, true);
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
            uint oswp = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
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

        // ФИКС 1 (T4): минимальный track-size чужого окна через WM_GETMINMAXINFO (с таймаутом,
        // чтобы не зависнуть на подвисшем процессе). При неудаче — консервативный SnapFollowMinDimPx.
        // Ревизия аудита: результат (включая неудачу — чтобы не долбить hung/busy окно по 50ms на кадр)
        // кэшируется на время драга в _divMinTrackCache; кэш чистится при захвате грипа.
        private bool TryGetMinTrackSize(IntPtr hwnd, out int minW, out int minH)
        {
            if (_divMinTrackCache.TryGetValue(hwnd, out var c)) { minW = c.minW; minH = c.minH; return c.ok; }
            minW = SnapFollowMinDimPx; minH = SnapFollowMinDimPx;
            var mmi = new MINMAXINFO();
            IntPtr ok = SendMessageTimeoutW(hwnd, WM_GETMINMAXINFO, IntPtr.Zero, ref mmi, SMTO_ABORTIFHUNG, 50, out _);
            bool got = ok != IntPtr.Zero;
            if (got)
            {
                if (mmi.ptMinTrackSize.X > 0) minW = Math.Max(minW, mmi.ptMinTrackSize.X);
                if (mmi.ptMinTrackSize.Y > 0) minH = Math.Max(minH, mmi.ptMinTrackSize.Y);
            }
            _divMinTrackCache[hwnd] = (minW, minH, got);
            return got;
        }

        // ФИКС 1 (T4): Y-зеркало ApplyDividerBatch — мы + соседи + co-tiles одним атомарным
        // DeferWindowPos-batch'ем (один кадр DWM => нет межкадрового шва). effDiv — общая граница
        // (visible coords); bottomEdge=true, когда разделитель — наш НИЖНИЙ край (сосед снизу, side 4).
        private void ApplyDividerBatchV(IntPtr main, RECT oR, int oOv, int effDiv, bool bottomEdge)
        {
            IntPtr hdwp = BeginDeferWindowPos(_divDragNbrs.Count + _divDragCoTiles.Count + 1);
            if (hdwp == IntPtr.Zero) return;
            uint oswp = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
            foreach (var nb in _divDragNbrs)
            {
                if (!GetWindowRect(nb, out RECT nR) || !TryGetVisibleBounds(nb, out RECT nV)) continue;
                if (bottomEdge)
                {
                    int newNT = effDiv + (nR.Top - nV.Top);
                    hdwp = DeferWindowPos(hdwp, nb, IntPtr.Zero, nR.Left, newNT, nR.Right - nR.Left, nR.Bottom - newNT, oswp);
                }
                else
                {
                    int newNB = effDiv + (nR.Bottom - nV.Bottom);
                    hdwp = DeferWindowPos(hdwp, nb, IntPtr.Zero, nR.Left, nR.Top, nR.Right - nR.Left, newNB - nR.Top, oswp);
                }
                if (hdwp == IntPtr.Zero) return;
            }
            if (bottomEdge)
            {
                int newOB = effDiv + oOv;
                hdwp = DeferWindowPos(hdwp, main, IntPtr.Zero, oR.Left, oR.Top, oR.Right - oR.Left, newOB - oR.Top, oswp);
            }
            else
            {
                int newOT = effDiv + oOv;
                hdwp = DeferWindowPos(hdwp, main, IntPtr.Zero, oR.Left, newOT, oR.Right - oR.Left, oR.Bottom - newOT, oswp);
            }
            if (hdwp == IntPtr.Zero) return;
            foreach (var co in _divDragCoTiles)
            {
                if (!GetWindowRect(co, out RECT cR) || !TryGetVisibleBounds(co, out RECT cV)) continue;
                if (bottomEdge)
                {
                    int newCB = effDiv + (cR.Bottom - cV.Bottom);
                    if (newCB - cR.Top < SnapFollowMinDimPx) continue;
                    hdwp = DeferWindowPos(hdwp, co, IntPtr.Zero, cR.Left, cR.Top, cR.Right - cR.Left, newCB - cR.Top, oswp);
                }
                else
                {
                    int newCT = effDiv + (cR.Top - cV.Top);
                    if (cR.Bottom - newCT < SnapFollowMinDimPx) continue;
                    hdwp = DeferWindowPos(hdwp, co, IntPtr.Zero, cR.Left, newCT, cR.Right - cR.Left, cR.Bottom - newCT, oswp);
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
                if (EnableSeamGapFix)
                {
                    // ФИКС 1 (DivGrip, Y-зеркало): кламп по MINMAXINFO соседа СНИЗУ + один атомарный batch.
                    foreach (var nb in _divDragNbrs)
                        if (GetWindowRect(nb, out RECT nbR) && TryGetVisibleBounds(nb, out RECT nbV) &&
                            TryGetMinTrackSize(nb, out _, out int minH))
                        {
                            int nbMaxDiv = nbR.Bottom - (nbR.Top - nbV.Top) - minH; // высота соседа >= его minH
                            if (effDiv > nbMaxDiv) effDiv = nbMaxDiv;
                        }
                    if (effDiv < lo) effDiv = lo;
                    ApplyDividerBatchV(main, oR, oOvB, effDiv, true);
                }
                else
                {
                    // Fallback: последовательные SetWindowPos (сосед → чтение факта → мы → co-tiles).
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
                if (EnableSeamGapFix)
                {
                    // ФИКС 1 (DivGrip, зеркало side 3): кламп по MINMAXINFO соседа СВЕРХУ + один атомарный batch.
                    foreach (var nb in _divDragNbrs)
                        if (GetWindowRect(nb, out RECT nbR) && TryGetVisibleBounds(nb, out RECT nbV) &&
                            TryGetMinTrackSize(nb, out _, out int minH))
                        {
                            int nbMinDiv = nbR.Top + (nbV.Bottom - nbR.Bottom) + minH; // высота соседа >= его minH
                            if (effDiv < nbMinDiv) effDiv = nbMinDiv;
                        }
                    if (effDiv > hi) effDiv = hi;
                    ApplyDividerBatchV(main, oR, oOvT, effDiv, false);
                }
                else
                {
                    // Fallback: последовательные SetWindowPos, см. ветку side 4.
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

        // ФИКС 1 (T4, frame-resize): «grower-first» pre-pass из WM_WINDOWPOSCHANGING.
        // Когда наш край при тяге рамки идёт ВНУТРЬ (мы сжимаемся), сосед должен ВЫРАСТИ.
        // Коммитим растущего соседа по ПРЕДЛОЖЕННОМУ (ещё не применённому) прямоугольнику
        // ДО того, как ОС применит наш кадр: расширившийся сосед уже накрыл шов, и в момент
        // нашего сжатия под ним не мелькает рабочий стол. Сжатие соседа (когда мы растём)
        // остаётся в post-pass (WM_WINDOWPOSCHANGED): наше растущее окно само закрывает шов.
        private void FollowFrameResizeNeighborsPre(IntPtr hwnd, IntPtr lParam)
        {
            bool dragRight = _sizingEdge == WMSZ_RIGHT || _sizingEdge == WMSZ_TOPRIGHT || _sizingEdge == WMSZ_BOTTOMRIGHT;
            bool dragLeft  = _sizingEdge == WMSZ_LEFT  || _sizingEdge == WMSZ_TOPLEFT  || _sizingEdge == WMSZ_BOTTOMLEFT;
            if (!dragRight && !dragLeft) return;
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((wp.flags & SWP_NOSIZE) != 0 && (wp.flags & SWP_NOMOVE) != 0) return;
            if (!GetWindowRect(hwnd, out RECT oR) || !TryGetVisibleBounds(hwnd, out RECT oV)) return;
            // Предложенный ВИДИМЫЙ край: сдвигаем текущие DWM-поля на предложенный прямоугольник.
            if (dragRight && _frameNbrsR.Count > 0)
            {
                int propVisRight = wp.x + wp.cx - (oR.Right - oV.Right);
                if (propVisRight >= oV.Right) return; // край идёт наружу (мы растём) — сосед сжимается, это post-pass
                foreach (var nb in _frameNbrsR)
                {
                    if (!GetWindowRect(nb, out RECT nR) || !TryGetVisibleBounds(nb, out RECT nV)) continue;
                    int ovL = nR.Left - nV.Left;
                    int newNL = propVisRight + ovL;
                    if (nR.Right - newNL < SnapFollowMinDimPx) continue;
                    SetWindowPos(nb, IntPtr.Zero, newNL, nR.Top, nR.Right - newNL, nR.Bottom - nR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
            }
            else if (dragLeft && _frameNbrsL.Count > 0)
            {
                int propVisLeft = wp.x + (oV.Left - oR.Left);
                if (propVisLeft <= oV.Left) return; // край идёт наружу — post-pass
                foreach (var nb in _frameNbrsL)
                {
                    if (!GetWindowRect(nb, out RECT nR) || !TryGetVisibleBounds(nb, out RECT nV)) continue;
                    int ovR = nR.Right - nV.Right;
                    int newNR = propVisLeft + ovR;
                    if (newNR - nR.Left < SnapFollowMinDimPx) continue;
                    SetWindowPos(nb, IntPtr.Zero, nR.Left, nR.Top, newNR - nR.Left, nR.Bottom - nR.Top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
            }
        }

        // BUG2: во время OS-модального ресайза РАМКОЙ SnapFollow выключен (_inSizeMove). Чтобы снапнутый
        // сосед не «оторвался», двигаем его ближний край к нашему фактическому краю. Соседи захвачены на
        // старте цикла (_frameNbrsR/_frameNbrsL), пока окна были вплотную, — открывшийся зазор не мешает.
        // При EnableSeamGapFix растущий сосед уже перемещён pre-pass'ом (см. выше); этот post-pass
        // идемпотентен (повторный SetWindowPos в те же координаты — no-op) и докоммичивает сжатие.
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
                    if (nR.Right - newNL < SnapFollowMinDimPx) continue; // не сжимаем соседа уже минимума
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
        }

        // BUG2: то же после ручного ресайза РАМКОЙ (shell может до-снапить наш край и после WeMoveSizeEnd).
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
                    // видимым. Принудительно переоцениваем со��едей В МОМЕНТ захвата: исчезнувший сосед
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
                        _divMinTrackCache.Clear(); // ревизия аудита: свежий MINMAXINFO-кэш на каждый драг

                        if (side == 3 || side == 4) FindDividerCoTilesV(side); else FindDividerCoTiles(side); // BUG2: also capture same-column/row co-tiles sharing this divider
                        SetCapture(h);
                    }
                    return IntPtr.Zero;
                }
                case WM_MOUSEMOVE:
                    if (_divDragging) { if (GetCursorPos(out POINT p)) { if (_divDragSide == 3 || _divDragSide == 4) UpdateDividerJointResizeV(p.Y); else UpdateDividerJointResize(p.X); } return IntPtr.Zero; }
                    break;
                case WM_LBUTTONUP:
                    if (_divDragging)
                    {
                        ArmDivReleaseRealign(); // BUG2: лечим пост-релизный дрейф shell-ре-снапа
                        _divDragging = false; _divDragNbrs.Clear(); _divDragCoTiles.Clear();
                        ReleaseCapture();
                        ScheduleEdgeGripRefresh();
                        return IntPtr.Zero;
                    }
                    break;
                case WM_CAPTURECHANGED:
                    if (_divDragging) { ArmDivReleaseRealign(); _divDragging = false; _divDragNbrs.Clear(); _divDragCoTiles.Clear(); ScheduleEdgeGripRefresh(); }
                    break;
            }
            return DefWindowProcW(h, msg, w, l);
        }

        /// <summary>
        /// Пересчёт позиции/видимости наружной полосы хвата. Только в покое (settle); во время _inSizeMove
        /// спрятана. Показывается только для свободного края; ширина обрезается по rcWork текущего монитора.
        /// </summary>
        private void RefreshEdgeGrip()
        {
            if (!EnableFreeEdgeGrip || !UseThemedSystemFrame) return;
            if (_edgeGripResizing) return;
            RefreshDividerGrips(); // 1.2/BUG2: оверлеи внутренних разделителей (независимо от free-edge грипа)
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || _inSizeMove || WindowState != WindowState.Normal || !IsVisible)
            {
                HideEdgeGrip();
                return;
            }
            if (!TryGetSnapInternalEdges(hwnd, out bool sl, out bool sr, out _, out _) || (!sl && !sr))
            {
                HideEdgeGrip();
                return;
            }
            if (!TryGetVisibleBounds(hwnd, out RECT vis) || !TryGetWorkArea(hwnd, out RECT wa))
            {
                HideEdgeGrip();
                return;
            }

            int x, w, ht;
            int hgt = Math.Max(1, vis.Bottom - vis.Top);
            // Реальный snap-сосед стоит ВПЛОТНУЮ к разделителю (зазор ~0-2px). TryFindSnapNeighbor ловит
            // окна с зазором до 400px (нужно для отслеживания при ресайзе, 1.1), поэтому здесь дополнительно
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
        }


        // ====================================================================
        //  Passive-follow
        // ====================================================================
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
                return;
            }
            if (R - L < SnapFollowMinDimPx || B - T < SnapFollowMinDimPx) return;
            if (L == win.Left && R == win.Right && T == win.Top && B == win.Bottom) return;
            SetWindowPos(hwnd, IntPtr.Zero, L, T, R - L, B - T, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
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
            // Не мешаем собственному модальному перетаскиванию/ресайзу (un-snap за шапку, ручной ресайз).
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
            // поймать финальное доразмещение группы силами shell (иначе появлялся зазор «после» перетягивания).
            if (justReleased && _snapDragEdge != 0) { _snapSettleEdge = _snapDragEdge; _snapSettleTicks = SnapSettleTicks; _snapDragEdge = 0; }

            // На нажатии латчим БЛИЖАЙШИЙ внутренний край и ФИКСИРУЕМ HWND обоих соседей (по разделителям).
            // Дальше тянем каждый край за СВОИМ конкретным окном-соседом — стабильно, без шумного перепоиска.
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
                    // этого в latch НЕ хватает: после первой joint-resize фоновое окно с близким ребром может победить
                    // настоящего соседа по bestGap. Snap-сосед ПО ОПРЕДЕЛЕНИЮ вплотную (0-2 px). Режем сильнее.
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
                }
            }

            // Активны, пока залачен край ИЛИ идёт доводка после отпускания.
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

            // Каждый наш внутренний край тянем за ЕГО зафиксированным соседом (по HWND): статичный сосед → край
            // стоит; уехавший сосед (групповое перераспределение shell) → край следует за ним вплотную. Так нет
            // ни сдвига (мы не двигаем край без причины), ни перекрытия (догоняем уехавшего соседа). Курсор —
            // fallback только для ЗАЛАЧЕННОГО края, если его сосед потерян.
            int ovL = win.Left - vis.Left, ovR = win.Right - vis.Right;
            int L = win.Left, R = win.Right;
            // БАГ 2b: cursor-fallback разрешён ТОЛЬКО если сосед БЫЛ найден на latch (_*Nbr != Zero), но сейчас
            // потерян (минимизирован/cloaked). Если на latch соседа не нашли вовсе — НЕ двигаем свой край за
            // курсором: иначе в 3-колонке наше окно ездит в одиночку, когда shell-полоса (~2 px) не подцепилась,
            // а наша 12-px latch — да. Результат — наложение/разрыв. Лучше не двинуться, чем разъехаться.
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
                bool trackedL = _leftNbr != IntPtr.Zero;
                bool trackedR = _rightNbr != IntPtr.Zero;
                SetWindowPos(hwnd, IntPtr.Zero, L, T, R - L, B - T,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
        }

        /// <summary>
        /// БАГ 1: ищет соседнее snapped-окно — партнёра по внутреннему разделителю (для rightEdge — справа,
        /// иначе слева). Партнёр: видимое не-cloaked не-tool окно на том же мониторе, с существенным
        /// вертикальным перекрытием, по нужную сторону от нас и наиболее примыкающее (минималь��ый зазор по X
        /// между нашим краем и его краем). Возвращает его ВИДИМЫЕ границы (DWMWA_EXTENDED_FRAME_BOUNDS).
        /// </summary>
        private bool FindSnapNeighbors(IntPtr self, RECT ourVis, bool rightEdge, System.Collections.Generic.List<IntPtr> outHwnds, out int nearEdgeX)
        {
            outHwnds.Clear();
            nearEdgeX = rightEdge ? ourVis.Right : ourVis.Left;
            IntPtr selfMon = MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST);
            int ourTop = ourVis.Top, ourBot = ourVis.Bottom;
            int ourH = Math.Max(1, ourBot - ourTop);
            var cands = new System.Collections.Generic.List<(IntPtr h, RECT v, int gap)>();
            // БАГ 2c: рабочая область нашего монитора — для проверки, что партнёр САМ приснаплен (дальний край у границы экрана).
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
                if (!withinSpan && !containsSpan) return true; // плитка целиком внутри нашего края (с допуском)

                int gap;
                if (rightEdge)
                {
                    if (v.Left <= ourVis.Left) return true;       // партнёр справа
                    gap = v.Left - ourVis.Right; // BUG2: знаковый зазор: <=0 перекрытие/вплотную (заведомо смежны), >0 настоящий разрыв
                }
                else
                {
                    if (v.Right >= ourVis.Right) return true;     // партнёр слева
                    gap = ourVis.Left - v.Right; // BUG2: знаковый зазор: <=0 перекрытие/вплотную (заведомо смежны), >0 настоящий разрыв
                }
                if (gap > SnapNeighborMaxGapPx) return true;                    // слишком далеко — не партнёр по разделителю
                if (haveWork)
                {
                    // партнёр до��жен ДОСТАВАТЬ до дальней границы рабочей области (последняя плитка колонки).
                    // СВЕС за край экрана допустим (OS/наш дрейф мог вытолкнуть дальний край наружу -
                    // мы его потом прибиваем обратно). Отвергаем ТОЛЬКО если дальний край НЕ ДОТЯГИВАЕТ (окно посреди экрана).
                    bool reachesEdge = rightEdge
                        ? v.Right >= waSelf.Right - SnapNeighborEdgeAlignPx
                        : v.Left <= waSelf.Left + SnapNeighborEdgeAlignPx;
                    if (!reachesEdge) return true;
                }
                cands.Add((h, v, gap));
                return true;
            }, IntPtr.Zero);

            if (cands.Count == 0) return false;
            // BUG2: сначала берём ОДИНОЧНОГО соседа, закрывающего наш край на ПОЛНУЮ высоту (обычный случай
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
            // одиночное окно, закрывающее лишь часть края, набор не образует (исключаем случайные окна рядом).
            cands.Sort((a, b) => a.v.Top.CompareTo(b.v.Top));
            // БАГ 2c: сосед(и) тащатся вместе с нами ТОЛЬКО если их прилегающие стороны ТОЧНО мостят наш край
            // без перекрытий и зазоров: одно окно с равной и полностью прилегающей стороной, либо набор окон,
            // чьи стороны в сумме = нашей (stacked). Любое лишнее/перекрывающее окно ломает мозаику -> НЕ партнёр.
            // Так случайное чужое окно у границы (в т.ч. половинной высоты) в группу НЕ попадает.
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
            // BUG2: НЕ объединяем несколько окон в мозаику по нашему краю. Это давало ложные захваты: постороннее
            // маленькое окно снизу-сбоку попадало в группу и тащилось за нами (оставаясь не вплотную). Реальный
            // snap-сосед (2 окна рядом / 3 колонки) - это одиночное окно на полную высоту, его берёт ветка выше.
            // BUG2 (regression fix): a genuine stacked tiling - e.g. two half-height windows covering our full
            // edge (top + bottom) - IS a valid neighbor group and MUST follow us during joint-resize. Reject only
            // when the candidates do not CONTIGUOUSLY tile our full height (stray / partial / overlapping windows).
            // The single full-height neighbor (2 windows side-by-side / 3 columns) is taken by the branch above.
            if (!tiled || Math.Abs(expectedTop - ourBot) > tileTolPx)
            {
return false;
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


            neighborHwnd = bestHwnd;
            neighborVis = best;
            return found;
        }

        /// <summary>
        /// БАГ 1: текущая видимая граница ЗАФИКСИРОВАННОГО (по HWND) соседа, если он ещё валиден (виден, не
        /// cloaked, вертикально перекрывает нас). rightSide=true → его ЛЕВАЯ граница (мы слева от него),
        /// иначе его ПРАВАЯ. Возвращает false, если сосед потерян/уехал — тогда соответствующий край держим.
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
        /// на запас тени (см. MaskShadowMargin* по сторонам), чтобы DWM-тень окна анимировалась вместе с ним. Откат
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
    }
}
