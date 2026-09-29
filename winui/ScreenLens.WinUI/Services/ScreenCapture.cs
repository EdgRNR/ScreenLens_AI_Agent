using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace ScreenLens.WinUI.Services
{
    /// <summary>
    /// 一张完整虚拟屏幕截图（GDI BitBlt 捕获，BGRA8）。
    /// 选区确认后按需裁剪编码为 PNG；用完即释放，不长期驻留内存。
    /// </summary>
    public sealed class VirtualScreenShot
    {
        public int Width { get; }
        public int Height { get; }

        /// <summary>虚拟屏幕左上角的屏幕物理坐标（多显示器可能为负）。</summary>
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

    /// <summary>虚拟屏幕捕获（GDI BitBlt，覆盖所有显示器）。</summary>
    public static class ScreenCapture
    {
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;

        public static string LastError { get; private set; } = "尚未执行屏幕捕获";

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

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

        /// <summary>捕获整个虚拟屏幕（所有显示器）。</summary>
        public static Task<VirtualScreenShot?> CaptureAsync()
            => Task.Run(Capture);

        private static VirtualScreenShot? Capture()
        {
            LastError = "";
            var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0)
            {
                LastError = $"虚拟屏幕尺寸无效：{vw}×{vh}，可能当前进程不在交互式桌面会话中。";
                return null;
            }

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
