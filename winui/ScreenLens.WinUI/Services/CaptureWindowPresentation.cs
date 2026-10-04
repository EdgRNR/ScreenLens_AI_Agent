using System;
using System.Runtime.InteropServices;

namespace ScreenLens.WinUI.Services
{
    // Hide the initial HWND surface from desktop composition without hiding the
    // HWND itself. WinUI can run layout/rendering while the window is cloaked.
    internal static class CaptureWindowPresentation
    {
        private const int DwmwaCloak = 13;
        private const int DwmwaCloaked = 14;

        internal static void Cloak(IntPtr hwnd) => SetCloaked(hwnd, true);

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
    }
}
