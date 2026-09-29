using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ScreenLens.WinUI.ViewModels;
using ScreenLens.WinUI.Views.Preview;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ScreenLens.WinUI
{
    public sealed partial class MainWindow : Window
    {
        private readonly DemoSettings _vm = DemoSettings.Instance;
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        private Type? _lastPageType;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        /// <summary>
        /// AppWindow.Resize 使用物理像素；这里按当前显示器 DPI 换算，
        /// 保证 100% / 150% 等缩放下窗口的**逻辑**尺寸一致（默认 1180x760 DIP）。
        /// </summary>
        private void ResizeToLogicalSize(int logicalWidth, int logicalHeight)
        {
            double scale = 1.0;
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) scale = dpi / 96.0;
            }
            catch
            {
                // 取不到 DPI 时退回 1:1
            }

            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Round(logicalWidth * scale),
                (int)Math.Round(logicalHeight * scale)));
        }

        public MainWindow()
        {
            InitializeComponent();

            // 默认窗口尺寸（后续仍可自由调整）
            ResizeToLogicalSize(1180, 760);

            // 窗口 / 任务栏图标：未打包运行不会自动读取 Assets，需显式指定多尺寸 ICO
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ScreenLens.ico");
                if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            }
            catch
            {
                // 图标缺失不应阻断启动
            }

            // 默认深色主题，并跟随主题模式即时预览
            ApplyTheme(_vm.ThemeMode);
            _vm.ThemeModeChanged += ApplyTheme;

            // 强调色变化即时写入主题字典（品牌区 / 主按钮 / 选区边框）
            _vm.AccentChanged += App.ApplyAccent;

            // 统一的“尚未接入功能”轻提示
            _vm.ToastRequested += ShowToast;
            _toastTimer.Tick += (_, _) => { ToastBar.IsOpen = false; _toastTimer.Stop(); };

            // 初始导航到通用页，并同步记录当前页面类型（供演示入口恢复选中态）
            _lastPageType = typeof(Views.Settings.GeneralPage);
            ContentFrame.Navigate(_lastPageType);
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
