using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ScreenLens.WinUI.Services
{
    // Hide the initial HWND surface from desktop composition without hiding the
    // HWND itself. WinUI can run layout/rendering while the window is cloaked.
    internal static class CaptureWindowPresentation
    {
        private const int DwmwaCloak = 13;
        private const int DwmwaCloaked = 14;

        internal static void ExcludeFromTaskbar(IntPtr hwnd)
        {
            const int exStyleIndex = -20;
            const int appWindow = 0x00040000, toolWindow = 0x00000080;
            SetStyle(hwnd, exStyleIndex, (GetWindowLong(hwnd, exStyleIndex) & ~appWindow) | toolWindow);
        }

        internal static void ShowForPreparation(IntPtr hwnd)
        {
            // Render the cloaked HWND without moving focus away from the
            // desktop. Activation belongs to the completed first-frame reveal.
            Cloak(hwnd);
            ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
        }

        internal static void ConfigureBorderless(IntPtr hwnd)
        {
            // Presenter settings alone can retain a native frame/client inset.
            // Use a popup surface with no resize, dialog, or extended edge.
            const int styleIndex = -16, exStyleIndex = -20;
            const int captionAndFrame = 0x00C00000 | 0x00040000;
            const int popup = unchecked((int)0x80000000);
            const int extendedEdges = 0x00000100 | 0x00000200 | 0x00020000;
            SetStyle(hwnd, styleIndex, (GetWindowLong(hwnd, styleIndex) & ~captionAndFrame) | popup);
            SetStyle(hwnd, exStyleIndex, GetWindowLong(hwnd, exStyleIndex) & ~extendedEdges);

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                // Windows 11 can draw a one-pixel border even on a captionless
                // window. Disable it and rounding explicitly for the overlay.
                var noRounding = 1; // DWMWCP_DONOTROUND
                Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, 33, ref noRounding, sizeof(int)));
                var noBorder = -2; // DWMWA_COLOR_NONE
                Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int)));
            }
            // Recalculate the non-client area without moving, resizing,
            // showing, activating, or changing the window's z-order.
            if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static void SetStyle(IntPtr hwnd, int index, int style)
        {
            Marshal.SetLastPInvokeError(0);
            if (SetWindowLong(hwnd, index, style) == 0 && Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        internal static void Cloak(IntPtr hwnd) => SetCloaked(hwnd, true);

        internal static void HideBeforeCapture(IntPtr hwnd)
        {
            // Hide animations can leave a translucent old surface on the
            // desktop after AppWindow.Hide returns. Remove it from desktop
            // composition first, then wait before GDI samples the screen.
            Cloak(hwnd);
            ShowWindow(hwnd, 0); // SW_HIDE; return value is previous visibility.
            Marshal.ThrowExceptionForHR(DwmFlush());
        }

        internal static void Reveal(IntPtr hwnd)
        {
            // Called after XAML has rendered and its compositor commit has
            // completed. Flush preceding DWM work before exposing the surface.
            Marshal.ThrowExceptionForHR(DwmFlush());
            SetCloaked(hwnd, false);
        }

        private static void SetCloaked(IntPtr hwnd, bool cloaked)
        {
            var value = cloaked ? 1 : 0;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int)));
        }

        internal static int GetCloakedFlags(IntPtr hwnd)
        {
            Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var value, sizeof(int)));
            return value;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);
        // Styles are 32-bit values on both x86 and x64, unlike HWND pointers.
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);
    }
}
