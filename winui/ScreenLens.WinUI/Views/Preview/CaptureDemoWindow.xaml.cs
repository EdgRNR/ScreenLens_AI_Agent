using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ScreenLens.WinUI.ViewModels;
using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace ScreenLens.WinUI.Views.Preview
{
    /// <summary>
    /// 截图选区演示窗口：
    /// 在 XAML 绘制的模拟桌面上演示遮罩、矩形/自由选区、尺寸标签与浮动工具条。
    /// - 拖拽绘制选区（矩形：拖对角；自由圈选：拖拽路径，均限制在客户区内）
    /// - 「选区后显示确认工具条」开关：开启时松开显示工具条；关闭时松开直接进入结果预览
    /// - Enter 确认、Esc 取消、方向键微调（仅当选区有效且边界内）
    /// 不注册全局热键，不截取真实屏幕。
    /// </summary>
    public sealed partial class CaptureDemoWindow : Window
    {
        private readonly DemoSettings _vm = DemoSettings.Instance;

        private bool _dragging;
        private Point _start;
        private Rect _sel;             // 当前选区（自由模式下为路径包围盒）
        private Rect _lastValidSel;   // 上一个有效选区（极小拖动时恢复）
        private readonly List<Point> _freePoints = new();
        private const double MinSize = 24;

        public CaptureDemoWindow()
        {
            InitializeComponent();

            Title = "截图选区演示 — 模拟桌面（ScreenLens）";
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 740));
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            {
                p.IsResizable = true;
                p.IsMaximizable = false;
            }

            // 跟随主窗口当前主题
            Root.RequestedTheme = App.ResolveTheme(_vm.ThemeMode);

            Root.SizeChanged += OnRootSizeChanged;
            Root.Loaded += (_, _) => HitLayer.Focus(FocusState.Programmatic);
            Activate();

            // 初始预置选区，便于直接看到遮罩 / 尺寸 / 工具条效果
            _sel = new Rect(180, 190, 460, 220);
            _lastValidSel = _sel;
            ApplySelection();
            if (_vm.ConfirmOnRelease) ShowToolbar();
        }

        // ---------- 窗口尺寸变化：夹紧选区并重新定位 ----------

        private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ClampSelection();
            ApplySelection();
            if (Toolbar.Visibility == Visibility.Visible) ShowToolbar();
        }

        private void ClampSelection()
        {
            var w = Root.ActualWidth;
            var h = Root.ActualHeight;
            if (_sel.Width > w) _sel.Width = w;
            if (_sel.Height > h) _sel.Height = h;
            _sel.X = Math.Clamp(_sel.X, 0, Math.Max(0, w - _sel.Width));
            _sel.Y = Math.Clamp(_sel.Y, 0, Math.Max(0, h - _sel.Height));
        }

        // ---------- 指针交互（仅左键可启动选区） ----------

        private bool IsFreeMode => ModeFree?.IsChecked == true;

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(Root);
            if (!p.Properties.IsLeftButtonPressed) return; // 忽略右键 / 中键

            _dragging = true;
            _start = p.Position;
            Toolbar.Visibility = Visibility.Collapsed;
            HitLayer.CapturePointer(e.Pointer);

            if (IsFreeMode)
            {
                _freePoints.Clear();
                _freePoints.Add(p.Position);
                FreePath.Visibility = Visibility.Visible;
                SelBorder.Visibility = Visibility.Collapsed;
                _sel = new Rect(p.Position.X, p.Position.Y, 0, 0);
            }
            else
            {
                _freePoints.Clear();
                FreePath.Visibility = Visibility.Collapsed;
                SelBorder.Visibility = Visibility.Visible;
                _sel = new Rect(_start.X, _start.Y, 0, 0);
            }
            ApplySelection();
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetCurrentPoint(Root);
            var x = Math.Clamp(p.Position.X, 0, Root.ActualWidth);
            var y = Math.Clamp(p.Position.Y, 0, Root.ActualHeight);

            if (IsFreeMode)
            {
                _freePoints.Add(new Point(x, y));
                RedrawFreePath();
                _sel = BoundingBox(_freePoints);
            }
            else
            {
                _sel = new Rect(
                    Math.Min(_start.X, x), Math.Min(_start.Y, y),
                    Math.Abs(x - _start.X), Math.Abs(y - _start.Y));
            }
            ApplySelection();
            e.Handled = true;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            HitLayer.ReleasePointerCapture(e.Pointer);

            var tooSmall = _sel.Width < MinSize || _sel.Height < MinSize;
            if (tooSmall)
            {
                // 恢复上一个有效选区（清除零碎选区）
                if (IsFreeMode)
                {
                    _freePoints.Clear();
                    FreePath.Visibility = Visibility.Collapsed;
                    SelBorder.Visibility = Visibility.Visible;
                }
                _sel = _lastValidSel;
                ApplySelection();
                ShowToolbar();
                e.Handled = true;
                return;
            }

            _lastValidSel = _sel;

            if (!_vm.ConfirmOnRelease)
            {
                // 「选区后显示确认工具条」关闭：松开直接进入识别（演示）
                OpenResult();
                e.Handled = true;
                return;
            }

            ShowToolbar();
            e.Handled = true;
        }

        // ---------- 渲染 ----------

        private static Rect BoundingBox(List<Point> pts)
        {
            if (pts.Count == 0) return new Rect(0, 0, 0, 0);
            double l = pts[0].X, t = pts[0].Y, r = pts[0].X, b = pts[0].Y;
            foreach (var p in pts)
            {
                l = Math.Min(l, p.X); t = Math.Min(t, p.Y);
                r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
            }
            return new Rect(l, t, r - l, b - t);
        }

        private void RedrawFreePath()
        {
            if (_freePoints.Count == 0) { FreePath.Visibility = Visibility.Collapsed; return; }
            FreePath.Visibility = Visibility.Visible;
            var pf = new PointCollection();
            foreach (var p in _freePoints) pf.Add(p);
            FreePath.Points = pf;
        }

        private void ApplySelection()
        {
            var w = Root.ActualWidth;
            var h = Root.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // 遮罩挖洞：上下左右四块（自由模式按路径包围盒）
            Canvas.SetLeft(MaskTop, 0); Canvas.SetTop(MaskTop, 0);
            MaskTop.Width = w; MaskTop.Height = Math.Max(0, _sel.Top);

            Canvas.SetLeft(MaskBottom, 0); Canvas.SetTop(MaskBottom, _sel.Bottom);
            MaskBottom.Width = w; MaskBottom.Height = Math.Max(0, h - _sel.Bottom);

            Canvas.SetLeft(MaskLeft, 0); Canvas.SetTop(MaskLeft, _sel.Top);
            MaskLeft.Width = Math.Max(0, _sel.Left); MaskLeft.Height = _sel.Height;

            Canvas.SetLeft(MaskRight, _sel.Right); Canvas.SetTop(MaskRight, _sel.Top);
            MaskRight.Width = Math.Max(0, w - _sel.Right); MaskRight.Height = _sel.Height;

            // 矩形选区边框
            Canvas.SetLeft(SelBorder, _sel.Left); Canvas.SetTop(SelBorder, _sel.Top);
            SelBorder.Width = _sel.Width; SelBorder.Height = _sel.Height;

            // 尺寸标签（选区上方，空间不足放下方）
            var badgeTop = _sel.Top >= 30 ? _sel.Top - 28 : _sel.Bottom + 4;
            SizeText.Text = $"{(int)_sel.Width} × {(int)_sel.Height}";
            Canvas.SetLeft(SizeBadge, Math.Max(0, _sel.Left)); Canvas.SetTop(SizeBadge, badgeTop);
        }

        private void ShowToolbar()
        {
            if (Toolbar.Visibility != Visibility.Visible) Toolbar.Visibility = Visibility.Visible;
            var toolbarW = Toolbar.ActualWidth > 0 ? Toolbar.ActualWidth : 430;
            var toolbarH = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight : 44;

            var left = Math.Clamp(_sel.Left, 0, Math.Max(0, Root.ActualWidth - toolbarW));
            var top = _sel.Bottom + 10 + toolbarH <= Root.ActualHeight
                ? _sel.Bottom + 10
                : Math.Max(0, _sel.Top - toolbarH - 10);

            Canvas.SetLeft(Toolbar, left);
            Canvas.SetTop(Toolbar, top);
        }

        // ---------- 工具条交互 ----------

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (ModeFree == null || ModeRect == null) return;

            if (ModeFree.IsChecked == true)
            {
                ModeHint.Message = "自由圈选（演示）：按住左键拖拽绘制自由路径，确认时按路径包围盒识别。";
                ModeHint.IsOpen = true;
                // 清除矩形选区视觉，等待新路径
                _freePoints.Clear();
                FreePath.Visibility = Visibility.Visible;
                SelBorder.Visibility = Visibility.Collapsed;
                _sel = new Rect(0, 0, 0, 0);
                ApplySelection();
                Toolbar.Visibility = Visibility.Collapsed;
            }
            else
            {
                ModeHint.IsOpen = false;
                _freePoints.Clear();
                FreePath.Visibility = Visibility.Collapsed;
                SelBorder.Visibility = Visibility.Visible;
                _sel = _lastValidSel;
                ApplySelection();
                ShowToolbar();
            }
        }

        private void OnMagnifierClick(object sender, RoutedEventArgs e)
        {
            ModeHint.Message = "放大镜为演示按钮，尚未接入功能。";
            ModeHint.IsOpen = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

        private void OnConfirmClick(object sender, RoutedEventArgs e) => OpenResult();

        private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            Close();
            args.Handled = true;
        }

        // 键盘确认 / 微调：仅在焦点位于捕获层（而非工具条按钮）时处理，
        // 不影响工具条上的 Tab / 方向键导航。
        private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.OriginalSource is not Grid g || g != HitLayer) return;

            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (_sel.Width >= MinSize && _sel.Height >= MinSize) OpenResult();
                e.Handled = true;
                return;
            }

            var delta = 8;
            var w = Root.ActualWidth;
            var h = Root.ActualHeight;
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left:
                    _sel.X = Math.Max(0, _sel.X - delta); break;
                case Windows.System.VirtualKey.Right:
                    _sel.X = Math.Min(w - _sel.Width, _sel.X + delta); break;
                case Windows.System.VirtualKey.Up:
                    _sel.Y = Math.Max(0, _sel.Y - delta); break;
                case Windows.System.VirtualKey.Down:
                    _sel.Y = Math.Min(h - _sel.Height, _sel.Y + delta); break;
                default: return;
            }
            ApplySelection();
            if (Toolbar.Visibility == Visibility.Visible) ShowToolbar();
            e.Handled = true;
        }

        private void OpenResult()
        {
            var result = new ResultDemoWindow();
            result.Activate();
            Close();
        }
    }
}
