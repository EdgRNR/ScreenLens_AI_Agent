using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace ScreenLens.WinUI.Services
{
    /// <summary>
    /// 一张显示器截图（GDI BitBlt 捕获，BGRA8）。
    /// 选区确认后按需裁剪编码为 PNG；用完即释放，不长期驻留内存。
    /// </summary>
    public sealed class VirtualScreenShot
    {
        public int Width { get; }
        public int Height { get; }

        /// <summary>截图显示器左上角的屏幕物理坐标（多显示器可能为负）。</summary>
        public int OriginX { get; }

        public int OriginY { get; }

        private byte[] _bgra;

        /// <summary>整幅 BGRA（自上而下逐行，行宽 = Width*4）。</summary>
        public byte[] Bgra => _bgra;

        internal VirtualScreenShot(int width, int height,
            int originX, int originY, byte[] bgra)
        {
            Width = width;
            Height = height;
            OriginX = originX;
            OriginY = originY;
            _bgra = bgra;
        }

        /// <summary>
        /// 释放整幅像素缓冲（4K 下约 30 MiB）。
        /// 裁剪完成后应立即调用，避免截图流程结束后仍持有大对象。
        /// </summary>
        public void ReleasePixels() => _bgra = Array.Empty<byte>();

        /// <summary>把指定物理像素区域裁剪并编码为 PNG。</summary>
        public async Task<byte[]?> CropToPngAsync(int x, int y, int w, int h,
            IReadOnlyList<Point>? maskPolygon = null)
        {
            if (_bgra.Length == 0 || Width <= 0 || Height <= 0
                || x >= Width || y >= Height || w <= 0 || h <= 0)
                return null;
            x = Math.Clamp(x, 0, Width);
            y = Math.Clamp(y, 0, Height);
            w = Math.Clamp(w, 1, Width - x);
            h = Math.Clamp(h, 1, Height - y);

            var crop = new byte[w * h * 4];
            var srcStride = Width * 4;
            var dstStride = w * 4;
            for (var row = 0; row < h; row++)
            {
                System.Buffer.BlockCopy(Bgra, (y + row) * srcStride + x * 4,
                    crop, row * dstStride, dstStride);
            }

            // 自由圈选：把多边形外像素设成白色，保留背景纹理的圈选内文字。
            if (maskPolygon is { Count: >= 3 })
            {
                var original = new byte[crop.Length];
                System.Buffer.BlockCopy(crop, 0, original, 0, crop.Length);
                Array.Fill(crop, (byte)0xFF);
                for (var py = 0; py < h; py++)
                {
                    var intersections = new List<double>();
                    var scanY = py + 0.5;
                    for (var i = 0; i < maskPolygon.Count; i++)
                    {
                        var a = maskPolygon[i];
                        var b = maskPolygon[(i + 1) % maskPolygon.Count];
                        if ((a.Y <= scanY && b.Y > scanY)
                            || (b.Y <= scanY && a.Y > scanY))
                            intersections.Add(a.X + (scanY - a.Y)
                                * (b.X - a.X) / (b.Y - a.Y));
                    }
                    intersections.Sort();
                    for (var pair = 0; pair + 1 < intersections.Count; pair += 2)
                    {
                        var left = Math.Clamp((int)Math.Ceiling(intersections[pair]), 0, w);
                        var right = Math.Clamp((int)Math.Floor(intersections[pair + 1]), 0, w);
                        for (var px = left; px < right; px++)
                        {
                            var at = py * dstStride + px * 4;
                            System.Buffer.BlockCopy(original, at, crop, at, 4);
                        }
                    }
                }
            }

            // GDI 是 BGRX；SoftwareBitmap Bgra8 通道顺序一致（BGRA）
            var sb = SoftwareBitmap.CreateCopyFromBuffer(
                crop.AsBuffer(), BitmapPixelFormat.Bgra8, w, h);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(
                BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(sb);
            await encoder.FlushAsync();

            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var size = (int)stream.Size;
            var bytes = new byte[size];
            await reader.LoadAsync((uint)size);
            reader.ReadBytes(bytes);
            return bytes;
        }
    }

    /// <summary>捕获鼠标所在显示器，避免将多显示器布局空洞放进选择窗口。</summary>
    public static class ScreenCapture
    {
        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private static readonly IntPtr DpiAwarenessContextPerMonitorV2 = new(-4);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public uint Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        public static string LastError { get; private set; } = "尚未执行屏幕捕获";

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr MonitorFromPoint(NativePoint point,
            uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr monitor,
            ref MonitorInfo info);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetThreadDpiAwarenessContext(
            IntPtr dpiContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleBitmap(
            IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest,
            int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc,
            int nYSrc, int dwRop);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start,
            uint cLines, byte[] lpBits, ref BITMAPINFOHEADER lpbi, uint usage);

        /// <summary>捕获热键触发时鼠标所在的显示器。</summary>
        public static Task<VirtualScreenShot?> CaptureAsync()
            => Task.Run(Capture);

        private static VirtualScreenShot? Capture()
        {
            LastError = "";
            // 显示器边界和 BitBlt 源坐标都使用同一线程的物理像素坐标系。
            // 不依赖线程池线程继承 UI 线程 DPI context 的实现细节。
            var previousDpiContext = SetThreadDpiAwarenessContext(
                DpiAwarenessContextPerMonitorV2);
            if (previousDpiContext == IntPtr.Zero)
                App.WriteLifecycleLog(
                    $"设置截图线程 DPI context 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            try
            {
                return CapturePerMonitorV2();
            }
            finally
            {
                if (previousDpiContext != IntPtr.Zero)
                    SetThreadDpiAwarenessContext(previousDpiContext);
            }
        }

        private static VirtualScreenShot? CapturePerMonitorV2()
        {
            if (!GetCursorPos(out var cursor))
            {
                LastError = $"读取鼠标位置失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return null;
            }

            // 鼠标、显示器边界和后续 GDI 捕获都在本线程的 PerMonitorV2
            // 上下文内完成，避免 UI 线程与线程池线程的 DPI 坐标系不一致。
            var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                LastError = $"定位鼠标所在显示器失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return null;
            }

            var info = new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>(),
            };
            if (!GetMonitorInfo(monitor, ref info)
                || info.Monitor.Right <= info.Monitor.Left
                || info.Monitor.Bottom <= info.Monitor.Top)
            {
                LastError = $"读取鼠标所在显示器边界失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return null;
            }

            var bounds = info.Monitor;
            App.WriteLifecycleLog(
                $"解析截图显示器成功：pid={Environment.ProcessId}, cursor=({cursor.X},{cursor.Y}), bounds=({bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom})");
            var vx = bounds.Left;
            var vy = bounds.Top;
            var vw = bounds.Right - bounds.Left;
            var vh = bounds.Bottom - bounds.Top;

            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                LastError = $"GetDC(桌面) 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return null;
            }
            var memDc = IntPtr.Zero;
            var bmp = IntPtr.Zero;
            try
            {
                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero)
                {
                    LastError = $"CreateCompatibleDC 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                    return null;
                }
                bmp = CreateCompatibleBitmap(screenDc, vw, vh);
                if (bmp == IntPtr.Zero)
                {
                    LastError = $"CreateCompatibleBitmap({vw}×{vh}) 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                    return null;
                }
                var old = SelectObject(memDc, bmp);
                try
                {
                    if (!BitBlt(memDc, 0, 0, vw, vh, screenDc, vx, vy,
                            SRCCOPY | CAPTUREBLT))
                    {
                        LastError = $"BitBlt 捕获屏幕失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                        return null;
                    }
                }
                finally
                {
                    if (old != IntPtr.Zero) SelectObject(memDc, old);
                }

                var bmi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = vw,
                    biHeight = -vh, // 负高 = 自上而下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                };
                var pixels = new byte[vw * vh * 4];
                var copied = GetDIBits(memDc, bmp, 0, (uint)vh, pixels,
                    ref bmi, 0);
                if (copied != vh)
                {
                    LastError = $"GetDIBits 只读取到 {copied}/{vh} 行：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                    return null;
                }

                // BI_RGB 32-bit output is BGRX: the high byte is undefined,
                // not an alpha channel. WinUI consumes BGRA and may interpret
                // arbitrary X bytes as transparency, producing black bands.
                for (var alpha = 3; alpha < pixels.Length; alpha += 4)
                    pixels[alpha] = byte.MaxValue;

                return new VirtualScreenShot(vw, vh, vx, vy, pixels);
            }
            finally
            {
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
