using System;
using System.Runtime.InteropServices;

namespace ScreenLens.WinUI.Services
{
    internal static class CaptureWindowActivation
    {
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const int GwlExStyle = -20;
        private const long WsExTopmost = 0x00000008L;

        internal readonly record struct Result(bool Positioned, int PositionError,
            bool IsTopmost, bool IsForeground, bool InputAttached, int AttachError);

        internal static bool IsTopmost(IntPtr hwnd)
            => (GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExTopmost) != 0;

        internal static Result Raise(IntPtr hwnd, bool requestForeground)
        {
            var positioned = Position(hwnd, requestForeground);
            var error = positioned ? 0 : Marshal.GetLastWin32Error();
            if (requestForeground) SetForegroundWindow(hwnd);

            var attached = false;
            var attachError = 0;
            if (requestForeground && (GetForegroundWindow() != hwnd || !IsTopmost(hwnd)))
            {
                // A tray-launched process can be denied foreground activation.
                // Share input only for this explicit capture activation, then
                // detach before returning to the dispatcher or awaiting frames.
                var currentThread = GetCurrentThreadId();
                var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                if (foregroundThread != 0 && foregroundThread != currentThread)
                {
                    attached = AttachThreadInput(currentThread, foregroundThread, true);
                    if (!attached) attachError = Marshal.GetLastWin32Error();
                }
                try
                {
                    SetForegroundWindow(hwnd);
                    positioned = Position(hwnd, requestForeground: true);
                    error = positioned ? 0 : Marshal.GetLastWin32Error();
                }
                finally
                {
                    if (attached) AttachThreadInput(currentThread, foregroundThread, false);
                }
            }

            // A successful SetWindowPos return alone does not prove the final
            // z-order: check the native style and foreground HWND as well.
            return new Result(positioned, error, IsTopmost(hwnd),
                GetForegroundWindow() == hwnd, attached, attachError);
        }

        private static bool Position(IntPtr hwnd, bool requestForeground)
            => SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | (requestForeground ? 0 : SwpNoActivate));

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint fromThread, uint toThread, bool attach);
    }
}
