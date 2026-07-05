using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ControlPanel
{
    // ============================================================================
    //  BorderlessWindow — ANIMATION partial (T5)
    //  Startup cloak/uncloak and screenshot-mask crossfade. The old gray mask
    //  chain is intentionally not carried over.
    // ============================================================================
    public partial class BorderlessWindow
    {
        private const int PeakGateTimeoutMs = 250;
        private readonly ManualResetEventSlim _peakGate = new(false);

        private const int ScreenshotAppearMs = 120;
        private const int ScreenshotDisappearMs = 90;
        private int _shotDurationMs;
        private IntPtr _shotScreenDc;
        private IntPtr _shotMemDc;
        private IntPtr _shotBmp;
        private IntPtr _shotOldBmp;
        private RECT _shotRect;
        private volatile bool _gateOnRender;

        private bool _startupHiding;
        private WindowState _prevWindowState = WindowState.Normal;
        private IntPtr _maskHwnd;
        private Thread? _maskThread;
        private volatile bool _maskAbort;
        private Action? _maskAtPeak;

        private const string MaskClassName = "ControlPanelStartupMask";
        private const int MaskColorRef = 0x001C1A19;
        private const int ThemeBorderColorRef = 0x00403432;
        private static bool _maskClassRegistered;
        private static WndProcDelegate? _maskWndProc;

        private void TryStartMaskReveal()
        {
            if (_startupRevealStarted) return;
            _startupRevealStarted = true;
            StartMaskReveal(new WindowInteropHelper(this).Handle);
        }

        private void HideMainForStartup(IntPtr hwnd)
        {
            if (!EnableStartupMask || hwnd == IntPtr.Zero) return;

            int cloak = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
            _startupHiding = true;
        }

        private void UncloakMain(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int cloak = 0;
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
        }

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

        private void AnimateToWindowState(WindowState target)
        {
            if (WindowState == target) return;

            if (!EnableStartupMask || !TryGetWindowMonitor(out RECT mon))
            {
                WindowState = target;
                return;
            }
            StartScreenshotCrossfade(mon, () => WindowState = target, gateOnContent: true,
                durationMs: target == WindowState.Maximized ? ScreenshotAppearMs : ScreenshotDisappearMs);
        }

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

        private static double SmoothStep(double t)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        private void EndMask()
        {
            _maskAbort = true;
            _peakGate.Set();
            if (_maskHwnd != IntPtr.Zero)
                CleanupScreenshotMask(_maskHwnd);
        }

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

            int maskCorner = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref maskCorner, sizeof(int));
            int maskNc = DWMNCRP_DISABLED;
            DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref maskNc, sizeof(int));
            SetWindowRgn(hwnd, CreateRectRgn(0, 0, w, h), false);

            _maskHwnd = hwnd;
            _shotScreenDc = screenDc; _shotMemDc = memDc; _shotBmp = bmp; _shotOldBmp = oldBmp;
            _shotRect = rect;
            _maskAtPeak = atPeak;
            _gateOnRender = gateOnContent;
            _shotDurationMs = durationMs;
            _peakGate.Reset();

            UpdateShotAlpha(hwnd, memDc, rect, 255);
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

            _maskAbort = false;
            _maskThread = new Thread(ScreenshotFadeLoop) { IsBackground = true, Name = "ScreenshotCrossfade" };
            _maskThread.Start();
        }

        private static void UpdateShotAlpha(IntPtr hwnd, IntPtr memDc, RECT rect, byte alpha)
        {
            var ptSrc = new POINT { X = 0, Y = 0 };
            var ptDst = new POINT { X = rect.Left, Y = rect.Top };
            var size = new SIZE { cx = rect.Right - rect.Left, cy = rect.Bottom - rect.Top };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 0 };
            UpdateLayeredWindow(hwnd, IntPtr.Zero, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, ULW_ALPHA);
        }

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
    }
}