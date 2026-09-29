using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ScreenLens.WinUI.Services;
using ScreenLens.WinUI.ViewModels;
using System;
using System.Text.Json.Nodes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace ScreenLens.WinUI.Views.Result
{
    /// <summary>
    /// 识别结果窗口：
    /// - 展示选区 OCR 文本与（可选）译文；
    /// - 复制用 Windows 剪贴板并给出成功 / 失败反馈；
    /// - 翻译仅发送识别文本（绝不发送截图），Provider 为关闭时禁用；
    /// - 关闭窗口即结束本次任务（进程退出，后台代理与托盘不受影响）。
    /// </summary>
    public sealed partial class ResultWindow : Window
    {
        private readonly DemoSettings _vm = DemoSettings.Instance;
        private readonly string _ocrText;
        private readonly int _screenX;
        private readonly int _screenY;
        private readonly int _regionW;
        private readonly int _regionH;
        private readonly Views.Capture.SelectionWindow? _owner;
        private bool _translating;

        public ResultWindow(JsonObject ocrResp, int screenX, int screenY,
            int regionW, int regionH,
            Views.Capture.SelectionWindow? owner)
        {
            _screenX = screenX;
            _screenY = screenY;
            _regionW = regionW;
            _regionH = regionH;
            _owner = owner;

            _ocrText = ocrResp["text"]?.GetValue<string>() ?? "";
            var elapsed = ocrResp["elapsed_ms"]?.GetValue<int>() ?? 0;

            InitializeComponent();
            Root.RequestedTheme = App.ResolveTheme(_vm.ThemeMode);
            App.RegisterResultWindow(this);

            Title = "识别结果 — ScreenLens";

            OcrText.Text = _ocrText;
            OcrText.FontSize = _vm.OriginalFontSize switch
            {
                0 => 13,
                2 => 17,
                _ => 15,
            };
            MetaText.Text = elapsed > 0 ? $"{elapsed} ms · {LinesOf(ocrResp)} 行" : "";

            // Provider == 2 表示关闭翻译
            if (_vm.Provider == 2)
            {
                TranslateBtn.IsEnabled = false;
                TranslateBtn.Content = "翻译已关闭";
            }

            if (_vm.CopyButtonLook == 1)
            {
                CopyBtn.Content = new FontIcon
                {
                    Glyph = "\uE8C8",
                    FontSize = 14,
                };
                CopyBtn.MinWidth = 40;
                ToolTipService.SetToolTip(CopyBtn, "复制识别文本");
            }

            PositionWindow();
            Root.Loaded += (_, _) => Root.Focus(FocusState.Programmatic);
        }

        private static string LinesOf(JsonObject resp)
        {
            var lines = resp["lines"]?.AsArray();
            return lines?.Count.ToString() ?? "0";
        }

        // ---------------------------------------------------------- 定位

        private void PositionWindow()
        {
            const int minW = 420;
            var w = Math.Clamp(Math.Max(_regionW, minW), minW, 900);
            var h = 360;

            int x, y;
            if (_vm.ResultPosition == 2
                && SettingsService.TryGetLastResultPosition(out var lastX, out var lastY))
            {
                x = lastX;
                y = lastY;
            }
            else if (_vm.ResultPosition == 1)
            {
                // 屏幕居中：以选区所在显示器为参照
                var area = Microsoft.UI.Windowing.DisplayArea
                    .GetFromPoint(new PointInt32(_screenX, _screenY),
                        Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
                if (area is not null)
                {
                    x = area.WorkArea.X
                        + (area.WorkArea.Width - w) / 2;
                    y = area.WorkArea.Y
                        + (area.WorkArea.Height - h) / 2;
                }
                else
                {
                    x = _screenX;
                    y = _screenY + _regionH + 12;
                }
            }
            else
            {
                // 跟随截图位置：选区正下方
                x = _screenX;
                y = _screenY + _regionH + 12;
            }

            // 限制在选区所在显示器工作区内
            var da = Microsoft.UI.Windowing.DisplayArea
                .GetFromPoint(new PointInt32(
                    Math.Max(x, _screenX), Math.Max(y, _screenY)),
                    Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            if (da is not null)
            {
                if (x + w > da.WorkArea.X + da.WorkArea.Width)
                {
                    x = da.WorkArea.X + da.WorkArea.Width - w;
                }
                if (y + h > da.WorkArea.Y + da.WorkArea.Height)
                {
                    y = da.WorkArea.Y + da.WorkArea.Height - h;
                }
                x = Math.Max(x, da.WorkArea.X);
                y = Math.Max(y, da.WorkArea.Y);
            }

            AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(
                x, y, w, h));
            if (_vm.ResultPosition == 2)
                _ = SettingsService.SaveLastResultPositionAsync(x, y);
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            {
                p.IsResizable = true;
                p.IsAlwaysOnTop = false;
            }
        }

        // ---------------------------------------------------------- 操作

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_ocrText))
            {
                ShowStatus("没有可复制的内容", InfoBarSeverity.Warning);
                return;
            }
            try
            {
                var pkg = new DataPackage
                {
                    RequestedOperation = DataPackageOperation.Copy,
                };
                pkg.SetText(_ocrText);
                Clipboard.SetContent(pkg);
                ShowStatus("已复制到剪贴板", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowStatus($"复制失败：{ex.Message}", InfoBarSeverity.Error);
            }
        }

        private async void OnTranslateClick(object sender, RoutedEventArgs e)
        {
            if (_translating || _vm.Provider == 2) return;
            if (string.IsNullOrEmpty(_ocrText))
            {
                ShowStatus("没有可翻译的文本", InfoBarSeverity.Warning);
                return;
            }

            _translating = true;
            TranslateBtn.IsEnabled = false;
            TranslateBtn.Content = "翻译中…";
            TranslatePanel.Visibility = Visibility.Visible;
            TranslatedText.Text = "";
            try
            {
                var resp = await BackendClient.Instance.CallAsync(
                    "TranslateText",
                    new JsonObject { ["text"] = _ocrText },
                    timeoutMs: 45_000);
                var text = resp["text"]?.GetValue<string>() ?? "";
                TranslatedText.Text = text;
                ShowStatus(null, InfoBarSeverity.Informational);
            }
            catch (BackendException ex)
            {
                ShowStatus(ex.Message, InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                ShowStatus($"翻译失败：{ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                _translating = false;
                TranslateBtn.IsEnabled = true;
                TranslateBtn.Content = "重新翻译";
            }
        }

        private async void OnRecaptureClick(object sender, RoutedEventArgs e)
        {
            // 先把结果窗口藏起来，避免它进入新一轮截图
            AppWindow.Hide();
            var shot = await ScreenCapture.CaptureAsync();
            if (shot is null)
            {
                AppWindow.Show();
                ShowStatus("屏幕捕获失败，请重试", InfoBarSeverity.Error);
                return;
            }
            var sel = new Views.Capture.SelectionWindow(shot);
            sel.Activate();
            Close(); // 本窗口关闭，进程由新选区窗口维持
        }

        private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                Close();
            }
        }

        private void ShowStatus(string? message, InfoBarSeverity severity)
        {
            if (message is null)
            {
                StatusInfo.IsOpen = false;
                return;
            }
            StatusInfo.Message = message;
            StatusInfo.Severity = severity;
            StatusInfo.IsOpen = true;
        }
    }
}
