using ScreenLens.WinUI.ViewModels;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenLens.WinUI.Services
{
    /// <summary>
    /// 设置数据的装配与持久化。
    ///
    /// 数据边界（阶段计划书 P2）：
    /// - 后端字段（快捷键 / 翻译服务）以 Python config.json 为唯一来源，
    ///   读写都经 IPC（GetSettings / SaveSettings），保存失败不谎报成功；
    /// - 纯前端偏好（主题 / 密度 / 截图交互等）存本地 frontend.json，
    ///   不写入也不覆盖 Python 配置的任何未知字段。
    /// </summary>
    public static class SettingsService
    {
        private static readonly string FrontendPrefsDir = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "ScreenLens");

        private static string FrontendPrefsPath =>
            Path.Combine(FrontendPrefsDir, "frontend.json");

        private static int? _lastResultX;
        private static int? _lastResultY;

        private static readonly SemaphoreSlim _saveMutex = new(1, 1);
        private static readonly SemaphoreSlim _backendSaveMutex = new(1, 1);
        private static Timer? _backendDebounce;
        private static Timer? _frontendDebounce;
        private static int _backendSaveVersion;

        public static string[] ProviderKeys { get; } =
            { "google_free", "openai", "none" };

        public static string[] LanguageKeys { get; } = { "zh", "en", "ja" };

        public static bool TryGetLastResultPosition(out int x, out int y)
        {
            x = _lastResultX ?? 0;
            y = _lastResultY ?? 0;
            return _lastResultX.HasValue && _lastResultY.HasValue;
        }

        public static async Task SaveLastResultPositionAsync(int x, int y)
        {
            _lastResultX = x;
            _lastResultY = y;
            await _saveMutex.WaitAsync();
            try
            {
                Directory.CreateDirectory(FrontendPrefsDir);
                JsonObject prefs;
                try
                {
                    prefs = File.Exists(FrontendPrefsPath)
                        ? JsonNode.Parse(await File.ReadAllTextAsync(FrontendPrefsPath))?.AsObject()
                            ?? new JsonObject()
                        : new JsonObject();
                }
                catch { prefs = new JsonObject(); }
                prefs["lastResultX"] = x;
                prefs["lastResultY"] = y;
                await File.WriteAllTextAsync(FrontendPrefsPath,
                    prefs.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 记忆位置失败不影响结果展示 */ }
            finally { _saveMutex.Release(); }
        }

        // ------------------------------------------------------------ 加载

        /// <summary>在创建 XAML 控件资源之前读取启动主题，避免先按系统主题缓存控件颜色。</summary>
        internal static int ReadStartupThemeMode()
        {
            try
            {
                var prefs = JsonNode.Parse(File.ReadAllText(FrontendPrefsPath))?.AsObject();
                var mode = prefs is null ? 2 : GetInt(prefs, "themeMode", 2);
                return mode is 0 or 1 or 2 ? mode : 2;
            }
            catch { return 2; }
        }

        /// <summary>从后端 + 本地偏好装配设置；后端不可用时保留默认值。</summary>
        /// <returns>错误信息（null = 成功）</returns>
        public static async Task<string?> LoadAsync(DemoSettings vm)
        {
            await LoadFrontendPreferencesAsync(vm);
            return await LoadBackendAsync(vm);
        }

        public static async Task LoadFrontendPreferencesAsync(DemoSettings vm)
        {
            try
            {
                if (File.Exists(FrontendPrefsPath))
                {
                    var prefs = JsonNode.Parse(
                        await File.ReadAllTextAsync(FrontendPrefsPath))
                        ?.AsObject();
                    if (prefs is not null)
                    {
                        vm.ThemeMode = GetInt(prefs, "themeMode", 2);
                        vm.CardDensity = GetInt(prefs, "cardDensity", 0);
                        // The old single captureMode preference is replaced by
                        // independent button mappings with the new defaults.
                        vm.LeftCaptureMode = GetInt(prefs, "leftCaptureMode", 1) == 0 ? 0 : 1;
                        vm.RightCaptureMode = GetInt(prefs, "rightCaptureMode", 0) == 1 ? 1 : 0;
                        vm.CaptureDimMaskEnabled = GetBool(prefs, "captureDimMaskEnabled", true);
                        vm.FreeformBorderEnabled = GetBool(prefs, "freeformBorderEnabled", true);
                        vm.FreeformGlowEnabled = GetBool(prefs, "freeformGlowEnabled", true);
                        vm.FreeformGlowWidthPercent = GetDouble(prefs, "freeformGlowWidthPercent", 100);
                        vm.FreeformGlowDepthEnabled = GetBool(prefs, "freeformGlowDepthEnabled", true);
                        vm.ConfirmOnRelease =
                            GetBool(prefs, "confirmOnRelease", true);
                        vm.ResultPosition = GetInt(prefs, "resultPosition", 0);
                        vm.OriginalFontSize = GetInt(prefs, "fontSize", 1);
                        vm.CopyButtonLook = GetInt(prefs, "copyButtonLook", 0);
                        _lastResultX = GetNullableInt(prefs, "lastResultX");
                        _lastResultY = GetNullableInt(prefs, "lastResultY");
                    }
                }
            }
            catch
            {
                // 偏好文件损坏：静默使用默认值
            }
        }

        public static async Task<string?> LoadBackendAsync(DemoSettings vm)
        {
            try
            {
                var resp = await BackendClient.Instance.CallAsync(
                    "GetSettings", null);
                var cfg = resp["config"]?.AsObject();
                if (cfg is not null)
                {
                    ApplyBackendToVm(vm, cfg);
                    return null;
                }
                return "后端返回的配置为空";
            }
            catch (BackendException e)
            {
                // 文案由 BackendClient 给出（含"后台代理未运行"等明确成因）
                return e.Message;
            }
            catch (Exception e)
            {
                return $"后台代理未运行或无法连接（{e.Message}）";
            }
        }

        private static void ApplyBackendToVm(DemoSettings vm, JsonObject cfg)
        {
            vm.HotkeyText = KeyboardToDisplay(
                cfg["hotkey"]?.GetValue<string>() ?? "ctrl+alt+a");

            var tr = cfg["translation"]?.AsObject();
            if (tr is null) return;
            var provider = tr["provider"]?.GetValue<string>() ?? "none";
            var providerIdx = Array.IndexOf(ProviderKeys, provider);
            vm.Provider = providerIdx >= 0 ? providerIdx : 2;
            var lang = tr["target_language"]?.GetValue<string>() ?? "zh";
            var langIdx = Array.IndexOf(LanguageKeys, lang);
            vm.TargetLanguage = langIdx >= 0 ? langIdx : 0;

            var oa = tr["openai"]?.AsObject();
            if (oa is not null)
            {
                vm.OpenAiBaseUrl = oa["base_url"]?.GetValue<string>() ?? "";
                vm.OpenAiModel = oa["model"]?.GetValue<string>() ?? "";
                vm.OpenAiApiKey = oa["api_key"]?.GetValue<string>() ?? "";
            }
        }

        // ------------------------------------------------------------ 保存

        /// <summary>把后端字段保存到 Python 配置（立即）。</summary>
        public static async Task<string?> SaveBackendAsync(DemoSettings vm)
        {
            await _backendSaveMutex.WaitAsync();
            try { return await SaveBackendCoreAsync(vm); }
            finally { _backendSaveMutex.Release(); }
        }

        private static async Task<string?> SaveBackendCoreAsync(DemoSettings vm)
        {
            var config = new JsonObject
            {
                ["hotkey"] = DisplayToKeyboard(vm.HotkeyText),
                ["translation"] = new JsonObject
                {
                    ["provider"] = ProviderKeys[vm.Provider],
                    ["target_language"] = LanguageKeys[vm.TargetLanguage],
                    ["openai"] = new JsonObject
                    {
                        ["base_url"] = vm.OpenAiBaseUrl ?? "",
                        ["api_key"] = vm.OpenAiApiKey ?? "",
                        ["model"] = vm.OpenAiModel ?? "",
                    },
                },
            };
            try
            {
                await BackendClient.Instance.CallAsync("SaveSettings",
                    new JsonObject { ["config"] = config });
                return null;
            }
            catch (BackendException e)
            {
                return $"保存失败：{e.Message}（配置未写入，请调整后重试）";
            }
            catch (Exception e)
            {
                return $"保存失败：后台代理不可用（{e.Message}）";
            }
        }

        /// <summary>热键变更走独立通道（注册成功才算数）。</summary>
        public static async Task<string?> SaveHotkeyAsync(string newHotkey)
        {
            try
            {
                await BackendClient.Instance.CallAsync("RegisterHotkey",
                    new JsonObject
                    {
                        ["hotkey"] = DisplayToKeyboard(newHotkey),
                    });
                return null;
            }
            catch (BackendException e)
            {
                return e.Message;
            }
            catch (Exception e)
            {
                return $"后台代理不可用（{e.Message}）";
            }
        }

        /// <summary>后端字段防抖保存（属性变更时调用，500ms 合并）。</summary>
        public static void SaveBackendDebounced(DemoSettings vm)
        {
            var version = Interlocked.Increment(ref _backendSaveVersion);
            _backendDebounce?.Dispose();
            _backendDebounce = new Timer(async _ =>
            {
                await _backendSaveMutex.WaitAsync();
                try
                {
                    if (version != Volatile.Read(ref _backendSaveVersion)) return;
                    var err = await SaveBackendCoreAsync(vm);
                    if (err is not null)
                    {
                        vm.ShowToast(err);
                    }
                }
                finally { _backendSaveMutex.Release(); }
            }, null, 500, Timeout.Infinite);
        }

        /// <summary>前端偏好防抖保存。</summary>
        public static void SaveFrontendDebounced(DemoSettings vm)
        {
            _frontendDebounce?.Dispose();
            _frontendDebounce = new Timer(async _ =>
            {
                await _saveMutex.WaitAsync();
                try
                {
                    var prefs = new JsonObject
                    {
                        ["themeMode"] = vm.ThemeMode,
                        ["cardDensity"] = vm.CardDensity,
                        ["leftCaptureMode"] = vm.LeftCaptureMode,
                        ["rightCaptureMode"] = vm.RightCaptureMode,
                        ["captureDimMaskEnabled"] = vm.CaptureDimMaskEnabled,
                        ["freeformBorderEnabled"] = vm.FreeformBorderEnabled,
                        ["freeformGlowEnabled"] = vm.FreeformGlowEnabled,
                        ["freeformGlowWidthPercent"] = vm.FreeformGlowWidthPercent,
                        ["freeformGlowDepthEnabled"] = vm.FreeformGlowDepthEnabled,
                        ["confirmOnRelease"] = vm.ConfirmOnRelease,
                        ["resultPosition"] = vm.ResultPosition,
                        ["fontSize"] = vm.OriginalFontSize,
                        ["copyButtonLook"] = vm.CopyButtonLook,
                        ["lastResultX"] = _lastResultX,
                        ["lastResultY"] = _lastResultY,
                    };
                    Directory.CreateDirectory(FrontendPrefsDir);
                    await File.WriteAllTextAsync(FrontendPrefsPath,
                        prefs.ToJsonString(new JsonSerializerOptions
                        {
                            WriteIndented = true,
                        }));
                }
                catch
                {
                    // 偏好保存失败不打扰用户
                }
                finally
                {
                    _saveMutex.Release();
                }
            }, null, 600, Timeout.Infinite);
        }

        // -------------------------------------------------------- 热键格式

        /// <summary>"Ctrl + Alt + Page Up" → "ctrl+alt+page up"（keyboard 库格式）。</summary>
        public static string DisplayToKeyboard(string display)
            => string.Join("+", (display ?? "").Split('+',
                StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                {
                    var normalized = part.Trim().ToLowerInvariant();
                    // keyboard 库的  键名为 ；配置串也用 + 分隔，
                    // 所以 UI 录制时用 Plus 表示，再在这里映射到其别名。
                    return normalized == "plus" ? "add" : normalized;
                }));

        /// <summary>"ctrl+alt+a" → "Ctrl + Alt + A"（界面显示格式）。</summary>
        public static string KeyboardToDisplay(string kb)
        {
            var parts = (kb ?? "").Split('+',
                StringSplitOptions.RemoveEmptyEntries);
            var outParts = new string[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                outParts[i] = p.Length <= 1
                    ? p.ToUpperInvariant()
                    : char.ToUpperInvariant(p[0]) + p[1..];
            }
            return string.Join(" + ", outParts);
        }

        private static int GetInt(JsonObject o, string key, int fallback)
        {
            try
            {
                return o[key]?.GetValue<int>() ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static int? GetNullableInt(JsonObject o, string key)
        {
            try { return o[key]?.GetValue<int>(); }
            catch { return null; }
        }

        /// <summary>保存草稿成功后才更新正在使用的配置。</summary>
        public static async Task<string?> SaveTranslationAsync(DemoSettings vm, JsonObject translation)
        {
            await _backendSaveMutex.WaitAsync();
            try
            {
                var config = new JsonObject
                {
                    ["hotkey"] = DisplayToKeyboard(vm.HotkeyText),
                    ["translation"] = translation.DeepClone(),
                };
                await BackendClient.Instance.CallAsync("SaveSettings", new JsonObject { ["config"] = config });
                var suppressed = vm.SuppressPersist;
                vm.SuppressPersist = true;
                try { ApplyBackendToVm(vm, config); }
                finally { vm.SuppressPersist = suppressed; }
                return null;
            }
            catch (BackendException ex) { return ex.Message; }
            catch (Exception) { return "配置未能保存，请确认后台代理正在运行后重试。"; }
            finally { _backendSaveMutex.Release(); }
        }

        private static double GetDouble(JsonObject o, string key, double fallback)
        {
            try { return o[key]?.GetValue<double>() ?? fallback; }
            catch { return fallback; }
        }

        private static bool GetBool(JsonObject o, string key, bool fallback)
        {
            try
            {
                return o[key]?.GetValue<bool>() ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }
}
