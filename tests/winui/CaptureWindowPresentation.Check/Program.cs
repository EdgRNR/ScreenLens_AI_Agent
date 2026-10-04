using System;
using System.Runtime.InteropServices;
using ScreenLens.WinUI.Services;

// Exercise the production DWM calls on a hidden HWND. This checks the native
// contract without capturing the desktop or showing a full-screen test window.
internal static class Program
{
    private static readonly WindowProc Procedure = DefWindowProc;

    private static void Main()
    {
        var className = $"ScreenLens.PresentationCheck.{Environment.ProcessId}";
        var nativeClass = new WindowClass
        {
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            ClassName = className,
        };
        Require(RegisterClass(ref nativeClass) != 0, "register hidden test class");
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            hwnd = CreateWindowEx(0, className, "", 0x80000000, -10000, -10000,
                4, 4, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Require(hwnd != IntPtr.Zero, "create hidden test window");
            for (var cycle = 0; cycle < 2; cycle++)
            {
                CaptureWindowPresentation.Cloak(hwnd);
                Require((CaptureWindowPresentation.GetCloakedFlags(hwnd) & 1) != 0,
                    "DWM reports application cloak before first show");
                Require(!IsWindowVisible(hwnd), "cloaking does not show the window");
                CaptureWindowPresentation.Reveal(hwnd);
                Require((CaptureWindowPresentation.GetCloakedFlags(hwnd) & 1) == 0,
                    "reveal removes application cloak after synchronization");
                Require(!IsWindowVisible(hwnd), "reveal leaves native visibility to the caller");
            }
            Console.WriteLine("PASS: hidden HWND startup cloak, synchronized reveal, visibility, and repeated use.");
        }
        finally
        {
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            UnregisterClass(className, IntPtr.Zero);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Style;
        internal IntPtr Procedure;
        internal int ClassExtra, WindowExtra;
        internal IntPtr Instance, Icon, Cursor, Background;
        internal string? MenuName, ClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WindowClass info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}
