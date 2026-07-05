using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ControlPanel
{
    // ============================================================================
    //  BorderlessWindow — UNSNAP partial (T5)
    //  Caption un-snap restore, anchor resize correction, and WinEvent restore chain.
    //  Diagnostics and watchdog-only code from the original were removed per PLAN.
    // ============================================================================
    public partial class BorderlessWindow
    {
        private const double CaptionUnsnapRestoreThresholdDip = 20.0;
        private const int UnsnapRestoreGrownMarginPx = 200;

        private bool _captionUnsnapPending;
        private bool _captionUnsnapDragging;
        private POINT _captionUnsnapDownPt;
        private RECT _captionUnsnapDownRect;
        private RECT _captionUnsnapRestoreRect;
        private int _captionUnsnapDragOffsetX;
        private int _captionUnsnapDragOffsetY;
        private RECT _lastFloatingRestoreRect;
        private bool _lastFloatingRestoreRectValid;
        private bool _captionUnsnapHandoffToShell;

        private IntPtr _unsnapWinEventHook;
        private WinEventDelegate? _unsnapWinEventProc;
        private bool _unsnapArmValid;
        private RECT _unsnapArmRestoreRect;
        private POINT _unsnapArmDownPt;
        private RECT _unsnapArmSnapRect;
        private bool _unsnapHasFloated;

        private bool AnchorUnsnapResize(IntPtr wParam, IntPtr lParam)
        {
            int edge = wParam.ToInt32();
            bool dragLeft = edge == WMSZ_LEFT || edge == WMSZ_TOPLEFT || edge == WMSZ_BOTTOMLEFT;
            bool dragRight = edge == WMSZ_RIGHT || edge == WMSZ_TOPRIGHT || edge == WMSZ_BOTTOMRIGHT;
            bool dragTop = edge == WMSZ_TOP || edge == WMSZ_TOPLEFT || edge == WMSZ_TOPRIGHT;
            bool dragBottom = edge == WMSZ_BOTTOM || edge == WMSZ_BOTTOMLEFT || edge == WMSZ_BOTTOMRIGHT;

            var rc = Marshal.PtrToStructure<RECT>(lParam);
            var before = rc;

            if (!dragLeft) rc.Left = _sizeAnchor.Left;
            if (!dragRight) rc.Right = _sizeAnchor.Right;
            if (!dragTop) rc.Top = _sizeAnchor.Top;
            if (!dragBottom) rc.Bottom = _sizeAnchor.Bottom;

            if (rc.Left == before.Left && rc.Right == before.Right &&
                rc.Top == before.Top && rc.Bottom == before.Bottom)
                return false;

            Marshal.StructureToPtr(rc, lParam, false);
            return true;
        }

        private bool TryBeginCaptionUnsnapRestoreDrag(MouseButtonEventArgs e)
        {
            if (!EnableCaptionUnsnapRestoreDrag || e.ClickCount != 1)
                return false;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return false;
            if (!GetCursorPos(out POINT cur)) return false;
            if (!GetWindowRect(hwnd, out RECT win)) return false;

            bool cand = IsCaptionUnsnapRestoreCandidate(hwnd, win);
            RECT restore = default;
            bool gotRestore = cand && TryGetCaptionRestoreRect(hwnd, win, out restore);
            if (!cand) return false;
            if (!gotRestore) return false;

            _captionUnsnapPending = true;
            _captionUnsnapDragging = false;
            _captionUnsnapHandoffToShell = false;
            _captionUnsnapDownPt = cur;
            _captionUnsnapDownRect = win;
            _captionUnsnapRestoreRect = restore;
            ArmUnsnapWinEventRestore(cur, win, restore);
            CaptureMouse();
            try
            {
                if (PresentationSource.FromVisual(this) is HwndSource src)
                    SetCapture(src.Handle);
            }
            catch
            {
            }
            HideEdgeGrip();
            return true;
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

            if (_captionUnsnapPending)
            {
                int dx = cur.X - _captionUnsnapDownPt.X;
                int dy = cur.Y - _captionUnsnapDownPt.Y;
                int absDx = Math.Abs(dx);

                if (WindowState == WindowState.Normal && dy < CaptionUnsnapRestoreThresholdPxY() &&
                    absDx >= CaptionUnsnapRestoreThresholdPxX() && absDx > Math.Max(0, dy))
                {
                    HandoffCaptionDragToShell(cur);
                    return true;
                }

                if (dy < CaptionUnsnapRestoreThresholdPxY())
                    return true;

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
            _unsnapArmValid = false;

            int snapW = Math.Max(1, _captionUnsnapDownRect.Right - _captionUnsnapDownRect.Left);
            int restoreW = Math.Max(1, _captionUnsnapRestoreRect.Right - _captionUnsnapRestoreRect.Left);
            int clickX = Math.Clamp(_captionUnsnapDownPt.X - _captionUnsnapDownRect.Left, 0, snapW);

            _captionUnsnapDragOffsetX = Math.Clamp((int)Math.Round((double)clickX * restoreW / snapW), 0, restoreW - 1);
            _captionUnsnapDragOffsetY = Math.Clamp(_captionUnsnapDownPt.Y - _captionUnsnapDownRect.Top, 0,
                Math.Max(0, _captionUnsnapRestoreRect.Bottom - _captionUnsnapRestoreRect.Top - 1));

            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;

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
            _captionUnsnapPending = false;
            _captionUnsnapDragging = false;
            _captionUnsnapHandoffToShell = false;

            if (releaseCapture && IsMouseCaptured)
                ReleaseMouseCapture();
            if (releaseCapture && wasActive)
                ReleaseCapture();

            if (wasActive)
                RefreshEdgeGrip();
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

            if (WindowState == WindowState.Maximized)
                return true;

            return WindowState == WindowState.Normal;
        }

        private bool IsWindowAtLeastCurrentMonitorHeight(IntPtr hwnd, RECT win)
        {
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;

            int monitorH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            int workH = mi.rcWork.Bottom - mi.rcWork.Top;
            int fullHeight = Math.Min(monitorH, workH);
            return (win.Bottom - win.Top) >= fullHeight;
        }

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

            if (_lastFloatingRestoreRectValid && IsUsableCaptionRestoreRect(hwnd, _lastFloatingRestoreRect, current))
            {
                restore = _lastFloatingRestoreRect;
                return true;
            }

            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            bool wpOk = GetWindowPlacement(hwnd, ref wp);
            bool wpUsable = wpOk && IsUsableCaptionRestoreRect(hwnd, wp.normalPosition, current);
            if (wpUsable)
            {
                restore = wp.normalPosition;
                return true;
            }

            return false;
        }

        private bool IsUsableCaptionRestoreRect(IntPtr hwnd, RECT rc, RECT current)
        {
            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (w < 100 || h < 100) return false;

            if (Math.Abs(w - (current.Right - current.Left)) <= 2 &&
                Math.Abs(h - (current.Bottom - current.Top)) <= 2)
                return false;

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

            if (IsWindowAtLeastCurrentMonitorHeight(hwnd, win))
                return;

            _lastFloatingRestoreRect = win;
            _lastFloatingRestoreRectValid = true;
        }

        private void InstallUnsnapWinEventHook()
        {
            if (!EnableUnsnapWinEventRestore || _unsnapWinEventHook != IntPtr.Zero) return;
            _unsnapWinEventProc = OnUnsnapWinEvent;
            _unsnapWinEventHook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND,
                IntPtr.Zero, _unsnapWinEventProc, 0, GetCurrentThreadId(), WINEVENT_OUTOFCONTEXT);
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

        private void ArmUnsnapWinEventRestore(POINT downPt, RECT snapRect, RECT restore)
        {
            if (!EnableUnsnapWinEventRestore) return;
            _unsnapArmValid = true;
            _unsnapArmDownPt = downPt;
            _unsnapArmSnapRect = snapRect;
            _unsnapArmRestoreRect = restore;
            _unsnapHasFloated = false;
        }

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

            if (!_unsnapHasFloated)
            {
                if (nearRestoreSize)
                {
                    _unsnapHasFloated = true;
                    return;
                }
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
                }
                return;
            }

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
                }
                else
                {
                    wp.flags |= SWP_NOMOVE | SWP_NOSIZE;
                    Marshal.StructureToPtr(wp, lParam, false);
                }
            }
        }

        private void OnUnsnapWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject != OBJID_WINDOW) return;
            if (hwnd != new WindowInteropHelper(this).Handle) return;

            if (eventType == EVENT_SYSTEM_MOVESIZEEND && _unsnapArmValid)
                Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(FinishUnsnapWinEventRestore));
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
            bool stillGrown = curH > restoreH + UnsnapRestoreGrownMarginPx;
            if (dy < CaptionUnsnapRestoreThresholdPxY() || !stillGrown)
                return;

            int w = Math.Max(1, r.Right - r.Left);
            int h = restoreH;
            int snapW = Math.Max(1, _unsnapArmSnapRect.Right - _unsnapArmSnapRect.Left);
            int clickX = Math.Clamp(_unsnapArmDownPt.X - _unsnapArmSnapRect.Left, 0, snapW);
            int offX = Math.Clamp((int)Math.Round((double)clickX * w / snapW), 0, w - 1);
            int offY = Math.Clamp(_unsnapArmDownPt.Y - _unsnapArmSnapRect.Top, 0, Math.Max(0, h - 1));
            int x = pt.X - offX;
            int y = pt.Y - offY;

            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        private bool FloatingWindowCrossesMonitor(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (WindowState != WindowState.Normal) return false;
            if (HasInternalSnapDivider(hwnd)) return false;
            if (!GetWindowRect(hwnd, out RECT r)) return false;
            var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            var m = mi.rcMonitor;
            return r.Bottom > m.Bottom || r.Right > m.Right || r.Top < m.Top || r.Left < m.Left;
        }

        private bool RectCrossesItsMonitor(IntPtr hwnd, RECT r)
        {
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            RECT m = mi.rcMonitor;
            return r.Bottom > m.Bottom || r.Right > m.Right || r.Top < m.Top || r.Left < m.Left;
        }
    }
}