using ScreenLens.WinUI.Models;
using ScreenLens.WinUI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenLens.WinUI.ViewModels
{
    /// <summary>
    /// 应用设置状态（历史名 DemoSettings，保留以减少 XAML 改动）。
    ///
    /// 数据边界：
    /// - 后端字段（热键 / 翻译服务 / OpenAI 参数）经 SettingsService
    ///   与 Python 配置双向同步，修改自动防抖保存；
    /// - 前端偏好（主题 / 密度 / 截图交互 / 结果显示）存本地
    ///   frontend.json，与 Python 配置完全隔离；
    /// - 加载期间 _suppressPersist 抑制回写。
    /// </summary>
    public sealed class DemoSettings : INotifyPropertyChanged
    {
        public static DemoSettings Instance { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool Set<T>(ref T field, T value,
            [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        private DemoSettings() { }

        /// <summary>加载装配期间抑制自动保存回环。</summary>
        internal bool SuppressPersist { get; set; }

        // ================================================== 后端字段（Python 配置）

        private string _hotkeyText = "Ctrl + Alt + A";
        /// <summary>截图快捷键（显示格式）；保存走 RegisterHotkey 通道。</summary>
        public string HotkeyText
        {
            get => _hotkeyText;
            set
            {
                if (Set(ref _hotkeyText, value))
                {
                    // Keep the display row in the model so pages do not need
                    // to subscribe to the singleton just to synchronize it.
                    Hotkeys[0].Keys = string.IsNullOrEmpty(value) ? "—" : value;
                }
            }
        }

        private int _targetLanguage; // 0=zh 1=en 2=ja
        public int TargetLanguage
        {
            get => _targetLanguage;
            set
            {
                if (Set(ref _targetLanguage, value) && !SuppressPersist)
                {
                    SettingsService.SaveBackendDebounced(this);
                }
            }
        }

        /// <summary>0 = Google 免费，1 = OpenAI 兼容，2 = 关闭</summary>
        private int _provider = 2;
        public int Provider
        {
            get => _provider;
            set
            {
                if (Set(ref _provider, value) && !SuppressPersist)
                {
                    SettingsService.SaveBackendDebounced(this);
                }
            }
        }

        private string _openAiBaseUrl = "";
        public string OpenAiBaseUrl
        {
            get => _openAiBaseUrl;
            set
            {
                if (Set(ref _openAiBaseUrl, value) && !SuppressPersist)
                {
                    SettingsService.SaveBackendDebounced(this);
                }
            }
        }

        private string _openAiModel = "gpt-4o-mini";
        public string OpenAiModel
        {
            get => _openAiModel;
            set
            {
                if (Set(ref _openAiModel, value) && !SuppressPersist)
                {
                    SettingsService.SaveBackendDebounced(this);
                }
            }
        }

        private string _openAiApiKey = "";
        /// <summary>API 密钥：仅用于设置编辑，绝不写日志或提示。</summary>
        public string OpenAiApiKey
        {
            get => _openAiApiKey;
            set
            {
                if (Set(ref _openAiApiKey, value) && !SuppressPersist)
                {
                    SettingsService.SaveBackendDebounced(this);
                }
            }
        }

        // ================================================== 前端偏好（本地）

        /// <summary>0 = 跟随系统，1 = 浅色，2 = 深色（默认）</summary>
        private int _themeMode = 2;
        public int ThemeMode
        {
            get => _themeMode;
            set
            {
                if (Set(ref _themeMode, value))
                {
                    if (!SuppressPersist) SettingsService.SaveFrontendDebounced(this);
                    ThemeModeChanged?.Invoke(value);
                }
            }
        }

        public event Action<int>? ThemeModeChanged;

        private bool _launchAtStartup;
        public bool LaunchAtStartup { get => _launchAtStartup; set => Set(ref _launchAtStartup, value); }

        private bool _startMinimized;
        public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }

        // ---------- 截图与选区 ----------
        /// <summary>左键选区：0 = 矩形选区，1 = 自由圈选（默认）</summary>
        private int _leftCaptureMode = 1;
        public int LeftCaptureMode
        {
            get => _leftCaptureMode;
            set
            {
                if (value is not (0 or 1)) return;
                if (Set(ref _leftCaptureMode, value) && !SuppressPersist)
                {
                    SettingsService.SaveFrontendDebounced(this);
                }
            }
        }

        /// <summary>右键选区：0 = 矩形选区（默认），1 = 自由圈选</summary>
        private int _rightCaptureMode;
        public int RightCaptureMode
        {
            get => _rightCaptureMode;
            set
            {
                if (value is not (0 or 1)) return;
                if (Set(ref _rightCaptureMode, value) && !SuppressPersist)
                    SettingsService.SaveFrontendDebounced(this);
            }
        }

        private bool _captureDimMaskEnabled = true;
        public bool CaptureDimMaskEnabled
        {
            get => _captureDimMaskEnabled;
            set
            {
                if (Set(ref _captureDimMaskEnabled, value) && !SuppressPersist)
                    SettingsService.SaveFrontendDebounced(this);
            }
        }

        /// <summary>仅控制自由圈选的外接矩形边框，不控制圈选轨迹。</summary>
        private bool _freeformBorderEnabled = true;
        public bool FreeformBorderEnabled
        {
            get => _freeformBorderEnabled;
            set
            {
                if (Set(ref _freeformBorderEnabled, value))
                {
                    Raise(nameof(CanEnableFreeformGlow));
                    Raise(nameof(FreeformGlowActive));
                    if (!SuppressPersist) SettingsService.SaveFrontendDebounced(this);
                }
            }
        }

        /// <summary>保留光效偏好；开启外接边框时仅暂停光效。</summary>
        private bool _freeformGlowEnabled = true;
        public bool FreeformGlowEnabled
        {
            get => _freeformGlowEnabled;
            set
            {
                if (Set(ref _freeformGlowEnabled, value))
                {
                    Raise(nameof(FreeformGlowActive));
                    if (!SuppressPersist) SettingsService.SaveFrontendDebounced(this);
                }
            }
        }

        public bool CanEnableFreeformGlow => !FreeformBorderEnabled;

        /// <summary>设置开关显示实际可用状态，禁用时不覆盖保存的偏好。</summary>
        public bool FreeformGlowActive
        {
            get => CanEnableFreeformGlow && FreeformGlowEnabled;
            set { if (CanEnableFreeformGlow) FreeformGlowEnabled = value; }
        }

        /// <summary>开启：松开鼠标显示确认工具条；关闭：松开鼠标直接开始识别</summary>
        private bool _confirmOnRelease = true;
        public bool ConfirmOnRelease
        {
            get => _confirmOnRelease;
            set
            {
                if (Set(ref _confirmOnRelease, value) && !SuppressPersist)
                {
                    SettingsService.SaveFrontendDebounced(this);
                }
            }
        }

        // ---------- OCR 与结果 ----------
        /// <summary>0 = 跟随截图位置，1 = 屏幕居中，2 = 记住上次位置</summary>
        private int _resultPosition;
        public int ResultPosition
        {
            get => _resultPosition;
            set
            {
                if (Set(ref _resultPosition, value) && !SuppressPersist)
                {
                    SettingsService.SaveFrontendDebounced(this);
                }
            }
        }

        /// <summary>0 = 小，1 = 中（默认），2 = 大</summary>
        private int _originalFontSize = 1;
        public int OriginalFontSize
        {
            get => _originalFontSize;
            set
            {
                if (Set(ref _originalFontSize, value) && !SuppressPersist)
                {
                    SettingsService.SaveFrontendDebounced(this);
                }
            }
        }

        /// <summary>0 = 图标 + 文字，1 = 仅图标</summary>
        private int _copyButtonLook;
        public int CopyButtonLook
        {
            get => _copyButtonLook;
            set
            {
                if (Set(ref _copyButtonLook, value) && !SuppressPersist)
                {
                    SettingsService.SaveFrontendDebounced(this);
                }
            }
        }

        // ---------- 外观 ----------
        /// <summary>0 = 舒适（默认），1 = 紧凑</summary>
        private int _cardDensity;
        public int CardDensity
        {
            get => _cardDensity;
            set
            {
                if (Set(ref _cardDensity, value))
                {
                    if (!SuppressPersist) SettingsService.SaveFrontendDebounced(this);
                    CardDensityChanged?.Invoke(value);
                }
            }
        }

        public event Action<int>? CardDensityChanged;

        // ---------- 快捷键 ----------

        public ObservableCollection<HotkeyEntry> Hotkeys { get; } = new()
        {
            new HotkeyEntry { Icon = "\uE722", Action = "截图并翻译", Description = "启动区域截图，识别后立即翻译", Keys = "Ctrl + Alt + A", Editable = true },
            new HotkeyEntry { Icon = "\uE8A5", Action = "截图并识别", Description = "仅识别选区文字，不进行翻译", Keys = "—", Editable = false },
            new HotkeyEntry { Icon = "\uE713", Action = "打开设置", Description = "打开 ScreenLens 设置窗口", Keys = "—", Editable = false },
            new HotkeyEntry { Icon = "\uE7E7", Action = "退出截图", Description = "取消进行中的截图并关闭遮罩", Keys = "Esc", Editable = false },
        };

        public void ResetHotkeys()
        {
            Hotkeys[0].Keys = "Ctrl + Alt + A";
        }

        // ---------- 轻量提示 ----------

        public event Action<string>? ToastRequested;

        /// <summary>对操作结果给出统一提示。</summary>
        public void ShowToast(string message) => ToastRequested?.Invoke(message);
    }
}
