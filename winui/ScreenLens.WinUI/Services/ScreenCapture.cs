using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Graphics;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace ScreenLens.WinUI.Services
{
    /// <summary>
    /// 一张覆盖所有显示器的虚拟桌面截图（GDI BitBlt 捕获，BGRA8）。
    /// 选区确认后按需裁剪编码为 PNG；用完即释放，不长期驻留内存。
    /// </summary>
    public sealed class VirtualScreenShot
    {
        public int Width { get; }
        public int Height { get; }

        /// <summary>虚拟桌面左上角的屏幕物理坐标（多显示器可能为负）。</summary>
        public int OriginX { get; }

        public int OriginY { get; }

        /// <summary>虚拟桌面内各显示器的物理像素边界。</summary>
        public IReadOnlyList<RectInt32> MonitorBounds { get; }

        private byte[] _bgra;

        /// <summary>整幅 BGRA（自上而下逐行，行宽 = Width*4）。</summary>
        public byte[] Bgra => _bgra;

        internal VirtualScreenShot(int width, int height,
            int originX, int originY, byte[] bgra,
            IReadOnlyList<RectInt32> monitorBounds)
        {
            Width = width;
            Height = height;
            OriginX = originX;
            OriginY = originY;
            _bgra = bgra;
            MonitorBounds = monitorBounds;
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

    /// <summary>捕获所有显示器并按虚拟桌面坐标合成为单张截图。</summary>
    public static class ScreenCapture
    {
        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;
        private const int BLACKNESS = 0x00000042;
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
        private struct MonitorInfo
        {
            public uint Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc,
            ref NativeRect bounds, IntPtr data);

        public static string LastError { get; private set; } = "尚未执行屏幕捕获";

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc,
            IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

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

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool PatBlt(IntPtr hdc, int x, int y,
            int width, int height, int rop);

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

        /// <summary>捕获所有已连接显示器；热键触发位置不影响可选区域。</summary>
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
                return CaptureVirtualDesktop();
            }
            finally
            {
                if (previousDpiContext != IntPtr.Zero)
                    SetThreadDpiAwarenessContext(previousDpiContext);
            }
        }

        private static VirtualScreenShot? CaptureVirtualDesktop()
        {
            var monitors = new List<NativeRect>();
            MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc,
                ref NativeRect bounds, IntPtr data) =>
            {
                var info = new MonitorInfo
                {
                    Size = (uint)Marshal.SizeOf<MonitorInfo>(),
                };
                if (!GetMonitorInfo(monitor, ref info))
                    return false;
                if (info.Monitor.Right > info.Monitor.Left
                    && info.Monitor.Bottom > info.Monitor.Top)
                    monitors.Add(info.Monitor);
                return true;
            };

            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback,
                    IntPtr.Zero))
            {
                LastError = $"枚举显示器失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return null;
            }
            GC.KeepAlive(callback);
            if (monitors.Count == 0)
            {
                LastError = "没有找到可用的显示器";
                return null;
            }

            var vx = monitors.Min(m => m.Left);
            var vy = monitors.Min(m => m.Top);
            var right = monitors.Max(m => m.Right);
            var bottom = monitors.Max(m => m.Bottom);
            var vw = right - vx;
            var vh = bottom - vy;
            long byteCount = (long)vw * vh * 4;
            if (vw <= 0 || vh <= 0 || byteCount > int.MaxValue)
            {
                LastError = $"虚拟桌面截图尺寸无效或过大：{vw}×{vh}";
                return null;
            }

            App.WriteLifecycleLog(
                $"解析虚拟桌面成功：pid={Environment.ProcessId}, monitors={monitors.Count}, bounds=({vx},{vy},{right},{bottom}), size={vw}x{vh}");

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
                    // 显示器之间的虚拟桌面空隙不属于任何屏幕，先明确填黑，
                    // 避免兼容位图未初始化的内容泄漏到选区覆盖层。
                    if (!PatBlt(memDc, 0, 0, vw, vh, BLACKNESS))
                    {
                        LastError = $"初始化虚拟桌面截图画布失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                        return null;
                    }

                    // 每台显示器单独 BitBlt，避免跨显示适配器的一次性
                    // BitBlt 失败；目标坐标统一相对虚拟桌面左上角。
                    foreach (var monitor in monitors)
                    {
                        var width = monitor.Right - monitor.Left;
                        var height = monitor.Bottom - monitor.Top;
                        if (!BitBlt(memDc, monitor.Left - vx,
                                monitor.Top - vy, width, height, screenDc,
                                monitor.Left, monitor.Top,
                                SRCCOPY | CAPTUREBLT))
                        {
                            LastError = $"BitBlt 捕获显示器区域 ({monitor.Left},{monitor.Top},{width}×{height}) 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                            return null;
                        }
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

                var monitorBounds = monitors.Select(m => new RectInt32(
                    m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top)).ToArray();
                return new VirtualScreenShot(vw, vh, vx, vy, pixels,
                    monitorBounds);
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
