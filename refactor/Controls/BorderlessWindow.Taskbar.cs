using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ControlPanel
{
    // ============================================================================
    //  BorderlessWindow — TASKBAR partial (T5)
    //  Auto-hide taskbar edge watcher, gap mask, and window placement persistence.
    // ============================================================================
    public partial class BorderlessWindow
    {
        private readonly DispatcherTimer _edgeWatcher;
        private RECT _currentMonitorRect;
        private int _taskbarHeight = 40;
        private bool _watchActive;
        private bool _shrunk;
        private bool _taskbarWasVisible;
        private int _waitTicks;
        private int _prevCursorY;
        private bool _hasPrevCursorY;
        private bool _armSuppressed;
        private bool _cursorDriven;

        private const int ArmBand = 3;
        private const int ArmTimeoutTicks = 25;
        private Window? _gapMask;

        private const int GapMaskHeight = 64;
        private const int GapMaskParked = -32000;
        // ABE_BOTTOM / ABM_GETAUTOHIDEBAREX / SW_SHOWNORMAL / SW_SHOWMINIMIZED объявлены в
        // BorderlessWindow.Interop.cs (T2) — здесь НЕ дублировать (partial-класс, CS0102).

        private static readonly string PlacementFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControlPanel", "window-placement.json");

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
            if (EnableGapMask)
                EnsureGapMaskHandle();
            _edgeWatcher.Start();
        }

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

            int dy = _hasPrevCursorY ? p.Y - _prevCursorY : 0;
            _prevCursorY = p.Y;
            _hasPrevCursorY = true;
            bool movingUp = dy < 0;

            bool nearEdge = onMonitor && p.Y >= bottom - ArmBand;
            bool atEdge = onMonitor && p.Y >= bottom - 1;
            bool visible = IsTaskbarCurrentlyVisible();

            if (!_shrunk)
            {
                if (!nearEdge)
                    _armSuppressed = false;

                if (nearEdge && !movingUp && !_armSuppressed)
                {
                    _taskbarWasVisible = false;
                    _waitTicks = 0;
                    _cursorDriven = false;
                    ShrinkBottom(true);
                }
                return;
            }

            if (visible)
            {
                _taskbarWasVisible = true;
                _waitTicks = 0;
                HideGapMask();
                return;
            }

            if (_taskbarWasVisible)
            {
                ShrinkBottom(false);
                _taskbarWasVisible = false;
                _waitTicks = 0;
                return;
            }

            if (!nearEdge)
            {
                ShrinkBottom(false);
                _waitTicks = 0;
                return;
            }

            if (EnableCursorDrive && !_cursorDriven && !atEdge && !movingUp)
            {
                _cursorDriven = true;
                SetCursorPos(p.X, bottom - 1);
                _prevCursorY = bottom - 1;
                return;
            }

            if (++_waitTicks > ArmTimeoutTicks)
            {
                ShrinkBottom(false);
                _waitTicks = 0;
                _armSuppressed = true;
            }
        }

        private void ShrinkBottom(bool shrink)
        {
            if (_shrunk == shrink) return;
            _shrunk = shrink;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            RECT m = _currentMonitorRect;
            int width = m.Right - m.Left;
            int height = (m.Bottom - m.Top) - (shrink ? 1 : 0);

            if (shrink)
                ShowGapMask();

            SetWindowPos(hwnd, IntPtr.Zero, m.Left, m.Top, width, height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOCOPYBITS | SWP_DEFERERASE);

            if (!shrink)
                HideGapMask();
        }

        private void ShowGapMask()
        {
            if (!EnableGapMask) return;

            IntPtr h = EnsureGapMaskHandle();
            if (h == IntPtr.Zero) return;

            RECT m = _currentMonitorRect;
            SetWindowPos(h, HWND_TOPMOST, m.Left, m.Bottom - 1, m.Right - m.Left, GapMaskHeight,
                SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

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
                    Width = 400,
                    Height = GapMaskHeight,
                    Left = GapMaskParked,
                    Top = GapMaskParked,
                };
                _gapMask.SourceInitialized += (_, _) =>
                {
                    IntPtr h = new WindowInteropHelper(_gapMask!).Handle;
                    IntPtr ex = GetWindowLongPtr(h, GWL_EXSTYLE);
                    SetWindowLongPtr(h, GWL_EXSTYLE,
                        new IntPtr(ex.ToInt64() | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
                };
                _gapMask.Show();
            }
            return new WindowInteropHelper(_gapMask).Handle;
        }

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
    }
}
