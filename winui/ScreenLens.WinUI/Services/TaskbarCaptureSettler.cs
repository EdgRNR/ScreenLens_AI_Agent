using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenLens.WinUI.Services
{
    // Explorer animates taskbar entries independently of our HWND and DwmFlush.
    // Sample only the taskbar strips until their pixels stop changing. Buffers
    // and GDI objects exist only during this bounded, off-dispatcher wait.
    internal static class TaskbarCaptureSettler
    {
        internal readonly record struct Result(int ElapsedMilliseconds, bool Settled, bool ProbeAvailable);

        internal static Task<Result> WaitAsync() => Task.Run(Wait);

        private static Result Wait()
        {
            var clock = Stopwatch.StartNew();
            var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            var probes = new List<TaskbarProbe>();
            try
            {
                foreach (var className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
                {
                    var window = IntPtr.Zero;
                    while ((window = FindWindowEx(IntPtr.Zero, window, className, null)) != IntPtr.Zero)
                    {
                        if (!IsWindowVisible(window)) continue;
                        probes.Add(new TaskbarProbe(window));
                    }
                }
                if (probes.Count == 0) return new Result((int)clock.ElapsedMilliseconds, true, true);
                var stability = new TaskbarStability();
                while (true)
                {
                    ulong fingerprint = 14695981039346656037UL;
                    foreach (var probe in probes)
                        fingerprint = unchecked((fingerprint ^ probe.ReadFingerprint()) * 1099511628211UL);
                    var elapsed = (int)clock.ElapsedMilliseconds;
                    if (stability.Observe(fingerprint, elapsed))
                        return new Result(elapsed, true, true);
                    if (elapsed >= TaskbarStability.MaximumWaitMilliseconds)
                        return new Result(elapsed, false, true);
                    Thread.Sleep(30);
                }
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception
                or InvalidOperationException or OverflowException)
            {
                // Auto-hidden taskbars, Explorer restarts and display changes
                // can invalidate a probe. Fall back without failing capture.
                var remaining = 450 - (int)clock.ElapsedMilliseconds;
                if (remaining > 0) Thread.Sleep(remaining);
                return new Result((int)clock.ElapsedMilliseconds, false, false);
            }
            finally
            {
                foreach (var probe in probes) probe.Dispose();
                if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi);
            }
        }

        private sealed class TaskbarProbe : IDisposable
        {
            private readonly IntPtr _window;
            private readonly NativeRect _bounds;
            private readonly byte[] _pixels;
            private IntPtr _screenDc, _memoryDc, _bitmap, _previousBitmap, _bits;

            internal TaskbarProbe(IntPtr window)
            {
                _window = window;
                if (!GetWindowRect(window, out _bounds)) ThrowNativeError();
                var width = _bounds.Right - _bounds.Left;
                var height = _bounds.Bottom - _bounds.Top;
                var bytes = checked(width * height * 4);
                if (width <= 0 || height <= 0 || bytes > 16 * 1024 * 1024)
                    throw new InvalidOperationException("Invalid taskbar bounds.");
                _pixels = new byte[bytes];
                try
                {
                    _screenDc = GetDC(IntPtr.Zero);
                    if (_screenDc == IntPtr.Zero) ThrowNativeError();
                    _memoryDc = CreateCompatibleDC(_screenDc);
                    if (_memoryDc == IntPtr.Zero) ThrowNativeError();
                    var header = new BitmapHeader
                    {
                        Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32,
                    };
                    _bitmap = CreateDIBSection(_screenDc, ref header, 0, out _bits, IntPtr.Zero, 0);
                    if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero) ThrowNativeError();
                    _previousBitmap = SelectObject(_memoryDc, _bitmap);
                    if (_previousBitmap == IntPtr.Zero || _previousBitmap == new IntPtr(-1)) ThrowNativeError();
                }
                catch { Dispose(); throw; }
            }

            internal ulong ReadFingerprint()
            {
                if (!GetWindowRect(_window, out var current) || !current.Equals(_bounds))
                    throw new InvalidOperationException("Taskbar moved during capture preparation.");
                if (!BitBlt(_memoryDc, 0, 0, _bounds.Right - _bounds.Left, _bounds.Bottom - _bounds.Top,
                    _screenDc, _bounds.Left, _bounds.Top, 0x40CC0020)) ThrowNativeError();
                if (!GdiFlush()) ThrowNativeError();
                Marshal.Copy(_bits, _pixels, 0, _pixels.Length);
                ulong hash = 14695981039346656037UL;
                // BGRX: ignore the undefined alpha byte when comparing frames.
                for (var i = 0; i < _pixels.Length; i += 4)
                {
                    var rgb = (uint)(_pixels[i] | _pixels[i + 1] << 8 | _pixels[i + 2] << 16);
                    hash = unchecked((hash ^ rgb) * 1099511628211UL);
                }
                return hash;
            }

            public void Dispose()
            {
                if (_memoryDc != IntPtr.Zero && _previousBitmap != IntPtr.Zero && _previousBitmap != new IntPtr(-1))
                    SelectObject(_memoryDc, _previousBitmap);
                if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
                if (_memoryDc != IntPtr.Zero) DeleteDC(_memoryDc);
                if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
                _bitmap = _memoryDc = _screenDc = _previousBitmap = _bits = IntPtr.Zero;
            }
        }

        private static void ThrowNativeError() => throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapHeader
        {
            internal uint Size;
            internal int Width, Height;
            internal ushort Planes, BitCount;
            internal uint Compression, ImageSize;
            internal int XPelsPerMeter, YPelsPerMeter;
            internal uint ColorsUsed, ColorsImportant;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint usage,
            out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr dc, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, int operation);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool GdiFlush();
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);
    }

    internal sealed class TaskbarStability
    {
        internal const int MaximumWaitMilliseconds = 900;
        private ulong? _previous;
        private int _stableSince;

        internal bool Observe(ulong fingerprint, int elapsedMilliseconds)
        {
            if (_previous != fingerprint)
            {
                _previous = fingerprint;
                _stableSince = elapsedMilliseconds;
            }
            // Allow queued shell changes to start before accepting stability;
            // then require a quiet interval longer than individual frame gaps.
            return elapsedMilliseconds >= 200 && elapsedMilliseconds - _stableSince >= 150;
        }
    }
}
