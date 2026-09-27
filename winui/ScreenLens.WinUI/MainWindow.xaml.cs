using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ScreenLens.WinUI.ViewModels;
using ScreenLens.WinUI.Views.Preview;
using System;

namespace ScreenLens.WinUI
{
    public sealed partial class MainWindow : Window
    {
        private readonly DemoSettings _vm = DemoSettings.Instance;
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        private Type? _lastPageType;

        public MainWindow()
        {
            InitializeComponent();

            // 默认深色主题，并跟随主题模式即时预览
            ApplyTheme(_vm.ThemeMode);
            _vm.ThemeModeChanged += ApplyTheme;

            // 统一的“尚未接入功能”轻提示
            _vm.ToastRequested += ShowToast;
            _toastTimer.Tick += (_, _) => { ToastBar.IsOpen = false; _toastTimer.Stop(); };

            ContentFrame.Navigate(typeof(Views.Settings.GeneralPage));
        }

        private void ApplyTheme(int mode) =>
            RootGrid.RequestedTheme = mode switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

        private void ShowToast(string message)
        {
            ToastBar.Message = message;
            ToastBar.IsOpen = true;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;

            if (tag == "CaptureDemo")
            {
                // 打开截图选区演示窗口（不改变导航选中态）
                new CaptureDemoWindow().Activate();
                if (args.SelectedItem is NavigationViewItem footer)
                {
                    footer.IsSelected = false;
                    // 恢复上一个页面选中态
                    SelectPage(_lastPageType);
                }
                return;
            }

            var pageType = tag switch
            {
                "General" => typeof(Views.Settings.GeneralPage),
                "Capture" => typeof(Views.Settings.CapturePage),
                "Ocr" => typeof(Views.Settings.OcrPage),
                "Translate" => typeof(Views.Settings.TranslatePage),
                "Hotkeys" => typeof(Views.Settings.HotkeysPage),
                "Appearance" => typeof(Views.Settings.AppearancePage),
                "About" => typeof(Views.Settings.AboutPage),
                _ => null,
            };

            if (pageType == null || pageType == _lastPageType) return;
            _lastPageType = pageType;
            ContentFrame.Navigate(pageType);
        }

        private void SelectPage(Type? pageType)
        {
            if (pageType == null) return;
            var tag = pageType.Name switch
            {
                nameof(Views.Settings.GeneralPage) => "General",
                nameof(Views.Settings.CapturePage) => "Capture",
                nameof(Views.Settings.OcrPage) => "Ocr",
                nameof(Views.Settings.TranslatePage) => "Translate",
                nameof(Views.Settings.HotkeysPage) => "Hotkeys",
                nameof(Views.Settings.AppearancePage) => "Appearance",
                nameof(Views.Settings.AboutPage) => "About",
                _ => "General",
            };

            foreach (var item in Nav.MenuItems)
            {
                if (item is NavigationViewItem nav && nav.Tag as string == tag)
                {
                    nav.IsSelected = true;
                    return;
                }
            }
        }
    }
}
