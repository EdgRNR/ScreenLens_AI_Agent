using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.Foundation;

namespace ScreenLens.WinUI.Views.Preview
{
    /// <summary>
    /// 截图选区演示窗口：
    /// 在 XAML 绘制的模拟桌面上演示遮罩、矩形选区、尺寸标签与浮动工具条。
    /// 拖拽可绘制新选区；Enter/确认进入结果预览，Esc/取消关闭。不注册全局热键。
    /// </summary>
    public sealed partial class CaptureDemoWindow : Window
    {
        private bool _dragging;
        private Point _start;
        private Rect _sel;
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

            Root.SizeChanged += (_, _) => ApplySelection();
            Activate();

            // 初始预置选区，便于直接看到遮罩 / 尺寸 / 工具条效果
            _sel = new Rect(180, 190, 460, 220);
            ApplySelection();
            ShowToolbar();
        }

        // ---------- 指针拖拽 ----------

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(Root);
            _dragging = true;
            _start = p.Position;
            Toolbar.Visibility = Visibility.Collapsed;
            HitLayer.CapturePointer(e.Pointer);
            _sel = new Rect(_start.X, _start.Y, 0, 0);
            ApplySelection();
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetCurrentPoint(Root);
            var x = Math.Clamp(p.Position.X, 0, Root.ActualWidth);
            var y = Math.Clamp(p.Position.Y, 0, Root.ActualHeight);
            _sel = new Rect(
                Math.Min(_start.X, x), Math.Min(_start.Y, y),
                Math.Abs(x - _start.X), Math.Abs(y - _start.Y));
            ApplySelection();
            e.Handled = true;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            HitLayer.ReleasePointerCapture(e.Pointer);

            if (_sel.Width < MinSize || _sel.Height < MinSize)
            {
                // 视为单击：恢复上一个选区
                if (_sel.IsEmpty || _sel.Width == 0)
                {
                    _sel = new Rect(180, 190, 460, 220);
                }
            }
            _sel.Intersect(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            ApplySelection();
            ShowToolbar();
            e.Handled = true;
        }

        // ---------- 渲染 ----------

        private void ApplySelection()
        {
            var w = Root.ActualWidth;
            var h = Root.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // 遮罩挖洞：上下左右四块
            Canvas.SetLeft(MaskTop, 0); Canvas.SetTop(MaskTop, 0);
            MaskTop.Width = w; MaskTop.Height = Math.Max(0, _sel.Top);

            Canvas.SetLeft(MaskBottom, 0); Canvas.SetTop(MaskBottom, _sel.Bottom);
            MaskBottom.Width = w; MaskBottom.Height = Math.Max(0, h - _sel.Bottom);

            Canvas.SetLeft(MaskLeft, 0); Canvas.SetTop(MaskLeft, _sel.Top);
            MaskLeft.Width = Math.Max(0, _sel.Left); MaskLeft.Height = _sel.Height;

            Canvas.SetLeft(MaskRight, _sel.Right); Canvas.SetTop(MaskRight, _sel.Top);
            MaskRight.Width = Math.Max(0, w - _sel.Right); MaskRight.Height = _sel.Height;

            // 选区边框
            Canvas.SetLeft(SelBorder, _sel.Left); Canvas.SetTop(SelBorder, _sel.Top);
            SelBorder.Width = _sel.Width; SelBorder.Height = _sel.Height;

            // 尺寸标签（选区上方，空间不足放下方）
            var badgeTop = _sel.Top >= 30 ? _sel.Top - 28 : _sel.Bottom + 4;
            SizeText.Text = $"{(int)_sel.Width} × {(int)_sel.Height}";
            Canvas.SetLeft(SizeBadge, _sel.Left); Canvas.SetTop(SizeBadge, badgeTop);
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
            if (ModeFree.IsChecked == true)
            {
                ModeHint.Message = "自由圈选为演示状态：拖拽仍以矩形选区示意，实际形状后续接入。";
                ModeHint.IsOpen = true;
            }
            else
            {
                ModeHint.IsOpen = false;
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

        private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                OpenResult();
                e.Handled = true;
            }
            // 方向键微调选区位置（演示键盘可达性）
            var delta = 8;
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left:
                    _sel.X -= delta; ApplySelection(); ShowToolbar(); e.Handled = true; break;
                case Windows.System.VirtualKey.Right:
                    _sel.X += delta; ApplySelection(); ShowToolbar(); e.Handled = true; break;
                case Windows.System.VirtualKey.Up:
                    _sel.Y -= delta; ApplySelection(); ShowToolbar(); e.Handled = true; break;
                case Windows.System.VirtualKey.Down:
                    _sel.Y += delta; ApplySelection(); ShowToolbar(); e.Handled = true; break;
            }
        }

        private void OpenResult()
        {
            var result = new ResultDemoWindow();
            result.Activate();
            Close();
        }
    }
}
