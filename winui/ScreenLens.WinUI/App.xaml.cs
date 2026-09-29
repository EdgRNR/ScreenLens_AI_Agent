using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using ScreenLens.WinUI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
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
        private MainWindow? _settingsWindow;
        private Views.Capture.SelectionWindow? _captureWindow;
        private Views.Result.ResultWindow? _resultWindow;
        private readonly HashSet<Window> _openWindows = new();
        private Services.SingleInstanceCoordinator? _singleInstance;
        private bool _captureStarting;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            if (!SingleInstanceCoordinator.TryBecomePrimary(
                    DispatcherQueue.GetForCurrentThread(),
                    HandleActivation, out _singleInstance))
            {
                var forwarded = SingleInstanceCoordinator
                    .ForwardToPrimaryAsync(GetLaunchArguments())
                    .GetAwaiter().GetResult();
                if (!forwarded)
                    WriteLog("SingleInstance", new InvalidOperationException(
                        "无法把启动请求转发给已有 ScreenLens 窗口。"));
                Environment.Exit(0);
            }
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
        /// 启动模式：
        /// - --capture：由后台代理热键唤起的截图流程（选区 → 识别 → 结果）；
        /// - 默认：设置窗口（托盘「设置」唤起）。
        /// 所有窗口关闭后进程退出，不隐藏驻留。
        /// </summary>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            HandleActivation(GetLaunchArguments());
        }

        private static string[] GetLaunchArguments()
            => Environment.GetCommandLineArgs().Skip(1).ToArray();

        private void HandleActivation(string[] args)
        {
            var capture = args.Any(a => a.Equals("--capture",
                    StringComparison.OrdinalIgnoreCase)
                || a.Equals("/capture", StringComparison.OrdinalIgnoreCase)
                || a.Equals("capture", StringComparison.OrdinalIgnoreCase));
            if (capture)
            {
                if (_captureStarting || _captureWindow is not null
                    || _resultWindow is not null)
                {
                    (_captureWindow as Window ?? _resultWindow)?.Activate();
                    return;
                }
                _ = StartCaptureFlowAsync(args);
                return;
            }

            if (_settingsWindow is null)
            {
                _settingsWindow = new MainWindow();
                _window = _settingsWindow;
                RegisterWindow(_settingsWindow);
                _settingsWindow.Activate();
                _ = LoadSettingsAsync();
            }
            else
            {
                _settingsWindow.Activate();
            }
        }

        private async Task StartCaptureFlowAsync(string[] args)
        {
            _captureStarting = true;
            try
            {
                // F5 / 热键 / 托盘均自动确保后台可用；用户无需手动先启动 Python。
                var backendError = await LoadSettingsAsync();
                if (backendError is not null)
                    ViewModels.DemoSettings.Instance.ShowToast(backendError);

                // 从设置窗口热键唤起时先隐藏设置窗口，避免截图把自己拍进去。
                var restoreSettings = _settingsWindow is not null;
                _settingsWindow?.AppWindow.Hide();
                VirtualScreenShot? shot;
                try
                {
                    if (restoreSettings) await Task.Delay(120);
                    shot = await ScreenCapture.CaptureAsync();
                }
                finally
                {
                    if (restoreSettings && _settingsWindow is not null)
                        _settingsWindow.AppWindow.Show();
                }
                if (shot is null)
                {
                    if (_openWindows.Count == 0) Exit();
                    return;
                }

                // 命令行可选预置选区：--region=x,y,w,h 和 --auto。
                Windows.Graphics.RectInt32? preset = null;
                var auto = false;
                foreach (var arg in args)
                {
                    if (arg.StartsWith("--region=", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = arg["--region=".Length..].Split(',');
                        if (parts.Length == 4
                            && int.TryParse(parts[0], out var rx)
                            && int.TryParse(parts[1], out var ry)
                            && int.TryParse(parts[2], out var rw)
                            && int.TryParse(parts[3], out var rh)
                            && rw > 0 && rh > 0)
                        {
                            preset = new Windows.Graphics.RectInt32(rx, ry, rw, rh);
                        }
                    }
                    else if (arg.Equals("--auto", StringComparison.OrdinalIgnoreCase))
                    {
                        auto = true;
                    }
                }

                _captureWindow = new Views.Capture.SelectionWindow(shot, preset, auto);
                RegisterWindow(_captureWindow);
                _captureWindow.Activate();
            }
            catch (Exception ex)
            {
                WriteLog("CaptureFlow", ex);
                ViewModels.DemoSettings.Instance.ShowToast($"截图流程启动失败：{ex.Message}");
                if (_openWindows.Count == 0) Exit();
            }
            finally
            {
                _captureStarting = false;
            }
        }

        private async Task<string?> LoadSettingsAsync()
        {
            var vm = ViewModels.DemoSettings.Instance;
            vm.SuppressPersist = true;
            try
            {
                await Services.SettingsService.LoadFrontendPreferencesAsync(vm);
                ApplyAccent(vm.AccentIndex);

                var startupError = await BackendBootstrapper.EnsureRunningAsync();
                if (startupError is not null)
                {
                    vm.BackendOnline = false;
                    vm.BackendStatus = startupError;
                    vm.ShowToast(startupError);
                    return startupError;
                }

                var err = await Services.SettingsService.LoadBackendAsync(vm);
                if (err is null)
                {
                    vm.BackendOnline = true;
                    vm.BackendStatus = "后台代理已连接";
                    return null;
                }
                vm.BackendOnline = false;
                vm.BackendStatus = err;
                vm.ShowToast($"设置未连接后台：{err}");
                return err;
            }
            finally
            {
                vm.SuppressPersist = false;
            }
        }

        internal static void RegisterWindow(Window window)
        {
            if (Current is App app) app.TrackWindow(window);
        }

        internal static void RegisterCaptureWindow(Views.Capture.SelectionWindow window)
        {
            if (Current is App app)
            {
                app._captureWindow = window;
                app.TrackWindow(window);
            }
        }

        internal static void RegisterResultWindow(Views.Result.ResultWindow window)
        {
            if (Current is App app)
            {
                app._resultWindow = window;
                app.TrackWindow(window);
            }
        }

        private void TrackWindow(Window window)
        {
            if (!_openWindows.Add(window)) return;
            window.Closed += (_, _) =>
            {
                _openWindows.Remove(window);
                if (ReferenceEquals(window, _settingsWindow)) _settingsWindow = null;
                if (ReferenceEquals(window, _captureWindow)) _captureWindow = null;
                if (ReferenceEquals(window, _resultWindow)) _resultWindow = null;
                if (_openWindows.Count == 0)
                {
                    _singleInstance?.Dispose();
                    _singleInstance = null;
                    Exit();
                }
            };
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
