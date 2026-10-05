using System;
using System.Runtime.InteropServices;
using ScreenLens.WinUI.Services;

// Exercise the production DWM calls on a hidden HWND. This checks the native
// contract without capturing the desktop or showing a full-screen test window.
internal static class Program
{
    private static readonly WindowProc Procedure = DefWindowProc;

    private static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static void Run()
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
            hwnd = CreateWindowEx(0x00020300, className, "", 0x00CF0000, -10000, -10000,
                320, 240, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Require(hwnd != IntPtr.Zero, "create hidden test window");
            var foregroundBefore = CaptureWindowActivation.GetForegroundWindow();
            Require(GetWindowRect(hwnd, out var boundsBefore), "read initial test window bounds");
            CaptureWindowPresentation.ConfigureBorderless(hwnd);
            Require(GetClientRect(hwnd, out var client) && client.Right == 320 && client.Bottom == 240,
                "borderless client covers every pixel of the outer window");
            Require((GetWindowLong(hwnd, -16) & 0x00C40000) == 0, "caption and resize frame removed");
            Require((GetWindowLong(hwnd, -20) & 0x00020300) == 0, "extended edges removed");
            Require(!IsWindowVisible(hwnd), "frame configuration never reveals the startup surface");
            Require(CaptureWindowActivation.GetForegroundWindow() == foregroundBefore,
                "frame configuration never steals foreground");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                Require(DwmGetWindowAttribute(hwnd, 33, out var corners, 4) >= 0 && corners == 1,
                    "DWM rounding disabled");
                var borderStatus = DwmGetWindowAttribute(hwnd, 34, out var borderColor, 4);
                // Some Windows builds support setting this attribute but not
                // reading it. ConfigureBorderless already checks the setter HR.
                Require(borderStatus == unchecked((int)0x80070057)
                    || (borderStatus >= 0 && borderColor == -2),
                    $"DWM border color disabled (hr=0x{borderStatus:X8}, color=0x{borderColor:X8})");
            }
            for (var cycle = 0; cycle < 2; cycle++)
            {
                CaptureWindowPresentation.Cloak(hwnd);
                Require((CaptureWindowPresentation.GetCloakedFlags(hwnd) & 1) != 0,
                    "DWM reports application cloak before first show");
                Require(!IsWindowVisible(hwnd), "cloaking does not show the window");
                // Native-visible but cloaked: exercise recapture hiding without
                // exposing a test surface or stealing focus on the desktop.
                ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
                Require(IsWindowVisible(hwnd), "test surface has native visibility while cloaked");
                CaptureWindowPresentation.HideBeforeCapture(hwnd);
                Require(!IsWindowVisible(hwnd), "recapture synchronously hides the old HWND");
                Require((CaptureWindowPresentation.GetCloakedFlags(hwnd) & 1) != 0,
                    "old surface stays out of desktop composition before capture");
                // Remove native topmost state to exercise recovery rather than
                // repeatedly passing because the window was already topmost.
                Require(SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0013),
                    "reset test window to ordinary z-order");
                Require(!CaptureWindowActivation.IsTopmost(hwnd), "topmost state was removed");
                var raised = CaptureWindowActivation.Raise(hwnd, requestForeground: false);
                Require(raised.Positioned && raised.IsTopmost, "restore actual native topmost state");
                Require(!raised.InputAttached, "ordinary z-order maintenance does not attach input");
                Require(CaptureWindowActivation.GetForegroundWindow() == foregroundBefore,
                    "ordinary z-order maintenance preserves foreground");
                Require(!IsWindowVisible(hwnd), "raising does not expose an unprepared or closing window");
                Require((CaptureWindowPresentation.GetCloakedFlags(hwnd) & 1) != 0,
                    "raising preserves startup cloak");
                Require(GetWindowRect(hwnd, out var boundsAfter) && boundsAfter.Equals(boundsBefore),
                    "raising preserves virtual desktop position and size");
                CaptureWindowPresentation.Reveal(hwnd);
                Require((CaptureWindowPresentation.GetCloakedFlags(hwnd) & 1) == 0,
                    "reveal removes application cloak after synchronization");
                Require(!IsWindowVisible(hwnd), "reveal leaves native visibility to the caller");
            }
            Console.WriteLine("PASS: frame-free client bounds, no DWM border/rounding, recapture synchronization, topmost recovery, foreground preservation, startup cloak, and synchronized reveal.");
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }

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
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,
        int x, int y, int width, int height, uint flags);
}
