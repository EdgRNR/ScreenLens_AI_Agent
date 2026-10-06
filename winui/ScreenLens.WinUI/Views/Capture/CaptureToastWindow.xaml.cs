using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;

namespace ScreenLens.WinUI.Views.Capture
{
    /// <summary>截图复制/保存完成后的短暂桌面提示，不依附于已关闭的截图覆盖层。</summary>
    public sealed partial class CaptureToastWindow : Window
    {
        private readonly string _message;
        private bool _closing;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint point);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        public CaptureToastWindow(string message)
        {
            _message = message;
            InitializeComponent();
            MessageText.Text = message;
            RootGrid.RequestedTheme = App.ResolveTheme(ViewModels.DemoSettings.Instance.ThemeMode);
            ConfigureWindow();
            Closed += (_, _) => _closing = true;
        }

        private void ConfigureWindow()
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // Presenter flags alone can leave a native frame around the client
            // area. Use the same borderless popup setup as the capture overlay
            // so the toast has exactly one visible card edge.
            ScreenLens.WinUI.Services.CaptureWindowPresentation.ConfigureBorderless(hwnd);
            // ConfigureBorderless disables the native frame's rounding for
            // capture overlays. The toast needs the compositor's antialiased
            // rounded clipping instead of a GDI HRGN, whose pixel edges are
            // visibly jagged on a dark desktop.
            var roundedCorners = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(hwnd, 33, ref roundedCorners, sizeof(int));
            var dpi = GetDpiForWindow(hwnd);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;
            // The success messages are short; keep a compact card instead of
            // leaving a large empty area to the right of the text.
            var width = (int)Math.Ceiling(240 * scale);
            var height = (int)Math.Ceiling(60 * scale);
            var cursor = GetCursorPos(out var point)
                ? new PointInt32(point.X, point.Y)
                : new PointInt32(0, 0);
            var area = DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Nearest);
            if (area is not null)
            {
                var x = area.WorkArea.X + (area.WorkArea.Width - width) / 2;
                var y = area.WorkArea.Y + 24;
                AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            }
            else
            {
                AppWindow.Resize(new SizeInt32(width, height));
            }

        }

        internal void ShowAfterCaptureWindow()
        {
            _ = ShowAfterDelayAsync();
        }

        private async Task ShowAfterDelayAsync()
        {
            await Task.Delay(120);
            if (_closing) return;
            Activate();
            // Show the completed-action notice immediately. The delay above
            // only lets the capture window finish closing; it is not an
            // animation delay.
            ToastCard.Opacity = 1;
            await Task.Delay(2400);
            if (_closing) return;
            Close();
        }
    }
}
