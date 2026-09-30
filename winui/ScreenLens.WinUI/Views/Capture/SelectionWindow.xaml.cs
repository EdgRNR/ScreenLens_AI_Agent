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
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace ScreenLens.WinUI.Views.Capture
{
    /// <summary>
    /// 真实截图选区窗口：
    /// - 覆盖整个虚拟桌面（无边框置顶），背景为所有显示器的冻结画面；
    /// - 拖拽绘制矩形选区（Enter / 双击确认，Esc 取消）；
    /// - 确认后裁剪选区为 PNG，经 IPC 交给 Python worker 做 OCR；
    /// - 「松开即识别」由前端偏好 ConfirmOnRelease 控制。
    /// 选区像素按截图位图与窗口客户区的实际宽高比例换算，再加上虚拟桌面原点。
    /// </summary>
    public sealed partial class SelectionWindow : Window
    {
        private readonly DemoSettings _vm = DemoSettings.Instance;
        private readonly VirtualScreenShot _shot;
        private readonly Windows.Graphics.RectInt32? _presetRegion;
        private readonly bool _autoRecognize;
        private readonly GeometryGroup _dimGeometry = new()
        {
            FillRule = FillRule.EvenOdd,
        };
        private readonly RectangleGeometry _screenGeometry = new();
        private readonly RectangleGeometry _selectionGeometry = new();

        private bool _dragging;
        private Point _start;
        private Rect _sel;
        private readonly List<Point> _freeformPoints = new();
        private readonly List<Border> _hintBars = new();
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
            _dimGeometry.Children.Add(_screenGeometry);
            _dimGeometry.Children.Add(_selectionGeometry);
            DimMask.Data = _dimGeometry;
            Root.RequestedTheme = App.ResolveTheme(_vm.ThemeMode);
            App.RegisterCaptureWindow(this);

            Title = "ScreenLens 截图";

            SetupPresenter();
            // Place the HWND over the full virtual desktop before it is first
            // shown; activation should not briefly expose the primary monitor.
            ApplyCaptureMonitorBounds();

            Root.Loaded += (_, _) =>
            {
                try
                {
                    Root.Focus(FocusState.Programmatic);
                    App.WriteLifecycleLog($"截图选区已布局：virtualOrigin=({_shot.OriginX},{_shot.OriginY}), pixels={_shot.Width}x{_shot.Height}, dips={Root.ActualWidth:F1}x{Root.ActualHeight:F1}, pixelScale=({PixelScaleX:F3},{PixelScaleY:F3}), hwnd={WindowBounds}");
                    CreateMonitorHints();
                    if (_presetRegion is { } r)
                    {
                        ApplyPresetRegion(r);
                    }
                    // Keep the native window transparent until its first XAML
                    // layout is complete, then reveal the prepared content.
                    Root.Opacity = 1;
                }
                catch (Exception ex)
                {
                    App.WriteLifecycleLog($"截图选区窗口布局失败：{ex.GetType().Name}: {ex.Message}");
                    LogCrash("SelectionWindow.Loaded", ex);
                    ErrorBar.Message = $"截图界面初始化失败：{ex.Message}";
                    ErrorBar.IsOpen = true;
                }
            };
        }

        /// <summary>把命令行预置的物理像素矩形换算为逻辑选区。</summary>
        private void ApplyPresetRegion(Windows.Graphics.RectInt32 r)
        {
            // 屏幕像素 → 虚拟桌面截图局部坐标 → XAML 客户区逻辑坐标。
            _sel = new Rect(
                (r.X - _shot.OriginX) / PixelScaleX,
                (r.Y - _shot.OriginY) / PixelScaleY,
                r.Width / PixelScaleX,
                r.Height / PixelScaleY);
            ApplySelection();
            SetHintVisibility(Visibility.Collapsed);
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

        /// <summary>覆盖完整虚拟桌面（物理像素坐标，原点可为负）。</summary>
        private void ApplyCaptureMonitorBounds()
        {
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                _shot.OriginX, _shot.OriginY, _shot.Width, _shot.Height));
        }

        private string WindowBounds
        {
            get
            {
                var p = AppWindow.Position;
                var s = AppWindow.Size;
                return $"({p.X},{p.Y},{s.Width},{s.Height})";
            }
        }

        private async Task LoadShotImageAsync()
        {
            try
            {
                // GDI 的 BGRX 缓冲区已在捕获阶段把 X 通道设为不透明；
                // Image 用 Stretch=Fill 显示在整个虚拟桌面客户区。
                var wb = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(
                    _shot.Width, _shot.Height);
                using (var stream = wb.PixelBuffer.AsStream())
                {
                    await stream.WriteAsync(_shot.Bgra, 0, _shot.Bgra.Length);
                }
                ShotImage.Source = wb;
                App.WriteLifecycleLog("截图选区背景位图已加载");
            }
            catch (Exception e)
            {
                LogCrash("LoadShotImageAsync", e);
                App.WriteLifecycleLog($"截图选区背景加载失败：{e.GetType().Name}: {e.Message}");
                ErrorBar.Message = $"截图背景加载失败：{e.Message}";
                ErrorBar.IsOpen = true;
            }
        }

        /// <summary>在首次显示窗口前完成冻结画面加载，避免先闪出黑色空层。</summary>
        internal Task PrepareForDisplayAsync() => LoadShotImageAsync();

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

        // 用截图像素尺寸 / 当前 XAML 客户区尺寸建立映射，避免不同 DPI
        // 或无边框窗口客户区取整误差造成副屏选区偏移、裁剪错位。
        private double PixelScaleX => Root.ActualWidth > 0
            ? _shot.Width / Root.ActualWidth
            : Root.XamlRoot?.RasterizationScale ?? 1.0;

        private double PixelScaleY => Root.ActualHeight > 0
            ? _shot.Height / Root.ActualHeight
            : Root.XamlRoot?.RasterizationScale ?? 1.0;

        private string HintMessage => _freeformSelection
            ? "自由圈选屏幕区域 · Enter 确认 · Esc 取消"
            : "拖拽选择识别区域 · Enter 确认 · Esc 取消";

        private void CreateMonitorHints()
        {
            HintCanvas.Children.Clear();
            _hintBars.Clear();

            foreach (var monitor in _shot.MonitorBounds)
            {
                var monitorWidth = monitor.Width / PixelScaleX;
                var monitorLeft = (monitor.X - _shot.OriginX) / PixelScaleX;
                var monitorTop = (monitor.Y - _shot.OriginY) / PixelScaleY;
                var availableWidth = Math.Max(1, monitorWidth - 32);
                var barWidth = Math.Min(440, availableWidth);
                var text = new TextBlock
                {
                    Text = HintMessage,
                    FontSize = 13,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(255, 242, 243, 247)),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                };
                var bar = new Border
                {
                    Width = barWidth,
                    Padding = new Thickness(16, 7, 16, 7),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(204, 31, 31, 38)),
                    CornerRadius = new CornerRadius(16),
                    Child = text,
                    IsHitTestVisible = false,
                };
                HintCanvas.Children.Add(bar);
                bar.Measure(new Size(barWidth, Root.ActualHeight));
                Canvas.SetLeft(bar, monitorLeft + monitorWidth / 2 - barWidth / 2);
                Canvas.SetTop(bar, monitorTop + 24);
                _hintBars.Add(bar);
            }
        }

        private void SetHintVisibility(Visibility visibility)
        {
            foreach (var bar in _hintBars)
                bar.Visibility = visibility;
        }

        private Rect GetSelectionBoundsOnScreen()
        {
            var left = _shot.OriginX + _sel.X * PixelScaleX;
            var top = _shot.OriginY + _sel.Y * PixelScaleY;
            return new Rect(left, top, _sel.Width * PixelScaleX,
                _sel.Height * PixelScaleY);
        }

        /// <summary>选出与当前选区重叠面积最大的显示器。</summary>
        private Windows.Graphics.RectInt32 GetSelectionMonitor()
        {
            var selection = GetSelectionBoundsOnScreen();
            var best = _shot.MonitorBounds[0];
            double bestArea = -1;
            foreach (var monitor in _shot.MonitorBounds)
            {
                var width = Math.Max(0, Math.Min(selection.Right,
                    monitor.X + monitor.Width) - Math.Max(selection.Left,
                    monitor.X));
                var height = Math.Max(0, Math.Min(selection.Bottom,
                    monitor.Y + monitor.Height) - Math.Max(selection.Top,
                    monitor.Y));
                var area = width * height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = monitor;
                }
            }
            return best;
        }

        private void PlaceSelectionControls()
        {
            var monitor = GetSelectionMonitor();
            var monitorLeft = (monitor.X - _shot.OriginX) / PixelScaleX;
            var monitorTop = (monitor.Y - _shot.OriginY) / PixelScaleY;
            var monitorRight = monitorLeft + monitor.Width / PixelScaleX;
            var monitorBottom = monitorTop + monitor.Height / PixelScaleY;
            var selected = GetSelectionBoundsOnScreen();
            var selectedLeft = Math.Max(monitor.X, selected.Left);
            var selectedTop = Math.Max(monitor.Y, selected.Top);
            var selectedRight = Math.Min(monitor.X + monitor.Width,
                selected.Right);
            var selectedBottom = Math.Min(monitor.Y + monitor.Height,
                selected.Bottom);
            var localLeft = (selectedLeft - _shot.OriginX) / PixelScaleX;
            var localTop = (selectedTop - _shot.OriginY) / PixelScaleY;
            var localRight = (selectedRight - _shot.OriginX) / PixelScaleX;
            var localBottom = (selectedBottom - _shot.OriginY) / PixelScaleY;

            // 尺寸标签贴近选区，但保持完整落在选区所属的显示器内。
            const double tagWidth = 120;
            const double tagHeight = 30;
            var tagX = localRight + 8;
            if (tagX + tagWidth > monitorRight - 8)
                tagX = localLeft - tagWidth - 8;
            tagX = Math.Clamp(tagX, monitorLeft + 8,
                Math.Max(monitorLeft + 8, monitorRight - tagWidth - 8));
            var tagY = localTop - tagHeight - 6;
            if (tagY < monitorTop + 8) tagY = localTop + 6;
            tagY = Math.Clamp(tagY, monitorTop + 8,
                Math.Max(monitorTop + 8, monitorBottom - tagHeight - 8));
            SizeTag.Margin = new Thickness(tagX, tagY, 0, 0);

            Toolbar.Visibility = Visibility.Visible;
            Toolbar.Width = Math.Min(440, Math.Max(1,
                monitorRight - monitorLeft - 24));
            Toolbar.Measure(new Size(Toolbar.Width, Root.ActualHeight));
            var toolbarWidth = Math.Max(Toolbar.Width,
                Toolbar.DesiredSize.Width);
            var toolbarHeight = Math.Max(Toolbar.ActualHeight,
                Toolbar.DesiredSize.Height);
            var rightSpace = monitorRight - localRight;
            var leftSpace = localLeft - monitorLeft;
            double toolbarX;
            if (rightSpace >= toolbarWidth + 16)
                toolbarX = localRight + 16;
            else if (leftSpace >= toolbarWidth + 16)
                toolbarX = localLeft - toolbarWidth - 16;
            else
                toolbarX = rightSpace >= leftSpace
                    ? localRight + 12
                    : localLeft - toolbarWidth - 12;

            toolbarX = Math.Clamp(toolbarX, monitorLeft + 8,
                Math.Max(monitorLeft + 8, monitorRight - toolbarWidth - 8));
            var selectionCenterY = (localTop + localBottom) / 2;
            var toolbarY = Math.Clamp(selectionCenterY - toolbarHeight / 2,
                monitorTop + 8,
                Math.Max(monitorTop + 8, monitorBottom - toolbarHeight - 8));
            Toolbar.Margin = new Thickness(toolbarX, toolbarY, 0, 0);
        }

        private void PlaceSizeTag()
        {
            var monitor = GetSelectionMonitor();
            var monitorLeft = (monitor.X - _shot.OriginX) / PixelScaleX;
            var monitorTop = (monitor.Y - _shot.OriginY) / PixelScaleY;
            var monitorRight = monitorLeft + monitor.Width / PixelScaleX;
            var monitorBottom = monitorTop + monitor.Height / PixelScaleY;
            var selected = GetSelectionBoundsOnScreen();
            var localLeft = (Math.Max(monitor.X, selected.Left)
                - _shot.OriginX) / PixelScaleX;
            var localTop = (Math.Max(monitor.Y, selected.Top)
                - _shot.OriginY) / PixelScaleY;
            var localRight = (Math.Min(monitor.X + monitor.Width,
                selected.Right) - _shot.OriginX) / PixelScaleX;
            var localBottom = (Math.Min(monitor.Y + monitor.Height,
                selected.Bottom) - _shot.OriginY) / PixelScaleY;

            SizeTag.Measure(new Size(monitorRight - monitorLeft,
                monitorBottom - monitorTop));
            var tagWidth = SizeTag.DesiredSize.Width;
            var tagHeight = SizeTag.DesiredSize.Height;
            var x = localRight + 8;
            if (x + tagWidth > monitorRight - 8)
                x = localLeft - tagWidth - 8;
            x = Math.Clamp(x, monitorLeft + 8,
                Math.Max(monitorLeft + 8, monitorRight - tagWidth - 8));

            var y = localTop - tagHeight - 6;
            if (y < monitorTop + 8) y = localTop + 6;
            y = Math.Clamp(y, monitorTop + 8,
                Math.Max(monitorTop + 8, monitorBottom - tagHeight - 8));
            SizeTag.Margin = new Thickness(x, y, 0, 0);
        }

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
            DimMask.Visibility = vis;
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

            _screenGeometry.Rect = new Rect(0, 0, Root.ActualWidth,
                Root.ActualHeight);
            _selectionGeometry.Rect = new Rect(_sel.X, _sel.Y,
                _sel.Width, _sel.Height);

            // 尺寸显示物理像素
            SizeText.Text = string.Format("{0:F0} × {1:F0}",
                _sel.Width * PixelScaleX, _sel.Height * PixelScaleY);
            PlaceSizeTag();
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
            PlaceSelectionControls();
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
            SetHintVisibility(Visibility.Collapsed);

            // 客户区逻辑坐标 → 虚拟桌面截图像素坐标。
            var scaleX = PixelScaleX;
            var scaleY = PixelScaleY;
            var px = (int)(_sel.X * scaleX);
            var py = (int)(_sel.Y * scaleY);
            var pw = (int)Math.Ceiling(_sel.Width * scaleX);
            var ph = (int)Math.Ceiling(_sel.Height * scaleY);

            BusyOverlay.Visibility = Visibility.Visible;
            BusyText.Text = "正在识别…";

            try
            {
                var maskPolygon = _freeformSelection
                    ? _freeformPoints.Select(p => new Point(
                        p.X * scaleX - px, p.Y * scaleY - py)).ToArray()
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

                JsonObject resp;

                // 截图遮罩先于后台初始化出现。连接/加载配置放在用户确认后，
                // 这样后台未运行时仍能选择区域，并在识别前得到明确的错误提示。
                var startupError = await BackendBootstrapper.EnsureRunningAsync();
                if (startupError is not null)
                    throw new BackendException("agent_unavailable", startupError);
                var settingsError = await SettingsService.LoadBackendAsync(_vm);
                if (settingsError is not null)
                    throw new BackendException("agent_unavailable", settingsError);

                resp = await BackendClient.Instance.CallAsync(
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
                SetHintVisibility(Visibility.Visible);
            }
            catch (Exception ex)
            {
                _confirmed = false;
                BusyOverlay.Visibility = Visibility.Collapsed;
                ErrorBar.Message = $"识别失败：{ex.Message}";
                ErrorBar.Severity = InfoBarSeverity.Error;
                ErrorBar.IsOpen = true;
                SetHintVisibility(Visibility.Visible);
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
