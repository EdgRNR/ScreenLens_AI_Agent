using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ScreenLens.WinUI.ViewModels;
using System;

namespace ScreenLens.WinUI.Views.Preview
{
    /// <summary>
    /// 识别结果演示窗口：展示原文/译文分层、状态标签与操作按钮。
    /// 按钮只更新本地演示状态（含模拟翻译延迟），不访问剪贴板、不发送网络请求。
    /// </summary>
    public sealed partial class ResultDemoWindow : Window
    {
        private bool _translated;

        public ResultDemoWindow()
        {
            InitializeComponent();

            Title = "识别结果预览（演示） — ScreenLens";
            AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 620));
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            {
                p.IsResizable = true;
            }

            // 跟随主窗口当前主题，保证文字对比可读
            Root.RequestedTheme = App.ResolveTheme(DemoSettings.Instance.ThemeMode);
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            Status.Severity = InfoBarSeverity.Informational;
            Status.Title = "复制";
            Status.Message = "演示：原文已标记复制，未写入剪贴板。";
        }

        private void OnTranslateClick(object sender, RoutedEventArgs e)
        {
            if (_translated) return; // 已翻译，防重复触发

            TranslateButton.IsEnabled = false;
            NotTranslatedText.Visibility = Visibility.Collapsed;
            TranslatedText.Visibility = Visibility.Collapsed;
            TranslatingRow.Visibility = Visibility.Visible;

            Status.Severity = InfoBarSeverity.Informational;
            Status.Title = "翻译中";
            Status.Message = "模拟延迟 1.5 秒（不发送网络请求）";

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                TranslatingRow.Visibility = Visibility.Collapsed;
                TranslatedText.Visibility = Visibility.Visible;
                TranslateButton.IsEnabled = true;
                _translated = true;

                Status.Severity = InfoBarSeverity.Success;
                Status.Title = "翻译完成";
                Status.Message = "示例译文（固定样例，非真实翻译）";
            };
            timer.Start();
        }

        private void OnRecaptureClick(object sender, RoutedEventArgs e)
        {
            var capture = new CaptureDemoWindow();
            capture.Activate();
            Close();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            Close();
            args.Handled = true;
        }
    }
}
