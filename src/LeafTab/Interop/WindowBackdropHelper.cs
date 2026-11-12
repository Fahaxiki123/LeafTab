using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace LeafTab.Interop
{
    internal static class WindowBackdropHelper
    {
        private const int GWL_STYLE = -16;
        private const int WS_SYSMENU = 0x00080000;

        private const int WM_STYLECHANGING = 0x007C;
        private const int WM_NCRBUTTONUP = 0x00A5;
        private const int HTCAPTION = 2;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        private const int DWMWCP_ROUND = 2;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE); // Suppresses AccentColor titlebar strip (ColorPrevalence=1)
        private const int DWMSBT_TRANSIENTWINDOW = 3;                    // Desktop Acrylic (real-time frosted blur)
        private const int DWMSBT_MAINWINDOW = 2;                         // Mica fallback

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STYLESTRUCT
        {
            public int styleOld;
            public int styleNew;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        [DllImport("dwmapi.dll")]
        private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool pfEnabled);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        /// <summary>
        /// 1. Suppresses Windows 11 DWM's native overlapping [_ □ X] caption buttons (strips WS_SYSMENU and guards WM_STYLECHANGING)
        ///    while preserving native WS_THICKFRAME | WS_CAPTION animations, Aero Snap, and titlebar right-click system menu.
        /// 2. Suppresses Windows 11 DWM's 37px AccentColor titlebar band (DWMWA_CAPTION_COLOR = DWMWA_COLOR_NONE) when ColorPrevalence=1.
        /// 3. Applies bounded-margin DWM Desktop Acrylic strictly to the top header & bottom footer so the center Win32
        ///    IExplorerBrowser (GDI ClearType text) remains 100% opaque and razor-sharp.
        /// </summary>
        public static bool TryApplyLiquidGlassBackdrop(Window window, IntPtr hwnd, int topGlassPx, int bottomGlassPx)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            HwndSource? source = HwndSource.FromHwnd(hwnd);
            if (source != null)
            {
                source.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                    SuppressNativeCaptionChromeHook(window, h, msg, wParam, lParam, ref handled));
            }

            // Strip WS_SYSMENU immediately so DWMWA_CAPTION_BUTTON_BOUNDS width becomes 0 (no duplicate system [_ □ X] buttons)
            int currentStyle = GetWindowLongW(hwnd, GWL_STYLE);
            if ((currentStyle & WS_SYSMENU) != 0)
            {
                SetWindowLongW(hwnd, GWL_STYLE, currentStyle & ~WS_SYSMENU);
                SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }

            int build = Environment.OSVersion.Version.Build;
            if (build >= 22000)
            {
                int lightMode = 0;
                NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref lightMode, sizeof(int));

                int cornerPref = DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

                // Critical when user has "Show accent color on title bars" (ColorPrevalence=1):
                // prevents DWM from painting a 37px colored titlebar band across the top half of our glass header.
                int colorNone = DWMWA_COLOR_NONE;
                NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorNone, sizeof(int));
            }

            if (build < 22621)
                return false;

            if (DwmIsCompositionEnabled(out bool compEnabled) != 0 || !compEnabled)
                return false;

            if (!IsSystemTransparencyEnabled())
                return false;

            if (source?.CompositionTarget != null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            UpdateGlassFrameMargins(hwnd, topGlassPx, bottomGlassPx);

            int acrylic = DWMSBT_TRANSIENTWINDOW;
            if (NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, sizeof(int)) == 0)
                return true;

            int mica = DWMSBT_MAINWINDOW;
            return NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, sizeof(int)) == 0;
        }

        public static void UpdateGlassFrameMargins(IntPtr hwnd, int topGlassPx, int bottomGlassPx)
        {
            if (hwnd == IntPtr.Zero)
                return;

            var margins = new MARGINS
            {
                cxLeftWidth = 0,
                cxRightWidth = 0,
                cyTopHeight = Math.Max(0, topGlassPx),
                cyBottomHeight = Math.Max(0, bottomGlassPx)
            };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }

        private static IntPtr SuppressNativeCaptionChromeHook(
            Window window,
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (msg == WM_STYLECHANGING && wParam.ToInt64() == GWL_STYLE && lParam != IntPtr.Zero)
            {
                STYLESTRUCT ss = Marshal.PtrToStructure<STYLESTRUCT>(lParam);
                if ((ss.styleNew & WS_SYSMENU) != 0)
                {
                    ss.styleNew &= ~WS_SYSMENU;
                    Marshal.StructureToPtr(ss, lParam, fDeleteOld: false);
                }
            }
            else if (msg == WM_NCRBUTTONUP && wParam.ToInt32() == HTCAPTION)
            {
                long xy = lParam.ToInt64();
                int screenX = (short)(xy & 0xFFFF);
                int screenY = (short)((xy >> 16) & 0xFFFF);
                Point dipPt = window.PointFromScreen(new Point(screenX, screenY));
                Point winScreenDip = new Point(window.Left + dipPt.X, window.Top + dipPt.Y);
                SystemCommands.ShowSystemMenu(window, winScreenDip);
                handled = true;
            }

            return IntPtr.Zero;
        }

        private static bool IsSystemTransparencyEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    writable: false);

                if (key?.GetValue("EnableTransparency") is int val)
                    return val != 0;
            }
            catch
            {
            }

            return true;
        }
    }
}
