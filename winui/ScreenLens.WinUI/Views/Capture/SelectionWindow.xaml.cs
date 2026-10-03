using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using ScreenLens.WinUI.Services;
using ScreenLens.WinUI.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
        private Point _lastPointerPosition;
        private Rect _sel;
        private readonly List<Point> _freeformPoints = new();
        private readonly List<Border> _hintBars = new();
        private Windows.Graphics.RectInt32? _selectionMonitor;
        private bool _freeformSelection;
        private Windows.Graphics.RectInt32? _toolbarMonitor;
        private bool _toolbarDragging;
        private bool _toolbarManuallyPositioned;
        private Point _toolbarDragPointerStart;
        private Point _toolbarDragPositionStart;
        private bool _committingDimensions;
        private const double MinSize = 8;

        private bool _confirmed;
        private DispatcherQueueTimer? _topmostRetryTimer;
        private int _topmostRetryCount;

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const int GwHwndPrev = 3;
        private const int GwlExStyle = -20;
        private const long WsExTopmost = 0x00000008L;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd,
            IntPtr hwndInsertAfter, int x, int y, int width, int height,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text,
            int maxCount);

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
            Closed += (_, _) =>
            {
                _topmostRetryTimer?.Stop();
                _topmostRetryTimer = null;
            };
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
                    EnsureCaptureWindowForeground("Loaded");
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
            _start = new Point(_sel.X, _sel.Y);
            _lastPointerPosition = _start;
            _selectionMonitor = GetMonitorAtRootPoint(new Point(
                _sel.X + _sel.Width / 2, _sel.Y + _sel.Height / 2));
            _toolbarMonitor = _selectionMonitor;
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

        internal void EnsureCaptureWindowForeground(string reason)
        {
            try
            {
                Activate();
                EnsureCaptureWindowTopmost(reason, requestForeground: true);
                StartTopmostRetryTimer();
            }
            catch (Exception ex)
            {
                App.WriteLifecycleLog(
                    $"截图窗口前置失败({reason})：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void EnsureCaptureWindowTopmost(string reason,
            bool requestForeground)
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                // HWND_TOPMOST is independent of foreground activation. Keep the
                // screenshot above ordinary apps without repeatedly stealing focus.
                var positioned = SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0,
                    0, SwpNoMove | SwpNoSize | SwpShowWindow | SwpNoActivate);
                var lastError = positioned ? 0 : Marshal.GetLastWin32Error();
                var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
                var hasTopmostStyle = (exStyle & WsExTopmost) != 0;
                var foreground = requestForeground
                    ? SetForegroundWindow(hwnd)
                    : false;
                if (requestForeground)
                    Root.Focus(FocusState.Programmatic);
                var isForeground = GetForegroundWindow() == hwnd;
                var above = GetWindow(hwnd, GwHwndPrev);
                var aboveTitle = new System.Text.StringBuilder(256);
                if (above != IntPtr.Zero)
                    GetWindowText(above, aboveTitle, aboveTitle.Capacity);
                App.WriteLifecycleLog(
                    $"截图层级检查({reason})：setTopmost={positioned}, error={lastError}, topmostStyle={hasTopmostStyle}, above='{aboveTitle}', setForeground={foreground}, isForeground={isForeground}");
            }
            catch (Exception ex)
            {
                App.WriteLifecycleLog(
                    $"截图层级检查失败({reason})：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void StartTopmostRetryTimer()
        {
            _topmostRetryTimer?.Stop();
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            if (dispatcher is null)
                return;

            _topmostRetryCount = 0;
            _topmostRetryTimer = dispatcher.CreateTimer();
            _topmostRetryTimer.Interval = TimeSpan.FromMilliseconds(100);
            _topmostRetryTimer.IsRepeating = true;
            _topmostRetryTimer.Tick += (_, _) =>
            {
                _topmostRetryCount++;
                EnsureCaptureWindowTopmost(
                    $"显示后重试{_topmostRetryCount}", requestForeground: false);
                if (_topmostRetryCount >= 6)
                {
                    _topmostRetryTimer?.Stop();
                    _topmostRetryTimer = null;
                }
            };
            _topmostRetryTimer.Start();
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

        private Windows.Graphics.RectInt32 GetMonitorAtRootPoint(Point point)
        {
            var screenX = _shot.OriginX + point.X * PixelScaleX;
            var screenY = _shot.OriginY + point.Y * PixelScaleY;
            foreach (var monitor in _shot.MonitorBounds)
            {
                if (screenX >= monitor.X
                    && screenX < monitor.X + monitor.Width
                    && screenY >= monitor.Y
                    && screenY < monitor.Y + monitor.Height)
                    return monitor;
            }

            // 防止显示器布局存在缝隙时没有命中，取中心点最近的显示器。
            return _shot.MonitorBounds
                .OrderBy(m => Math.Pow(screenX - (m.X + m.Width / 2.0), 2)
                    + Math.Pow(screenY - (m.Y + m.Height / 2.0), 2))
                .First();
        }

        private void PlaceSelectionControls()
        {
            if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
                return;

            var monitor = _toolbarMonitor ?? _selectionMonitor
                ?? GetMonitorAtRootPoint(_lastPointerPosition);
            _toolbarMonitor = monitor;
            var monitorLeft = (monitor.X - _shot.OriginX) / PixelScaleX;
            var monitorTop = (monitor.Y - _shot.OriginY) / PixelScaleY;
            var monitorRight = Math.Min(Root.ActualWidth,
                monitorLeft + monitor.Width / PixelScaleX);
            var monitorBottom = Math.Min(Root.ActualHeight,
                monitorTop + monitor.Height / PixelScaleY);
            monitorLeft = Math.Max(0, monitorLeft);
            monitorTop = Math.Max(0, monitorTop);
            const double inset = 8;
            const double gap = 8;

            Toolbar.Visibility = Visibility.Visible;
            var maxToolbarWidth = Math.Max(1,
                monitorRight - monitorLeft - inset * 2);
            // Margin stores the toolbar's absolute position inside Root. XAML's
            // Measure includes that margin in DesiredSize, so measuring again
            // without clearing it makes the reported size grow by the previous
            // position on every placement pass (and eventually sends the bar
            // to a distant edge). Preserve manual coordinates, measure only
            // the toolbar's content, then restore its position below.
            var currentToolbarX = Toolbar.Margin.Left;
            var currentToolbarY = Toolbar.Margin.Top;
            Toolbar.Margin = new Thickness(0);
            Toolbar.Width = double.NaN;
            Toolbar.MaxWidth = maxToolbarWidth;
            Toolbar.Measure(new Size(maxToolbarWidth, double.PositiveInfinity));
            var toolbarWidth = Math.Min(maxToolbarWidth,
                Toolbar.DesiredSize.Width);
            var toolbarHeight = Toolbar.DesiredSize.Height;

            if (_toolbarManuallyPositioned)
            {
                SetToolbarPosition(currentToolbarX, currentToolbarY,
                    monitorLeft, monitorTop, monitorRight, monitorBottom,
                    toolbarWidth, toolbarHeight, inset);
                return;
            }

            // Anchor to the actual release point (a rectangle corner or the
            // freeform path endpoint), not the selection's distant bounding
            // box center. This keeps the controls beside where drawing ended.
            var selectedLeft = Math.Max(_sel.Left, monitorLeft);
            var selectedTop = Math.Max(_sel.Top, monitorTop);
            var selectedRight = Math.Min(_sel.Right, monitorRight);
            var selectedBottom = Math.Min(_sel.Bottom, monitorBottom);
            if (selectedRight <= selectedLeft || selectedBottom <= selectedTop)
            {
                selectedLeft = Math.Clamp(_lastPointerPosition.X, monitorLeft, monitorRight);
                selectedRight = selectedLeft;
                selectedTop = Math.Clamp(_lastPointerPosition.Y, monitorTop, monitorBottom);
                selectedBottom = selectedTop;
            }

            // The toolbar is anchored to the selection bounds, not the pointer
            // release coordinate. Use an explicit preference order so it does
            // not jump to a distant side based on heuristic distance scores.
            var safeLeft = Math.Min(monitorRight - inset, monitorLeft + inset);
            var safeTop = Math.Min(monitorBottom - inset, monitorTop + inset);
            var safeRight = Math.Max(safeLeft, monitorRight - inset);
            var safeBottom = Math.Max(safeTop, monitorBottom - inset);
            var maxX = Math.Max(safeLeft, safeRight - toolbarWidth);
            var maxY = Math.Max(safeTop, safeBottom - toolbarHeight);
            var centeredX = Math.Clamp(
                (selectedLeft + selectedRight - toolbarWidth) / 2,
                safeLeft, maxX);
            var centeredY = Math.Clamp(
                (selectedTop + selectedBottom - toolbarHeight) / 2,
                safeTop, maxY);
            double bestX;
            double bestY;
            string chosenSide;
            // The order is intentional and deterministic: below, above, right,
            // then left. Each side is accepted only when the complete toolbar
            // fits in the current monitor while remaining flush to the box.
            var belowY = selectedBottom + gap;
            var aboveY = selectedTop - toolbarHeight - gap;
            var rightX = selectedRight + gap;
            var leftX = selectedLeft - toolbarWidth - gap;
            if (belowY <= maxY)
            {
                chosenSide = "below";
                bestX = centeredX;
                bestY = belowY;
            }
            else if (aboveY >= safeTop)
            {
                chosenSide = "above";
                bestX = centeredX;
                bestY = aboveY;
            }
            else if (rightX <= maxX)
            {
                chosenSide = "right";
                bestX = rightX;
                bestY = centeredY;
            }
            else if (leftX >= safeLeft)
            {
                chosenSide = "left";
                bestX = leftX;
                bestY = centeredY;
            }
            else
            {
                // If the selected area consumes the monitor, no side can fit
                // without overlap. Keep the toolbar visible and choose the
                // fallback with the smallest overlap, in the same priority order.
                var candidates = new[]
                {
                    (Side: "below", X: centeredX, Y: belowY),
                    (Side: "above", X: centeredX, Y: aboveY),
                    (Side: "right", X: rightX, Y: centeredY),
                    (Side: "left", X: leftX, Y: centeredY),
                };
                var fallback = candidates
                    .Select((candidate, index) =>
                    {
                        var x = Math.Clamp(candidate.X, safeLeft, maxX);
                        var y = Math.Clamp(candidate.Y, safeTop, maxY);
                        var overlapX = Math.Max(0, Math.Min(selectedRight, x + toolbarWidth) - Math.Max(selectedLeft, x));
                        var overlapY = Math.Max(0, Math.Min(selectedBottom, y + toolbarHeight) - Math.Max(selectedTop, y));
                        return (candidate.Side, X: x, Y: y,
                            Score: overlapX * overlapY * 10000 + index);
                    })
                    .OrderBy(candidate => candidate.Score)
                    .First();
                chosenSide = fallback.Side;
                bestX = fallback.X;
                bestY = fallback.Y;
            }

            SetToolbarPosition(bestX, bestY, monitorLeft, monitorTop,
                monitorRight, monitorBottom, toolbarWidth, toolbarHeight, inset);
            App.WriteLifecycleLog(
                $"截图工具条定位：side={chosenSide}, placed=({bestX:F1},{bestY:F1}), toolbar=({toolbarWidth:F1}x{toolbarHeight:F1}), safe=({safeLeft:F1},{safeTop:F1},{safeRight:F1},{safeBottom:F1}), selection=({_sel.X:F1},{_sel.Y:F1},{_sel.Width:F1},{_sel.Height:F1}), monitor=({monitor.X},{monitor.Y},{monitor.Width},{monitor.Height})");
        }

        private void SetToolbarPosition(double x, double y,
            double monitorLeft, double monitorTop, double monitorRight,
            double monitorBottom, double toolbarWidth, double toolbarHeight,
            double inset = 8)
        {
            var minX = Math.Min(monitorRight - inset, monitorLeft + inset);
            var minY = Math.Min(monitorBottom - inset, monitorTop + inset);
            var maxX = Math.Max(minX, monitorRight - inset - toolbarWidth);
            var maxY = Math.Max(minY, monitorBottom - inset - toolbarHeight);
            Toolbar.Margin = new Thickness(Math.Clamp(x, minX, maxX),
                Math.Clamp(y, minY, maxY), 0, 0);
        }

        // ------------------------------------------------------- 指针交互

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (BusyOverlay.Visibility == Visibility.Visible) return;
            if (IsWithinToolbar(e.OriginalSource as DependencyObject)) return;
            var p = e.GetCurrentPoint(Root);
            if (!p.Properties.IsLeftButtonPressed) return;

            _dragging = true;
            _toolbarManuallyPositioned = false;
            _start = p.Position;
            _lastPointerPosition = _start;
            _selectionMonitor = GetMonitorAtRootPoint(_start);
            _toolbarMonitor = _selectionMonitor;
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
            _lastPointerPosition = p.Position;
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
            var end = e.GetCurrentPoint(Root).Position;
            _lastPointerPosition = end;
            _dragging = false;
            try { Root.ReleasePointerCapture(e.Pointer); }
            catch { /* 忽略 */ }

            if (_freeformSelection && _freeformPoints.Count > 0)
            {
                if (_freeformPoints[^1] != end) _freeformPoints.Add(end);
                if (_freeformPoints.Count >= 3
                    && _freeformPoints[^1] != _freeformPoints[0])
                    _freeformPoints.Add(_freeformPoints[0]);
                UpdateFreeformBounds();
            }
            else if (!_freeformSelection)
            {
                _sel = new Rect(
                    Math.Min(_start.X, end.X),
                    Math.Min(_start.Y, end.Y),
                    Math.Abs(end.X - _start.X),
                    Math.Abs(end.Y - _start.Y));
                ApplySelection();
            }

            // Keep the whole toolbar on the display where drawing ended.
            _toolbarMonitor = GetMonitorAtRootPoint(end);

            if (_sel.Width < MinSize || _sel.Height < MinSize
                || (_freeformSelection && _freeformPoints.Count < 4))
            {
                // 视为误触：清除选区
                _sel = new Rect(0, 0, 0, 0);
                _selectionMonitor = null;
                _toolbarMonitor = null;
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
                    CommitDimensionEdits();
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

        private void SyncDimensionInputs()
        {
            if (_sel.Width < MinSize || _sel.Height < MinSize)
                return;
            SelectionWidthBox.Text = Math.Max(1,
                (int)Math.Round(_sel.Width * PixelScaleX)).ToString();
            SelectionHeightBox.Text = Math.Max(1,
                (int)Math.Round(_sel.Height * PixelScaleY)).ToString();
            CenterDimensionText(SelectionWidthBox);
            CenterDimensionText(SelectionHeightBox);
        }

        private void OnDimensionTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
                CenterDimensionText(textBox);
        }

        private static void CenterDimensionText(TextBox textBox)
        {
            var controlWidth = textBox.ActualWidth > 0
                ? textBox.ActualWidth
                : textBox.Width;
            var controlHeight = textBox.ActualHeight > 0
                ? textBox.ActualHeight
                : textBox.Height;
            if (controlWidth <= 0 || double.IsNaN(controlWidth)
                || controlHeight <= 0 || double.IsNaN(controlHeight))
                return;

            var measuredText = new TextBlock
            {
                Text = textBox.Text ?? string.Empty,
                FontFamily = textBox.FontFamily,
                FontSize = textBox.FontSize,
                FontWeight = textBox.FontWeight,
                FontStyle = textBox.FontStyle,
                FontStretch = textBox.FontStretch,
                TextWrapping = TextWrapping.NoWrap,
            };
            measuredText.Measure(new Size(double.PositiveInfinity,
                double.PositiveInfinity));

            var textAreaWidth = Math.Max(0, controlWidth
                - textBox.BorderThickness.Left - textBox.BorderThickness.Right);
            var leftPadding = Math.Max(0,
                (textAreaWidth - measuredText.DesiredSize.Width) / 2);
            var textAreaHeight = Math.Max(0, controlHeight
                - textBox.BorderThickness.Top - textBox.BorderThickness.Bottom);
            var topPadding = Math.Max(0,
                (textAreaHeight - measuredText.DesiredSize.Height) / 2);
            // The edit view stays left/top aligned. Inset the measured text by
            // half of the spare width and height to center it on both axes.
            textBox.Padding = new Thickness(leftPadding, topPadding, 0, 0);
        }

        private void OnDimensionLostFocus(object sender, RoutedEventArgs e)
            => CommitDimensionEdits();

        private void OnDimensionKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
                return;
            e.Handled = true;
            CommitDimensionEdits();
        }

        private void CommitDimensionEdits()
        {
            if (_committingDimensions || Toolbar.Visibility != Visibility.Visible
                || _sel.Width < MinSize || _sel.Height < MinSize)
                return;

            _committingDimensions = true;
            try
            {
                if (!int.TryParse(SelectionWidthBox.Text?.Trim(), out var widthPx)
                    || !int.TryParse(SelectionHeightBox.Text?.Trim(), out var heightPx))
                {
                    SyncDimensionInputs();
                    return;
                }

                var scaleX = PixelScaleX;
                var scaleY = PixelScaleY;
                var availableWidth = Math.Max(1,
                    (int)Math.Floor((Root.ActualWidth - _sel.X) * scaleX));
                var availableHeight = Math.Max(1,
                    (int)Math.Floor((Root.ActualHeight - _sel.Y) * scaleY));
                var minimumWidth = Math.Min(availableWidth,
                    Math.Max(1, (int)Math.Ceiling(MinSize * scaleX)));
                var minimumHeight = Math.Min(availableHeight,
                    Math.Max(1, (int)Math.Ceiling(MinSize * scaleY)));
                widthPx = Math.Clamp(widthPx, minimumWidth, availableWidth);
                heightPx = Math.Clamp(heightPx, minimumHeight, availableHeight);

                var old = _sel;
                var newWidth = widthPx / scaleX;
                var newHeight = heightPx / scaleY;
                if (_freeformSelection && old.Width > 0 && old.Height > 0)
                {
                    var xScale = newWidth / old.Width;
                    var yScale = newHeight / old.Height;
                    for (var i = 0; i < _freeformPoints.Count; i++)
                    {
                        var point = _freeformPoints[i];
                        _freeformPoints[i] = new Point(
                            old.X + (point.X - old.X) * xScale,
                            old.Y + (point.Y - old.Y) * yScale);
                    }
                    UpdateFreeformBounds();
                }
                else
                {
                    _sel = new Rect(old.X, old.Y, newWidth, newHeight);
                    ApplySelection();
                }

                SyncDimensionInputs();
                if (Toolbar.Visibility == Visibility.Visible)
                {
                    // Do not move a toolbar under the pointer while a button
                    // press is transitioning from TextBox focus to its Click.
                    DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
                    {
                        if (Toolbar.Visibility == Visibility.Visible)
                            PlaceSelectionControls();
                    });
                }
            }
            finally
            {
                _committingDimensions = false;
            }
        }

        private bool IsWithinToolbar(DependencyObject? source)
        {
            while (source is not null)
            {
                if (ReferenceEquals(source, Toolbar))
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void OnToolbarDragPressed(object sender, PointerRoutedEventArgs e)
        {
            if (Toolbar.Visibility != Visibility.Visible
                || sender is not UIElement handle)
                return;
            _toolbarDragging = true;
            _toolbarDragPointerStart = e.GetCurrentPoint(Root).Position;
            _toolbarDragPositionStart = new Point(
                Toolbar.Margin.Left, Toolbar.Margin.Top);
            handle.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnToolbarDragMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_toolbarDragging)
                return;
            var monitor = _toolbarMonitor ?? GetMonitorAtRootPoint(
                _lastPointerPosition);
            var monitorLeft = Math.Max(0,
                (monitor.X - _shot.OriginX) / PixelScaleX);
            var monitorTop = Math.Max(0,
                (monitor.Y - _shot.OriginY) / PixelScaleY);
            var monitorRight = Math.Min(Root.ActualWidth,
                (monitor.X - _shot.OriginX + monitor.Width) / PixelScaleX);
            var monitorBottom = Math.Min(Root.ActualHeight,
                (monitor.Y - _shot.OriginY + monitor.Height) / PixelScaleY);
            var width = Toolbar.ActualWidth > 0
                ? Toolbar.ActualWidth : Toolbar.DesiredSize.Width;
            var height = Toolbar.ActualHeight > 0
                ? Toolbar.ActualHeight : Toolbar.DesiredSize.Height;
            var pointer = e.GetCurrentPoint(Root).Position;
            SetToolbarPosition(
                _toolbarDragPositionStart.X + pointer.X - _toolbarDragPointerStart.X,
                _toolbarDragPositionStart.Y + pointer.Y - _toolbarDragPointerStart.Y,
                monitorLeft, monitorTop, monitorRight, monitorBottom,
                width, height);
            _toolbarManuallyPositioned = true;
            e.Handled = true;
        }

        private void OnToolbarDragReleased(object sender, PointerRoutedEventArgs e)
        {
            _toolbarDragging = false;
            if (sender is UIElement handle)
            {
                try { handle.ReleasePointerCapture(e.Pointer); }
                catch { /* Pointer may already have been released by the system. */ }
            }
            e.Handled = true;
        }

        private void OnToolbarDragCanceled(object sender, PointerRoutedEventArgs e)
        {
            _toolbarDragging = false;
            e.Handled = true;
        }

        private void ShowToolbar()
        {
            _toolbarManuallyPositioned = false;
            SyncDimensionInputs();
            PlaceSelectionControls();
        }

        private void HideToolbar()
        {
            Toolbar.Visibility = Visibility.Collapsed;
        }

        // -------------------------------------------------------- 确认/取消

        private void OnConfirmClick(object sender, RoutedEventArgs e)
        {
            CommitDimensionEdits();
            _ = ConfirmSelectionAsync();
        }

        private void OnReselectClick(object sender, RoutedEventArgs e)
        {
            HideToolbar();
            _sel = new Rect(0, 0, 0, 0);
            _selectionMonitor = null;
            _toolbarMonitor = null;
            _toolbarManuallyPositioned = false;
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
