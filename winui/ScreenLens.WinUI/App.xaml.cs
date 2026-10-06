using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
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
        private bool _captureStartCancelled;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // XamlControlsResources contains theme-specific aliases used by
            // control templates and visual states. Initialize them with the
            // saved application theme before any capture/result/settings UI.
            var themeMode = SettingsService.ReadStartupThemeMode();
            var vm = ViewModels.DemoSettings.Instance;
            vm.SuppressPersist = true;
            try { vm.ThemeMode = themeMode; }
            finally { vm.SuppressPersist = false; }
            if (themeMode is 1 or 2)
                RequestedTheme = themeMode == 1 ? ApplicationTheme.Light : ApplicationTheme.Dark;
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
                if (forwarded)
                {
                    WriteLifecycleLog("重复启动已转发给主实例，本进程正常退出");
                    Environment.Exit(0);
                }

                // 互斥体已存在就绝不能继续成为第二个 UI 实例。转发失败时
                // 记录并退出；保留临时实例会让连按热键启动多个独立截图窗口。
                WriteLifecycleLog("重复启动无法联系主实例，本次请求安全退出，不创建第二个 UI 实例");
                WriteLog("SingleInstance", new TimeoutException(
                    "主实例未在超时时间内确认激活请求；为避免重复 UI 实例，本次请求已退出。"));
                Environment.Exit(2);
                throw new InvalidOperationException("无法联系 ScreenLens WinUI 主实例。");
            }
        }

        private static void WriteLog(string source, Exception ex)
        {
            var entry = $"[{DateTime.Now:HH:mm:ss.fff}] {source}\n{ex}\n\n";
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "screenlens_winui_crash.log");
                File.AppendAllText(path, entry);
            }
            catch
            {
                try
                {
                    var fallback = Path.Combine(Path.GetTempPath(),
                        $"screenlens_winui_{Environment.ProcessId}.log");
                    File.AppendAllText(fallback, entry);
                }
                catch { }
            }
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

        internal static void WriteLifecycleLog(string message)
        {
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] " +
                $"pid={Environment.ProcessId} {message}{Environment.NewLine}";
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ScreenLens", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "frontend.log"), entry);
            }
            catch
            {
                try
                {
                    var fallback = Path.Combine(Path.GetTempPath(),
                        $"screenlens_winui_{Environment.ProcessId}.log");
                    File.AppendAllText(fallback, entry);
                }
                catch { }
            }
        }

        private static string[] GetLaunchArguments()
            => Environment.GetCommandLineArgs().Skip(1).ToArray();

        private void HandleActivation(string[] args)
        {
            if (args.Any(a => a.Equals("--cancel-capture", StringComparison.OrdinalIgnoreCase)))
            {
                if (_captureStarting) _captureStartCancelled = true;
                _captureWindow?.CancelFromShortcut();
                if (_openWindows.Count == 0 && !_captureStarting) ExitIfNoWindows();
                return;
            }
            var capture = args.Any(a => a.Equals("--capture",
                    StringComparison.OrdinalIgnoreCase)
                || a.Equals("/capture", StringComparison.OrdinalIgnoreCase)
                || a.Equals("capture", StringComparison.OrdinalIgnoreCase));
            if (capture)
            {
                WriteLifecycleLog("收到截图激活请求");
                if (_captureStarting || _captureWindow is not null
                    || _resultWindow is not null)
                {
                    WriteLifecycleLog("截图流程已在运行，激活现有窗口");
                    if (_captureWindow is not null)
                    {
                        _captureWindow.SetCaptureAction(CaptureActionFromArgs(args));
                        _captureWindow.EnsureCaptureWindowForeground(
                            "重复截图热键");
                    }
                    else
                        _resultWindow?.Activate();
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
            _captureStartCancelled = false;
            WriteLifecycleLog("截图流程开始");
            try
            {
                // 截图 UI 不依赖后台服务。先快速读取本地交互偏好并显示遮罩；
                // Agent/配置加载延后到用户确认选区后，避免 IPC 延迟或故障吞掉热键。
                var vm = ViewModels.DemoSettings.Instance;
                vm.SuppressPersist = true;
                try
                {
                    await Services.SettingsService.LoadFrontendPreferencesAsync(vm);
                }
                finally { vm.SuppressPersist = false; }

                // 从设置窗口热键唤起时先隐藏设置窗口，避免截图把自己拍进去。
                var restoreSettings = _settingsWindow is not null;
                _settingsWindow?.AppWindow.Hide();
                VirtualScreenShot? shot;
                if (restoreSettings) await Task.Delay(120);
                shot = await ScreenCapture.CaptureAsync();
                if (shot is null)
                {
                    var error = $"屏幕捕获失败：{ScreenCapture.LastError}";
                    WriteLifecycleLog(error);
                    ShowCaptureFailure(error);
                    return;
                }
                WriteLifecycleLog($"虚拟桌面截图完成：origin=({shot.OriginX},{shot.OriginY}), size={shot.Width}x{shot.Height}");
                if (_captureStartCancelled)
                {
                    shot.ReleasePixels();
                    RestoreSettingsAfterCapture();
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

                _captureWindow = new Views.Capture.SelectionWindow(shot, preset, auto,
                    CaptureActionFromArgs(args));
                RegisterWindow(_captureWindow);
                await _captureWindow.PrepareForDisplayAsync();
                await _captureWindow.ShowForCaptureAsync();
                WriteLifecycleLog("截图选区窗口已创建并激活");
            }
            catch (Exception ex)
            {
                try { _captureWindow?.Close(); }
                catch { /* 窗口可能尚未完成初始化 */ }
                _captureWindow = null;
                if (_captureStartCancelled)
                {
                    RestoreSettingsAfterCapture();
                    return;
                }
                WriteLifecycleLog($"截图流程异常：{ex.GetType().Name}: {ex.Message}");
                WriteLog("CaptureFlow", ex);
                ShowCaptureFailure($"截图流程启动失败：{ex.Message}");
            }
            finally
            {
                _captureStarting = false;
                if (_captureStartCancelled) ExitIfNoWindows();
            }
        }

        private static string CaptureActionFromArgs(string[] args)
        {
            var action = args.FirstOrDefault(a => a.StartsWith("--capture-action=",
                StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
            return action is "translate" or "ocr" ? action : "capture";
        }

        private async Task<string?> LoadSettingsAsync()
        {
            var vm = ViewModels.DemoSettings.Instance;
            vm.SuppressPersist = true;
            try
            {
                await Services.SettingsService.LoadFrontendPreferencesAsync(vm);

                var startupError = await BackendBootstrapper.EnsureRunningAsync();
                if (startupError is not null)
                {
                    vm.ShowToast(startupError);
                    return startupError;
                }

                var err = await Services.SettingsService.LoadBackendAsync(vm);
                if (err is not null) vm.ShowToast($"读取后台设置失败：{err}");
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

        internal static void ShowCaptureToast(string message)
        {
            if (Current is App app)
            {
                var toast = new Views.Capture.CaptureToastWindow(message);
                app.TrackWindow(toast);
                toast.ShowAfterCaptureWindow();
            }
        }

        private void TrackWindow(Window window)
        {
            if (!_openWindows.Add(window)) return;
            window.Closed += (_, _) =>
            {
                _openWindows.Remove(window);
                if (ReferenceEquals(window, _settingsWindow)) _settingsWindow = null;
                if (ReferenceEquals(window, _captureWindow))
                {
                    _captureWindow = null;
                    RestoreSettingsAfterCapture();
                }
                if (ReferenceEquals(window, _resultWindow))
                {
                    _resultWindow = null;
                    RestoreSettingsAfterCapture();
                }
                WriteLifecycleLog($"窗口关闭：{window.GetType().Name}，剩余窗口数={_openWindows.Count}");
                ExitIfNoWindows();
            };
        }

        private void RestoreSettingsAfterCapture()
        {
            // 截图期间保持设置窗口隐藏，避免它在截图画面和选区层之间闪回。
            // 结果窗仍打开时继续隐藏，直到用户关闭结果再恢复设置页。
            if (_captureWindow is null && _resultWindow is null && _settingsWindow is not null)
                _settingsWindow.AppWindow.Show();
        }

        private void ExitIfNoWindows()
        {
            if (_openWindows.Count != 0) return;
            WriteLifecycleLog("没有打开的窗口，释放单实例并结束进程");
            _singleInstance?.Dispose();
            _singleInstance = null;
            Exit();
            // WinUI 3 的 dispatcher/窗口关闭路径在某些异常激活场景中仍可能
            // 留下无窗口进程。此分支已确认没有任何受管窗口，完成 IPC 清理后
            // 直接结束进程，避免残留进程继续占用构建产物。
            Environment.Exit(0);
        }

        private void ShowCaptureFailure(string message)
        {
            // 截图失败时不可静默退出：没有设置窗口就打开一个承载错误提示，
            // 让用户能看到原因；用户关闭窗口后仍走统一无窗口退出清理。
            if (_settingsWindow is null)
            {
                _settingsWindow = new MainWindow();
                _window = _settingsWindow;
                RegisterWindow(_settingsWindow);
            }
            _settingsWindow.Activate();
            ViewModels.DemoSettings.Instance.ShowToast(message);
        }

        // ---------- 主题助手 ----------

        /// <summary>把设置中的主题模式解析为元素主题，供各窗口统一外观。</summary>
        public static ElementTheme ResolveTheme(int mode) => mode switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

    }
}
