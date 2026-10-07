using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ScreenLens.WinUI.Services;
using ScreenLens.WinUI.ViewModels;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly string _captureAction;
        private readonly int _screenX;
        private readonly int _screenY;
        private readonly int _regionW;
        private readonly int _regionH;
        private readonly Views.Capture.SelectionWindow? _owner;
        private bool _translating;
        private CancellationTokenSource? _translationCancellation;
        private Task? _translationTask;
        private readonly DispatcherTimer _translationRefresh = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly StringBuilder _translationText = new();
        private bool _translationDirty;
        private bool _closeRequested;
        private bool _allowClose;
        private bool _closed;
        private bool _recapturing;
        private bool _hasTranslation;
        private bool _showTranslation;
        private bool _comparisonRequested = true;
        private InputNonClientPointerSource? _captionPointerSource;
        private RectInt32? _captionRect;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(PointInt32 point, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType,
            out uint dpiX, out uint dpiY);

        public ResultWindow(JsonObject ocrResp, int screenX, int screenY,
            int regionW, int regionH,
            Views.Capture.SelectionWindow? owner, string captureAction = "capture")
        {
            _screenX = screenX;
            _screenY = screenY;
            _regionW = regionW;
            _regionH = regionH;
            _owner = owner;
            _captureAction = captureAction;

            _ocrText = ocrResp["text"]?.GetValue<string>() ?? "";
            var elapsed = ocrResp["elapsed_ms"]?.GetValue<int>() ?? 0;

            InitializeComponent();
            // Copy the saved default once. This window's selection is never
            // bound back to DemoSettings or persisted by SettingsService.
            TargetLanguageBox.SelectedIndex = Math.Clamp(_vm.TargetLanguage, 0,
                TargetLanguageBox.Items.Count - 1);
            ApplyTheme(_vm.ThemeMode);
            _vm.ThemeModeChanged += ApplyTheme;
            _vm.PropertyChanged += OnSettingsChanged;
            _translationRefresh.Tick += (_, _) => FlushTranslation();
            AppWindow.Closing += OnAppWindowClosing;
            Closed += (_, _) =>
            {
                _closed = true;
                _translationCancellation?.Cancel();
                _translationRefresh.Stop();
                _vm.PropertyChanged -= OnSettingsChanged;
                AppWindow.Closing -= OnAppWindowClosing;
                _vm.ThemeModeChanged -= ApplyTheme;
                AppWindow.Changed -= OnAppWindowChanged;
            };
            App.RegisterResultWindow(this);

            Title = "识别结果 — ScreenLens";
            ConfigureWindowChrome();

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
                TargetLanguageBox.IsEnabled = false;
            }

            if (_vm.CopyButtonLook == 1)
            {
                CopyBtn.Content = new FontIcon
                {
                    Glyph = "\uE8C8",
                    FontSize = 14,
                };
            }

            PositionWindow();
            UpdateReadingLayout();
            Root.Loaded += (_, _) =>
            {
                Root.Focus(FocusState.Programmatic);
            };
            if (_captureAction == "translate")
                Root.Loaded += OnAutomaticTranslationLoaded;
            HeaderDragArea.LayoutUpdated += (_, _) => UpdateDragRegion();
        }

        private void ConfigureWindowChrome()
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                // Keep the native resize border, but remove the separate
                // system caption, icon and caption buttons entirely.
                presenter.SetBorderAndTitleBar(true, false);
                presenter.IsResizable = true;
                presenter.IsMinimizable = true;
                presenter.IsMaximizable = true;
                presenter.PreferredMinimumWidth = 360;
                presenter.PreferredMinimumHeight = 300;
            }
            _captionPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            AppWindow.Changed += OnAppWindowChanged;
            UpdateMaximizeButton();
        }

        private void UpdateDragRegion()
        {
            if (_closed || _captionPointerSource is null || Root.XamlRoot is null)
                return;
            if (HeaderDragArea.ActualWidth <= 0 || HeaderDragArea.ActualHeight <= 0)
            {
                // A very narrow window can give the title no space. Do not
                // leave the previous larger drag rectangle over the buttons.
                if (_captionRect is not null)
                {
                    _captionPointerSource.ClearRegionRects(NonClientRegionKind.Caption);
                    _captionRect = null;
                }
                return;
            }

            // Non-client regions use client-relative physical pixels. Recheck
            // after layout so resizing and moving between DPI scales stay aligned.
            var scale = Root.XamlRoot.RasterizationScale;
            var origin = HeaderDragArea.TransformToVisual(Root)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var rect = new RectInt32(
                (int)Math.Round(origin.X * scale),
                (int)Math.Round(origin.Y * scale),
                (int)Math.Round(HeaderDragArea.ActualWidth * scale),
                (int)Math.Round(HeaderDragArea.ActualHeight * scale));
            if (_captionRect is { } previous && previous.X == rect.X && previous.Y == rect.Y
                && previous.Width == rect.Width && previous.Height == rect.Height)
                return;
            _captionPointerSource.SetRegionRects(NonClientRegionKind.Caption, new[] { rect });
            _captionRect = rect;
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
            => UpdateMaximizeButton();

        private void UpdateMaximizeButton()
        {
            var maximized = AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Maximized;
            MaximizeIcon.Glyph = maximized ? "\uE923" : "\uE922";
            var label = maximized ? "还原" : "最大化";
            ToolTipService.SetToolTip(MaximizeBtn, label);
            AutomationProperties.SetName(MaximizeBtn, label);
        }

        private void ApplyTheme(int mode)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => ApplyTheme(mode));
                return;
            }
            if (_closed) return;
            Root.RequestedTheme = App.ResolveTheme(mode);
        }

        private static string LinesOf(JsonObject resp)
        {
            var lines = resp["lines"]?.AsArray();
            return lines?.Count.ToString() ?? "0";
        }

        // ---------------------------------------------------------- 定位

        private void PositionWindow()
        {
            var remembered = _vm.ResultPosition == 2
                && SettingsService.TryGetLastResultPosition(out _, out _);
            var reference = new PointInt32(_screenX + _regionW / 2, _screenY + _regionH / 2);
            if (remembered && SettingsService.TryGetLastResultPosition(out var lastX, out var lastY))
                reference = new PointInt32(lastX, lastY);
            var area = DisplayArea.GetFromPoint(reference, DisplayAreaFallback.Nearest);
            var work = area?.WorkArea ?? new RectInt32(_screenX, _screenY, 1920, 1080);

            // Get the target monitor's DPI before positioning the HWND. The
            // newly created window may still belong to a different DPI monitor.
            var monitor = MonitorFromPoint(reference, 2); // MONITOR_DEFAULTTONEAREST
            if (GetDpiForMonitor(monitor, 0, out var dpi, out _) != 0 || dpi == 0)
                dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;
            var size = ResultWindowLayout.InitialSize(_ocrText, OcrText.FontSize,
                work.Width / scale, work.Height / scale);
            var w = (int)Math.Ceiling(size.Width * scale);
            var h = (int)Math.Ceiling(size.Height * scale);

            int x, y;
            if (remembered)
            {
                x = reference.X;
                y = reference.Y;
            }
            else if (_vm.ResultPosition == 1)
            {
                x = work.X + (work.Width - w) / 2;
                y = work.Y + (work.Height - h) / 2;
            }
            else
            {
                // 跟随截图位置：选区正下方
                x = _screenX;
                y = _screenY + _regionH + 12;
            }

            // Clamp to this monitor, including when the selection touches its
            // bottom edge. Do not choose a different monitor from the proposed Y.
            var bounds = ResultWindowLayout.FitToWorkArea(new ResultBounds(x, y, w, h),
                new ResultBounds(work.X, work.Y, work.Width, work.Height));
            AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            if (_vm.ResultPosition == 2)
                _ = SettingsService.SaveLastResultPositionAsync(bounds.X, bounds.Y);
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            {
                p.IsResizable = true;
                p.IsAlwaysOnTop = false;
            }
        }

        // ---------------------------------------------------------- 操作

        private void OnReadingAreaSizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateReadingLayout();

        private void OnOriginalTabClick(object sender, RoutedEventArgs e) => SelectReading(false);

        private void OnTranslationTabClick(object sender, RoutedEventArgs e) => SelectReading(true);

        private void OnOriginalTextFocus(object sender, RoutedEventArgs e) => SelectReading(false);

        private void OnTranslatedTextFocus(object sender, RoutedEventArgs e) => SelectReading(true);

        private void SelectReading(bool translation)
        {
            _showTranslation = translation && _hasTranslation;
            UpdateReadingLayout();
        }

        private void OnCompareClick(object sender, RoutedEventArgs e)
        {
            _comparisonRequested = CompareBtn.IsChecked == true;
            UpdateReadingLayout();
        }

        private void UpdateReadingLayout()
        {
            if (_closed || OriginalPanel is null) return;
            var wide = ResultWindowLayout.CanCompare(ReadingArea.ActualWidth);
            var compare = _hasTranslation && wide && _comparisonRequested;
            var originalVisible = compare || !_showTranslation;
            var translatedVisible = _hasTranslation && (compare || _showTranslation);

            OriginalTab.IsChecked = !_showTranslation;
            TranslationTab.IsChecked = _showTranslation;
            TranslationTab.IsEnabled = _hasTranslation;
            CompareBtn.IsEnabled = _hasTranslation && wide;
            CompareBtn.IsChecked = compare;
            ToolTipService.SetToolTip(CompareBtn, !_hasTranslation ? "翻译后可左右对照"
                : !wide ? "拉宽窗口或最大化后可左右对照" : "左右对照原文与译文");
            OriginalPanel.Visibility = originalVisible ? Visibility.Visible : Visibility.Collapsed;
            TranslatePanel.Visibility = translatedVisible ? Visibility.Visible : Visibility.Collapsed;
            OriginalColumn.Width = originalVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            TranslatedColumn.Width = translatedVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            OriginalLabel.Visibility = TranslationLabel.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
            OriginalPanel.Margin = new Thickness(0, 0, compare ? 6 : 0, 0);
            TranslatePanel.Margin = new Thickness(compare ? 6 : 0, 0, 0, 0);
            var copyLabel = _showTranslation ? "复制译文" : "复制原文";
            if (CopyBtn.Content is not FontIcon) CopyBtn.Content = copyLabel;
            ToolTipService.SetToolTip(CopyBtn, copyLabel);
            AutomationProperties.SetName(CopyBtn, copyLabel);
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Minimize();
        }

        private void OnMaximizeClick(object sender, RoutedEventArgs e)
        {
            if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
            if (presenter.State == OverlappedPresenterState.Maximized)
                presenter.Restore();
            else
                presenter.Maximize();
        }

        private async void OnCloseClick(object sender, RoutedEventArgs e) => await CloseResultAsync();

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DemoSettings.Provider) && !_translating && !_closed)
            {
                TranslateBtn.IsEnabled = _vm.Provider != 2;
                TranslateBtn.Content = _vm.Provider == 2 ? "翻译已关闭" : "翻译";
                TargetLanguageBox.IsEnabled = _vm.Provider != 2;
            }
        }

        private void OnAutomaticTranslationLoaded(object sender, RoutedEventArgs e)
        {
            Root.Loaded -= OnAutomaticTranslationLoaded;
            if (_vm.Provider == 2)
            {
                ShowStatus("翻译服务已关闭，请在设置中启用。", InfoBarSeverity.Warning);
                return;
            }
            OnTranslateClick(TranslateBtn, e);
        }

        private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose || !_translating) return;
            args.Cancel = true;
            _ = CloseResultAsync();
        }

        private async Task CancelTranslationAsync()
        {
            _translationCancellation?.Cancel();
            if (_translationTask is { } task) await task;
        }

        private async Task CloseResultAsync()
        {
            if (_closed || _closeRequested) return;
            _closeRequested = true;
            await CancelTranslationAsync();
            if (_closed) return;
            _allowClose = true;
            Close();
        }

        private void FlushTranslation()
        {
            if (_closed || !_translationDirty) return;
            _translationDirty = false;
            TranslatedText.Text = _translationText.ToString();
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            var text = _showTranslation ? TranslatedText.Text : _ocrText;
            var label = _showTranslation ? "译文" : "原文";
            if (string.IsNullOrEmpty(text))
            {
                ShowStatus($"没有可复制的{label}", InfoBarSeverity.Warning);
                return;
            }
            try
            {
                var pkg = new DataPackage
                {
                    RequestedOperation = DataPackageOperation.Copy,
                };
                pkg.SetText(text);
                Clipboard.SetContent(pkg);
                ShowStatus($"{label}已复制到剪贴板", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowStatus($"复制失败：{ex.Message}", InfoBarSeverity.Error);
            }
        }

        private async void OnTranslateClick(object sender, RoutedEventArgs e)
        {
            if (_closed || _recapturing || _closeRequested) return;
            if (_translating) { await CancelTranslationAsync(); return; }
            if (_vm.Provider == 2) return;
            if (string.IsNullOrEmpty(_ocrText))
            {
                ShowStatus("没有可翻译的文本", InfoBarSeverity.Warning);
                return;
            }

            _translationTask = TranslateAsync();
            await _translationTask;
            _translationTask = null;
        }

        private async Task TranslateAsync()
        {
            var targetLanguage = (TargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh";
            using var cancellation = new CancellationTokenSource();
            _translationCancellation = cancellation;
            _translating = true;
            TranslateBtn.Content = "停止翻译";
            TargetLanguageBox.IsEnabled = false;
            _hasTranslation = true;
            TranslatedText.Text = "";
            _translationText.Clear();
            SelectReading(true);
            _translationRefresh.Start();
            ShowStatus("正在翻译…", InfoBarSeverity.Informational);
            var completed = false;
            try
            {
                var resp = await BackendClient.Instance.CallAsync(
                    "TranslateText",
                    new JsonObject { ["text"] = _ocrText, ["stream"] = true,
                        ["target_language"] = targetLanguage,
                        ["request_token"] = Guid.NewGuid().ToString("N") },
                    timeoutMs: 85_000, ct: cancellation.Token, onDelta: text =>
                    {
                        if (_closed || cancellation.IsCancellationRequested) return;
                        if (_translationText.Length + text.Length > 1_000_000)
                            throw new BackendException("response_too_large", "译文超过大小限制");
                        _translationText.Append(text);
                        _translationDirty = true;
                    });
                if (_closed) return;
                var text = resp["text"]?.GetValue<string>() ?? "";
                TranslatedText.Text = text;
                completed = true;
                ShowStatus(null, InfoBarSeverity.Informational);
            }
            catch (OperationCanceledException)
            {
                if (!_closed && !_closeRequested && !_recapturing)
                    ShowStatus("翻译已停止，已保留收到的内容。", InfoBarSeverity.Informational);
            }
            catch (BackendException ex)
            {
                if (!_closed) ShowStatus(ex.Message, InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                if (!_closed) ShowStatus($"翻译失败：{ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                _translating = false;
                _translationCancellation = null;
                _translationRefresh.Stop();
                // On success the final response is authoritative (including whitespace).
                if (!completed) FlushTranslation();
                else _translationDirty = false;
                if (!_closed)
                {
                    TranslateBtn.IsEnabled = _vm.Provider != 2;
                    TranslateBtn.Content = _vm.Provider == 2 ? "翻译已关闭" : "重新翻译";
                    TargetLanguageBox.IsEnabled = _vm.Provider != 2;
                }
            }
        }

        private async void OnRecaptureClick(object sender, RoutedEventArgs e)
        {
            if (_closed || _recapturing) return;
            _recapturing = true;
            RecaptureBtn.IsEnabled = false;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Views.Capture.SelectionWindow? sel = null;
            try
            {
                await CancelTranslationAsync();
                if (_closed) return;
                CaptureWindowPresentation.HideBeforeCapture(hwnd);
                var shot = await ScreenCapture.CaptureAsync();
                if (shot is null)
                    throw new InvalidOperationException($"屏幕捕获失败：{ScreenCapture.LastError}");
                if (_closed)
                {
                    shot.ReleasePixels();
                    return;
                }
                sel = new Views.Capture.SelectionWindow(shot, captureAction: _captureAction);
                await sel.PrepareForDisplayAsync();
                if (_closed)
                {
                    sel.Close();
                    return;
                }
                await sel.ShowForCaptureAsync();
                Close(); // 本窗口关闭，进程由新选区窗口维持
            }
            catch (Exception ex)
            {
                sel?.Close();
                if (_closed) return;
                // A failed recapture must restore the result window as well
                // as native visibility; Show alone does not remove its cloak.
                try { CaptureWindowPresentation.Reveal(hwnd); }
                catch (Exception restoreError)
                {
                    App.WriteLifecycleLog($"重新截图恢复窗口失败：{restoreError.Message}");
                }
                AppWindow.Show();
                ShowStatus($"重新截图失败：{ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                _recapturing = false;
                if (!_closed) RecaptureBtn.IsEnabled = true;
            }
        }

        private async void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                await CloseResultAsync();
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
