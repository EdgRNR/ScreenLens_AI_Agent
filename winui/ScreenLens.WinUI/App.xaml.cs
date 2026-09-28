using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ScreenLens.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        }

        private static void WriteLog(string source, Exception ex)
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "screenlens_winui_crash.log");
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {source}\n{ex}\n\n");
            }
            catch { }
        }

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
            => WriteLog("UnhandledException", e.Exception);

        private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex) WriteLog("AppDomain", ex);
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request for the process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Activate();
        }

        // ---------- 主题与强调色助手 ----------

        /// <summary>把演示主题模式解析为元素主题，供独立演示窗口跟随主窗口外观。</summary>
        public static ElementTheme ResolveTheme(int mode) => mode switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        private static readonly (string Accent, string Hover, string Pressed)[] AccentPalette =
        {
            ("#3D9BFF", "#5CA8FF", "#2B7FE0"), // ScreenLens 蓝
            ("#7C6FF0", "#9284F5", "#6355DB"), // 黛紫
            ("#4CC38A", "#66D0A0", "#3AA173"), // 青碧
            ("#E5602F", "#EE7A4E", "#C74E23"), // 熔橙
        };

        /// <summary>
        /// 把选中的强调色写入 Light/Dark 主题字典。引用 {ThemeResource} 的
        /// 品牌区、选区边框、主按钮等元素会即时刷新。
        /// </summary>
        public static void ApplyAccent(int index)
        {
            if (index < 0 || index >= AccentPalette.Length) return;
            var (accent, hover, pressed) = AccentPalette[index];

            var resources = Current.Resources;
            foreach (var key in new[] { "Light", "Dark" })
            {
                if (resources.ThemeDictionaries[key] is not ResourceDictionary dict) continue;
                dict["ScreenLensAccentBrush"] = MakeBrush(accent);
                dict["ScreenLensAccentHoverBrush"] = MakeBrush(hover);
                dict["ScreenLensAccentPressedBrush"] = MakeBrush(pressed);
            }
        }

        private static SolidColorBrush MakeBrush(string hex)
        {
            var c = Windows.UI.Color.FromArgb(
                0xFF,
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16));
            return new SolidColorBrush(c);
        }
    }
}
