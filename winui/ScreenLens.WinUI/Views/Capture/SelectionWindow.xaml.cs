using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using ScreenLens.WinUI.Services;
using ScreenLens.WinUI.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace ScreenLens.WinUI.Views.Capture
{
    /// <summary>
    /// 真实截图选区窗口：
    /// - 覆盖整个虚拟屏幕（无边框置顶），背景为 GDI 捕获的冻结画面；
    /// - 拖拽绘制矩形选区（Enter / 双击确认，Esc 取消）；
    /// - 确认后裁剪选区为 PNG，经 IPC 交给 Python worker 做 OCR；
    /// - 「松开即识别」由前端偏好 ConfirmOnRelease 控制。
    /// 选区物理坐标 = 逻辑坐标 × RasterizationScale + 虚拟屏原点。
    /// </summary>
    public sealed partial class SelectionWindow : Window
    {
        private readonly DemoSettings _vm = DemoSettings.Instance;
        private readonly VirtualScreenShot _shot;
        private readonly Windows.Graphics.RectInt32? _presetRegion;
        private readonly bool _autoRecognize;

        private bool _dragging;
        private Point _start;
        private Rect _sel;
        private readonly List<Point> _freeformPoints = new();
        private bool _freeformSelection;
        private const double MinSize = 8;

        private bool _confirmed;

        /// <param name="shot">冻结的虚拟屏截图</param>
        /// <param name="presetRegion">可选：预置选区（虚拟屏物理像素），
        /// 供脚本化调用（--region=x,y,w,h）</param>
        /// <param name="autoRecognize">预置选区后是否立即识别（--auto）</param>
        public SelectionWindow(VirtualScreenShot shot,
            Windows.Graphics.RectInt32? presetRegion = null,
            bool autoRecognize = false)
        {
            _shot = shot;
            _presetRegion = presetRegion;
            _autoRecognize = autoRecognize;
            _freeformSelection = _vm.DefaultCaptureMode == 1;
            InitializeComponent();
            Root.RequestedTheme = App.ResolveTheme(_vm.ThemeMode);
            App.RegisterCaptureWindow(this);

            Title = "ScreenLens 截图";

            SetupPresenter();
            _ = LoadShotImageAsync();

            Root.Loaded += (_, _) =>
            {
                // 窗口首次显示后再套用覆盖整个虚拟屏幕的边界：
                // 在 Activate 之前 MoveAndResize 会被系统按默认尺寸覆盖。
                ApplyFullVirtualScreenBounds();
                Root.Focus(FocusState.Programmatic);
                HintText.Text = _freeformSelection
                    ? "自由圈选屏幕区域 · Enter 确认 · Esc 取消"
                    : "拖拽选择识别区域 · Enter 确认 · Esc 取消";
                if (_presetRegion is { } r)
                {
                    ApplyPresetRegion(r);
                }
            };
        }

        /// <summary>把命令行预置的物理像素矩形换算为逻辑选区。</summary>
        private void ApplyPresetRegion(Windows.Graphics.RectInt32 r)
        {
            // 相对虚拟屏原点 → 相对窗口客户区，再除以缩放得到逻辑坐标
            var scale = Scale <= 0 ? 1.0 : Scale;
            _sel = new Rect(
                r.X / scale,
                r.Y / scale,
                r.Width / scale,
                r.Height / scale);
            ApplySelection();
            HintBar.Visibility = Visibility.Collapsed;
            if (_autoRecognize)
            {
                _ = ConfirmSelectionAsync();
            }
            else
            {
                ShowToolbar();
            }
        }

        // ---------------------------------------------------------- 窗口

        private void SetupPresenter()
        {
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(false, false);
                p.IsResizable = false;
                p.IsMaximizable = false;
                p.IsMinimizable = false;
                p.IsAlwaysOnTop = true;
            }
        }

        /// <summary>覆盖整个虚拟屏幕（物理像素坐标，多显示器可为负）。</summary>
        private void ApplyFullVirtualScreenBounds()
        {
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                _shot.OriginX, _shot.OriginY, _shot.Width, _shot.Height));
        }

        private async Task LoadShotImageAsync()
        {
            try
            {
                // 用 WriteableBitmap 同步填充 BGRA（不需要预乘 alpha），
                // GDI 抓屏数据的 alpha 字节不是 0，预乘转换复杂；
                // Image 用 Stretch=Fill 显示会自动按窗口逻辑尺寸缩放。
                var wb = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(
                    _shot.Width, _shot.Height);
                using (var stream = wb.PixelBuffer.AsStream())
                {
                    await stream.WriteAsync(_shot.Bgra, 0, _shot.Bgra.Length);
                }
                ShotImage.Source = wb;
            }
            catch (Exception e)
            {
                LogCrash("LoadShotImageAsync", e);
            }
        }

        private static void LogCrash(string source, Exception ex)
        {
            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "screenlens_winui_crash.log");
                System.IO.File.AppendAllText(path,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {source}\n{ex}\n\n");
            }
            catch { /* 忽略 */ }
        }

        private double Scale => Root.XamlRoot?.RasterizationScale ?? 1.0;

        // ------------------------------------------------------- 指针交互

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (BusyOverlay.Visibility == Visibility.Visible) return;
            var p = e.GetCurrentPoint(Root);
            if (!p.Properties.IsLeftButtonPressed) return;

            _dragging = true;
            _start = p.Position;
            HideToolbar();
            Root.CapturePointer(e.Pointer);
            _freeformPoints.Clear();
            if (_freeformSelection)
            {
                _freeformPoints.Add(_start);
                FreeformLine.Visibility = Visibility.Visible;
            }
            _sel = new Rect(_start.X, _start.Y, 0, 0);
            ApplySelection();
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetCurrentPoint(Root);
            if (_freeformSelection)
            {
                var last = _freeformPoints[^1];
                var dx = p.Position.X - last.X;
                var dy = p.Position.Y - last.Y;
                if (dx * dx + dy * dy >= 9)
                {
                    _freeformPoints.Add(p.Position);
                    UpdateFreeformBounds();
                }
                return;
            }
            var w = Math.Abs(p.Position.X - _start.X);
            var h = Math.Abs(p.Position.Y - _start.Y);
            _sel = new Rect(
                Math.Min(_start.X, p.Position.X),
                Math.Min(_start.Y, p.Position.Y),
                w, h);
            ApplySelection();
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            try { Root.ReleasePointerCapture(e.Pointer); }
            catch { /* 忽略 */ }

            if (_freeformSelection && _freeformPoints.Count > 0)
            {
                var end = e.GetCurrentPoint(Root).Position;
                if (_freeformPoints[^1] != end) _freeformPoints.Add(end);
                if (_freeformPoints.Count >= 3
                    && _freeformPoints[^1] != _freeformPoints[0])
                    _freeformPoints.Add(_freeformPoints[0]);
                UpdateFreeformBounds();
            }

            if (_sel.Width < MinSize || _sel.Height < MinSize
                || (_freeformSelection && _freeformPoints.Count < 4))
            {
                // 视为误触：清除选区
                _sel = new Rect(0, 0, 0, 0);
                _freeformPoints.Clear();
                FreeformLine.Visibility = Visibility.Collapsed;
                ApplySelection();
                return;
            }
            if (_vm.ConfirmOnRelease)
            {
                ShowToolbar();
            }
            else
            {
                _ = ConfirmSelectionAsync();
            }
        }

        private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (BusyOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    CancelOcr();
                }
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                Close();
            }
            else if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (_sel.Width >= MinSize && _sel.Height >= MinSize)
                {
                    _ = ConfirmSelectionAsync();
                }
            }
        }

        // -------------------------------------------------------- 选区渲染

        private void ApplySelection()
        {
            var has = _sel.Width >= MinSize && _sel.Height >= MinSize;
            var vis = has ? Visibility.Visible : Visibility.Collapsed;
            SelBorder.Visibility = vis;
            DimTop.Visibility = DimBottom.Visibility = vis;
            DimLeft.Visibility = DimRight.Visibility = vis;
            SizeTag.Visibility = vis;
            if (!has)
            {
                return;
            }

            SelBorder.Margin = new Thickness(
                _sel.X, _sel.Y, 0, 0);
            SelBorder.Width = _sel.Width;
            SelBorder.Height = _sel.Height;
            SelBorder.HorizontalAlignment = HorizontalAlignment.Left;
            SelBorder.VerticalAlignment = VerticalAlignment.Top;

            DimTop.Width = DimBottom.Width = DimLeft.Width = DimRight.Width =
                Root.ActualWidth;
            DimTop.Height = _sel.Y;

            DimBottom.Margin = new Thickness(0, _sel.Y + _sel.Height, 0, 0);
            DimBottom.Height = Math.Max(0, Root.ActualHeight - _sel.Y - _sel.Height);

            DimLeft.Margin = new Thickness(0, _sel.Y, 0, 0);
            DimLeft.Width = _sel.X;
            DimLeft.Height = _sel.Height;

            DimRight.Margin = new Thickness(_sel.X + _sel.Width, _sel.Y, 0, 0);
            DimRight.Width = Math.Max(0, Root.ActualWidth - _sel.X - _sel.Width);
            DimRight.Height = _sel.Height;

            // 尺寸显示物理像素
            SizeText.Text = string.Format("{0:F0} × {1:F0}",
                _sel.Width * Scale, _sel.Height * Scale);
            SizeTag.Margin = new Thickness(
                _sel.X + _sel.Width + 8,
                Math.Max(4, _sel.Y - 30),
                0, 0);
            if (_freeformSelection) UpdateFreeformLine();
        }

        private void UpdateFreeformBounds()
        {
            if (_freeformPoints.Count == 0) return;
            var left = _freeformPoints.Min(p => p.X);
            var top = _freeformPoints.Min(p => p.Y);
            var right = _freeformPoints.Max(p => p.X);
            var bottom = _freeformPoints.Max(p => p.Y);
            _sel = new Rect(left, top, right - left, bottom - top);
            ApplySelection();
        }

        private void UpdateFreeformLine()
        {
            var points = new PointCollection();
            foreach (var point in _freeformPoints) points.Add(point);
            FreeformLine.Points = points;
            FreeformLine.Visibility = _freeformPoints.Count >= 2
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowToolbar()
        {
            Toolbar.Visibility = Visibility.Visible;
            Toolbar.Margin = new Thickness(
                0, 0, 0, Math.Max(16,
                    Root.ActualHeight - _sel.Y - _sel.Height - 56));
        }

        private void HideToolbar()
        {
            Toolbar.Visibility = Visibility.Collapsed;
        }

        // -------------------------------------------------------- 确认/取消

        private void OnConfirmClick(object sender, RoutedEventArgs e)
            => _ = ConfirmSelectionAsync();

        private void OnReselectClick(object sender, RoutedEventArgs e)
        {
            HideToolbar();
            _sel = new Rect(0, 0, 0, 0);
            _freeformPoints.Clear();
            FreeformLine.Visibility = Visibility.Collapsed;
            ApplySelection();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

        private async Task ConfirmSelectionAsync()
        {
            if (_confirmed) return;
            _confirmed = true;
            HideToolbar();
            HintBar.Visibility = Visibility.Collapsed;

            // 逻辑坐标 → 截图像素坐标（截图即虚拟屏物理像素）
            var px = (int)(_sel.X * Scale);
            var py = (int)(_sel.Y * Scale);
            var pw = (int)Math.Ceiling(_sel.Width * Scale);
            var ph = (int)Math.Ceiling(_sel.Height * Scale);

            BusyOverlay.Visibility = Visibility.Visible;
            BusyText.Text = "正在识别…";

            try
            {
                var maskPolygon = _freeformSelection
                    ? _freeformPoints.Select(p => new Point(
                        p.X * Scale - px, p.Y * Scale - py)).ToArray()
                    : null;
                var png = await _shot.CropToPngAsync(px, py, pw, ph,
                    maskPolygon);
                if (png is null || png.Length == 0)
                {
                    throw new BackendException("image_too_large", "选区裁剪失败");
                }

                // 调试：把裁剪后的 PNG 写到磁盘便于排查 OCR 为空的情况。
                // 由环境变量 SCREENLENS_DEBUG_DUMP 启用（值为目录）。
                var dumpDir = System.Environment.GetEnvironmentVariable("SCREENLENS_DEBUG_DUMP");
                if (!string.IsNullOrEmpty(dumpDir))
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(dumpDir);
                        var name = $"shot_{System.DateTime.Now:HHmmss_fff}_x{px}y{py}w{pw}h{ph}.png";
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dumpDir, name), png);
                    }
                    catch { /* 忽略调试写入失败 */ }
                }

                var resp = await BackendClient.Instance.CallAsync(
                    "RecognizeImage", null, png, timeoutMs: 90_000);

                // 裁剪已完成：立即释放整幅像素与显示位图（几十 MiB），
                // 本进程随后只保留结果窗口所需的数据。
                _shot.ReleasePixels();
                ShotImage.Source = null;

                // 屏幕物理坐标（供结果窗口定位）
                var screenX = _shot.OriginX + px;
                var screenY = _shot.OriginY + py;

                BusyOverlay.Visibility = Visibility.Collapsed;

                var result = new Views.Result.ResultWindow(resp, screenX, screenY,
                    pw, ph, this);
                result.Activate();
                // 本窗口在结果窗口显示后关闭（进程由结果窗口维持）
                Close();
            }
            catch (BackendException ex)
            {
                _confirmed = false;
                BusyOverlay.Visibility = Visibility.Collapsed;
                ErrorBar.Message = ex.Message;
                ErrorBar.Severity = InfoBarSeverity.Error;
                ErrorBar.IsOpen = true;
                HintBar.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                _confirmed = false;
                BusyOverlay.Visibility = Visibility.Collapsed;
                ErrorBar.Message = $"识别失败：{ex.Message}";
                ErrorBar.Severity = InfoBarSeverity.Error;
                ErrorBar.IsOpen = true;
                HintBar.Visibility = Visibility.Visible;
            }
        }

        /// <summary>取消进行中的 OCR（Esc 在识别中时触发）。</summary>
        private void CancelOcr()
        {
            _ = BackendClient.Instance.CallAsync("CancelRequest", null,
                timeoutMs: 3000);
        }
    }
}
